using System;
using System.Collections;
using System.Collections.Generic;
using AIBridge.Pilot;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>Docking ports: what there is, docking with another vessel, and letting go again.</summary>
    public static class DockApi
    {
        static BridgeHost host;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("ports", "Docking ports on this vessel and on the target vessel, with their state. params: {target}", Ports).AlwaysAllowed = true;
            Rpc.Sync("dock", "Dock with another vessel using RCS (rendezvous first). params: {target: vessel name or id (default: the current target), my_port, target_port, speed, roll}", Dock);
            Rpc.Async("undock", "Release a docked docking port; the vessel becomes two. params: {port}", Undock);
        }

        static List<ModuleDockingNode> PortsOf(Vessel v)
        {
            var list = new List<ModuleDockingNode>();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleDockingNode n) list.Add(n);
            return list;
        }

        static bool Free(ModuleDockingNode n) => n.state != null && n.state.StartsWith("Ready", StringComparison.OrdinalIgnoreCase) && n.otherNode == null;

        static bool Compatible(ModuleDockingNode a, ModuleDockingNode b)
        {
            if (a.nodeTypes != null && b.nodeTypes != null && a.nodeTypes.Count > 0 && b.nodeTypes.Count > 0) return a.nodeTypes.Overlaps(b.nodeTypes);
            return a.nodeType == b.nodeType;
        }

        static JObj Describe(ModuleDockingNode n)
        {
            var o = new JObj();
            o.Add("id", n.part.flightID);
            o.Add("part", n.part.partInfo.title);
            o.Add("size", n.nodeType);
            o.Add("state", n.state);
            if (n.otherNode != null && n.otherNode.part != null) o.Add("joined_to", n.otherNode.part.partInfo.title);
            if (n.part.ShieldedFromAirstream) o.Add("note", "inside a closed bay or fairing");
            return o;
        }

        static Vessel ResolveTarget(Args a, Vessel v, bool required)
        {
            string name = a.Str("target");
            Vessel target = null;
            if (!string.IsNullOrEmpty(name))
            {
                target = NavApi.FindVessel(name);
                if (target == null) throw new ApiException("No vessel named '" + name + "'. Use vessels to list them.");
            }
            else if (v.targetObject is Vessel tv) target = tv;
            else if (v.targetObject is ModuleDockingNode tn) target = tn.vessel;
            if (target == v) throw new ApiException("That is the vessel being flown.");
            if (target == null && required) throw new ApiException("Say which vessel to dock with: target (a vessel name or id), or set a target first.");
            return target;
        }

        static object Ports(Args a)
        {
            Vessel v = FlightApi.Active();
            var o = new JObj();
            var mine = new List<object>();
            foreach (ModuleDockingNode n in PortsOf(v)) mine.Add(Describe(n));
            o.Add("this_vessel", mine);
            Vessel target = ResolveTarget(a, v, false);
            if (target != null)
            {
                o.Add("target", target.GetDisplayName());
                o.Add("distance_m", Math.Round((target.GetWorldPos3D() - v.GetWorldPos3D()).magnitude, 1));
                if (!target.loaded) o.Add("target_ports", "unknown: the target is too far away to be loaded (get within about 2 km)");
                else
                {
                    var theirs = new List<object>();
                    foreach (ModuleDockingNode n in PortsOf(target)) theirs.Add(Describe(n));
                    o.Add("target_ports", theirs);
                }
            }
            int thrusters = 0;
            foreach (Part p in v.parts) if (p.FindModuleImplementing<ModuleRCS>() != null) thrusters++;
            o.Add("rcs_thrusters", thrusters);
            return o;
        }

        static ModuleDockingNode Pick(List<ModuleDockingNode> ports, string selector, string whose)
        {
            if (string.IsNullOrEmpty(selector)) return null;
            foreach (ModuleDockingNode n in ports)
                if (n.part.flightID.ToString() == selector.Trim()) return n;
            foreach (ModuleDockingNode n in ports)
                if (n.part.partInfo.title.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0 || n.part.partInfo.name.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0) return n;
            throw new ApiException("No docking port matching '" + selector + "' on " + whose + ". Use ports to list them.");
        }

        static object Dock(Args a)
        {
            Vessel v = FlightApi.Active();
            if (!v.IsControllable) throw new ApiException("The vessel is not controllable.");
            Vessel target = ResolveTarget(a, v, true);
            double distance = (target.GetWorldPos3D() - v.GetWorldPos3D()).magnitude;
            if (!target.loaded || distance > 2300)
                throw new ApiException("'" + target.GetDisplayName() + "' is " + Math.Round(distance) + " m away. Rendezvous first: plan transfer and match_velocity until it is within about 2 km (200 m is better) and nearly at rest relative to this vessel.");
            double relative = (v.obt_velocity - target.obt_velocity).magnitude;
            if (relative > 15) throw new ApiException("The relative speed is " + Math.Round(relative, 1) + " m/s. Match velocities first (plan match_velocity, or hold target_retrograde and burn).");

            List<ModuleDockingNode> mine = PortsOf(v), theirs = PortsOf(target);
            if (mine.Count == 0) throw new ApiException("This vessel has no docking port.");
            if (theirs.Count == 0) throw new ApiException("'" + target.GetDisplayName() + "' has no docking port.");
            int thrusters = 0;
            foreach (Part p in v.parts)
            {
                ModuleRCS rcs = p.FindModuleImplementing<ModuleRCS>();
                if (rcs != null && rcs.rcsEnabled) thrusters++;
            }
            if (thrusters == 0) throw new ApiException("This vessel has no RCS thrusters; docking needs them to move sideways.");

            ModuleDockingNode my = Pick(mine, a.Str("my_port"), "this vessel"), their = Pick(theirs, a.Str("target_port"), "the target");
            if (my != null && !Free(my)) throw new ApiException("That port of this vessel is not free (" + my.state + ").");
            if (their != null && !Free(their)) throw new ApiException("That port of the target is not free (" + their.state + ").");

            // Whatever was not chosen: the free, matching pair whose target port faces this vessel most squarely.
            double best = double.NegativeInfinity;
            ModuleDockingNode bestMine = null, bestTheirs = null;
            foreach (ModuleDockingNode m in mine)
            {
                if ((my != null && m != my) || !Free(m) || m.part.ShieldedFromAirstream) continue;
                foreach (ModuleDockingNode t in theirs)
                {
                    if ((their != null && t != their) || !Free(t) || !Compatible(m, t)) continue;
                    double facing = Vector3d.Dot(((Vector3d)t.nodeTransform.forward).normalized, ((Vector3d)m.nodeTransform.position - (Vector3d)t.nodeTransform.position).normalized);
                    if (v.GetReferenceTransformPart() == m.part) facing += 0.5;   // prefer the port the player is controlling from
                    if (facing > best) { best = facing; bestMine = m; bestTheirs = t; }
                }
            }
            if (bestMine == null)
                throw new ApiException("No free pair of docking ports of the same size. This vessel: " + Sizes(mine) + ". Target: " + Sizes(theirs) + ".");

            var p2 = new DockProgram { Mine = bestMine, Theirs = bestTheirs, MaxSpeed = OrbitMath.Clamp(a.Num("speed", 2), 0.3, 5) };
            if (a.Has("roll")) p2.Roll = a.Num("roll");
            Autopilot.I.Start(p2);
            JObj status = Autopilot.I.Status();
            status.Add("my_port", Describe(bestMine)).Add("target_port", Describe(bestTheirs)).Add("distance_m", Math.Round(distance, 1)).Add("relative_speed", Math.Round(relative, 2));
            return status;
        }

        static string Sizes(List<ModuleDockingNode> ports)
        {
            var parts = new List<string>();
            foreach (ModuleDockingNode n in ports) parts.Add(n.nodeType + " (" + n.state + ")");
            return string.Join(", ", parts.ToArray());
        }

        static IEnumerator Undock(Call c)
        {
            Vessel v = FlightApi.Active();
            var joined = new List<ModuleDockingNode>();
            foreach (ModuleDockingNode n in PortsOf(v)) if (DockProgram.IsDocked(n)) joined.Add(n);
            if (joined.Count == 0) throw new ApiException("No docking port of this vessel is docked.");
            ModuleDockingNode port = Pick(joined, c.Args.Str("port"), "this vessel") ?? joined[0];
            string was = port.state;
            int before = v.parts.Count;
            Autopilot.I.Release("undocking", true);
            host.Note("undocking " + port.part.partInfo.title);
            if (was.Contains("PreAttached")) port.Decouple();
            else if (port.sameVesselDockJoint != null) port.UndockSameVessel();
            else port.Undock();
            yield return new WaitSec(1.5f);
            Vessel active = FlightGlobals.ActiveVessel;
            var o = new JObj().Add("undocked", port.part.partInfo.title).Add("now_flying", active.GetDisplayName()).Add("parts", active.parts.Count).Add("parts_before", before);
            var near = new List<object>();
            foreach (Vessel other in FlightGlobals.Vessels)
            {
                if (other == null || other == active || !other.loaded) continue;
                double d = (other.GetWorldPos3D() - active.GetWorldPos3D()).magnitude;
                if (d < 500) near.Add(new JObj().Add("name", other.GetDisplayName()).Add("id", other.persistentId).Add("distance_m", Math.Round(d, 1)).Add("parts", other.parts.Count));
            }
            o.Add("nearby", near);
            c.Result = o;
        }
    }
}
