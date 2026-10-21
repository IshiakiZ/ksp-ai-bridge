using System;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Works out the de-orbit burn that brings a vessel down over a chosen spot on a world without air:
    /// a burn half an orbit earlier that lowers the periapsis to just above the spot and tilts the orbit
    /// so that its track passes overhead when the vessel gets there, allowing for the ground turning
    /// underneath in the meantime.
    /// </summary>
    public static class LandingPlanner
    {
        public sealed class Deorbit
        {
            public double UT, ArrivalUT, PeriapsisAltitude, MissDistance, TerrainAltitude;
            public Vector3d WorldDv;
        }

        /// <summary>Where a point fixed to the ground will be (as a direction from the body's centre, in today's axes) after the body has turned for a while.</summary>
        public static Vector3d GroundDirectionAfter(CelestialBody body, Vector3d direction, double seconds)
        {
            if (!body.rotates || body.rotationPeriod == 0 || seconds == 0) return direction;
            Vector3d axis = ((Vector3d)body.angularVelocity).normalized;
            if (axis.sqrMagnitude < 0.5) return direction;
            Vector3d along = axis * Vector3d.Dot(direction, axis);
            Vector3d across = direction - along;
            double size = across.magnitude;
            if (size < 1e-9) return direction;
            // The ground's own velocity says which way "forward in time" is, so no handedness guesswork.
            Vector3d forward = body.getRFrmVel(body.position + direction * body.Radius);
            if (forward.sqrMagnitude < 1e-12) return direction;
            forward = Vector3d.Exclude(axis, forward).normalized;
            // (by the size of the turn: "forward" is the ground's own way already, backward on a world that turns backward, as Real
            // Worlds' Venus and Pluto do; until 2026-10-20 such a world's ground was not turned at all)
            double angle = 2.0 * Math.PI * seconds / Math.Abs(body.rotationPeriod);
            return along + (across.normalized * Math.Cos(angle) + forward * Math.Sin(angle)) * size;
        }

        public static Vector3d SurfaceDirection(CelestialBody body, double lat, double lon) => body.GetSurfaceNVector(lat, lon).normalized;

        static Orbit After(Orbit current, double ut, Vector3d worldDv)
        {
            var o = new Orbit();
            o.UpdateFromStateVectors(current.getRelativePositionAtUT(ut), current.getOrbitalVelocityAtUT(ut) + worldDv.xzy, current.referenceBody, ut);
            return o;
        }

        /// <summary>Closest the descent orbit's ground track comes to the spot in the half orbit after the burn, in metres over the ground.</summary>
        static double Miss(Orbit descent, CelestialBody body, Vector3d spotNow, double burnUT, double now, out double atUT)
        {
            if (double.IsNaN(descent.semiMajorAxis) || descent.eccentricity >= 1) { atUT = burnUT; return 1e9; }
            double period = descent.eccentricity < 1 ? descent.period : 3600;
            double lo = burnUT + period * 0.25, hi = burnUT + period * 0.75;
            Func<double, double> angle = t =>
            {
                Vector3d ship = descent.getRelativePositionAtUT(t).xzy.normalized;
                return Vector3d.Angle(ship, GroundDirectionAfter(body, spotNow, t - now));
            };
            // Coarse scan first: the miss is not unimodal over half an orbit when the track is well off.
            double best = double.MaxValue;
            atUT = lo;
            for (int n = 0; n <= 40; n++)
            {
                double t = lo + (hi - lo) * n / 40.0, a = angle(t);
                if (a < best) { best = a; atUT = t; }
            }
            double left = Math.Max(lo, atUT - (hi - lo) / 40.0), right = Math.Min(hi, atUT + (hi - lo) / 40.0);
            for (int n = 0; n < 40; n++)
            {
                double m1 = left + (right - left) / 3.0, m2 = right - (right - left) / 3.0;
                if (angle(m1) < angle(m2)) right = m2; else left = m1;
            }
            atUT = (left + right) / 2.0;
            return angle(atUT) * Math.PI / 180.0 * body.Radius;
        }

        /// <summary>
        /// The burn at a given moment that leaves the vessel flying level, at the speed whose far apsis is the
        /// wanted periapsis, in a plane turned by <paramref name="tiltDeg"/> about the vertical. (Turning the
        /// velocity rather than adding a sideways push keeps the speed, and so the periapsis, where it was asked for.)
        /// </summary>
        static Vector3d BurnFor(Orbit current, double ut, double periapsisRadius, double tiltDeg)
        {
            double r = OrbitMath.Radius(current, ut);
            double speed = OrbitMath.SpeedAt(current.referenceBody, r, (r + periapsisRadius) / 2.0);
            double tilt = tiltDeg * Math.PI / 180.0;
            Vector3d level = OrbitMath.Horizontal(current, ut), normal = OrbitMath.Normal(current);
            return (level * Math.Cos(tilt) + normal * Math.Sin(tilt)) * speed - OrbitMath.Velocity(current, ut);
        }

        /// <summary>
        /// Cost of a candidate burn: how far the track misses the spot, in metres, plus what the approach
        /// costs in fuel, at three metres of miss for each metre a second. The fuel is the burn itself,
        /// the extra descent when the track is still above its low point at the spot, and the wasted climb
        /// when it is already past it. Many burns reach the spot: one made opposite it needs an enormous
        /// plane change to move the track sideways at all, one made a third of an orbit before needs a
        /// few degrees and arrives a little high. Counting only the miss and the height took whichever came
        /// first (a 46 degree, 430 m/s burn in one test).
        /// </summary>
        static double Cost(Orbit current, CelestialBody body, Vector3d spotNow, double ut, double tiltDeg, double rPe, double now)
        {
            Vector3d burn = BurnFor(current, ut, rPe, tiltDeg);
            Orbit descent = After(current, ut, burn);
            double miss = Miss(descent, body, spotNow, ut, now, out double at);
            if (miss >= 1e8) return miss;
            Vector3d position = descent.getRelativePositionAtUT(at), velocity = descent.getOrbitalVelocityAtUT(at);
            double high = Math.Max(0, position.magnitude - rPe);
            double gravity = body.gravParameter / (rPe * rPe);
            double climbing = Math.Max(0, Vector3d.Dot(position.normalized, velocity));
            return miss + 3.0 * (burn.magnitude + Math.Sqrt(2.0 * gravity * high) + 2.0 * climbing);
        }

        public static Deorbit Plan(Vessel v, double lat, double lon, double clearance)
        {
            Orbit current = v.orbit;
            CelestialBody body = current.referenceBody;
            if (current.eccentricity >= 1.0) throw new ApiException("The vessel is on an escape trajectory; capture into an orbit first.");
            double now = Planetarium.GetUniversalTime();
            Vector3d spotNow = SurfaceDirection(body, lat, lon);
            double terrain = Math.Max(0, body.TerrainAltitude(lat, lon, true));
            double periapsisAltitude = terrain + clearance;
            if (current.PeA < periapsisAltitude + 500) periapsisAltitude = Math.Max(terrain + 800, current.PeA - 500);

            Deorbit plan = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                double rPe = body.Radius + periapsisAltitude;
                Func<double, double, double> cost = (ut, tilt) => Cost(current, body, spotNow, ut, tilt, rPe, now);

                // Where in the next orbit to burn and how far to turn the plane. For each of a few tilts take
                // the best moment out of every 5 degrees of orbit, polish time and tilt together from there,
                // and keep whichever ends up cheapest: the families differ in fuel, not in whether they hit.
                double period = current.period, bestUT = now + 60, bestCost = double.MaxValue, normalDv = 0;
                foreach (double startTilt in new[] { 0.0, -5.0, 5.0, -12.0, 12.0, -25.0, 25.0, -45.0, 45.0 })
                {
                    double tryUT = now + 60, tryCost = double.MaxValue, tryTilt = startTilt;
                    for (int n = 0; n < 72; n++)
                    {
                        double ut = now + 60 + period * n / 72.0;
                        double c = cost(ut, startTilt);
                        if (c < tryCost) { tryCost = c; tryUT = ut; }
                    }
                    double stepT = period / 72.0, stepN = 4;
                    for (int iteration = 0; iteration < 300 && (stepT > 0.02 || stepN > 0.0005); iteration++)
                    {
                        bool improved = false;
                        foreach (double dt in new[] { stepT, -stepT })
                        {
                            double ut = tryUT + dt;
                            if (ut < now + 30) continue;
                            double c = cost(ut, tryTilt);
                            if (c < tryCost) { tryCost = c; tryUT = ut; improved = true; break; }
                        }
                        foreach (double dn in new[] { stepN, -stepN })
                        {
                            double c = cost(tryUT, tryTilt + dn);
                            if (c < tryCost) { tryCost = c; tryTilt += dn; improved = true; break; }
                        }
                        if (!improved) { stepT *= 0.5; stepN *= 0.5; }
                    }
                    if (tryCost < bestCost) { bestCost = tryCost; bestUT = tryUT; normalDv = tryTilt; }
                }

                Vector3d dv = BurnFor(current, bestUT, rPe, normalDv);
                Orbit descent = After(current, bestUT, dv);
                double miss = Miss(descent, body, spotNow, bestUT, now, out double arrival);
                plan = new Deorbit { UT = bestUT, ArrivalUT = arrival, PeriapsisAltitude = periapsisAltitude, MissDistance = miss, WorldDv = dv, TerrainAltitude = terrain };

                // Make sure the last stretch of the approach clears the hills before the spot.
                double shortfall = 0;
                for (double t = arrival - 420; t <= arrival; t += 6)
                {
                    if (t <= bestUT) continue;
                    Vector3d ship = descent.getRelativePositionAtUT(t).xzy;
                    Vector3d ground = GroundDirectionAfter(body, ship.normalized, -(t - now));
                    Vector3d world = body.position + ground * body.Radius;
                    double under = Math.Max(0, body.TerrainAltitude(body.GetLatitude(world), body.GetLongitude(world), true));
                    double gap = ship.magnitude - body.Radius - under;
                    if (gap < 1000) shortfall = Math.Max(shortfall, 1000 - gap);
                }
                if (shortfall <= 0) break;
                periapsisAltitude += shortfall + 300;
                if (periapsisAltitude > current.PeA - 300) break;
            }
            return plan;
        }
    }
}
