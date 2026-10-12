using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AIBridge.Pilot;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>Kerbals outside their ships: going out, walking, the jetpack, ladders, flags and getting back in.</summary>
    public static class EvaApi
    {
        static BridgeHost host;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Async("eva", "Extra-vehicular activity. params: {action: status|start|walk|fly|board|let_go|get_up|jump|jetpack|flag|stop, kerbal, part, lat, lon, target, heading, distance_m, radius_m, run, on, name, speed}", Eva);
        }

        static KerbalEVA Kerbal(Vessel v) => v != null && v.isEVA ? v.rootPart.FindModuleImplementing<KerbalEVA>() : null;

        static KerbalEVA RequireKerbal()
        {
            Vessel v = FlightApi.Active();
            KerbalEVA eva = Kerbal(v);
            if (eva == null) throw new ApiException("The vessel being flown is not a kerbal on EVA. Use action start to send one out, or switch_vessel to one already outside.");
            return eva;
        }

        static JObj Status(Vessel v)
        {
            var o = new JObj();
            KerbalEVA eva = Kerbal(v);
            if (eva == null)
            {
                o.Add("on_eva", false);
                var crew = new List<object>();
                foreach (ProtoCrewMember k in v.GetVesselCrew()) crew.Add(k.name + " (" + k.experienceTrait.Title + ")");
                o.Add("crew_aboard", crew);
                var outside = new List<object>();
                foreach (Vessel other in FlightGlobals.Vessels)
                    if (other != null && other.isEVA && other.loaded) outside.Add(new JObj().Add("name", other.GetDisplayName()).Add("id", other.persistentId).Add("distance_m", Math.Round((other.GetWorldPos3D() - v.GetWorldPos3D()).magnitude, 1)));
                if (outside.Count > 0) o.Add("kerbals_outside", outside);
                return o;
            }
            EvaControl ctl = EvaControl.For(eva);
            o.Add("on_eva", true);
            o.Add("kerbal", v.GetDisplayName());
            o.Add("doing", ctl.State);
            o.Add("body", v.mainBody.bodyName);
            o.Add("situation", v.situation.ToString());
            o.Add("lat", Math.Round(v.latitude, 6)).Add("lon", Math.Round(v.longitude, 6));
            if (v.LandedOrSplashed || v.radarAltitude < 5000) o.Add("facing", Math.Round(Facing(v, eva)));
            if (v.radarAltitude < 5000) o.Add("height_above_ground_m", Math.Round(v.radarAltitude, 1));
            if (v.LandedOrSplashed || v.radarAltitude < 5000) o.Add("speed", Math.Round(v.srfSpeed, 2));
            o.Add("on_ladder", eva.OnALadder);
            double power = eva.linPower / Math.Max(0.01, v.totalMass), gravity = Guidance.LocalGravity(v);
            var pack = new JObj().Add("fitted", eva.HasJetpack);
            if (eva.HasJetpack)
            {
                pack.Add("on", eva.JetpackDeployed).Add("propellant", Math.Round(eva.Fuel, 2) + "/" + Math.Round(eva.FuelCapacity, 2));
                if (v.LandedOrSplashed || v.radarAltitude < 2000) pack.Add("can_lift_here", power > gravity * 1.15);
            }
            o.Add("jetpack", pack);
            Part hatch = ctl.HatchInReach;
            if (hatch != null) o.Add("hatch_in_reach", hatch.partInfo.title + " of " + hatch.vessel.GetDisplayName());
            if (ctl.LadderInReach && !eva.OnALadder) o.Add("ladder_in_reach", true);

            var near = new List<object>();
            foreach (Vessel other in FlightGlobals.Vessels)
            {
                if (other == null || other == v || !other.loaded) continue;
                if (other.vesselType == VesselType.Debris || other.vesselType == VesselType.SpaceObject) continue;
                double d = (other.GetWorldPos3D() - v.GetWorldPos3D()).magnitude;
                if (d > 2500) continue;
                var n = new JObj().Add("name", other.GetDisplayName()).Add("id", other.persistentId).Add("type", other.vesselType.ToString().ToLowerInvariant()).Add("distance_m", Math.Round(d, 1));
                if (!v.LandedOrSplashed) n.Add("relative_speed", Math.Round((v.obt_velocity - other.obt_velocity).magnitude, 2));
                if (v.LandedOrSplashed && other.LandedOrSplashed) n.Add("bearing", Math.Round(SurfaceApi.Bearing(v.latitude, v.longitude, other.latitude, other.longitude)));
                Part seat = FreeSeat(other, eva.transform.position);
                if (seat != null && seat.airlock != null) n.Add("nearest_hatch_m", Math.Round((seat.airlock.position - eva.transform.position).magnitude, 1));
                else if (other.GetCrewCapacity() > 0) n.Add("seats", "full");
                near.Add(n);
            }
            if (near.Count > 0) o.Add("nearby", near);
            JObj ap = Autopilot.I.Status();
            if (Autopilot.I.Program != null) o.Add("program", Autopilot.I.Program.Name + ": " + Autopilot.I.Program.Phase);
            else if (ap["last_program"] != null) o.Add("last_program", ap["last_program"]);
            return o;
        }

#if DEV
        /// <summary>A kerbal turned on the spot to a compass bearing. The kerbal's own body is turned, and with it the way the
        /// kerbal last meant to go, which the game turns it back towards as soon as it moves.</summary>
        static void Turn(KerbalEVA eva, Vessel v, double degrees)
        {
            double heading = degrees * Math.PI / 180.0;
            Vector3d up = FlightGlobals.getUpAxis(v.mainBody, v.CoMD);
            Vector3d east = Vector3d.Cross(up, v.mainBody.transform.up).normalized, north = Vector3d.Cross(east, up);
            Vector3 ahead = (Vector3)(north * Math.Cos(heading) + east * Math.Sin(heading));
            Quaternion to = Quaternion.LookRotation(ahead, (Vector3)up);
            Rigidbody body = eva.part != null ? eva.part.rb : null;
            Unpin(eva);
            eva.transform.rotation = to;
            if (body != null) { body.rotation = to; body.angularVelocity = Vector3.zero; }
            FieldInfo meant = typeof(KerbalEVA).GetField("tgtFwd", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (meant != null) meant.SetValue(eva, ahead);
        }

        /// <summary>A kerbal that has stood still for a moment is pinned to the spot by the game (a fixed joint to the world, so
        /// that it does not creep down a slope), facing as it stands: moved or turned, it is pulled back. The pin is taken off;
        /// the game puts a new one on where the kerbal comes to stand. (The same pins are why every kerbal near by is found a
        /// metre or two off after the game's own set-position has been used on one of them: the world is shifted to keep the
        /// flown vessel near its middle, and the pins are not shifted with it.)</summary>
        static void Unpin(KerbalEVA eva)
        {
            BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo off = typeof(KerbalEVA).GetMethod("RemoveRBAnchor", hidden, null, Type.EmptyTypes, null);
            FieldInfo count = typeof(KerbalEVA).GetField("kerbalAnchorTimeCounter", hidden);
            try
            {
                if (off != null) off.Invoke(eva, null);
                if (count != null) count.SetValue(eva, 0f);
            }
            catch (Exception) { }
        }
#endif

        /// <summary>The compass bearing a kerbal faces (0 north, 90 east).</summary>
        static double Facing(Vessel v, KerbalEVA eva)
        {
            Vector3d up = FlightGlobals.getUpAxis(v.mainBody, v.CoMD);
            Vector3d east = Vector3d.Cross(up, v.mainBody.transform.up).normalized, north = Vector3d.Cross(east, up);
            Vector3d ahead = eva.transform.forward;
            double bearing = Math.Atan2(Vector3d.Dot(ahead, east), Vector3d.Dot(ahead, north)) * 180.0 / Math.PI;
            return bearing < 0 ? bearing + 360.0 : bearing;
        }

        /// <summary>The closest part of a vessel that has a hatch and an empty seat.</summary>
        static Part FreeSeat(Vessel ship, Vector3 from)
        {
            Part best = null;
            float bestDistance = float.MaxValue;
            foreach (Part p in ship.parts)
            {
                if (p.CrewCapacity <= 0 || p.protoModuleCrew.Count >= p.CrewCapacity || p.airlock == null) continue;
                float d = (p.airlock.position - from).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = p; }
            }
            return best;
        }

        static IEnumerator Eva(Call c)
        {
            Args a = c.Args;
            Vessel v = FlightApi.Active();
            string action = (a.Str("action") ?? "status").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            switch (action)
            {
                case "status":
                    c.Result = Status(v);
                    yield break;

                case "start": case "exit": case "go_out": case "go_eva":
                {
                    if (v.isEVA) throw new ApiException(v.GetDisplayName() + " is already outside.");
                    if (FlightEVA.fetch == null) throw new ApiException("EVA is not available right now.");
                    if (HighLogic.CurrentGame != null && !HighLogic.CurrentGame.Parameters.Flight.CanEVA) throw new ApiException("This save does not allow EVA.");
                    string wanted = a.Str("kerbal");
                    ProtoCrewMember who = null;
                    Part from = null;
                    foreach (Part p in v.parts)
                    {
                        foreach (ProtoCrewMember k in p.protoModuleCrew)
                        {
                            if (!string.IsNullOrEmpty(wanted) && k.name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (who == null || (from != null && from.airlock == null && p.airlock != null)) { who = k; from = p; }
                        }
                    }
                    if (who == null) throw new ApiException(string.IsNullOrEmpty(wanted) ? "Nobody is aboard this vessel." : "No kerbal named '" + wanted + "' is aboard.");
                    if (from.airlock == null) throw new ApiException(who.name + " is in " + from.partInfo.title + ", which has no hatch.");
                    Autopilot.I.Release("a kerbal is going outside", false);
                    host.Note(who.name + " is going on EVA");
                    string ship = v.GetDisplayName();
                    KerbalEVA spawned = FlightEVA.fetch.spawnEVA(who, from, from.airlock, true);
                    if (spawned == null) throw new ApiException("The hatch is blocked, so " + who.name + " cannot get out.");
                    float deadline = Time.realtimeSinceStartup + 8f;
                    while (Time.realtimeSinceStartup < deadline && (FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.isEVA || FlightGlobals.ActiveVessel.packed)) yield return null;
                    yield return new WaitSec(1.0f);
                    Vessel now = FlightGlobals.ActiveVessel;
                    JObj status = Status(now);
                    status.Add("left", ship);
                    c.Result = status;
                    yield break;
                }

                case "stop":
                    Autopilot.I.Release("EVA movement stopped", false);
                    EvaControl.ReleaseAll();
                    c.Result = Status(v);
                    yield break;
            }

            KerbalEVA eva = RequireKerbal();
            EvaControl ctl = EvaControl.For(eva);
            switch (action)
            {
                case "let_go": case "letgo": case "release_ladder":
                    if (!eva.OnALadder) throw new ApiException("The kerbal is not holding a ladder (" + ctl.State + ").");
                    Autopilot.I.Release("letting go of the ladder", false);
                    eva.fsm.RunEvent(eva.On_ladderLetGo);
                    yield return new WaitSec(0.8f);
                    break;

                case "get_up": case "stand": case "recover":
                {
                    if (!eva.isRagdoll && !ctl.In(eva.st_ragdoll)) break;
                    // The game stands a fallen kerbal up only on a movement key; press one for a moment.
                    float until = Time.realtimeSinceStartup + 6f;
                    while (Time.realtimeSinceStartup < until && (eva.isRagdoll || ctl.In(eva.st_ragdoll, eva.st_recover)))
                    {
                        ctl.WalkTarget = (Vector3d)eva.transform.position + (Vector3d)eva.transform.forward * 3.0;
                        yield return null;
                    }
                    ctl.Stop();
                    yield return new WaitSec(0.5f);
                    break;
                }

                case "jump":
                    if (!ctl.In(eva.st_idle_gr, eva.st_idle_b_gr, eva.st_walk_acd, eva.st_walk_fps, eva.st_run_acd, eva.st_run_fps, eva.st_bound_gr_acd, eva.st_bound_gr_fps))
                        throw new ApiException("The kerbal can only jump while standing or walking on the ground (" + ctl.State + ").");
                    eva.fsm.RunEvent(eva.On_jump_start);
                    yield return new WaitSec(0.8f);
                    break;

                case "jetpack": case "pack":
                {
                    if (!eva.HasJetpack) throw new ApiException("This kerbal has no jetpack.");
                    bool on = a.Bool("on", !eva.JetpackDeployed);
                    if (on != eva.JetpackDeployed)
                    {
                        if (!ctl.In(eva.st_idle_gr, eva.st_idle_b_gr, eva.st_idle_fl)) throw new ApiException("The jetpack can only be switched while standing still or floating free (" + ctl.State + ").");
                        eva.ToggleJetpack();
                        yield return new WaitSec(1.0f);
                    }
                    break;
                }

                case "walk": case "move": case "run": case "go":
                {
                    if (!v.LandedOrSplashed && !ctl.OnGroundAndReady) throw new ApiException("The kerbal is not on the ground (" + ctl.State + "). In space use action fly.");
                    if (eva.OnALadder) throw new ApiException("The kerbal is holding a ladder; use action let_go first.");
                    SurfaceApi.Spot spot = ResolveSpot(a, v);
                    var p = new EvaProgram { Mode = EvaProgram.Kind.Walk, Eva = eva, Lat = spot.Lat, Lon = spot.Lon, Label = spot.Label, Radius = OrbitMath.Clamp(a.Num("radius_m", a.Has("target") ? 4 : 1.5), 0.5, 200), Run = a.Bool("run", action != "walk") };
                    Autopilot.I.Start(p);
                    JObj s = Status(v);
                    s.Add("distance_m", Math.Round(SurfaceApi.GroundDistance(v.mainBody, v.latitude, v.longitude, spot.Lat, spot.Lon), 1));
                    c.Result = s;
                    yield break;
                }

                case "fly": case "jet":
                {
                    Vessel ship = ResolveShip(a, v, false);
                    Part goal = PickPart(ship, a.Str("part"), eva.transform.position);
                    var p = new EvaProgram { Mode = EvaProgram.Kind.Fly, Eva = eva, Goal = goal, Label = (goal.airlock != null ? "the hatch of " : "") + ship.GetDisplayName(), Radius = OrbitMath.Clamp(a.Num("radius_m", 1.0), 0.3, 50),
                                             StandOff = OrbitMath.Clamp(a.Num("distance_m", 1.5), 0.3, 200), MaxSpeed = OrbitMath.Clamp(a.Num("speed", 1.5), 0.2, 6) };
                    Autopilot.I.Start(p);
                    c.Result = Status(v);
                    yield break;
                }

                case "board": case "enter": case "get_in":
                {
                    Vessel ship = ResolveShip(a, v, true);
                    Part seat = FreeSeat(ship, eva.transform.position);
                    if (seat == null) throw new ApiException("'" + ship.GetDisplayName() + "' has no free seat behind a hatch.");
                    var p = new EvaProgram { Mode = EvaProgram.Kind.Board, Eva = eva, Goal = seat, Label = ship.GetDisplayName(), MaxSpeed = OrbitMath.Clamp(a.Num("speed", 1.5), 0.2, 6) };
                    Autopilot.I.Start(p);
                    c.Result = Status(v);
                    yield break;
                }

                case "flag": case "plant_flag":
                {
                    if (!ctl.In(eva.st_idle_gr, eva.st_idle_b_gr)) throw new ApiException("A flag can only be planted while standing still on the ground (" + ctl.State + ").");
                    if (eva.flagItems <= 0) throw new ApiException("This kerbal has no flag left to plant.");
                    Autopilot.I.Release("planting a flag", false);
                    int flagsBefore = CountFlags();
                    eva.PlantFlag();
                    host.Note(v.GetDisplayName() + " is planting a flag");
                    // The game asks for a name for the site once the flag is in; fill it in and confirm.
                    float deadline = Time.realtimeSinceStartup + 25f;
                    string siteName = a.Str("name"), plaque = a.Str("plaque");
                    bool named = false;
                    while (Time.realtimeSinceStartup < deadline && !named)
                    {
                        yield return new WaitSec(0.5f);
                        if (CountFlags() <= flagsBefore || DialogApi.Titles().Count == 0) continue;
                        yield return new WaitSec(0.5f);
                        DialogApi.FillText(null, new List<string> { string.IsNullOrEmpty(siteName) ? null : siteName, string.IsNullOrEmpty(plaque) ? null : plaque });
                        yield return null;
                        try { DialogApi.Press(null, "OK"); named = true; } catch (ApiException) { }
                    }
                    yield return new WaitSec(1.0f);
                    JObj s = Status(FlightGlobals.ActiveVessel);
                    s.Add("flag_planted", CountFlags() > flagsBefore);
                    string open = DialogApi.Summary();
                    if (open.Length > 0) s.Add("dialog_open", open);
                    c.Result = s;
                    yield break;
                }

#if DEV
                // (Development build, for setting kerbals out for a picture: the helmet and its ring off or on, as the kerbal's own
                // menu does it; the kerbal turned on the spot to a compass heading; the kerbal moved a few metres as it stands.)
                case "helmet":
                {
                    bool on = a.Bool("on", false);
                    eva.ToggleHelmetAndNeckRing(on, on);
                    yield return new WaitSec(0.3f);
                    break;
                }

                case "face":
                    Turn(eva, v, a.RequireNum("heading"));
                    yield return new WaitSec(0.3f);
                    break;

                // (The kerbal moved, as it stands, to a place a few metres off, and turned. The game's own way of putting a vessel
                // somewhere (dev_set_position) is no way to set kerbals out side by side: a moment after it has been used on one,
                // every other kerbal near by is a metre or two from where it stood. See Unpin.)
                case "put":
                {
                    Vector3d up = FlightGlobals.getUpAxis(v.mainBody, v.CoMD);
                    Vector3d east = Vector3d.Cross(up, v.mainBody.transform.up).normalized, north = Vector3d.Cross(east, up);
                    double metres = Math.PI / 180.0 * v.mainBody.Radius, across = a.RequireNum("lon") - v.longitude;
                    across -= 360.0 * Math.Round(across / 360.0);
                    Vector3d by = north * ((a.RequireNum("lat") - v.latitude) * metres) + east * (across * metres * Math.Cos(v.latitude * Math.PI / 180.0));
                    if (by.magnitude > 60) throw new ApiException("That is " + Math.Round(by.magnitude) + " m off: put moves a kerbal a few metres. Use dev_set_position to get near first.");
                    Rigidbody body = eva.part != null ? eva.part.rb : null;
                    Vector3 to = eva.transform.position + (Vector3)by;
                    Unpin(eva);
                    eva.transform.position = to;
                    if (body != null) { body.position = to; body.velocity = Vector3.zero; }
                    if (a.Has("heading")) Turn(eva, v, a.Num("heading"));
                    yield return new WaitSec(0.5f);
                    break;
                }
#endif

                default:
                    throw new ApiException("action must be one of: status, start, walk, fly, board, let_go, get_up, jump, jetpack, flag, stop.");
            }
            c.Result = Status(FlightGlobals.ActiveVessel);
        }

        static int CountFlags()
        {
            int n = 0;
            foreach (Vessel v in FlightGlobals.Vessels) if (v != null && v.vesselType == VesselType.Flag) n++;
            return n;
        }

        static SurfaceApi.Spot ResolveSpot(Args a, Vessel v)
        {
            if (a.Has("target"))
            {
                Vessel other = NavApi.FindVessel(a.Str("target"));
                if (other == null) throw new ApiException("No vessel named '" + a.Str("target") + "'.");
                if (other.mainBody != v.mainBody) throw new ApiException("'" + other.GetDisplayName() + "' is not on " + v.mainBody.bodyName + ".");
                return new SurfaceApi.Spot { Lat = other.latitude, Lon = SurfaceApi.NormLon(other.longitude), Label = other.GetDisplayName() };
            }
            return SurfaceApi.Resolve(a, v);
        }

        static Vessel ResolveShip(Args a, Vessel v, bool needsSeat)
        {
            string name = a.Str("target");
            if (!string.IsNullOrEmpty(name))
            {
                Vessel named = NavApi.FindVessel(name);
                if (named == null) throw new ApiException("No vessel named '" + name + "'.");
                if (named == v) throw new ApiException("That is the kerbal.");
                if (!named.loaded) throw new ApiException("'" + named.GetDisplayName() + "' is too far away.");
                return named;
            }
            Vessel best = null;
            double bestDistance = double.MaxValue;
            foreach (Vessel other in FlightGlobals.Vessels)
            {
                if (other == null || other == v || !other.loaded || other.isEVA || other.vesselType == VesselType.Flag || other.vesselType == VesselType.Debris) continue;
                if (needsSeat && FreeSeat(other, v.rootPart.transform.position) == null) continue;
                double d = (other.GetWorldPos3D() - v.GetWorldPos3D()).magnitude;
                if (d < bestDistance) { bestDistance = d; best = other; }
            }
            if (best == null) throw new ApiException(needsSeat ? "No vessel with a free seat is nearby." : "No other vessel is nearby; give target.");
            return best;
        }

        static Part PickPart(Vessel ship, string selector, Vector3 from)
        {
            if (!string.IsNullOrEmpty(selector))
            {
                List<Part> found = PartApi.Select(ship.parts, selector);
                return found[0];
            }
            Part seat = FreeSeat(ship, from);
            return seat ?? ship.rootPart;
        }
    }
}
