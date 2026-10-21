using System;
using System.Collections;
using System.Collections.Generic;
using AIBridge.Pilot;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>Navigation: autopilot programs, maneuver nodes, time warp, waiting, bodies, targets and vessel switching.</summary>
    public static class NavApi
    {
        static BridgeHost host;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("ascent", "Fly from the ground to a circular orbit. params: {altitude_km, inclination, turn_start_speed, turn_end_altitude_km, turn_shape, max_q_kpa, max_aoa, warp}", Ascent);
            Rpc.Sync("execute_node", "Fly the next maneuver node. params: {warp, autostage, tolerance}", ExecuteNode);
            Rpc.Sync("land", "Land: powered descent on airless bodies (optionally at a chosen spot), heat shield and parachutes (with engine assist) where there is air. params: {lat, lon | target | place | heading, distance_m, offset_m, clearance_m, touchdown_speed, safety_factor, warp, autostage, periapsis_km, stage, chutes, engines, deorbit}", Land);
            Rpc.Sync("abort_program", "Stop the running autopilot program and cut the throttle.", a => { Autopilot.I.Release("aborted by the AI", true); return Autopilot.I.Status(); });

            Rpc.Sync("nodes", "List maneuver nodes and the trajectory each one leads to.", a => Nodes(FlightApi.Active())).AlwaysAllowed = true;
            Rpc.Sync("add_node", "Create a maneuver node. params: {at: apoapsis|periapsis, in_seconds, ut, prograde, normal, radial}", AddNode);
            Rpc.Sync("edit_node", "Change a maneuver node. params: {index, prograde, normal, radial, ut, shift_seconds, add_prograde, add_normal, add_radial}", EditNode);
            Rpc.Sync("remove_nodes", "Delete maneuver nodes. params: {index} (omit for all)", RemoveNodes);

            Rpc.Async("wait", "Block until something happens. params: {timeout_s, until: {...}, stop_on_event}", Wait).AlwaysAllowed = true;
            Rpc.Async("warp", "Time warp. params: {to: apoapsis|periapsis|node|soi, seconds, ut, lead_s, rate, timeout_s}", WarpCmd);
            Rpc.Async("set_time", "Set the game's clock straight to a later time, without a warp: craft are held on rails for the moment it takes, as a warp holds them (landed ones stay where they stand, orbiting ones go on along their orbits). Forward only. params: {ut, seconds or hours (on from now), sun: degrees above the horizon at the craft (at the space centre outside flight), rising: true (morning, as given) or false (evening)}. On a world whose day is long (Venus's 117 days) the sun reached is a few degrees off the one asked for; the result's sun_deg says where it stands.", SetTimeCmd);

            Rpc.Sync("bodies", "Planets and moons with the numbers needed for planning. params: {name}", Bodies).AlwaysAllowed = true;
            Rpc.Sync("vessels", "All vessels in the save. params: {include_debris}", Vessels).AlwaysAllowed = true;
            Rpc.Sync("set_target", "Set or clear the navigation target. params: {name: body, vessel name or vessel id} ('none' clears)", SetTarget);
            Rpc.Async("switch_vessel", "Take control of another vessel. params: {name: vessel name or id from vessels}", SwitchVessel);
        }

        static double R(double v, int digits = 1) => double.IsNaN(v) || double.IsInfinity(v) ? double.NaN : Math.Round(v, digits);

        // ---------------------------------------------------------------- programs

        static object Ascent(Args a)
        {
            Vessel v = FlightApi.Active();
            if (!v.IsControllable) throw new ApiException("The vessel is not controllable (no crew, no powered probe core, or no signal).");
            CelestialBody body = v.mainBody;
            double floor = body.atmosphere ? body.atmosphereDepth : 5000;
            var p = new AscentProgram();
            p.TargetAltitude = a.Has("altitude_km") ? a.Num("altitude_km") * 1000.0 : a.Num("altitude_m", body.atmosphere ? body.atmosphereDepth + 10000 : Math.Max(15000, body.Radius * 0.05));
            if (p.TargetAltitude < floor + 1000) throw new ApiException("Target altitude must be above " + Math.Round((floor + 1000) / 1000.0) + " km for " + Places.Name(body) + ".");
            if (p.TargetAltitude > body.sphereOfInfluence - body.Radius) throw new ApiException("Target altitude is outside " + Places.Name(body) + "'s sphere of influence.");
            p.Inclination = a.Num("inclination", 0);
            if (a.Has("turn_start_speed")) p.TurnStartSpeed = Math.Max(5, a.Num("turn_start_speed"));
            if (a.Has("turn_end_altitude_km")) p.TurnEndAltitude = a.Num("turn_end_altitude_km") * 1000.0;
            if (a.Has("turn_shape")) p.TurnShape = OrbitMath.Clamp(a.Num("turn_shape"), 0.2, 1.0);
            if (a.Has("max_q_kpa")) p.MaxQ = a.Num("max_q_kpa");
            if (a.Has("max_aoa")) p.MaxAoA = OrbitMath.Clamp(a.Num("max_aoa"), 1, 30);
            p.UseWarp = a.Bool("warp", true);
            // Asked again for the ascent that is already being flown: carry on rather than start over.
            // (A model that is unsure whether its command took tends to repeat it, and every restart
            // throws away the turn that was in progress.)
            if (Autopilot.I.Program is AscentProgram flying && !flying.Done && Math.Abs(flying.TargetAltitude - p.TargetAltitude) < 500 && Math.Abs(flying.Inclination - p.Inclination) < 0.5)
            {
                JObj same = Autopilot.I.Status();
                same.Add("note", "This ascent is already under way and was left running. Wait for it with wait until program_done.");
                return same;
            }
            Autopilot.I.Start(p);
            return Autopilot.I.Status();
        }

        static object ExecuteNode(Args a)
        {
            Vessel v = FlightApi.Active();
            if (v.patchedConicSolver == null || v.patchedConicSolver.maneuverNodes.Count == 0) throw new ApiException("There is no maneuver node. Create one with plan or add_node.");
            if (!v.IsControllable) throw new ApiException("The vessel is not controllable.");
            var p = new NodeProgram { UseWarp = a.Bool("warp", true), AutoStage = a.Bool("autostage", true), Tolerance = OrbitMath.Clamp(a.Num("tolerance", 0.02), 0.005, 5) };
            Autopilot.I.Start(p);
            return Autopilot.I.Status();
        }

        static object Land(Args a)
        {
            Vessel v = FlightApi.Active();
            if (v.LandedOrSplashed) throw new ApiException("The vessel is already on the surface.");
            CelestialBody body = v.mainBody;
            if (!body.hasSolidSurface) throw new ApiException(Places.Name(body) + " has no surface to land on.");
            bool warp = a.Bool("warp", true), autostage = a.Bool("autostage", true);
            return body.atmosphere && v.altitude < body.sphereOfInfluence ? LandInAtmosphere(a, v, warp) : LandOnAirless(a, v, warp, autostage);
        }

        static ManeuverNode ReplaceNodes(Vessel v, double ut, Vector3d worldDv)
        {
            PatchedConicSolver solver = Solver(v);
            while (solver.maneuverNodes.Count > 0) solver.maneuverNodes[0].RemoveSelf();
            return Create(v, ut, OrbitMath.ToNode(v.orbit, ut, worldDv));
        }

        static double DvAvailable(Vessel v)
        {
            VesselDeltaV dv = v.VesselDeltaV;
            return dv != null && dv.IsReady ? dv.TotalDeltaVActual : double.NaN;
        }

        static object LandOnAirless(Args a, Vessel v, bool warp, bool autostage)
        {
            if (!v.IsControllable) throw new ApiException("The vessel is not controllable.");
            CelestialBody body = v.mainBody;
            var p = new LandProgram
            {
                TouchdownSpeed = OrbitMath.Clamp(a.Num("touchdown_speed", 1.5), 0.5, 6),
                SafetyFactor = OrbitMath.Clamp(a.Num("safety_factor", 1.2), 1.0, 3.0),
                UseWarp = warp,
                AutoStage = autostage
            };
            JObj plan = null;
            if (SurfaceApi.HasSpot(a))
            {
                SurfaceApi.Spot spot = SurfaceApi.Resolve(a, v);
                if (a.Has("target"))
                {
                    // Next to the other vessel, not on top of it.
                    SurfaceApi.Spot beside = SurfaceApi.Offset(body, spot.Lat, spot.Lon, a.Num("offset_heading", 90), OrbitMath.Clamp(a.Num("offset_m", 80), 20, 5000));
                    spot.Lat = beside.Lat;
                    spot.Lon = beside.Lon;
                    spot.Label = "a spot beside " + spot.Label;
                }
                p.HasTarget = true;
                p.TargetLat = spot.Lat;
                p.TargetLon = spot.Lon;
                p.TargetLabel = spot.Label;
                double terrain = Math.Max(0, body.TerrainAltitude(spot.Lat, spot.Lon, true));
                bool inOrbit = v.orbit.eccentricity < 1.0 && v.orbit.PeA > terrain + 4000;
                if (inOrbit && a.Bool("deorbit", true))
                {
                    LandingPlanner.Deorbit d = LandingPlanner.Plan(v, spot.Lat, spot.Lon, OrbitMath.Clamp(a.Num("clearance_m", 2500), 800, 20000));
                    ManeuverNode node = ReplaceNodes(v, d.UT, d.WorldDv);
                    p.Prelude = new NodeProgram { UseWarp = warp, AutoStage = autostage, Tolerance = 0.05 };
                    p.PlannedArrivalUT = d.ArrivalUT;
                    double now = Planetarium.GetUniversalTime();
                    plan = new JObj().Add("deorbit_burn_dv", R(node.DeltaV.magnitude, 1)).Add("burn_in_s", R(d.UT - now, 0)).Add("arrives_over_target_in_s", R(d.ArrivalUT - now, 0))
                        .Add("approach_altitude_m", R(d.PeriapsisAltitude, 0)).Add("target_terrain_altitude_m", R(d.TerrainAltitude, 0)).Add("predicted_track_miss_m", R(d.MissDistance, 0));
                    double needed = node.DeltaV.magnitude + OrbitMath.CircularSpeed(body, body.Radius + d.PeriapsisAltitude) * 1.15 + Math.Sqrt(2 * body.GeeASL * 9.80665 * Math.Max(0, d.PeriapsisAltitude - d.TerrainAltitude));
                    double have = DvAvailable(v);
                    plan.Add("dv_needed_estimate", R(needed, 0));
                    if (!double.IsNaN(have)) { plan.Add("dv_available", R(have, 0)); if (have < needed) plan.Add("warning", "This vessel probably does not have the delta-v for the landing."); }
                }
                else if (!inOrbit) plan = new JObj().Add("note", "Already on the way down: steering for the target with the braking burn only.");
            }
            Autopilot.I.Start(p);
            JObj status = Autopilot.I.Status();
            if (plan != null) status.Add("plan", plan);
            return status;
        }

        static bool HasChutes(Vessel v)
        {
            foreach (Part part in v.parts) if (part.FindModuleImplementing<ModuleParachute>() != null) return true;
            return false;
        }

        static bool HasWheelsOrLegs(Vessel v)
        {
            foreach (Part part in v.parts) if (part.FindModuleImplementing<ModuleWheelBase>() != null) return true;
            return false;
        }

        static object LandInAtmosphere(Args a, Vessel v, bool warp)
        {
            CelestialBody body = v.mainBody;
            var p = new ReentryProgram
            {
                TouchdownSpeed = OrbitMath.Clamp(a.Num("touchdown_speed", 2.0), 0.5, 8),
                UseWarp = warp,
                UseChutes = a.Bool("chutes", true),
                AllowEngines = a.Bool("engines", true)
            };
            if (a.Has("stage")) p.StageBeforeEntry = a.Bool("stage");
            JObj plan = new JObj();
            bool above = v.altitude > body.atmosphereDepth;
            if (above && v.orbit.PeA > body.atmosphereDepth * 0.6 && a.Bool("deorbit", true))
            {
                if (!v.IsControllable) throw new ApiException("The vessel is not controllable, so it cannot fly the de-orbit burn.");
                bool aiming = SurfaceApi.HasSpot(a);
                // An aimed entry comes in steeper: less time in thin air means less for drag errors to add up.
                double periapsis = a.Has("periapsis_km") ? a.Num("periapsis_km") * 1000.0 : body.atmosphereDepth * (aiming ? 0.30 : 0.42);
                if (periapsis > body.atmosphereDepth * 0.8) throw new ApiException("periapsis_km must be well inside the atmosphere (below " + R(body.atmosphereDepth * 0.8 / 1000) + " km) or the vessel will skip back out.");
                double now = Planetarium.GetUniversalTime();
                double ut = a.Has("ut") ? a.Num("ut") : now + Math.Max(30, a.Num("in_seconds", 60));
                Vector3d dv = OrbitMath.ChangeApsisDv(v.orbit, ut, body.Radius + periapsis);
                if (aiming)
                {
                    SurfaceApi.Spot spot = SurfaceApi.Resolve(a, v);
                    bool drop = p.StageBeforeEntry ?? (HasChutes(v) && !HasWheelsOrLegs(v));
                    EntryModel model = EntryModel.For(v, drop);
                    EntryPredictor.Plan aim = EntryPredictor.Aim(v, model, spot.Lat, spot.Lon, periapsis);
                    if (aim == null) throw new ApiException("Could not work out an entry that reaches " + spot.Label + " from this orbit. Try a different periapsis_km, or land without a target.");
                    // Still far too fast near the ground for parachutes to open: the air is too thin to do the
                    // braking, so the engine must. Then it can also do the aiming, and far better than drag can
                    // be predicted: come in toward a point above the spot and brake to a stop there.
                    double arriving = aim.ArrivalSpeed;
                    if (p.AllowEngines && arriving > 260 && ReentryProgram.HasUsableEngine(v) && DvAvailable(v) > arriving * 1.25 + 150)
                    {
                        double arriveHeight = OrbitMath.Clamp(a.Num("clearance_m", HasChutes(v) && p.UseChutes ? 2500 : 900), 300, 6000);
                        // While it brakes the vessel falls below the path it would have coasted along (in the
                        // Duna test by 1.8 km), so point the coasting path that much higher.
                        double burnTime = arriving * 1.4 / Math.Max(1.0, 0.8 * ReentryProgram.PotentialThrustAccel(v));
                        double sag = OrbitMath.Clamp(0.5 * body.GeeASL * 9.80665 * burnTime * burnTime, 0, 4000);
                        EntryPredictor.Plan high = EntryPredictor.Aim(v, model, spot.Lat, spot.Lon, periapsis, arriveHeight + sag);
                        if (high != null)
                        {
                            aim = high;
                            p.PoweredAim = true;
                            p.ArriveHeight = arriveHeight;
                            plan.Add("engine_braking", "The air is too thin to slow this vessel (it would still be doing " + R(arriving, 0) + " m/s near the ground), so the engine brakes it to a stop " +
                                     R(arriveHeight, 0) + " m above the spot and it comes straight down from there" + (HasChutes(v) && p.UseChutes ? " under parachutes and thrust." : "."));
                        }
                    }
                    ut = aim.UT;
                    dv = aim.WorldDv;
                    p.HasTarget = true;
                    p.TargetLat = spot.Lat;
                    p.TargetLon = spot.Lon;
                    p.TargetLabel = spot.Label;
                    p.PredictedMiss = aim.MissDistance;
                    plan.Add("aiming_at", spot.Label).Add("predicted_miss_m", R(aim.MissDistance, 0)).Add("lands_in_s", R(aim.LandUT - now, 0)).Add("predicted_peak_g", R(aim.PeakG, 1))
                        .Add("plane_change_deg", R(aim.Tilt, 2)).Add("mass_at_entry_t", R(model.Mass, 3)).Add("parts_at_entry", model.Parts).Add("drag_area_mach3", R(model.Area(3), 2))
                        .Add("note", p.PoweredAim ? "Braking on the engine closes in on the spot itself, as on an airless world: expect tens of metres."
                             : "The landing point is predicted with the game's own drag figures for a descent without engine thrust: allow a kilometre or two. Parachutes are held until 3.5 km up so they do not drag it short.");
                }
                ManeuverNode node = ReplaceNodes(v, ut, dv);
                p.Prelude = new NodeProgram { UseWarp = warp, AutoStage = a.Bool("autostage", true), Tolerance = aiming ? 0.02 : 0.1 };
                plan.Add("deorbit_burn_dv", R(node.DeltaV.magnitude, 1)).Add("burn_in_s", R(ut - now, 0)).Add("entry_periapsis_m", R(periapsis, 0));
                double have = DvAvailable(v);
                if (!double.IsNaN(have) && have > 1 && have < node.DeltaV.magnitude) plan.Add("warning", "Only " + R(have, 0) + " m/s of delta-v is available; the burn will fall short.");
            }
            else if (above && v.orbit.PeA > body.atmosphereDepth)
                throw new ApiException("The periapsis (" + R(v.orbit.PeA / 1000) + " km) is above the atmosphere (" + R(body.atmosphereDepth / 1000) + " km). Lower it first, or allow the de-orbit burn.");
            if (SurfaceApi.HasSpot(a) && !p.HasTarget)
                plan.Add("note", "Not aiming: a spot can only be aimed for from orbit, before the de-orbit burn. This lands under the current track.");
            Autopilot.I.Start(p);
            JObj status = Autopilot.I.Status();
            if (plan.Count > 0) status.Add("plan", plan);
            return status;
        }

        // ---------------------------------------------------------------- maneuver nodes

        static PatchedConicSolver Solver(Vessel v)
        {
            if (v.patchedConicSolver == null)
                throw new ApiException("Maneuver nodes are not available for this vessel (in career mode they need an upgraded Tracking Station and Mission Control).");
            return v.patchedConicSolver;
        }

        static JObj NodeInfo(Vessel v, ManeuverNode node, int index)
        {
            double ut = Planetarium.GetUniversalTime();
            var o = new JObj();
            o.Add("index", index);
            o.Add("ut", R(node.UT, 1));
            o.Add("in_s", R(node.UT - ut, 1));
            o.Add("dv", R(node.DeltaV.magnitude, 1));
            o.Add("prograde", R(node.DeltaV.z, 2));
            o.Add("normal", R(node.DeltaV.y, 2));
            o.Add("radial", R(node.DeltaV.x, 2));
            if (index == 0)
            {
                double burn = Guidance.BurnTime(v, node.GetBurnVector(v.orbit).magnitude);
                if (!double.IsNaN(burn)) o.Add("burn_s", R(burn, 1));
            }
            if (node.nextPatch != null)
            {
                o.Add("result", FlightApi.OrbitInfo(node.nextPatch, true));
                JObj approach = TargetApproach(v, node.nextPatch, node.UT);
                if (approach != null) o.Add("target_approach", approach);
            }
            return o;
        }

        public static object Nodes(Vessel v)
        {
            var list = new List<object>();
            PatchedConicSolver solver = v.patchedConicSolver;
            if (solver == null) return list;
            solver.UpdateFlightPlan();
            for (int n = 0; n < solver.maneuverNodes.Count; n++) list.Add(NodeInfo(v, solver.maneuverNodes[n], n));
            return list;
        }

        /// <summary>Closest pass of a planned orbit to the current target, when both circle the same body.</summary>
        static JObj TargetApproach(Vessel v, Orbit planned, double fromUT)
        {
            ITargetable target = v.targetObject;
            if (target == null) return null;
            Orbit targetOrbit = target.GetOrbit();
            if (targetOrbit == null) return null;

            // An encounter with a target body shows up as a later patch around that body.
            if (target is CelestialBody body)
            {
                for (Orbit p = planned; p != null && p.activePatch; p = p.nextPatch)
                {
                    if (p.referenceBody == body)
                        return new JObj().Add("encounter", Places.Name(body)).Add("periapsis_m", R(p.PeA, 0)).Add("arrives_in_s", R(p.StartUT - Planetarium.GetUniversalTime(), 0)).Add("inclination_deg", R(p.inclination, 1));
                    if (p.patchEndTransition == Orbit.PatchTransitionType.FINAL) break;
                }
            }
            if (targetOrbit.referenceBody != planned.referenceBody) return null;
            double span = planned.eccentricity < 1.0 ? planned.period : 6.0 * 3600.0;
            if (planned.patchEndTransition != Orbit.PatchTransitionType.FINAL) span = Math.Min(span, planned.EndUT - fromUT);
            if (span <= 0 || double.IsNaN(span) || double.IsInfinity(span)) return null;
            double best = OrbitMath.ClosestApproach(planned, targetOrbit, fromUT, span, out double bestUT);
            double relSpeed = (planned.getOrbitalVelocityAtUT(bestUT) - targetOrbit.getOrbitalVelocityAtUT(bestUT)).magnitude;
            var o = new JObj().Add("target", target.GetName()).Add("closest_distance_m", R(best, 0)).Add("in_s", R(bestUT - Planetarium.GetUniversalTime(), 0)).Add("relative_speed", R(relSpeed, 1));
            if (target is CelestialBody tb) o.Add("note", best < tb.sphereOfInfluence ? "passes inside its sphere of influence" : "misses its sphere of influence (" + R(tb.sphereOfInfluence, 0) + " m)");
            return o;
        }

        static double ResolveUT(Vessel v, Args a, Orbit orbit)
        {
            double now = Planetarium.GetUniversalTime();
            if (a.Has("ut")) return a.Num("ut");
            if (a.Has("in_seconds")) return now + a.Num("in_seconds");
            string at = (a.Str("at") ?? "").ToLowerInvariant();
            switch (at)
            {
                case "apoapsis": case "ap":
                    if (orbit.eccentricity >= 1.0) throw new ApiException("This orbit is an escape trajectory and has no apoapsis.");
                    return OrbitMath.NextApoapsisUT(orbit, now);
                case "periapsis": case "pe":
                    return OrbitMath.NextPeriapsisUT(orbit, now);
                case "now": case "":
                    return now + (at == "now" ? 20 : double.NaN);
            }
            throw new ApiException("'at' must be apoapsis, periapsis or now (or give in_seconds / ut).");
        }

        static ManeuverNode Create(Vessel v, double ut, Vector3d nodeDv)
        {
            PatchedConicSolver solver = Solver(v);
            if (double.IsNaN(ut)) throw new ApiException("Say when the node happens: at (apoapsis|periapsis|now), in_seconds or ut.");
            if (ut < Planetarium.GetUniversalTime() - 1) throw new ApiException("The node time is in the past.");
            ManeuverNode node = solver.AddManeuverNode(ut);
            node.DeltaV = nodeDv;
            solver.UpdateFlightPlan();
            return node;
        }

        static object AddNode(Args a)
        {
            Vessel v = FlightApi.Active();
            double ut = ResolveUT(v, a, v.orbit);
            ManeuverNode node = Create(v, ut, new Vector3d(a.Num("radial", 0), a.Num("normal", 0), a.Num("prograde", 0)));
            host.Note("added maneuver node, " + Math.Round(node.DeltaV.magnitude, 1) + " m/s");
            return Nodes(v);
        }

        static object EditNode(Args a)
        {
            Vessel v = FlightApi.Active();
            PatchedConicSolver solver = Solver(v);
            int index = a.Int("index", 0);
            if (index < 0 || index >= solver.maneuverNodes.Count) throw new ApiException("No maneuver node with index " + index + ".");
            ManeuverNode node = solver.maneuverNodes[index];
            Vector3d dv = node.DeltaV;
            if (a.Has("prograde")) dv.z = a.Num("prograde");
            if (a.Has("normal")) dv.y = a.Num("normal");
            if (a.Has("radial")) dv.x = a.Num("radial");
            dv.z += a.Num("add_prograde", 0);
            dv.y += a.Num("add_normal", 0);
            dv.x += a.Num("add_radial", 0);
            node.DeltaV = dv;
            if (a.Has("ut")) node.UT = a.Num("ut");
            node.UT += a.Num("shift_seconds", 0);
            solver.UpdateFlightPlan();
            return Nodes(v);
        }

        static object RemoveNodes(Args a)
        {
            Vessel v = FlightApi.Active();
            PatchedConicSolver solver = Solver(v);
            if (a.Has("index"))
            {
                int index = a.Int("index");
                if (index < 0 || index >= solver.maneuverNodes.Count) throw new ApiException("No maneuver node with index " + index + ".");
                solver.maneuverNodes[index].RemoveSelf();
            }
            else
                while (solver.maneuverNodes.Count > 0) solver.maneuverNodes[0].RemoveSelf();
            solver.UpdateFlightPlan();
            return Nodes(v);
        }

        public static object PlanSimple(Args a)
        {
            Vessel v = FlightApi.Active();
            if (v.LandedOrSplashed) throw new ApiException("The vessel is on the surface; maneuver planning needs it to be in flight.");
            Orbit orbit = v.orbit;
            CelestialBody body = orbit.referenceBody;
            string maneuver = (a.Require("maneuver") ?? "").ToLowerInvariant().Replace(" ", "_");
            double now = Planetarium.GetUniversalTime();
            double ut;
            Vector3d worldDv;
            switch (maneuver)
            {
                case "circularize": case "circularise":
                {
                    string at = a.Str("at") ?? (orbit.eccentricity >= 1.0 || orbit.timeToPe < orbit.timeToAp ? "periapsis" : "apoapsis");
                    ut = ResolveUT(v, new Args(new Dictionary<string, object> { { "at", at }, { "ut", a.Raw("ut") }, { "in_seconds", a.Raw("in_seconds") } }), orbit);
                    worldDv = OrbitMath.CircularizeDv(orbit, ut);
                    break;
                }
                case "set_apoapsis": case "apoapsis":
                {
                    double alt = AltitudeArg(a);
                    ut = a.Has("at") || a.Has("ut") || a.Has("in_seconds") ? ResolveUT(v, a, orbit) : OrbitMath.NextPeriapsisUT(orbit, now);
                    double r = OrbitMath.Radius(orbit, ut);
                    if (body.Radius + alt < r - 1) throw new ApiException("The new apoapsis (" + R(alt / 1000) + " km) would be below the altitude where the burn happens (" + R((r - body.Radius) / 1000) + " km). Burn at apoapsis with set_periapsis instead.");
                    worldDv = OrbitMath.ChangeApsisDv(orbit, ut, body.Radius + alt);
                    break;
                }
                case "set_periapsis": case "periapsis":
                {
                    double alt = AltitudeArg(a);
                    if (!a.Has("at") && !a.Has("ut") && !a.Has("in_seconds") && orbit.eccentricity >= 1.0) throw new ApiException("On an escape trajectory, say when to burn (in_seconds or ut).");
                    ut = a.Has("at") || a.Has("ut") || a.Has("in_seconds") ? ResolveUT(v, a, orbit) : OrbitMath.NextApoapsisUT(orbit, now);
                    double r = OrbitMath.Radius(orbit, ut);
                    if (body.Radius + alt > r + 1) throw new ApiException("The new periapsis (" + R(alt / 1000) + " km) would be above the altitude where the burn happens (" + R((r - body.Radius) / 1000) + " km). Burn at periapsis with set_apoapsis instead.");
                    worldDv = OrbitMath.ChangeApsisDv(orbit, ut, body.Radius + alt);
                    break;
                }
                default:
                    throw new ApiException("Unknown maneuver '" + maneuver + "'. Available: circularize, set_apoapsis, set_periapsis" + PlannerApi.ExtraManeuvers + ".");
            }
            ManeuverNode node = Create(v, ut, OrbitMath.ToNode(orbit, ut, worldDv));
            host.Note("planned " + maneuver + ": " + Math.Round(node.DeltaV.magnitude, 1) + " m/s");
            return Nodes(v);
        }

        static double AltitudeArg(Args a)
        {
            if (a.Has("altitude_km")) return a.Num("altitude_km") * 1000.0;
            if (a.Has("altitude_m")) return a.Num("altitude_m");
            throw new ApiException("Give altitude_km.");
        }

        // ---------------------------------------------------------------- waiting and warp

        static readonly string[] conditionKeys =
        {
            "program_done", "ut", "altitude_above", "altitude_below", "apoapsis_above", "apoapsis_below", "periapsis_above", "periapsis_below",
            "time_to_apoapsis_below", "time_to_periapsis_below", "vertical_speed_above", "vertical_speed_below", "speed_above", "speed_below",
            "height_above_ground_below", "situation", "body", "node_in_below", "dynamic_pressure_below", "fuel_below",
            "sequence_done", "scene", "distance_to_target_below"
        };

        /// <summary>Reject unknown condition names up front so a typo cannot turn into an endless wait.</summary>
        public static void CheckConditions(Args until)
        {
            foreach (string key in until.Map.Keys)
                if (Array.IndexOf(conditionKeys, key) < 0) throw new ApiException("Unknown wait condition '" + key + "'. Available: " + string.Join(", ", conditionKeys));
        }

        public static string Met(Args u, Vessel v)
        {
            if (u == null) return null;
            Autopilot ap = Autopilot.I;
            if (u.Bool("sequence_done", false) && !SequenceApi.Running) return SequenceApi.Summary() ?? "no flight plan was running";
            if (u.Has("scene") && GameApi.SceneName(HighLogic.LoadedScene).Equals(u.Str("scene"), StringComparison.OrdinalIgnoreCase) && SceneWatch.Ready) return "scene is " + u.Str("scene");
            if (u.Bool("program_done", false) && ap.Program == null) return "program finished: " + (ap.LastProgramSummary ?? "none was running");
            if (u.Has("ut") && Planetarium.GetUniversalTime() >= u.Num("ut")) return "reached the requested time";
            if (v == null) return null;
            Orbit o = v.orbit;
            if (u.Has("altitude_above") && v.altitude > u.Num("altitude_above")) return "altitude above " + u.Num("altitude_above");
            if (u.Has("altitude_below") && v.altitude < u.Num("altitude_below")) return "altitude below " + u.Num("altitude_below");
            if (u.Has("apoapsis_above") && o.ApA > u.Num("apoapsis_above")) return "apoapsis above " + u.Num("apoapsis_above");
            if (u.Has("apoapsis_below") && o.ApA < u.Num("apoapsis_below")) return "apoapsis below " + u.Num("apoapsis_below");
            if (u.Has("periapsis_above") && o.PeA > u.Num("periapsis_above")) return "periapsis above " + u.Num("periapsis_above");
            if (u.Has("periapsis_below") && o.PeA < u.Num("periapsis_below")) return "periapsis below " + u.Num("periapsis_below");
            if (u.Has("time_to_apoapsis_below") && o.timeToAp < u.Num("time_to_apoapsis_below")) return "time to apoapsis below " + u.Num("time_to_apoapsis_below");
            if (u.Has("time_to_periapsis_below") && o.timeToPe < u.Num("time_to_periapsis_below")) return "time to periapsis below " + u.Num("time_to_periapsis_below");
            if (u.Has("vertical_speed_above") && v.verticalSpeed > u.Num("vertical_speed_above")) return "vertical speed above " + u.Num("vertical_speed_above");
            if (u.Has("vertical_speed_below") && v.verticalSpeed < u.Num("vertical_speed_below")) return "vertical speed below " + u.Num("vertical_speed_below");
            if (u.Has("speed_above") && v.srfSpeed > u.Num("speed_above")) return "surface speed above " + u.Num("speed_above");
            if (u.Has("speed_below") && v.srfSpeed < u.Num("speed_below")) return "surface speed below " + u.Num("speed_below");
            if (u.Has("height_above_ground_below") && v.radarAltitude < u.Num("height_above_ground_below")) return "height above ground below " + u.Num("height_above_ground_below");
            if (u.Has("dynamic_pressure_below") && v.dynamicPressurekPa < u.Num("dynamic_pressure_below")) return "dynamic pressure below " + u.Num("dynamic_pressure_below");
            if (u.Has("situation"))
                foreach (string s in u.Strings("situation"))
                    if (v.situation.ToString().Equals(s, StringComparison.OrdinalIgnoreCase)) return "situation is " + v.situation;
            if (u.Has("body") && v.mainBody == Places.Body(u.Str("body"))) return "now in the sphere of influence of " + Places.Name(v.mainBody);
            if (u.Has("node_in_below") && v.patchedConicSolver != null && v.patchedConicSolver.maneuverNodes.Count > 0 &&
                v.patchedConicSolver.maneuverNodes[0].UT - Planetarium.GetUniversalTime() < u.Num("node_in_below")) return "maneuver node is closer than " + u.Num("node_in_below") + " s";
            if (u.Has("distance_to_target_below") && v.targetObject != null && v.targetObject.GetTransform() != null &&
                ((Vector3d)v.targetObject.GetTransform().position - v.CoMD).magnitude < u.Num("distance_to_target_below")) return "closer to the target than " + u.Num("distance_to_target_below") + " m";
            if (u.Has("fuel_below"))
            {
                Guidance.EngineStats(v, out double _, out double _, out double _, out int burning, out int flamedOut);
                if (burning == 0 && flamedOut > 0) return "engines flamed out";
            }
            return null;
        }

        static IEnumerator Wait(Call c)
        {
            Args until = c.Args.Obj("until");
            if (until != null) CheckConditions(until);
            float timeout = (float)OrbitMath.Clamp(c.Args.Num("timeout_s", 30), 0.2, 600);
            bool stopOnEvent = c.Args.Bool("stop_on_event", true);
            int firstEvent = c.Args.Has("since_event") ? c.Args.Int("since_event") : Autopilot.I.Log.LastId;
            float started = Time.realtimeSinceStartup;
            double startUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0;
            string reason = null;
            while (reason == null)
            {
                Vessel v = HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                reason = Met(until, v);
                if (reason == null && stopOnEvent && Autopilot.I.Log.LastId > firstEvent) reason = "flight event";
                // (the player has said something to the AI: this is where it hears it, with everything left running as it is)
                if (reason == null && ChatApi.Waiting > 0) reason = "the player said something";
                if (reason == null && Time.realtimeSinceStartup - started >= timeout) reason = "timeout";
                if (reason == null && c.Abandoned) yield break;
                if (reason == null) yield return null;
            }
            var o = new JObj();
            o.Add("stopped_because", reason);
            o.Add("waited_real_s", R(Time.realtimeSinceStartup - started, 1));
            if (Planetarium.fetch != null) o.Add("game_time_passed_s", R(Planetarium.GetUniversalTime() - startUT, 1));
            List<object> events = Autopilot.I.Log.Since(firstEvent, 40);
            if (events.Count > 0) o.Add("events", events);
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null)
                o.Add("vessel", Brief(FlightGlobals.ActiveVessel));
            else
            {
                o.Add("scene", GameApi.SceneName(HighLogic.LoadedScene));
                string plan = SequenceApi.Summary();
                if (plan != null) o.Add("flight_plan", plan);
            }
            o.Add("last_event_id", Autopilot.I.Log.LastId);
            if (ChatApi.Waiting > 0) o.Add("player_messages", ChatApi.Take());
            c.Result = o;
        }

        /// <summary>A short telemetry line-up for polling loops, a fraction of the size of the full vessel report.</summary>
        public static JObj Brief(Vessel v)
        {
            var o = new JObj();
            o.Add("situation", v.situation.ToString());
            o.Add("body", Places.Name(v.mainBody));
            o.Add("met_s", R(v.missionTime, 0));
            o.Add("altitude_m", R(v.altitude, 0));
            if (v.radarAltitude < 20000 && !v.Splashed) o.Add("height_above_ground_m", R(v.radarAltitude, 0));
            o.Add("surface_speed", R(v.srfSpeed, 1));
            o.Add("vertical_speed", R(v.verticalSpeed, 1));
            if (!v.LandedOrSplashed)
            {
                Orbit orbit = v.orbit;
                if (orbit.eccentricity < 1.0) { o.Add("apoapsis_m", R(orbit.ApA, 0)); o.Add("time_to_apoapsis_s", R(orbit.timeToAp, 0)); }
                o.Add("periapsis_m", R(orbit.PeA, 0));
            }
            o.Add("stage", v.currentStage);
            o.Add("throttle", R(v.ctrlState.mainThrottle, 2));
            Guidance.EngineStats(v, out double maxThrust, out double thrust, out double _, out int burning, out int flamedOut);
            o.Add("thrust_kn", R(thrust, 0));
            if (flamedOut > 0) o.Add("engines_flamed_out", flamedOut);
            VesselDeltaV dv = v.VesselDeltaV;
            if (dv != null && dv.IsReady) o.Add("dv_left", R(dv.TotalDeltaVActual, 0));
            JObj ap = Autopilot.I.Status();
            if (Autopilot.I.Program != null) o.Add("autopilot", Autopilot.I.Program.Name + ": " + Autopilot.I.Program.Phase);
            else if (ap["last_program"] != null) o.Add("last_program", ap["last_program"]);
            string plan = SequenceApi.Summary();
            if (plan != null) o.Add("flight_plan", plan);
            return o;
        }

        static double lastWarpTarget = double.NaN;

        /// <summary>
        /// After a physics warp the game stays in that mode, where "warp to" only ever reaches 4x. Switch
        /// back to on-rails warp wherever the game allows it: on the ground, or above the air.
        /// </summary>
        static void PreferRails()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || TimeWarp.fetch == null || TimeWarp.WarpMode != TimeWarp.Modes.LOW) return;
            if (!v.LandedOrSplashed && v.altitude < v.mainBody.atmosphereDepth) return;
            if (TimeWarp.CurrentRateIndex != 0) TimeWarp.SetRate(0, true, false);
            TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
        }

        /// <summary>
        /// The game's clock set straight to a later time (the owner, 2026-10-13: "make it so that you can change the time
        /// without doing time warp"). The game's own Planetarium.SetUniversalTime only sets the number: a craft in physics
        /// would then stand where it stood while the planet turned under it. So, as a warp does, every loaded craft is put
        /// on rails first (a landed one is then held by its place on the planet, an orbiting one by its orbit), the clock is
        /// set, the game is given a few frames to put everything where it is at the new time, and the craft are let go.
        /// </summary>
        static IEnumerator SetTimeCmd(Call c)
        {
            Args a = c.Args;
            if (!HighLogic.LoadedSceneHasPlanetarium || Planetarium.fetch == null) throw new ApiException("The game's clock cannot be set in this scene.");
            double now = Planetarium.GetUniversalTime(), target;
            CelestialBody body = HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.mainBody : FlightGlobals.GetHomeBody();
            // (the sun is judged where the craft is, or else at the launch pad, wherever the game has its space centre)
            if (!Places.Pad(out double lat, out double lon)) { lat = 0.0; lon = 0.0; }
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ActiveVessel != null) { lat = FlightGlobals.ActiveVessel.latitude; lon = FlightGlobals.ActiveVessel.longitude; }
            if (a.Has("ut")) target = a.Num("ut");
            else if (a.Has("seconds")) target = now + a.Num("seconds");
            else if (a.Has("hours")) target = now + a.Num("hours") * 3600.0;
            else if (a.Has("sun")) target = SunAt(body, lat, lon, now, a.Num("sun"), a.Bool("rising", true));
            else throw new ApiException("Give one of: ut, seconds, hours, sun (degrees above the horizon, with rising: true or false).");
            if (double.IsNaN(target)) throw new ApiException("The sun never stands " + a.Num("sun") + " degrees up there.");
            if (!(target > now + 0.5)) throw new ApiException("Only forward: the time asked for is not later than now (" + R(now, 0) + ").");
            if (TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0) { TimeWarp.fetch.CancelAutoWarp(); TimeWarp.SetRate(0, true, false); yield return new WaitSec(0.3f); }
            var held = new List<Vessel>();
            var onPad = new HashSet<Vessel>();
            bool warped = false;
            if (HighLogic.LoadedScene == GameScenes.FLIGHT)
            {
                foreach (Vessel v in FlightGlobals.VesselsLoaded)
                    if (v != null && !v.packed) { if (v.situation == Vessel.Situations.PRELAUNCH) onPad.Add(v); held.Add(v); }
                // (The craft held on rails by the game itself, as its warp holds them: the lowest step of its warp, for the moment
                // of the jump. Put on rails by hand, as until 2026-10-18, a craft within the game's range was taken off them again
                // at the next frame (22 ms later in the log), and the clock moved on under a craft still flying by its physics, which
                // stayed where it was in space while the planet turned under it.)
                if (held.Count > 0 && TimeWarp.fetch != null)
                {
                    TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
                    TimeWarp.SetRate(1, true, false);
                    yield return new WaitSec(0.3f);
                    warped = TimeWarp.CurrentRateIndex >= 1;
                    if (!warped)
                        throw new ApiException("The game will not hold the craft on rails here (in the air, or under thrust), so its clock cannot be set: land, splash down or be in space first.");
                }
            }
            yield return null;
            Planetarium.SetUniversalTime(target);
            // (a few frames for the planets, the sun and the craft on rails to be put where they are at the new time)
            yield return new WaitSec(0.4f);
            if (warped) TimeWarp.SetRate(0, true, false);
            yield return new WaitSec(0.5f);
            // (a craft that stood ready on the pad stands ready still: off the rails the game calls it merely landed)
            foreach (Vessel v in onPad) if (v != null && v.loaded && v.situation == Vessel.Situations.LANDED) v.situation = Vessel.Situations.PRELAUNCH;
            double after = Planetarium.GetUniversalTime();
            c.Result = new JObj().Add("ut", R(after, 1)).Add("was", R(now, 1)).Add("on_s", R(after - now, 1)).Add("held_on_rails", held.Count)
                                 .Add("sun_deg", R(SunElevation(body, lat, lon, after, after), 1));
        }

        /// <summary>
        /// How fast the sun goes round the sky at a place on a world, in radians a second, as the hour hand goes round: the world's
        /// turn on its axis (backward where its period is negative, as Real Worlds' Venus, Uranus and Pluto turn) less the sun's
        /// own way round the sky as the world (or the planet a moon goes round) goes round the sun. (Since 2026-10-20: the sun was
        /// held where it stood and the world turned forward only, once: on Venus, turning backward once in 243 days with a solar
        /// day of 117, "the sun never stands 30 degrees up there".)
        /// </summary>
        static double SunRate(CelestialBody body)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (body == null) return 0.0;
            double spin = body.rotates && body.rotationPeriod != 0 ? 2.0 * Math.PI / body.rotationPeriod : 0.0;
            CelestialBody planet = body;
            while (planet.referenceBody != null && planet.referenceBody != sun && planet.referenceBody != planet) planet = planet.referenceBody;
            double year = planet != sun && planet.orbit != null ? planet.orbit.period : 0.0;
            double sense = planet.orbit != null && Math.Cos(planet.orbit.inclination * Math.PI / 180.0) < 0 ? -1.0 : 1.0;
            return spin - (year > 0 ? sense * 2.0 * Math.PI / year : 0.0);
        }

        /// <summary>
        /// How high the sun stands (degrees) at a place on a planet at a time not far from now: the place carried round the
        /// planet's axis, in the direction its own east goes (so no handedness can be got backwards), by the sun's turn round its
        /// sky since now (SunRate: the world's turn less the sun's own way round it); the sun's height above the world's equator
        /// taken as it is now (it changes by a degree a day or less, a little more on a slow world over its long day).
        /// </summary>
        static double SunElevation(CelestialBody body, double lat, double lon, double now, double ut)
        {
            CelestialBody sun = Planetarium.fetch.Sun;
            if (body == null || sun == null || body == sun) return double.NaN;
            Vector3d axis = body.GetSurfaceNVector(90.0, 0.0).normalized, up = body.GetSurfaceNVector(lat, lon).normalized;
            Vector3d east = body.GetSurfaceNVector(lat, lon + 0.01) - up;
            Vector3d along = axis * Vector3d.Dot(up, axis), round = up - along;
            double r = round.magnitude;
            if (r < 1e-6) return Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(up, (sun.position - body.position).normalized)))) * 57.29578;
            Vector3d p = round / r;
            east = (east - axis * Vector3d.Dot(east, axis) - p * Vector3d.Dot(east, p)).normalized;
            double turn = SunRate(body) * (ut - now);
            Vector3d upThen = along + r * (p * Math.Cos(turn) + east * Math.Sin(turn));
            Vector3d toSun = (sun.position - body.position).normalized;
            return Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(upThen.normalized, toSun)))) * 57.29578;
        }

        /// <summary>The first time after now that the sun stands so high there, going up (or down).</summary>
        static double SunAt(CelestialBody body, double lat, double lon, double now, double degrees, bool rising)
        {
            // (a solar day, the sun's round of the sky: Venus's 117 days where the old search went round its 243-day turn once)
            double rate = Math.Abs(SunRate(body)), day = rate > 1e-12 ? 2.0 * Math.PI / rate : 21600.0;
            double step = day / 720.0, t0 = now + 1.0, e0 = SunElevation(body, lat, lon, now, t0);
            for (double t1 = t0 + step; t1 < now + day * 1.2; t1 += step)
            {
                double e1 = SunElevation(body, lat, lon, now, t1);
                bool crossed = rising ? e0 < degrees && e1 >= degrees : e0 > degrees && e1 <= degrees;
                if (crossed)
                {
                    double lo = t1 - step, hi = t1;
                    for (int n = 0; n < 30; n++)
                    {
                        double mid = 0.5 * (lo + hi), em = SunElevation(body, lat, lon, now, mid);
                        if (rising ? em < degrees : em > degrees) lo = mid; else hi = mid;
                    }
                    return hi;
                }
                t0 = t1; e0 = e1;
            }
            return double.NaN;
        }

        static IEnumerator WarpCmd(Call c)
        {
            Args a = c.Args;
            if (!HighLogic.LoadedSceneHasPlanetarium || TimeWarp.fetch == null) throw new ApiException("Time warp is not available in this scene.");
            double now = Planetarium.GetUniversalTime();
            Vessel v = HighLogic.LoadedScene == GameScenes.FLIGHT ? FlightGlobals.ActiveVessel : null;

            if (a.Has("rate"))
            {
                int index = (int)OrbitMath.Clamp(a.Int("rate"), 0, 7);
                TimeWarp.fetch.CancelAutoWarp();
                TimeWarp.SetRate(index, false, true);
                yield return new WaitSec(0.5f);
                c.Result = new JObj().Add("rate", TimeWarp.CurrentRate).Add("mode", TimeWarp.WarpMode == TimeWarp.Modes.HIGH ? "on_rails" : "physics");
                yield break;
            }

            double target = double.NaN;
            double lead = a.Num("lead_s", 0);
            if (a.Has("ut")) target = a.Num("ut");
            else if (a.Has("seconds")) target = now + a.Num("seconds");
            else
            {
                if (v == null) throw new ApiException("Give 'seconds' or 'ut' (the 'to' option needs a vessel in flight).");
                Orbit orbit = v.orbit;
                switch ((a.Str("to") ?? "").ToLowerInvariant())
                {
                    case "apoapsis": case "ap": target = now + orbit.timeToAp; break;
                    case "periapsis": case "pe": target = now + orbit.timeToPe; break;
                    case "node": case "maneuver":
                        if (v.patchedConicSolver == null || v.patchedConicSolver.maneuverNodes.Count == 0) throw new ApiException("There is no maneuver node to warp to.");
                        target = v.patchedConicSolver.maneuverNodes[0].UT;
                        if (!a.Has("lead_s")) lead = 60;
                        break;
                    case "soi": case "soi_change":
                        if (orbit.patchEndTransition == Orbit.PatchTransitionType.FINAL) throw new ApiException("This trajectory never leaves " + Places.Name(orbit.referenceBody) + "'s sphere of influence.");
                        target = orbit.EndUT + 5;
                        break;
                    default: throw new ApiException("Give one of: to (apoapsis|periapsis|node|soi), seconds, ut, or rate (0-7).");
                }
            }
            target -= lead;
            if (target - now < 1.0)
            {
                c.Result = new JObj().Add("warped_s", 0).Add("note", "already at or past that time");
                yield break;
            }

            host.Note("time warp for " + KSPUtil.PrintTimeCompact(target - now, false));
            float timeout = (float)OrbitMath.Clamp(a.Num("timeout_s", 120), 5, 600);
            float started = Time.realtimeSinceStartup;
            bool resuming = Math.Abs(target - lastWarpTarget) < 2.0 && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH;
            if (!resuming)
            {
                TimeWarp.fetch.CancelAutoWarp();
                PreferRails();
                TimeWarp.fetch.WarpTo(target);
            }
            lastWarpTarget = target;
            float lastProgressCheck = started;
            double lastUT = now;
            bool physicsFallback = false;
            int settleTries = 0;
            string stopped = null;
            int firstEvent = Autopilot.I.Log.LastId;
            while (stopped == null)
            {
                yield return null;
                double ut = Planetarium.GetUniversalTime();
                if (ut >= target - 0.5) { stopped = "arrived"; break; }
                if (Time.realtimeSinceStartup - started > timeout) { stopped = "still warping"; break; }
                if (c.Abandoned) { stopped = "abandoned"; break; }
                if (Autopilot.I.Log.LastId > firstEvent && a.Bool("stop_on_event", true)) { stopped = "flight event"; break; }
                // (the player has said something: the AI is given it now, and a warp on rails goes on by itself meanwhile)
                if (ChatApi.Waiting > 0) { stopped = "still warping"; break; }
                if (Time.realtimeSinceStartup - lastProgressCheck > 3f)
                {
                    // KSP refuses on-rails warp in atmosphere or under thrust; fall back to 4x physics warp.
                    if (!physicsFallback && ut - lastUT < 6.0 && TimeWarp.CurrentRateIndex == 0)
                    {
                        Vessel craft = FlightGlobals.ActiveVessel;
                        if (craft != null && craft.LandedOrSplashed && settleTries++ < 5)
                        {
                            // A vessel on the ground may warp on rails once it has stopped rocking; ask again.
                            TimeWarp.fetch.CancelAutoWarp();
                            PreferRails();
                            TimeWarp.fetch.WarpTo(target);
                        }
                        else
                        {
                            physicsFallback = true;
                            TimeWarp.fetch.CancelAutoWarp();
                            TimeWarp.SetRate(3, false, false);
                        }
                    }
                    lastProgressCheck = Time.realtimeSinceStartup;
                    lastUT = ut;
                }
            }
            // On-rails auto-warp stops itself at the target, so a slow warp may simply be left running.
            bool leaveRunning = stopped == "still warping" && !physicsFallback;
            if (!leaveRunning && (stopped != "arrived" || physicsFallback || TimeWarp.CurrentRateIndex != 0))
            {
                TimeWarp.fetch.CancelAutoWarp();
                TimeWarp.SetRate(0, true, false);
                lastWarpTarget = double.NaN;
            }
            for (int n = 0; n < 5; n++) yield return null;
            var o = new JObj().Add("stopped_because", stopped).Add("warped_s", R(Planetarium.GetUniversalTime() - now, 1)).Add("ut", R(Planetarium.GetUniversalTime(), 1));
            if (stopped == "still warping")
                o.Add("remaining_s", R(target - Planetarium.GetUniversalTime(), 0)).Add("next", leaveRunning ? "The warp is still running and will stop by itself; call warp again with the same arguments to keep waiting." : "Call warp again with the same arguments to continue.");
            if (physicsFallback) o.Add("note", "on-rails warp was not allowed here (atmosphere or thrust), used 4x physics warp");
            if (ChatApi.Waiting > 0) o.Add("player_messages", ChatApi.Take());
            List<object> events = Autopilot.I.Log.Since(firstEvent, 20);
            if (events.Count > 0) o.Add("events", events);
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ActiveVessel != null) o.Add("vessel", Brief(FlightGlobals.ActiveVessel));
            c.Result = o;
        }

        // ---------------------------------------------------------------- bodies, vessels, targets

        static object Bodies(Args a)
        {
            if (FlightGlobals.Bodies == null) throw new ApiException("No game is loaded.");
            string only = a.Str("name");
            CelestialBody wanted = string.IsNullOrEmpty(only) ? null : Places.Body(only);
            var list = new List<object>();
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (!string.IsNullOrEmpty(only) && b != wanted) continue;
                var o = new JObj();
                o.Add("name", Places.Name(b));
                if (b.orbit != null && b.referenceBody != null && b.referenceBody != b) o.Add("orbits", Places.Name(b.referenceBody));
                o.Add("radius_m", R(b.Radius, 0));
                o.Add("surface_gravity", R(b.GeeASL * 9.80665, 2));
                o.Add("mu", b.gravParameter);
                if (!double.IsInfinity(b.sphereOfInfluence)) o.Add("soi_m", R(b.sphereOfInfluence, 0));
                o.Add("atmosphere_m", b.atmosphere ? b.atmosphereDepth : 0);
                if (b.atmosphere && b.atmosphereContainsOxygen) o.Add("oxygen", true);
                double low = b.atmosphere ? b.atmosphereDepth + 10000 : Math.Max(10000, Math.Round(b.Radius * 0.05 / 1000) * 1000);
                o.Add("low_orbit_m", low);
                o.Add("low_orbit_speed", R(OrbitMath.CircularSpeed(b, b.Radius + low), 0));
                o.Add("rotation_period_s", R(b.rotationPeriod, 0));
                if (b.orbit != null && b.referenceBody != null && b.referenceBody != b)
                    o.Add("orbit", new JObj().Add("sma_m", R(b.orbit.semiMajorAxis, 0)).Add("period_s", R(b.orbit.period, 0)).Add("inclination_deg", R(b.orbit.inclination, 2)).Add("eccentricity", R(b.orbit.eccentricity, 3)));
                if (!string.IsNullOrEmpty(only))
                {
                    var moons = new List<object>();
                    foreach (CelestialBody m in b.orbitingBodies) moons.Add(Places.Name(m));
                    if (moons.Count > 0) o.Add("satellites", moons);
                    o.Add("has_surface", b.hasSolidSurface);
                    o.Add("ocean", b.ocean);
                    if (b.rotates && b.rotationPeriod != 0)
                    {
                        // (a world turning backward, as Venus does in Real Worlds, has one too, flown the other way round)
                        double sync = Math.Pow(b.gravParameter * Math.Pow(Math.Abs(b.rotationPeriod) / (2 * Math.PI), 2), 1.0 / 3.0) - b.Radius;
                        if (sync + b.Radius < b.sphereOfInfluence) { o.Add("synchronous_orbit_m", R(sync, 0)); if (b.rotationPeriod < 0) o.Add("turns_backward", true); }
                    }
                }
                list.Add(o);
            }
            if (list.Count == 0) throw new ApiException("No body named '" + only + "'.");
            return list;
        }

        static object Vessels(Args a)
        {
            if (FlightGlobals.Vessels == null) throw new ApiException("No game is loaded.");
            bool debris = a.Bool("include_debris", false);
            Vessel active = HighLogic.LoadedScene == GameScenes.FLIGHT ? FlightGlobals.ActiveVessel : null;
            var list = new List<object>();
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null) continue;
                if (!debris && (v.vesselType == VesselType.Debris || v.vesselType == VesselType.SpaceObject || v.vesselType == VesselType.Flag || v.vesselType == VesselType.Unknown || v.vesselType == VesselType.DroppedPart)) continue;
                var o = new JObj();
                o.Add("name", v.GetDisplayName());
                o.Add("id", v.persistentId);
                o.Add("type", v.vesselType.ToString().ToLowerInvariant());
                o.Add("situation", v.situation.ToString());
                o.Add("body", Places.Name(v.mainBody));
                if (v == active) o.Add("active", true);
                if (v.LandedOrSplashed) { o.Add("lat", R(v.latitude, 3)); o.Add("lon", R(v.longitude, 3)); }
                else if (v.orbit != null)
                {
                    if (v.orbit.eccentricity < 1.0) o.Add("apoapsis_m", R(v.orbit.ApA, 0));
                    o.Add("periapsis_m", R(v.orbit.PeA, 0));
                    o.Add("inclination_deg", R(v.orbit.inclination, 2));
                }
                if (active != null && v != active && v.mainBody == active.mainBody) o.Add("distance_m", R((v.GetWorldPos3D() - active.GetWorldPos3D()).magnitude, 0));
                list.Add(o);
            }
            return list;
        }

        /// <summary>Find a vessel by name (exact, then partial) or by the id the vessels list gives, which tells same-named vessels apart.</summary>
        public static Vessel FindVessel(string name)
        {
            Vessel partial = null;
            name = (name ?? "").Trim();
            if (uint.TryParse(name, out uint id))
                foreach (Vessel v in FlightGlobals.Vessels)
                    if (v != null && v.persistentId == id) return v;
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null) continue;
                if (v.GetDisplayName().Equals(name, StringComparison.OrdinalIgnoreCase)) return v;
                if (partial == null && v.GetDisplayName().IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) partial = v;
            }
            return partial;
        }

        static object SetTarget(Args a)
        {
            Vessel v = FlightApi.Active();
            string name = a.Require("name").Trim();
            if (name.Equals("none", StringComparison.OrdinalIgnoreCase) || name.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                FlightGlobals.fetch.SetVesselTarget(null, true);
                return new JObj().Add("target", null);
            }
            CelestialBody body = Places.Body(name);
            if (body != null)
            {
                if (body == v.mainBody) throw new ApiException("You are already orbiting " + Places.Name(body) + "; it cannot be the target.");
                FlightGlobals.fetch.SetVesselTarget(body, true);
            }
            else
            {
                Vessel other = FindVessel(name);
                if (other == null) throw new ApiException("No body or vessel named '" + name + "'. Use bodies or vessels to list them.");
                if (other == v) throw new ApiException("A vessel cannot target itself.");
                FlightGlobals.fetch.SetVesselTarget(other, true);
            }
            host.Note("target: " + name);
            return new JObj().Add("target", v.targetObject != null ? FlightApi.TargetInfo(v) : null);
        }

        static IEnumerator SwitchVessel(Call c)
        {
            string name = c.Args.Require("name");
            if (!HighLogic.LoadedSceneIsGame || FlightGlobals.Vessels == null) throw new ApiException("No game is loaded.");
            Vessel target = FindVessel(name);
            if (target == null) throw new ApiException("No vessel named '" + name + "'. Use vessels to list them.");
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && target == FlightGlobals.ActiveVessel) { c.Result = FlightApi.VesselSummary(target); yield break; }

            Autopilot.I.Release("switching vessel", true);
            host.Note("switching to '" + target.GetDisplayName() + "'");
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && target.loaded)
            {
                FlightGlobals.ForceSetActiveVessel(target);
                yield return new WaitSec(1.0f);
            }
            else
            {
                yield return GameApi.WaitClearToSave();
                GameApi.SaveBeforeLeaving();
                int index = FlightGlobals.Vessels.IndexOf(target);
                FlightDriver.StartAndFocusVessel("persistent", index);
                yield return SceneWatch.WaitFor(GameScenes.FLIGHT, 180f);
            }
            yield return FlightApi.Settle();
            c.Result = FlightApi.VesselSummary(FlightGlobals.ActiveVessel);
        }
    }
}
