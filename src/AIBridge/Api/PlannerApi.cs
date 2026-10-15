using System;
using System.Collections;
using System.Collections.Generic;
using AIBridge.Pilot;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// Maneuver planning that needs a search rather than a formula: transfers to a moon or another vessel,
    /// the trip home from a moon, plane changes and velocity matching. Each planner makes a first guess
    /// from orbital mechanics, then refines the node against KSP's own patched-conic prediction, so the
    /// answer agrees with what the map view will show.
    /// </summary>
    public static class PlannerApi
    {
        public const string ExtraManeuvers = ", transfer, return, correct, match_planes, match_velocity";
        static BridgeHost host;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Async("plan", "Create a node for a maneuver. params: {maneuver: circularize|set_apoapsis|set_periapsis|transfer|return|correct|match_planes|match_velocity, at, altitude_km, periapsis_km, body, in_seconds}", Plan);
        }

        static IEnumerator Plan(Call c)
        {
            Vessel v = FlightApi.Active();
            string maneuver = (c.Args.Require("maneuver") ?? "").ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
            switch (maneuver)
            {
                case "transfer": case "hohmann": case "transfer_to_target": yield return Transfer(c, v); break;
                case "return": case "return_from_moon": case "return_home": yield return ReturnHome(c, v); break;
                case "correct": case "correction": case "course_correction": yield return Correct(c, v); break;
                case "match_planes": case "match_plane": MatchPlanes(c, v); break;
                case "match_velocity": case "match_velocities": MatchVelocity(c, v); break;
                default: c.Result = NavApi.PlanSimple(c.Args); break;
            }
        }

        static PatchedConicSolver Solver(Vessel v)
        {
            if (v.patchedConicSolver == null)
                throw new ApiException("Maneuver nodes are not available for this vessel (in career mode they need an upgraded Tracking Station and Mission Control).");
            if (v.LandedOrSplashed) throw new ApiException("The vessel is on the surface; maneuver planning needs it to be in flight.");
            return v.patchedConicSolver;
        }

        static void ClearNodes(PatchedConicSolver solver)
        {
            while (solver.maneuverNodes.Count > 0) solver.maneuverNodes[0].RemoveSelf();
        }

        /// <summary>First trajectory segment after the node that is around <paramref name="body"/>, or null.</summary>
        static Orbit PatchAround(ManeuverNode node, CelestialBody body)
        {
            int guard = 0;
            for (Orbit p = node.nextPatch; p != null && guard++ < 8; p = p.nextPatch)
            {
                if (p.referenceBody == body) return p;
                if (p.patchEndTransition == Orbit.PatchTransitionType.FINAL) break;
            }
            return null;
        }

        /// <summary>
        /// Derivative-free search: step each variable both ways, keep what improves, halve the steps when
        /// nothing does. Yields now and then so the game keeps rendering.
        /// </summary>
        static IEnumerator Search(double[] x, double[] step, double[] minStep, Func<double[], double> cost, int maxEvals)
        {
            double best = cost(x);
            int evals = 1, sinceYield = 0;
            while (evals < maxEvals)
            {
                bool fine = true;
                for (int n = 0; n < x.Length; n++) if (step[n] > minStep[n]) fine = false;
                if (fine) break;
                bool improved = false;
                for (int n = 0; n < x.Length; n++)
                {
                    foreach (double sign in new[] { 1.0, -1.0 })
                    {
                        // Keep walking in a direction for as long as it pays off.
                        while (evals < maxEvals)
                        {
                            double old = x[n];
                            x[n] = old + sign * step[n];
                            double value = cost(x);
                            evals++;
                            if (++sinceYield >= 10) { sinceYield = 0; yield return null; }
                            if (value < best - 1e-6) { best = value; improved = true; }
                            else { x[n] = old; break; }
                        }
                    }
                }
                if (!improved) for (int n = 0; n < x.Length; n++) step[n] *= 0.5;
            }
            cost(x);
        }

        // ---------------------------------------------------------------- transfer to target

        static IEnumerator Transfer(Call c, Vessel v)
        {
            PatchedConicSolver solver = Solver(v);
            ITargetable target = v.targetObject;
            if (target == null) throw new ApiException("Set a target first (set_target), e.g. the Mun or another vessel.");
            Orbit o1 = v.orbit, o2 = target.GetOrbit();
            CelestialBody body = o1.referenceBody;
            var targetBody = target as CelestialBody;
            if (o2 != null && o2.referenceBody != body && targetBody != null && body.orbit != null && o2.referenceBody == body.referenceBody)
            {
                yield return Interplanetary(c, v, targetBody);
                yield break;
            }
            if (o2 == null || o2.referenceBody != body)
            {
                string where = o2 != null ? Places.Name(o2.referenceBody) : "something else";
                throw new ApiException("'" + target.GetName() + "' orbits " + where + ", not " + Places.Name(body) + ". This planner handles targets orbiting the same body as your vessel (its moons, other vessels) and bodies that share your body's parent (other planets from a planetary orbit). Get into orbit around the right body first, or build the node by hand with add_node.");
            }
            if (o1.eccentricity >= 1.0) throw new ApiException("The vessel is on an escape trajectory; get into a closed orbit first.");

            double now = Planetarium.GetUniversalTime();
            double mu = body.gravParameter;
            double r1 = o1.semiMajorAxis, r2 = o2.semiMajorAxis;
            double transferTime = Math.PI * Math.Sqrt(Math.Pow((r1 + r2) / 2.0, 3) / mu);
            double n1 = 2.0 * Math.PI / o1.period, n2 = 2.0 * Math.PI / o2.period;
            if (Math.Abs(n1 - n2) < 1e-9) throw new ApiException("The target is on almost the same orbit as you; change your orbit's height first so that you drift relative to it.");

            // How far ahead of the vessel the target is right now, measured along the vessel's direction of travel.
            Vector3d normal = o1.GetOrbitNormal().normalized;
            Vector3d p1 = o1.getRelativePositionAtUT(now), vel1 = o1.getOrbitalVelocityAtUT(now);
            Vector3d p2 = o2.getRelativePositionAtUT(now);
            p2 -= normal * Vector3d.Dot(p2, normal);
            double lead = Vector3d.Angle(p1, p2) * Math.PI / 180.0;
            if (Vector3d.Dot(p2, vel1) < 0) lead = 2.0 * Math.PI - lead;
            double required = Math.PI - n2 * transferTime;
            double wait = Mod(lead - required, 2.0 * Math.PI) / (n1 - n2);
            if (n1 < n2) wait = Mod(required - lead, 2.0 * Math.PI) / (n2 - n1);
            double synodic = 2.0 * Math.PI / Math.Abs(n1 - n2);
            double minLead = Math.Max(90.0, c.Args.Num("min_lead_s", 90));
            while (wait < minLead) wait += synodic;

            double desired;
            if (targetBody != null)
            {
                double defaultPe = targetBody.atmosphere ? targetBody.atmosphereDepth + 20000 : Math.Max(15000, targetBody.Radius * 0.1);
                desired = targetBody.Radius + (c.Args.Has("periapsis_km") ? c.Args.Num("periapsis_km") * 1000.0 : defaultPe);
            }
            else desired = c.Args.Num("distance_m", 0);

            ClearNodes(solver);
            double ut0 = now + wait;
            ManeuverNode node = solver.AddManeuverNode(ut0);
            node.DeltaV = OrbitMath.ToNode(o1, ut0, OrbitMath.ChangeApsisDv(o1, ut0, r2));
            solver.UpdateFlightPlan();
            double baseRadial = node.DeltaV.x, baseNormal = node.DeltaV.y;

            Func<double[], double> cost = x =>
            {
                node.UT = x[0];
                node.DeltaV = new Vector3d(baseRadial, baseNormal, x[1]);
                solver.UpdateFlightPlan();
                if (targetBody != null)
                {
                    Orbit encounter = PatchAround(node, targetBody);
                    if (encounter != null) return Math.Abs(encounter.PeR - desired);
                }
                Orbit planned = node.nextPatch;
                if (planned == null) return 1e15;
                double span = planned.eccentricity < 1.0 ? planned.period : transferTime * 2.0;
                double miss = OrbitMath.ClosestApproach(planned, o2, x[0], Math.Min(span, transferTime * 1.6), out double _);
                return targetBody != null ? 1e10 + miss : Math.Max(0, miss - desired);
            };

            // Coarse scan of the departure time first: the phase-angle estimate assumes circular, coplanar orbits.
            double[] xBest = { ut0, node.DeltaV.z };
            double bestCost = cost(xBest);
            if (bestCost > (targetBody != null ? 1e9 : 20000))
            {
                for (int n = -12; n <= 12; n++)
                {
                    double ut = ut0 + n * o1.period / 60.0;
                    if (ut < now + minLead) continue;
                    double value = cost(new[] { ut, xBest[1] });
                    if (value < bestCost) { bestCost = value; xBest[0] = ut; }
                    if ((n & 3) == 0) yield return null;
                }
            }
            double[] x = { xBest[0], xBest[1] };
            yield return Search(x, new[] { o1.period / 200.0, 4.0 }, new[] { 0.05, 0.004 }, cost, 260);
            if (node.UT < now + 20) { node.UT += o1.period; solver.UpdateFlightPlan(); }

            host.Note("planned transfer to " + target.GetName() + ": " + Math.Round(node.DeltaV.magnitude, 1) + " m/s");
            var result = new JObj().Add("nodes", NavApi.Nodes(v));
            if (targetBody != null && PatchAround(node, targetBody) == null)
                result.Add("warning", "The search did not find an encounter with " + Places.Name(targetBody) + ". Check that your orbit is roughly circular and in the same plane as the target (match_planes), then plan again.");
            else if (targetBody != null)
                result.Add("next_step", "execute_node, then warp to=soi, then plan circularize at periapsis around " + Places.Name(targetBody) + " and execute it to capture.");
            c.Result = result;
        }

        static double Mod(double a, double m) { double r = a % m; return r < 0 ? r + m : r; }

        /// <summary>A duration in the game's own days (six hours on Kerbin), e.g. "230 d 1 h".</summary>
        static string Span(double seconds)
        {
            double day = KSPUtil.dateTimeFormatter != null ? KSPUtil.dateTimeFormatter.Day : 21600.0;
            if (seconds < 3600) return Math.Round(seconds / 60.0) + " min";
            if (seconds < day) return Math.Round(seconds / 3600.0, 1) + " h";
            double days = Math.Floor(seconds / day);
            return days + " d " + Math.Round((seconds - days * day) / 3600.0) + " h";
        }

        // ---------------------------------------------------------------- to another planet (or sibling moon)

        /// <summary>
        /// Leave the body being orbited for a sibling body (Kerbin to Duna, or Mun to Minmus). Waits for the
        /// Hohmann phase angle, then searches the ejection time and burn size for an encounter.
        /// </summary>
        static IEnumerator Interplanetary(Call c, Vessel v, CelestialBody target)
        {
            PatchedConicSolver solver = Solver(v);
            Orbit park = v.orbit;
            CelestialBody home = park.referenceBody;
            CelestialBody sun = home.referenceBody;
            if (park.eccentricity >= 1.0) throw new ApiException("Get into a closed parking orbit around " + Places.Name(home) + " first.");

            double now = Planetarium.GetUniversalTime();
            double mu = sun.gravParameter;
            Orbit oh = home.orbit, ot = target.orbit;
            double r1 = oh.semiMajorAxis, r2 = ot.semiMajorAxis;
            double transferTime = Math.PI * Math.Sqrt(Math.Pow((r1 + r2) / 2.0, 3) / mu);
            double n1 = 2.0 * Math.PI / oh.period, n2 = 2.0 * Math.PI / ot.period;

            Vector3d normal = oh.GetOrbitNormal().normalized;
            Vector3d p1 = oh.getRelativePositionAtUT(now), vel1 = oh.getOrbitalVelocityAtUT(now);
            Vector3d p2 = ot.getRelativePositionAtUT(now);
            p2 -= normal * Vector3d.Dot(p2, normal);
            double lead = Vector3d.Angle(p1, p2) * Math.PI / 180.0;
            if (Vector3d.Dot(p2, vel1) < 0) lead = 2.0 * Math.PI - lead;
            double required = Math.PI - n2 * transferTime;
            double wait = n1 > n2 ? Mod(lead - required, 2.0 * Math.PI) / (n1 - n2) : Mod(required - lead, 2.0 * Math.PI) / (n2 - n1);
            if (wait < 600) wait += 2.0 * Math.PI / Math.Abs(n1 - n2);

            // Speed needed relative to the home body once clear of it, and the burn that produces it.
            double excess = Math.Abs(OrbitMath.SpeedAt(sun, r1, (r1 + r2) / 2.0) - OrbitMath.CircularSpeed(sun, r1));
            double rPark = park.semiMajorAxis;
            double dvGuess = Math.Sqrt(excess * excess + 2.0 * home.gravParameter / rPark) - OrbitMath.CircularSpeed(home, rPark);

            double defaultPe = target.atmosphere ? target.atmosphereDepth + 30000 : Math.Max(20000, target.Radius * 0.15);
            double desired = target.Radius + (c.Args.Has("periapsis_km") ? c.Args.Num("periapsis_km") * 1000.0 : defaultPe);

            ClearNodes(solver);
            double ut0 = now + wait;
            ManeuverNode node = solver.AddManeuverNode(ut0);
            Func<double[], double> cost = x =>
            {
                node.UT = x[0];
                node.DeltaV = new Vector3d(0, x.Length > 2 ? x[2] : 0, x[1]);
                solver.UpdateFlightPlan();
                Orbit arrival = PatchAround(node, target);
                if (arrival != null) return Math.Abs(arrival.PeR - desired);
                Orbit cruise = PatchAround(node, sun);
                if (cruise == null) return 1e16;
                double miss = OrbitMath.ClosestApproach(cruise, ot, cruise.StartUT, transferTime * 1.4, out double _);
                return 1e10 + miss;
            };

            // Where around the parking orbit the burn happens sets the departure direction: scan one full orbit.
            double bestUT = ut0, bestCost = double.MaxValue;
            const int slots = 48;
            for (int n = 0; n < slots; n++)
            {
                double ut = ut0 + park.period * n / slots;
                double value = cost(new[] { ut, dvGuess });
                if (value < bestCost) { bestCost = value; bestUT = ut; }
                if ((n & 3) == 0) yield return null;
            }
            // A metre per second at departure moves the arrival by thousands of kilometres, so search finely.
            // The third variable (a normal component) steers out of the orbital plane to meet a tilted target orbit.
            double[] x = { bestUT, dvGuess, 0.0 };
            yield return Search(x, new[] { park.period / 300.0, 6.0, 4.0 }, new[] { 0.002, 0.0002, 0.0002 }, cost, 3000);

            Orbit result = PatchAround(node, target);
            host.Note("planned transfer to " + Places.Name(target) + ": " + Math.Round(node.DeltaV.magnitude, 1) + " m/s in " + Span(node.UT - now));
            var o = new JObj().Add("nodes", NavApi.Nodes(v));
            o.Add("departure_in", Span(node.UT - now));
            if (result != null)
                o.Add("arrival", new JObj().Add("body", Places.Name(target)).Add("periapsis_m", Math.Round(result.PeA)).Add("reaches_its_soi_in", Span(result.StartUT - now)));
            else
            {
                Orbit cruise = PatchAround(node, sun);
                double miss = cruise != null ? OrbitMath.ClosestApproach(cruise, ot, cruise.StartUT, transferTime * 1.4, out double _) : double.NaN;
                o.Add("warning", "No encounter with " + Places.Name(target) + " was found; the closest pass is about " + Math.Round(miss / 1e6) + " thousand km. Execute this burn, then plan a mid-course correction with add_node / edit_node while watching target_approach.");
            }
            o.Add("next_step", "warp to=node, execute_node, warp to=soi (several times), fine-tune with a small correction burn if needed, then circularize at periapsis.");
            c.Result = o;
        }

        // ---------------------------------------------------------------- mid-course correction

        /// <summary>
        /// A small burn part-way through a transfer that puts the arrival periapsis where it was meant to be.
        /// Burns never come out exact, and over an interplanetary distance a centimetre per second matters.
        /// </summary>
        static IEnumerator Correct(Call c, Vessel v)
        {
            PatchedConicSolver solver = Solver(v);
            CelestialBody body;
            string bodyName = c.Args.Str("body");
            if (!string.IsNullOrEmpty(bodyName))
            {
                body = Places.Body(bodyName);
                if (body == null) throw new ApiException("No body named '" + bodyName + "'.");
            }
            else
            {
                body = v.targetObject as CelestialBody;
                // Once inside the sphere of influence of the body being approached, the game drops it as the target.
                if (body == null && v.orbit.eccentricity >= 1.0 && v.mainBody != Planetarium.fetch.Sun) body = v.mainBody;
                if (body == null) throw new ApiException("Say which body to arrive at: set it as the target, or pass body (e.g. \"Kerbin\" on the way home).");
            }
            double defaultPe = body.atmosphere ? body.atmosphereDepth + 30000 : Math.Max(20000, body.Radius * 0.15);
            double desired = body.Radius + (c.Args.Has("periapsis_km") ? c.Args.Num("periapsis_km") * 1000.0 : defaultPe);

            double now = Planetarium.GetUniversalTime();
            Orbit orbit = v.orbit;
            double ut;
            if (c.Args.Has("in_seconds")) ut = now + Math.Max(30, c.Args.Num("in_seconds"));
            else if (c.Args.Has("ut")) ut = c.Args.Num("ut");
            // By default burn soon, but after leaving the sphere of influence the vessel is still climbing out of.
            else if (orbit.referenceBody != body && orbit.patchEndTransition == Orbit.PatchTransitionType.ESCAPE) ut = orbit.EndUT + 900;
            else ut = now + 600;

            ClearNodes(solver);
            ManeuverNode node = solver.AddManeuverNode(ut);
            node.DeltaV = Vector3d.zero;
            solver.UpdateFlightPlan();
            Orbit targetOrbit = body.orbit;
            Func<double[], double> cost = x =>
            {
                node.DeltaV = new Vector3d(x[0], x[1], x[2]);
                solver.UpdateFlightPlan();
                Orbit arrival = PatchAround(node, body);
                // Prefer the smallest burn among those that hit the periapsis.
                if (arrival != null) return Math.Abs(arrival.PeR - desired) + 0.5 * (Math.Abs(x[0]) + Math.Abs(x[1]) + Math.Abs(x[2]));
                Orbit cruise = node.nextPatch;
                if (cruise == null || targetOrbit == null || cruise.referenceBody != targetOrbit.referenceBody) return 1e16;
                double span = cruise.eccentricity < 1.0 ? cruise.period : 1e7;
                return 1e10 + OrbitMath.ClosestApproach(cruise, targetOrbit, node.UT, span, out double _);
            };
            double[] x0 = { 0.0, 0.0, 0.0 };
            yield return Search(x0, new[] { 2.0, 2.0, 2.0 }, new[] { 0.0005, 0.0005, 0.0005 }, cost, 2500);

            Orbit result = PatchAround(node, body);
            host.Note("planned course correction: " + Math.Round(node.DeltaV.magnitude, 2) + " m/s");
            var o = new JObj().Add("nodes", NavApi.Nodes(v));
            if (result == null) o.Add("warning", "The search found no trajectory that reaches " + Places.Name(body) + " from here. The miss may be too large for a small correction.");
            else o.Add("arrival", new JObj().Add("body", Places.Name(body)).Add("periapsis_m", Math.Round(result.PeA)).Add("inclination_deg", Math.Round(result.inclination, 1)));
            c.Result = o;
        }

        // ---------------------------------------------------------------- return from a moon

        static IEnumerator ReturnHome(Call c, Vessel v)
        {
            PatchedConicSolver solver = Solver(v);
            Orbit o1 = v.orbit;
            CelestialBody moon = o1.referenceBody;
            CelestialBody parent = moon.referenceBody;
            if (parent == null || parent == moon || moon.orbit == null)
                throw new ApiException("'return' is for coming home from a moon. You are orbiting " + Places.Name(moon) + ", which has no parent body to return to.");
            if (o1.eccentricity >= 1.0) throw new ApiException("The vessel is already on an escape trajectory. Use nodes to see where it leads.");

            double desiredPe = c.Args.Has("periapsis_km") ? c.Args.Num("periapsis_km") * 1000.0 : (parent.atmosphere ? parent.atmosphereDepth * 0.45 : Math.Max(15000, parent.Radius * 0.1));
            double desired = parent.Radius + desiredPe;
            double now = Planetarium.GetUniversalTime();

            // Leave the moon backwards along its orbit with just enough speed to fall to the wanted periapsis.
            double moonRadius = moon.orbit.semiMajorAxis;
            double moonSpeed = OrbitMath.CircularSpeed(parent, moonRadius);
            double arriveSpeed = OrbitMath.SpeedAt(parent, moonRadius, (moonRadius + desired) / 2.0);
            double excess = Math.Abs(moonSpeed - arriveSpeed);
            double r = o1.semiMajorAxis;
            double burnSpeed = Math.Sqrt(excess * excess + 2.0 * moon.gravParameter / r);
            double dvGuess = burnSpeed - OrbitMath.CircularSpeed(moon, r);

            ClearNodes(solver);
            ManeuverNode node = solver.AddManeuverNode(now + 120);
            Func<double[], double> cost = x =>
            {
                node.UT = x[0];
                node.DeltaV = new Vector3d(0, 0, x[1]);
                solver.UpdateFlightPlan();
                Orbit home = PatchAround(node, parent);
                if (home == null)
                {
                    Orbit after = node.nextPatch;
                    return 1e12 - (after != null && after.eccentricity < 1.0 ? after.ApR : 0);
                }
                return Math.Abs(home.PeR - desired);
            };

            double bestUT = now + 120, bestCost = double.MaxValue;
            const int slots = 36;
            for (int n = 0; n < slots; n++)
            {
                double ut = now + 120 + o1.period * n / slots;
                double value = cost(new[] { ut, dvGuess * 1.03 });
                if (value < bestCost) { bestCost = value; bestUT = ut; }
                if ((n & 3) == 0) yield return null;
            }
            double[] x = { bestUT, dvGuess * 1.03 };
            yield return Search(x, new[] { o1.period / 150.0, 3.0 }, new[] { 0.05, 0.004 }, cost, 300);

            Orbit result = PatchAround(node, parent);
            host.Note("planned return to " + Places.Name(parent) + ": " + Math.Round(node.DeltaV.magnitude, 1) + " m/s");
            var o = new JObj().Add("nodes", NavApi.Nodes(v));
            if (result == null) o.Add("warning", "The search did not find a trajectory that escapes " + Places.Name(moon) + ". Is there enough delta-v, and is the orbit roughly circular?");
            else o.Add("arrival", new JObj().Add("body", Places.Name(parent)).Add("periapsis_m", Math.Round(result.PeA)).Add("leaves_" + Places.Name(moon).ToLowerInvariant() + "_soi_in_s", Math.Round(result.StartUT - now)));
            c.Result = o;
        }

        // ---------------------------------------------------------------- plane and velocity matching

        static void MatchPlanes(Call c, Vessel v)
        {
            PatchedConicSolver solver = Solver(v);
            ITargetable target = v.targetObject;
            if (target == null) throw new ApiException("Set a target first (set_target).");
            Orbit o1 = v.orbit, o2 = target.GetOrbit();
            if (o2 == null || o2.referenceBody != o1.referenceBody) throw new ApiException("The target must orbit the same body as your vessel.");
            if (o1.eccentricity >= 1.0) throw new ApiException("Get into a closed orbit first.");

            double now = Planetarium.GetUniversalTime();
            Vector3d h1 = o1.GetOrbitNormal().normalized, h2 = o2.GetOrbitNormal().normalized;
            double relative = Vector3d.Angle(h1, h2);
            if (relative < 0.01) { c.Result = new JObj().Add("note", "Planes already match (relative inclination " + Math.Round(relative, 3) + "°)."); return; }
            Vector3d line = Vector3d.Cross(h1, h2).normalized;

            // The two crossing points are opposite each other; take whichever comes first unless told otherwise.
            double bestUT = double.MaxValue;
            Vector3d bestDv = Vector3d.zero;
            string which = (c.Args.Str("at") ?? "").ToLowerInvariant();
            double highestRadius = -1;
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                double ta = o1.GetTrueAnomalyOfZupVector(line * sign);
                double ut = o1.GetUTforTrueAnomaly(ta, o1.period);
                while (ut < now + 30) ut += o1.period;
                Vector3d pos = o1.getRelativePositionAtUT(ut), vel = o1.getOrbitalVelocityAtUT(ut);
                // Rotate the velocity about the position vector; pick the sense that lines the planes up.
                Vector3d a = QuaternionD.AngleAxis(relative, pos.normalized) * vel;
                Vector3d b = QuaternionD.AngleAxis(-relative, pos.normalized) * vel;
                Vector3d rotated = Vector3d.Angle(Vector3d.Cross(pos, a), h2 * Vector3d.Dot(Vector3d.Cross(pos, vel), h1)) < Vector3d.Angle(Vector3d.Cross(pos, b), h2 * Vector3d.Dot(Vector3d.Cross(pos, vel), h1)) ? a : b;
                Vector3d dv = (rotated - vel).xzy;
                bool take;
                if (which == "highest" || which == "cheapest") { take = pos.magnitude > highestRadius; if (take) highestRadius = pos.magnitude; }
                else take = ut < bestUT;
                if (take) { bestUT = ut; bestDv = dv; }
            }
            ClearNodes(solver);
            ManeuverNode node = solver.AddManeuverNode(bestUT);
            node.DeltaV = OrbitMath.ToNode(o1, bestUT, bestDv);
            solver.UpdateFlightPlan();
            host.Note("planned plane match: " + Math.Round(node.DeltaV.magnitude, 1) + " m/s");
            c.Result = new JObj().Add("relative_inclination_deg", Math.Round(relative, 3)).Add("nodes", NavApi.Nodes(v));
        }

        static void MatchVelocity(Call c, Vessel v)
        {
            PatchedConicSolver solver = Solver(v);
            ITargetable target = v.targetObject;
            if (target == null) throw new ApiException("Set a target first (set_target).");
            Orbit o1 = v.orbit, o2 = target.GetOrbit();
            if (o2 == null || o2.referenceBody != o1.referenceBody) throw new ApiException("The target must orbit the same body as your vessel.");
            double now = Planetarium.GetUniversalTime();
            double span = o1.eccentricity < 1.0 ? o1.period : 6 * 3600.0;
            double miss = OrbitMath.ClosestApproach(o1, o2, now + 5, span, out double ut);
            if (c.Args.Has("in_seconds")) ut = now + c.Args.Num("in_seconds");
            Vector3d dv = (o2.getOrbitalVelocityAtUT(ut) - o1.getOrbitalVelocityAtUT(ut)).xzy;
            ClearNodes(solver);
            ManeuverNode node = solver.AddManeuverNode(ut);
            node.DeltaV = OrbitMath.ToNode(o1, ut, dv);
            solver.UpdateFlightPlan();
            host.Note("planned velocity match: " + Math.Round(node.DeltaV.magnitude, 1) + " m/s");
            c.Result = new JObj().Add("closest_approach_m", Math.Round(miss)).Add("nodes", NavApi.Nodes(v));
        }
    }
}
