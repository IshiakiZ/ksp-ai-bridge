using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// How much drag the vessel will have on the way down: the game's own drag-cube figures for the parts
    /// that will still be attached at entry, tail-first, tabulated against Mach number.
    /// </summary>
    public sealed class EntryModel
    {
        const int Steps = 33;
        const double MachStep = 0.25;

        public double Mass;
        public int Parts;
        readonly double[] area = new double[Steps];
        static readonly System.Reflection.FieldInfo originalDrag =
            typeof(DragCubeList).GetField("weightedDragOrig", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>The parts left after every decoupler staged before the parachutes has fired.</summary>
        public static List<Part> KeptParts(Vessel v, bool dropStages)
        {
            var kept = new List<Part>(v.parts);
            if (!dropStages) return kept;
            int chuteStage = -1;
            foreach (Part p in v.parts)
                if (p.FindModuleImplementing<ModuleParachute>() != null && p.inverseStage > chuteStage) chuteStage = p.inverseStage;
            if (chuteStage < 0) return kept;
            var gone = new HashSet<Part>();
            foreach (Part p in v.parts)
            {
                ModuleDecouplerBase d = p.FindModuleImplementing<ModuleDecouplerBase>();
                if (d == null || !d.stagingEnabled || d.isDecoupled || p.inverseStage <= chuteStage || p == v.rootPart) continue;
                Collect(p, gone);
            }
            if (gone.Count == 0) return kept;
            kept.RemoveAll(gone.Contains);
            bool chuteKept = false;
            foreach (Part p in kept) if (p.FindModuleImplementing<ModuleParachute>() != null) chuteKept = true;
            return chuteKept ? kept : new List<Part>(v.parts);
        }

        static void Collect(Part p, HashSet<Part> into)
        {
            if (!into.Add(p)) return;
            foreach (Part child in p.children) Collect(child, into);
        }

        public static EntryModel For(Vessel v, bool dropStages)
        {
            List<Part> kept = KeptParts(v, dropStages);
            var m = new EntryModel { Parts = kept.Count };
            foreach (Part p in kept) m.Mass += p.mass + p.GetResourceMass();
            // The drag figures have to be the ones the vessel will have after separation. They differ from
            // today's in that a face a dropped part is bolted to (the heat shield's underside) is hidden
            // now, and its drag coefficient is cut down with it. Uncover those faces, read the figures, and
            // let the game put everything back. (Checked against the game after a real separation: the
            // figures agree to three decimals. The shroud's own drag cube stays in use, so it is left alone.)
            var keptSet = new HashSet<Part>(kept);
            var touched = new List<Part>();
            var unshrouded = new List<Part>();
            foreach (Part p in kept)
            {
                if (p.DragCubes == null || p.DragCubes.None) continue;
                bool borders = false;
                foreach (AttachNode node in p.attachNodes) if (node.attachedPart != null && !keptSet.Contains(node.attachedPart)) borders = true;
                if (!borders) continue;
                touched.Add(p);
                Refresh(p);
                float[] exposed = p.DragCubes.AreaOccluded, full = p.DragCubes.WeightedArea, drag = p.DragCubes.WeightedDrag;
                float[] own = originalDrag != null ? originalDrag.GetValue(p.DragCubes) as float[] : null;
                foreach (AttachNode node in p.attachNodes)
                {
                    if (node.attachedPart == null || keptSet.Contains(node.attachedPart)) continue;
                    int face = FaceToward(node.orientation);
                    exposed[face] = full[face];
                    if (own != null) drag[face] = own[face];
                }
            }
            try
            {
                // Tail-first: the air arrives from the direction the nose points away from.
                Vector3 nose = v.ReferenceTransform.up;
                for (int n = 0; n < Steps; n++)
                {
                    double sum = 0;
                    foreach (Part p in kept)
                    {
                        if (p.ShieldedFromAirstream || p.DragCubes == null || p.DragCubes.None) continue;
                        p.DragCubes.SetDrag(p.partTransform.InverseTransformDirection(nose), (float)(n * MachStep));
                        sum += p.DragCubes.AreaDrag;
                    }
                    m.area[n] = sum;
                }
            }
            finally
            {
                foreach (Part p in unshrouded)
                {
                    p.DragCubes.SetCubeWeight("Fairing", 1f);
                    p.DragCubes.SetCubeWeight("Clean", 0f);
                }
                foreach (Part p in touched) Refresh(p);
            }
            return m;
        }

        /// <summary>Per-part drag figures at one Mach number, tail-first, as the parts are right now (for checking the model).</summary>
        public static List<object> Breakdown(Vessel v, List<Part> parts, float mach)
        {
            var rows = new List<object>();
            Vector3 nose = v.ReferenceTransform.up;
            foreach (Part p in parts)
            {
                if (p.DragCubes == null || p.DragCubes.None) continue;
                p.DragCubes.SetDrag(p.partTransform.InverseTransformDirection(nose), mach);
                var cubes = new List<string>();
                foreach (DragCube c in p.DragCubes.Cubes) cubes.Add(c.Name + "=" + c.Weight);
                rows.Add(new JObj().Add("part", p.partInfo.name).Add("area_drag", Math.Round(p.DragCubes.AreaDrag, 3)).Add("cubes", string.Join(" ", cubes.ToArray()))
                    .Add("exposed", string.Join(" ", Array.ConvertAll(p.DragCubes.AreaOccluded, x => Math.Round(x, 2).ToString())))
                    .Add("full", string.Join(" ", Array.ConvertAll(p.DragCubes.WeightedArea, x => Math.Round(x, 2).ToString())))
                    .Add("cd", string.Join(" ", Array.ConvertAll(p.DragCubes.WeightedDrag, x => Math.Round(x, 2).ToString()))));
            }
            return rows;
        }

        static bool HasCube(Part p, string name)
        {
            foreach (DragCube cube in p.DragCubes.Cubes) if (cube.Name == name) return true;
            return false;
        }

        /// <summary>Have the game recompute a part's combined drag cube and what its neighbours hide.</summary>
        static void Refresh(Part p)
        {
            p.DragCubes.ForceUpdate(true, true);
            p.DragCubes.SetDragWeights();
            p.DragCubes.RequestOcclusionUpdate();
            p.DragCubes.SetPartOcclusion();
        }

        /// <summary>Index of the drag-cube face (+X, -X, +Y, -Y, +Z, -Z) that looks most nearly along a direction.</summary>
        static int FaceToward(Vector3 direction)
        {
            Vector3[] faces = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            int best = 0;
            float bestDot = float.NegativeInfinity;
            for (int n = 0; n < 6; n++)
            {
                float d = Vector3.Dot(direction.normalized, faces[n]);
                if (d > bestDot) { bestDot = d; best = n; }
            }
            return best;
        }

        public double Area(double mach)
        {
            double x = Math.Max(0, mach) / MachStep;
            int i = (int)x;
            if (i >= Steps - 1) return area[Steps - 1];
            return area[i] + (area[i + 1] - area[i]) * (x - i);
        }
    }

    /// <summary>
    /// Flies a vessel down through the atmosphere on paper: gravity plus the game's drag law, in air that
    /// turns with the planet, from the top of the atmosphere to the ground.
    /// </summary>
    public static class EntryPredictor
    {
        /// <summary>
        /// Where an orbit that dips into the atmosphere ends. The result is the landing point as a direction
        /// from the body's centre fixed to the ground, expressed in today's axes (so it can be compared with
        /// any other ground point directly), and the time of landing.
        /// </summary>
        /// <summary>Why the last prediction failed, when it did.</summary>
        public static string Why;

        /// <summary>
        /// How much warmer or colder than the standard curve the air is at a place right now: the game
        /// shifts it with latitude and with the sun (peaking mid-afternoon), which changes the density.
        /// </summary>
        public static double TemperatureOffset(CelestialBody body, double lat, double lon)
        {
            try
            {
                Vector3d up = body.GetSurfaceNVector(lat, lon);
                CelestialBody sun = Planetarium.fetch.Sun;
                Vector3d sunVector = (sun.position - (body.position + up * body.Radius)).normalized;
                Vector3d lagged = Quaternion.AngleAxis(45f * Mathf.Sign((float)body.rotationPeriod), body.bodyTransform.up) * (Vector3)up;
                double sunShare = (1.0 + Vector3d.Dot(sunVector, lagged)) * 0.5;
                float absLat = (float)Math.Abs(lat);
                return body.latitudeTemperatureBiasCurve.Evaluate(absLat) + body.latitudeTemperatureSunMultCurve.Evaluate(absLat) * sunShare;
            }
            catch { return 0; }
        }

        public static double Temperature(CelestialBody body, double altitude, double offset) =>
            body.GetTemperature(altitude) + body.atmosphereTemperatureSunMultCurve.Evaluate((float)altitude) * offset;

        public static bool Land(Orbit orbit, double fromUT, EntryModel model, double groundAltitude, double temperatureOffset, out Vector3d groundDirection, out double landUT, out double peakG)
        {
            groundDirection = Vector3d.zero;
            landUT = fromUT;
            peakG = 0;
            CelestialBody body = orbit.referenceBody;
            double radius = body.Radius, top = body.atmosphereDepth, now = Planetarium.GetUniversalTime();
            Why = null;
            if (!body.atmosphere || double.IsNaN(orbit.semiMajorAxis) || orbit.PeA > top - 2000) { Why = "the orbit after the burn does not dip into the atmosphere (periapsis " + Math.Round(orbit.PeA / 1000.0, 1) + " km)"; return false; }

            // Find the moment the orbit comes down through the top of the atmosphere.
            double period = orbit.eccentricity < 1 ? orbit.period : 7200, t = fromUT, step = Math.Max(5.0, period / 400.0);
            if (orbit.getRelativePositionAtUT(t).magnitude - radius > top)
            {
                int guard = 0;
                while (orbit.getRelativePositionAtUT(t + step).magnitude - radius > top && guard++ < 800) t += step;
                if (guard >= 800) { Why = "could not find where the orbit meets the atmosphere"; return false; }
                double lo = t, hi = t + step;
                for (int n = 0; n < 30; n++)
                {
                    double mid = (lo + hi) / 2.0;
                    if (orbit.getRelativePositionAtUT(mid).magnitude - radius > top) lo = mid; else hi = mid;
                }
                t = hi;
            }

            Vector3d r = orbit.getRelativePositionAtUT(t).xzy, vel = orbit.getOrbitalVelocityAtUT(t).xzy;
            Vector3d spin = body.angularVelocity;
            double mu = body.gravParameter;
            float cube = PhysicsGlobals.DragCubeMultiplier * PhysicsGlobals.DragMultiplier;
            FloatCurve reynolds = PhysicsGlobals.DragCurvePseudoReynolds;

            Func<Vector3d, Vector3d, double, Vector3d> accel = (pos, v, time) =>
            {
                double d = pos.magnitude;
                Vector3d a = -pos * (mu / (d * d * d));
                double altitude = d - radius;
                if (altitude >= top) return a;
                double pressure = body.GetPressure(altitude);
                if (pressure <= 0) return a;
                double density = body.GetDensity(pressure, Temperature(body, altitude, temperatureOffset));
                Vector3d air = v - Vector3d.Cross(spin, pos);
                double speed = air.magnitude;
                if (speed < 0.01 || density <= 0) return a;
                double mach = speed / Math.Max(1.0, body.GetSpeedOfSound(pressure, density));
                double force = 0.0005 * density * speed * speed * model.Area(mach) * cube * reynolds.Evaluate((float)(density * speed));
                return a - air * (force / (model.Mass * speed));
            };

            for (int n = 0; n < 30000; n++)
            {
                double altitude = r.magnitude - radius;
                if (altitude <= groundAltitude) break;
                if (altitude > top + 500 && Vector3d.Dot(r, vel) > 0) { Why = "the vessel would skip back out of the atmosphere"; return false; }
                double dt = altitude > 45000 ? 2.0 : altitude > 3000 ? 0.5 : 0.25;
                Vector3d a1 = accel(r, vel, t);
                Vector3d midR = r + vel * (dt * 0.5), midV = vel + a1 * (dt * 0.5);
                Vector3d a2 = accel(midR, midV, t + dt * 0.5);
                double felt = (a2 + midR * (mu / Math.Pow(midR.magnitude, 3))).magnitude / 9.80665;
                if (felt > peakG) peakG = felt;
                r += midV * dt;
                vel += a2 * dt;
                t += dt;
            }
            landUT = t;
            FinalSpeed = (vel - Vector3d.Cross(spin, r)).magnitude;
            groundDirection = LandingPlanner.GroundDirectionAfter(body, r.normalized, -(t - now));
            return true;
        }

        public sealed class Plan
        {
            public double UT, LandUT, MissDistance, PeakG, Tilt, ArrivalSpeed;
            public Vector3d WorldDv;
        }

        static Vector3d BurnFor(Orbit current, double ut, double periapsisRadius, double tiltDeg)
        {
            double r = OrbitMath.Radius(current, ut);
            double speed = OrbitMath.SpeedAt(current.referenceBody, r, (r + periapsisRadius) / 2.0);
            double tilt = tiltDeg * Math.PI / 180.0;
            Vector3d level = OrbitMath.Horizontal(current, ut), normal = OrbitMath.Normal(current);
            return (level * Math.Cos(tilt) + normal * Math.Sin(tilt)) * speed - OrbitMath.Velocity(current, ut);
        }

        static Orbit After(Orbit current, double ut, Vector3d worldDv)
        {
            var o = new Orbit();
            o.UpdateFromStateVectors(current.getRelativePositionAtUT(ut), current.getOrbitalVelocityAtUT(ut) + worldDv.xzy, current.referenceBody, ut);
            return o;
        }

        /// <summary>
        /// Time the de-orbit burn (and tilt it slightly) so that the predicted landing point is the target.
        /// The landing point slides along the ground as the burn is delayed, so two predictions give the
        /// rate, and a few corrections on time and tilt finish the job.
        /// </summary>
        /// <summary>Speed through the air at the end of the last descent that <see cref="Land"/> worked out.</summary>
        public static double FinalSpeed { get; private set; }

        /// <param name="above">Aim for a point this far above the ground at the spot instead of the ground itself.</param>
        public static Plan Aim(Vessel v, EntryModel model, double lat, double lon, double periapsisAltitude, double above = 0)
        {
            Orbit current = v.orbit;
            CelestialBody body = current.referenceBody;
            double now = Planetarium.GetUniversalTime(), rPe = body.Radius + periapsisAltitude;
            Vector3d target = LandingPlanner.SurfaceDirection(body, lat, lon);
            double ground = Math.Max(0, body.TerrainAltitude(lat, lon, true)) + above;
            Vector3d normal = OrbitMath.Normal(current);

            double warmth = TemperatureOffset(body, lat, lon);
            Func<double, double, Vector3d?> land = (ut, tilt) =>
                Land(After(current, ut, BurnFor(current, ut, rPe, tilt)), ut, model, ground, warmth, out Vector3d where, out double _, out double _) ? where : (Vector3d?)null;
            // Miss split into "along the track" and "across it", as angles seen from the body's centre.
            Func<Vector3d, double[]> miss = where =>
            {
                Vector3d along = Vector3d.Cross(normal, where).normalized;      // direction of travel over the ground (either sense; consistent)
                Vector3d across = Vector3d.Cross(where, along).normalized;
                Vector3d gap = target - where;
                return new[] { Vector3d.Dot(gap, along), Vector3d.Dot(gap, across) };
            };

            double ut0 = now + 90;
            Vector3d? first = land(ut0, 0), second = land(ut0 + 60, 0);
            if (!first.HasValue || !second.HasValue) throw new ApiException("Cannot predict the entry: " + (Why ?? "unknown reason") + " (entry mass " + Math.Round(model.Mass, 3) + " t, drag area at Mach 1: " + Math.Round(model.Area(1), 2) + ").");
            // Positions along the ground track as angles about the orbit's axis, measured from the first
            // predicted landing point. Delaying the burn slides the landing point round at a steady rate.
            Vector3d e1 = Vector3d.Exclude(normal, first.Value).normalized, e2 = Vector3d.Cross(normal, e1).normalized;
            Func<Vector3d, double> along = x => Math.Atan2(Vector3d.Dot(x, e2), Vector3d.Dot(x, e1));
            double rate = along(second.Value) / 60.0;
            if (Math.Abs(rate) < 1e-7) throw new ApiException("Cannot aim: delaying the burn does not move the predicted landing point.");
            double toTarget = along(target);
            double turn = rate > 0 ? toTarget : -toTarget;
            if (turn < 0) turn += 2.0 * Math.PI;
            double ut = ut0 + turn / Math.Abs(rate), tilt = 0;

            Vector3d? best = land(ut, tilt);
            if (!best.HasValue) throw new ApiException("Cannot predict the entry for the chosen burn time: " + (Why ?? "unknown reason") + ".");
            for (int iteration = 0; iteration < 7; iteration++)
            {
                double[] m = miss(best.Value);
                if (Math.Sqrt(m[0] * m[0] + m[1] * m[1]) * body.Radius < 150) break;
                Vector3d? dT = land(ut + 4, tilt), dN = land(ut, tilt + 0.4);
                if (!dT.HasValue || !dN.HasValue) break;
                double[] mT = miss(dT.Value), mN = miss(dN.Value);
                // Two equations, two unknowns: how the miss changes with time and with tilt.
                double a = (mT[0] - m[0]) / 4.0, b = (mN[0] - m[0]) / 0.4, c = (mT[1] - m[1]) / 4.0, d = (mN[1] - m[1]) / 0.4;
                double det = a * d - b * c;
                if (Math.Abs(det) < 1e-18) break;
                double stepT = (-m[0] * d + m[1] * b) / det, stepN = (-m[1] * a + m[0] * c) / det;
                stepT = OrbitMath.Clamp(stepT, -600, 600);
                stepN = OrbitMath.Clamp(stepN, -6, 6);
                double tryUT = Math.Max(now + 45, ut + stepT), tryTilt = OrbitMath.Clamp(tilt + stepN, -35, 35);
                Vector3d? next = land(tryUT, tryTilt);
                if (!next.HasValue) break;
                ut = tryUT;
                tilt = tryTilt;
                best = next;
            }

            Vector3d dv = BurnFor(current, ut, rPe, tilt);
            Land(After(current, ut, dv), ut, model, ground, warmth, out Vector3d final, out double landUT, out double peak);
            return new Plan { UT = ut, LandUT = landUT, Tilt = tilt, WorldDv = dv, PeakG = peak, ArrivalSpeed = FinalSpeed, MissDistance = Vector3d.Angle(final, target) * Math.PI / 180.0 * body.Radius };
        }
    }
}
