using System;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>Aerodynamic estimates taken from the same numbers the game's lift model uses.</summary>
    public static class Aero
    {
        public const double Rad2Deg = 180.0 / Math.PI, Deg2Rad = Math.PI / 180.0;

        /// <summary>Total lift rating of the surfaces that hold the aircraft up (wings and elevons, not vertical fins).</summary>
        public static double WingRating(Vessel v, out FloatCurve liftCurve)
        {
            liftCurve = null;
            double total = 0;
            Vector3 top = -v.ReferenceTransform.forward;
            foreach (Part p in v.parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleLiftingSurface ls) || ls.deflectionLiftCoeff <= 0f) continue;
                    Transform t = p.transform;
                    Vector3 lift = ls.transformDir == ModuleLiftingSurface.TransformDir.Z ? t.forward : ls.transformDir == ModuleLiftingSurface.TransformDir.Y ? t.up : t.right;
                    total += ls.deflectionLiftCoeff * Mathf.Abs(Vector3.Dot(lift, top));
                    if (liftCurve == null && ls.liftCurve != null && !(ls is ModuleControlSurface)) liftCurve = ls.liftCurve;
                    else if (liftCurve == null) liftCurve = ls.liftCurve;
                }
            }
            return total;
        }

        /// <summary>Slowest speed at which the wings carry the aircraft's weight at the given angle of attack, here and now.</summary>
        public static double LiftSpeed(Vessel v, double aoaDeg)
        {
            double rating = WingRating(v, out FloatCurve curve);
            if (rating < 0.05 || curve == null) return double.NaN;
            double cl = curve.Evaluate((float)Math.Sin(aoaDeg * Deg2Rad));
            if (cl <= 0.01) return double.NaN;
            double weight = v.totalMass * Guidance.LocalGravity(v);
            double q = weight / (PhysicsGlobals.LiftMultiplier * cl * rating);
            double density = v.atmDensity > 0.01 ? v.atmDensity : v.mainBody.atmosphere ? v.mainBody.GetDensity(v.mainBody.GetPressure(0), v.mainBody.GetTemperature(0)) : 0;
            if (density <= 0.001) return double.NaN;
            return Math.Sqrt(2.0 * q / density);
        }

        /// <summary>The craft's velocity through the air: over the ground less the wind (the same as over the ground without a weather mod).</summary>
        public static Vector3d AirVelocity(Vessel v) => v.srf_velocity - Api.WeatherApi.WindOn(v);

        /// <summary>Speed through the air, which is what the wings feel: m/s.</summary>
        public static double Airspeed(Vessel v) => AirVelocity(v).magnitude;

        /// <summary>Angle of attack in degrees, measured in the aircraft's own vertical plane, against the air.</summary>
        public static double AngleOfAttack(Vessel v)
        {
            Vector3d air = AirVelocity(v);
            if (air.magnitude < 12) return 0;
            Transform t = v.ReferenceTransform;
            return Math.Atan2(Vector3d.Dot(air, t.forward), Vector3d.Dot(air, t.up)) * Rad2Deg;
        }

        /// <summary>Degrees of right bank: positive with the right wing below the horizon. NaN when pointing straight up or down.</summary>
        public static double Bank(Vessel v)
        {
            Transform t = v.ReferenceTransform;
            Vector3d up = Guidance.Up(v), nose = t.up;
            Vector3d levelRight = Vector3d.Cross(up, nose);
            if (levelRight.sqrMagnitude < 0.03) return double.NaN;
            levelRight = levelRight.normalized;
            Vector3d levelUp = Vector3d.Cross(nose, levelRight).normalized;
            return Math.Atan2(-Vector3d.Dot(t.right, levelUp), Vector3d.Dot(t.right, levelRight)) * Rad2Deg;
        }

        public static double HeadingOf(Vessel v, Vector3d horizontal) =>
            (Math.Atan2(Vector3d.Dot(horizontal, Guidance.East(v)), Vector3d.Dot(horizontal, Guidance.North(v))) * Rad2Deg + 360.0) % 360.0;

        /// <summary>Compass direction the nose points.</summary>
        public static double NoseHeading(Vessel v) => HeadingOf(v, Vector3d.Exclude(Guidance.Up(v), v.ReferenceTransform.up));

        /// <summary>Compass direction of travel over the ground, falling back to the nose when nearly stopped.</summary>
        public static double Track(Vessel v)
        {
            Vector3d h = Vector3d.Exclude(Guidance.Up(v), v.srf_velocity);
            return h.magnitude > 8 ? HeadingOf(v, h) : NoseHeading(v);
        }

        /// <summary>Compass direction of travel through the air (the way the airflow comes from, turned round): in a crosswind it is off the track over the ground.</summary>
        public static double AirTrack(Vessel v)
        {
            Vector3d h = Vector3d.Exclude(Guidance.Up(v), AirVelocity(v));
            return h.magnitude > 8 ? HeadingOf(v, h) : NoseHeading(v);
        }

        public static double Pitch(Vessel v) => 90.0 - Vector3d.Angle(v.ReferenceTransform.up, Guidance.Up(v));

        public static double Wrap(double deg)
        {
            while (deg > 180) deg -= 360;
            while (deg < -180) deg += 360;
            return deg;
        }

        /// <summary>Height of the lowest wheel above whatever is underneath (runway included).</summary>
        public static double HeightAboveSurface(Vessel v)
        {
            double h = v.heightFromTerrain >= 0 ? v.heightFromTerrain : v.radarAltitude;
            return h;
        }
    }
}
