using System;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Orbit helpers in world axes. KSP's Orbit class returns vectors with Y and Z swapped, hence the ".xzy".
    /// Conventions follow the stock maneuver gizmo: node delta-v is (radial-out, normal, prograde).
    /// </summary>
    public static class OrbitMath
    {
        public static Vector3d Velocity(Orbit o, double ut) => o.getOrbitalVelocityAtUT(ut).xzy;
        public static Vector3d Position(Orbit o, double ut) => o.getRelativePositionAtUT(ut).xzy;
        public static Vector3d Prograde(Orbit o, double ut) => Velocity(o, ut).normalized;
        public static Vector3d Up(Orbit o, double ut) => Position(o, ut).normalized;
        public static Vector3d RadialOut(Orbit o, double ut) => Vector3d.Exclude(Prograde(o, ut), Up(o, ut)).normalized;
        public static Vector3d Horizontal(Orbit o, double ut) => Vector3d.Exclude(Up(o, ut), Prograde(o, ut)).normalized;
        /// <summary>Unit vector along the gizmo's "normal" handle.</summary>
        public static Vector3d Normal(Orbit o) => o.GetOrbitNormal().xzy.normalized;

        public static Vector3d ToNode(Orbit o, double ut, Vector3d worldDv) =>
            new Vector3d(Vector3d.Dot(RadialOut(o, ut), worldDv), Vector3d.Dot(Normal(o), worldDv), Vector3d.Dot(Prograde(o, ut), worldDv));

        public static double Radius(Orbit o, double ut) => Position(o, ut).magnitude;

        public static double CircularSpeed(CelestialBody body, double radius) => Math.Sqrt(body.gravParameter / radius);

        /// <summary>Speed on an orbit with the given semi-major axis at the given radius (vis-viva).</summary>
        public static double SpeedAt(CelestialBody body, double radius, double sma) => Math.Sqrt(Math.Max(0, body.gravParameter * (2.0 / radius - 1.0 / sma)));

        /// <summary>Delta-v (world) that makes the orbit circular at <paramref name="ut"/>.</summary>
        public static Vector3d CircularizeDv(Orbit o, double ut)
        {
            double r = Radius(o, ut);
            return CircularSpeed(o.referenceBody, r) * Horizontal(o, ut) - Velocity(o, ut);
        }

        /// <summary>Delta-v (world) at <paramref name="ut"/> that moves the opposite apsis to <paramref name="newApsisRadius"/>.</summary>
        public static Vector3d ChangeApsisDv(Orbit o, double ut, double newApsisRadius)
        {
            double r = Radius(o, ut);
            double sma = (r + newApsisRadius) / 2.0;
            double speed = SpeedAt(o.referenceBody, r, sma);
            return speed * Horizontal(o, ut) - Velocity(o, ut);
        }

        public static double NextApoapsisUT(Orbit o, double ut) => o.eccentricity < 1.0 ? o.GetNextApoapsisTime(ut) : double.NaN;
        public static double NextPeriapsisUT(Orbit o, double ut) => o.GetNextPeriapsisTime(ut);

        /// <summary>Angle in degrees between two orbital planes.</summary>
        public static double RelativeInclination(Orbit a, Orbit b) => Vector3d.Angle(a.GetOrbitNormal(), b.GetOrbitNormal());

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>
        /// Smallest distance between two orbits around the same body during [fromUT, fromUT + span],
        /// found by sampling and then narrowing in on the best sample.
        /// </summary>
        public static double ClosestApproach(Orbit a, Orbit b, double fromUT, double span, out double atUT)
        {
            atUT = fromUT;
            if (span <= 0 || double.IsNaN(span) || double.IsInfinity(span)) return (a.getRelativePositionAtUT(fromUT) - b.getRelativePositionAtUT(fromUT)).magnitude;
            const int samples = 180;
            double best = double.MaxValue;
            for (int n = 0; n <= samples; n++)
            {
                double t = fromUT + span * n / samples;
                double d = (a.getRelativePositionAtUT(t) - b.getRelativePositionAtUT(t)).sqrMagnitude;
                if (d < best) { best = d; atUT = t; }
            }
            double lo = Math.Max(fromUT, atUT - span / samples), hi = Math.Min(fromUT + span, atUT + span / samples);
            for (int n = 0; n < 36; n++)
            {
                double m1 = lo + (hi - lo) / 3.0, m2 = hi - (hi - lo) / 3.0;
                double d1 = (a.getRelativePositionAtUT(m1) - b.getRelativePositionAtUT(m1)).sqrMagnitude;
                double d2 = (a.getRelativePositionAtUT(m2) - b.getRelativePositionAtUT(m2)).sqrMagnitude;
                if (d1 < d2) hi = m2; else lo = m1;
            }
            atUT = (lo + hi) / 2.0;
            return (a.getRelativePositionAtUT(atUT) - b.getRelativePositionAtUT(atUT)).magnitude;
        }

        /// <summary>Spherical interpolation between two directions. (KSP's own Vector3d.Slerp is a dead native stub.)</summary>
        public static Vector3d Slerp(Vector3d from, Vector3d to, double t)
        {
            from = from.normalized;
            to = to.normalized;
            double theta = Math.Acos(Clamp(Vector3d.Dot(from, to), -1.0, 1.0));
            if (theta < 1e-6) return to;
            double s = Math.Sin(theta);
            return (from * (Math.Sin((1.0 - t) * theta) / s) + to * (Math.Sin(t * theta) / s)).normalized;
        }
    }
}
