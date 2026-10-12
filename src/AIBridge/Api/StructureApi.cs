using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// How well a craft is holding together.
    ///
    /// In flight this is measured. The game joins each part to its parent with one or three physics joints,
    /// and (as its PartJoint sets them up) those joints do not hold the parts rigidly: they are springs, very
    /// stiff ones along the joint and softer ones against bending, inside a hard stop a metre out. So a joint
    /// under load gives: the two parts are pulled a little apart and bent a little out of line, more the
    /// harder they are loaded, and it only BREAKS when it has been pulled right out to that stop and the load
    /// there is more than the joint's strength. (Which is why an overloaded craft in this game stretches and
    /// sags before it comes apart, and why physics time warp, which softens the springs, pulls craft to
    /// pieces.) Unity's own figure for the force on a joint leaves the springs' force out, so it reads nothing
    /// until the stop is reached, and is no use as a warning. What is measured here instead is what can be
    /// seen: how far each joint has been pulled apart, as a part of the way to its stop, and how far it is
    /// bent. The worst of each is kept for the whole flight, since a rocket is strained hardest for a few
    /// seconds (lift-off, maximum dynamic pressure, staging) that an AI looking once a minute would miss.
    /// With that go the things that break parts without breaking joints: heat, g-force and pressure against
    /// each part's limit, and whether the game has those limits switched on.
    ///
    /// In the editor nothing is under load, so it is worked out instead: what each joint will have to pass on
    /// when the first engines light, against the strength the game will give it (a joint that is stronger
    /// than its load cannot break from thrust, however far it stretches), and how far a sideways shove of one
    /// g would bend the craft there, from the stiffness the game will give that joint's spring. It is an
    /// estimate for comparing one design with another, and says so.
    /// </summary>
    public static class StructureApi
    {
        public static void Register(BridgeHost h)
        {
#if DEV
            Rpc.Sync("dev_joints", "Development build only: the physics joints of a part as Unity has them. params: {part}", a =>
            {
                var list = new List<object>();
                foreach (Part p in PartApi.Select(FlightApi.Active().parts, a.Require("part")))
                {
                    if (p.attachJoint == null || list.Count >= 6) continue;
                    Rigidbody body = p.rb;
                    var one = new JObj().Add("part", p.partInfo.title).Add("parent", p.parent != null ? p.parent.partInfo.title : null).Add("has_body", body != null)
                        .Add("mass_t", body != null ? body.mass : 0f).Add("asleep", body != null && body.IsSleeping()).Add("physicsless", p.physicalSignificance.ToString());
                    var joints = new List<object>();
                    foreach (ConfigurableJoint j in p.attachJoint.joints)
                    {
                        if (j == null) { joints.Add("gone"); continue; }
                        joints.Add(new JObj().Add("on", j.gameObject.name).Add("to", j.connectedBody != null ? j.connectedBody.name : "nothing")
                            .Add("force", j.currentForce.ToString("F2")).Add("torque", j.currentTorque.ToString("F2")).Add("break_force", j.breakForce).Add("break_torque", j.breakTorque)
                            .Add("pulled_apart_m", j.connectedBody != null ? (j.transform.TransformPoint(j.anchor) - j.connectedBody.transform.TransformPoint(j.connectedAnchor)).magnitude : -1f)
                            .Add("linear", j.xMotion + "/" + j.yMotion + "/" + j.zMotion).Add("angular", j.angularXMotion + "/" + j.angularYMotion + "/" + j.angularZMotion)
                            .Add("linear_limit", j.linearLimit.limit).Add("linear_limit_spring", j.linearLimitSpring.spring)
                            .Add("x_drive", j.xDrive.positionSpring + " / " + j.xDrive.positionDamper + " / " + j.xDrive.maximumForce)
                            .Add("ang_x_drive", j.angularXDrive.positionSpring + " / " + j.angularXDrive.positionDamper + " / " + j.angularXDrive.maximumForce)
                            .Add("ang_yz_drive", j.angularYZDrive.positionSpring + " / " + j.angularYZDrive.positionDamper + " / " + j.angularYZDrive.maximumForce)
                            .Add("rotation_drive_mode", j.rotationDriveMode.ToString()).Add("projection", j.projectionMode.ToString()).Add("anchor", j.anchor.ToString("F2")));
                    }
                    one.Add("joints", joints);
                    list.Add(one);
                }
                return list;
            }).AlwaysAllowed = true;
#endif
            Rpc.Sync("structure", "How well the craft is holding together. In flight: every joint's load against what breaks it, how far the craft is bent, heat, g-force and pressure against each part's limit, now and at the worst moment of the flight so far. In the editor: an estimate of the same before launch. params: {limit: how many of the worst to list (default 6), part: only joints of parts matching this, reset: true forgets the flight's worst so far}", Structure).AlwaysAllowed = true;
        }

        static double R(double v, int digits = 1) => double.IsNaN(v) || double.IsInfinity(v) ? double.NaN : Math.Round(v, digits);

        // ---------------------------------------------------------------- the worst of the flight so far

        sealed class Worst
        {
            public double Joint, Bend, Sway, G, Pressure, Heat;      // joint and heat as parts of the limit (1 = at it); bend in degrees; sway in metres; g; kPa
            public string JointWhere, BendWhere, HeatWhere;
            public double JointAt, BendAt, SwayAt, GAt, PressureAt, HeatAt;          // mission time of each
            public int Samples;
        }

        static Guid watched;
        static Worst worst = new Worst();
        static int tick;
        static readonly List<JObj> breaks = new List<JObj>();     // the joints that have broken under load in this flight, as they were when they went

        /// <summary>
        /// A joint has let go (the game says so before it takes the joint away): what it was like at that moment is kept.
        /// Called by the flight log, which listens for it. (Not listened for here: the game's events take only methods of
        /// an object, and throw on a static one.)
        /// </summary>
        public static void Broke(PartJoint link, float force)
        {
            try
            {
                // (with no force given it was taken apart on purpose: a decoupler firing, a port undocking)
                if (link == null || force <= 0f || link.Child == null || link.Child.vessel == null || link.Child.vessel != FlightGlobals.ActiveVessel) return;
                Part p = link.Child;
                double share = link.joints != null ? Share(link, out float apart, out float stop, out bool _) : 0;
                var o = new JObj().Add("part", p.partInfo.title).Add("joined_to", link.Parent != null ? link.Parent.partInfo.title : "?").Add("at_met_s", R(p.vessel.missionTime, 1))
                    .Add("under_kn", R(force, 0)).Add("pulled_apart_percent", R(share * 100, 0));
                breaks.Add(o);
                if (breaks.Count > 12) breaks.RemoveAt(0);
            }
            catch (Exception) { }
        }

        /// <summary>Called every physics step: every third one, the craft being flown is measured and the worst kept.</summary>
        public static void FixedTick()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready) return;
            if (++tick % 3 != 0) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !v.loaded || v.packed || v.isEVA) return;
            if (v.id != watched) { watched = v.id; worst = new Worst(); breaks.Clear(); }
            double now = v.missionTime;
            Part root = v.rootPart;
            if (root == null) return;
            Quaternion rootBack = Quaternion.Inverse(root.transform.rotation);
            List<Part> parts = v.parts;
            for (int n = 0; n < parts.Count; n++)
            {
                Part p = parts[n];
                if (p == null) continue;
                PartJoint link = p.attachJoint;
                if (link != null && link.joints != null)
                {
                    double share = Share(link);
                    if (share > worst.Joint) { worst.Joint = share; worst.JointWhere = Between(p); worst.JointAt = now; }
                }
                if (p == root || p.parent == null || Moves(p)) continue;
                float bend = Bend(p, rootBack);
                if (bend > worst.Bend) { worst.Bend = bend; worst.BendWhere = Between(p); worst.BendAt = now; }
                float sway = (root.transform.InverseTransformPoint(p.transform.position) - p.orgPos).magnitude;
                if (sway > worst.Sway) { worst.Sway = sway; worst.SwayAt = now; }
                double heat = HeatShare(p);
                if (heat > worst.Heat) { worst.Heat = heat; worst.HeatWhere = p.partInfo.title; worst.HeatAt = now; }
            }
            if (v.geeForce > worst.G) { worst.G = v.geeForce; worst.GAt = now; }
            double pressure = v.staticPressurekPa + v.dynamicPressurekPa;
            if (pressure > worst.Pressure) { worst.Pressure = pressure; worst.PressureAt = now; }
            worst.Samples++;
        }

        // ---------------------------------------------------------------- measuring one part

        /// <summary>
        /// How close the joint is to letting go, 1 being the point of breaking: how far it has been pulled apart as a part
        /// of the way to its stop; or, where a joint is rigid or already at its stop (only then does Unity give a force),
        /// the pull and the twist on it as parts of what breaks it. The worst of its physics joints counts.
        /// </summary>
        static double Share(PartJoint link) => Share(link, out _, out _, out _);

        static double Share(PartJoint link, out float apart, out float stop, out bool atStop)
        {
            apart = 0f; stop = 0f; atStop = false;
            double share = 0;
            for (int n = 0; n < link.joints.Count; n++)
            {
                ConfigurableJoint one = link.joints[n];
                if (one == null || one.connectedBody == null) continue;
                float pulled = (one.transform.TransformPoint(one.anchor) - one.connectedBody.transform.TransformPoint(one.connectedAnchor)).magnitude;
                float limit = one.xMotion == ConfigurableJointMotion.Limited ? one.linearLimit.limit : 0f;
                if (pulled > apart) apart = pulled;
                if (limit > stop) stop = limit;
                double here = limit > 0f ? pulled / limit : 0;
                float f = one.currentForce.magnitude, t = one.currentTorque.magnitude;
                double forced = Math.Max(one.breakForce > 0f && !float.IsInfinity(one.breakForce) ? f / one.breakForce : 0, one.breakTorque > 0f && !float.IsInfinity(one.breakTorque) ? t / one.breakTorque : 0);
                if (forced > 0.02) atStop = true;
                share = Math.Max(share, Math.Max(here, forced));
            }
            return share;
        }

        /// <summary>How far, in degrees, the part is turned against its parent from the way the two were put together.</summary>
        static float Bend(Part p, Quaternion rootBack)
        {
            Quaternion now = Quaternion.Inverse(rootBack * p.parent.transform.rotation) * (rootBack * p.transform.rotation);
            Quaternion built = Quaternion.Inverse(p.parent.orgRot) * p.orgRot;
            return Quaternion.Angle(now, built);
        }

        /// <summary>Whether the part is meant to move against its parent (a hinge, a piston, a rotor, a wheel's leg, a kerbal): not flexing, then.</summary>
        static bool Moves(Part p)
        {
            for (Part up = p; up != null; up = up.parent)
            {
                if (up.isRobotic()) return true;
                if (up == p && (up.isKerbalEVA() || up.FindModuleImplementing<ModuleWheelBase>() != null)) return true;
            }
            return false;
        }

        static double HeatShare(Part p)
        {
            double inside = p.maxTemp > 0 ? p.temperature / p.maxTemp : 0;
            double skin = p.skinMaxTemp > 0 ? p.skinTemperature / p.skinMaxTemp : 0;
            return Math.Max(inside, skin);
        }

        static string Between(Part p) => p.parent == null ? p.partInfo.title : "'" + p.partInfo.title + "' on '" + p.parent.partInfo.title + "'";

        static string How(Part p) => p.attachMode == AttachModes.SRF_ATTACH ? "on its surface" : "end to end";

        // ---------------------------------------------------------------- the command

        static object Structure(Args a)
        {
            int limit = Math.Max(1, Math.Min(40, a.Int("limit", 6)));
            string only = a.Str("part");
            if (HighLogic.LoadedScene == GameScenes.EDITOR)
            {
                ShipConstruct built = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
                if (built == null || built.parts.Count == 0) throw new ApiException("The editor is empty.");
                return StructureEstimate.Estimate(built, limit, only);
            }
            Vessel v = FlightApi.Active();
            if (a.Bool("reset", false)) { worst = new Worst(); watched = v.id; breaks.Clear(); }
            var o = new JObj().Add("vessel", v.GetDisplayName()).Add("parts", v.parts.Count).Add("mass_t", R(v.totalMass, 2)).Add("situation", v.situation.ToString());
            if (v.packed || !v.loaded)
            {
                o.Add("measured", false);
                o.Add("note", "The craft is on rails (time warp, or just loaded): nothing is under load and nothing can break. Ask again at normal speed.");
                return o;
            }

            // ---- what the game is holding the craft to
            var rules = new JObj();
            bool joints = !CheatOptions.UnbreakableJoints;
            bool gRule = false, pressureRule = false;
            try
            {
                var advanced = HighLogic.CurrentGame.Parameters.CustomParams<GameParameters.AdvancedParams>();
                gRule = advanced.GPartLimits; pressureRule = advanced.PressurePartLimits;
            }
            catch (Exception) { }
            rules.Add("joints_can_break", joints).Add("parts_break_from_g_force", gRule).Add("parts_break_from_pressure", pressureRule)
                 .Add("parts_break_from_heat", !CheatOptions.IgnoreMaxTemperature).Add("crash_damage", !CheatOptions.NoCrashDamage);
            o.Add("rules_in_this_save", rules);

            // ---- the loads on it now
            o.Add("now", new JObj().Add("g_force", R(v.geeForce, 2)).Add("dynamic_pressure_kpa", R(v.dynamicPressurekPa, 2)).Add("mach", R(v.mach, 2))
                .Add("turning_deg_s", R(v.angularVelocity.magnitude * Mathf.Rad2Deg, 1)));

            // ---- every joint
            Part root = v.rootPart;
            Quaternion rootBack = Quaternion.Inverse(root.transform.rotation);
            var loaded = new List<KeyValuePair<double, JObj>>();
            var bent = new List<KeyValuePair<double, JObj>>();
            var hot = new List<KeyValuePair<double, JObj>>();
            int count = 0, struts = 0, autostruts = 0, rigid = 0, clamps = 0;
            double worstShare = 0, worstBend = 0, worstSway = 0, worstHeat = 0, leastG = double.MaxValue, leastPressure = double.MaxValue;
            string leastGPart = null, leastPressurePart = null;
            foreach (Part p in v.parts)
            {
                if (p == null) continue;
                if (p is CompoundPart cp && cp.target != null && p.partInfo.name != "fuelLine") struts++;
                if (p.autoStrutMode != Part.AutoStrutMode.Off) autostruts++;
                if (p.rigidAttachment) rigid++;
                if (p.FindModuleImplementing<LaunchClamp>() != null) clamps++;
                if (p.gTolerance < leastG) { leastG = p.gTolerance; leastGPart = p.partInfo.title; }
                if (!p.ShieldedFromAirstream && p.maxPressure < leastPressure) { leastPressure = p.maxPressure; leastPressurePart = p.partInfo.title; }
                double heat = HeatShare(p);
                worstHeat = Math.Max(worstHeat, heat);
                bool wanted = string.IsNullOrEmpty(only) || p.partInfo.title.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0 || p.partInfo.name.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0;
                if (heat > 0.5 && wanted)
                    hot.Add(new KeyValuePair<double, JObj>(heat, new JObj().Add("part", p.partInfo.title).Add("id", p.flightID).Add("inside_k", R(p.temperature, 0)).Add("inside_limit_k", R(p.maxTemp, 0))
                        .Add("skin_k", R(p.skinTemperature, 0)).Add("skin_limit_k", R(p.skinMaxTemp, 0)).Add("percent_of_limit", R(heat * 100, 0))));
                PartJoint link = p.attachJoint;
                if (link == null || link.joints == null || p.parent == null) continue;
                count++;
                double share = Share(link, out float apart, out float stop, out bool atStop);
                worstShare = Math.Max(worstShare, share);
                bool moves = Moves(p);
                float bend = moves ? 0f : Bend(p, rootBack);
                float sway = moves ? 0f : (root.transform.InverseTransformPoint(p.transform.position) - p.orgPos).magnitude;
                worstBend = Math.Max(worstBend, bend); worstSway = Math.Max(worstSway, sway);
                if (!wanted) continue;
                var j = new JObj().Add("part", p.partInfo.title).Add("id", p.flightID).Add("joined_to", p.parent.partInfo.title).Add("joined", How(p));
                j.Add("percent_of_breaking", R(share * 100, 1));
                j.Add("pulled_apart_m", R(apart, 3));
                if (stop > 0f) j.Add("stop_at_m", R(stop, 2));
                if (!moves) j.Add("bent_deg", R(bend, 2));
                if (atStop) j.Add("at_its_stop", true);
                if (p.rigidAttachment) j.Add("rigid", true);
                if (link.joints.Count > 1) j.Add("physics_joints", link.joints.Count);
                if (p.autoStrutMode != Part.AutoStrutMode.Off) j.Add("autostrut", p.autoStrutMode.ToString());
                loaded.Add(new KeyValuePair<double, JObj>(share, j));
                if (!moves) bent.Add(new KeyValuePair<double, JObj>(bend, new JObj().Add("part", p.partInfo.title).Add("id", p.flightID).Add("joined_to", p.parent.partInfo.title).Add("bent_deg", R(bend, 2)).Add("out_of_place_m", R(sway, 3))));
            }
            loaded.Sort((x, y) => y.Key.CompareTo(x.Key)); bent.Sort((x, y) => y.Key.CompareTo(x.Key)); hot.Sort((x, y) => y.Key.CompareTo(x.Key));

            var jointsOut = new JObj().Add("count", count).Add("nearest_to_breaking_percent", R(worstShare * 100, 1));
            jointsOut.Add("most_loaded", Top(loaded, limit));
            o.Add("joints", jointsOut);
            o.Add("flex", new JObj().Add("worst_bend_deg", R(worstBend, 2)).Add("furthest_out_of_place_m", R(worstSway, 3)).Add("most_bent", Top(bent, limit)));
            var heatOut = new JObj().Add("hottest_percent_of_limit", R(worstHeat * 100, 0));
            if (hot.Count > 0) heatOut.Add("hot_parts", Top(hot, limit));
            o.Add("heat", heatOut);
            if (leastGPart != null) o.Add("g_force", new JObj().Add("now", R(v.geeForce, 2)).Add("lowest_limit", R(leastG, 0)).Add("of", leastGPart).Add("percent_of_limit", R(v.geeForce / leastG * 100, 0)).Add("enforced", gRule));
            if (leastPressurePart != null)
            {
                double pressure = v.staticPressurekPa + v.dynamicPressurekPa;
                o.Add("pressure", new JObj().Add("now_kpa", R(pressure, 1)).Add("lowest_limit_kpa", R(leastPressure, 0)).Add("of", leastPressurePart).Add("percent_of_limit", R(pressure / leastPressure * 100, 0)).Add("enforced", pressureRule));
            }
            o.Add("stiffening", new JObj().Add("struts", struts).Add("autostruts", autostruts).Add("rigid_attachments", rigid).Add("launch_clamps", clamps));

            // ---- the worst of the flight so far (kept for the craft being flown, from when it was last switched to)
            if (worst.Samples > 0 && watched == v.id)
            {
                var w = new JObj();
                w.Add("joint_percent_of_breaking", R(worst.Joint * 100, 1));
                if (worst.JointWhere != null) w.Add("joint", worst.JointWhere).Add("joint_at_met_s", R(worst.JointAt, 0));
                w.Add("bend_deg", R(worst.Bend, 2));
                if (worst.BendWhere != null) w.Add("bend_at", worst.BendWhere).Add("bend_at_met_s", R(worst.BendAt, 0));
                w.Add("out_of_place_m", R(worst.Sway, 3));
                w.Add("g_force", R(worst.G, 2)).Add("g_at_met_s", R(worst.GAt, 0));
                w.Add("pressure_kpa", R(worst.Pressure, 1)).Add("pressure_at_met_s", R(worst.PressureAt, 0));
                w.Add("heat_percent_of_limit", R(worst.Heat * 100, 0));
                if (worst.HeatWhere != null && worst.Heat > 0.5) w.Add("hottest", worst.HeatWhere).Add("heat_at_met_s", R(worst.HeatAt, 0));
                w.Add("measured_for_s", R(worst.Samples * 3 * Time.fixedDeltaTime, 0));
                o.Add("worst_so_far", w);
            }

            if (breaks.Count > 0 && watched == v.id) o.Add("broken_in_this_flight", new List<object>(breaks.ToArray()));

            // ---- what to make of it
            // (how bad: joints pulled a quarter of the way to their stop, or bent six degrees, is a craft about to come apart. A thin-necked
            // test craft that folded and destroyed itself read 7% and 4.5 degrees five seconds before, 41% and 9 degrees at the end; one that
            // flew well read 3% and half a degree at its worst)
            double high = Math.Max(worstShare, watched == v.id ? worst.Joint : 0);
            double bendHigh = Math.Max(worstBend, watched == v.id ? worst.Bend : 0);
            double bad = Math.Max(high / 0.25, bendHigh / 6.0);
            string verdict = bad >= 1 ? "coming apart" : bad >= 0.5 ? "heavily strained" : bad >= 0.25 ? "strained, with room to spare" : "sound";
            o.Add("verdict", verdict);
            var notes = new List<object>();
            if (joints && high >= 0.12) notes.Add("A joint has been pulled " + R(high * 100, 0) + "% of the way to where it breaks. Throttle down or turn more gently now, and do not use physics time warp (it softens every joint); in the next design put struts across that joint, use a wider decoupler or adapter there, or set autostrut on the heavy parts beyond it.");
            if (bendHigh >= 3) notes.Add("The craft has bent by " + R(bendHigh, 1) + " degrees at one joint. A rocket that bends like that steers badly (the engine no longer points through the centre of mass) and can fold up altogether: throttle down now, and stiffen that joint with struts or autostrut in the next design.");
            if (worstHeat >= 0.85 && !CheatOptions.IgnoreMaxTemperature) notes.Add("A part is at " + R(worstHeat * 100, 0) + "% of the temperature that destroys it.");
            if (gRule && leastGPart != null && v.geeForce / leastG >= 0.7) notes.Add("G-force is at " + R(v.geeForce / leastG * 100, 0) + "% of what '" + leastGPart + "' stands.");
            if (notes.Count > 0) o.Add("notes", notes);
            o.Add("how_to_read", "In this game a loaded joint gives like a stiff spring and breaks once it has been pulled out to a stop (1 m for most). percent_of_breaking is how far towards that stop the joint has been pulled, and bent_deg how far it is bent out of line. In ordinary flight a joint is pulled one to three percent and bent under a degree. A craft that is pulled several percent and bent several degrees is folding, and is usually destroyed by its own parts hitting one another long before any joint reaches its stop. Both change from moment to moment, so judge by worst_so_far.");
            return o;
        }

        static List<object> Top(List<KeyValuePair<double, JObj>> sorted, int limit)
        {
            var list = new List<object>();
            for (int n = 0; n < sorted.Count && n < limit; n++) list.Add(sorted[n].Value);
            return list;
        }

        // ---------------------------------------------------------------- in the editor: an estimate (StructureEstimate)

        /// <summary>
        /// The estimate in a few figures, for the reply to a build: whether the craft folds up under its own thrust in any stage, its
        /// hardest-pulled and most bendable joints, and what to do about them. (Nothing, if it cannot be worked out.)
        /// </summary>
        public static JObj Brief(ShipConstruct ship) => StructureEstimate.Brief(ship);
    }
}
