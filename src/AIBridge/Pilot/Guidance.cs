using System;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>Reference directions and angles used by both the autopilot and the telemetry reports.</summary>
    public static class Guidance
    {
        public const double G0 = 9.80665;

        public static Vector3d Up(Vessel v) => (v.CoMD - v.mainBody.position).normalized;

        public static Vector3d North(Vessel v)
        {
            Vector3d up = Up(v);
            Vector3d pole = v.mainBody.position + (Vector3d)v.mainBody.transform.up * v.mainBody.Radius;
            return Vector3d.Exclude(up, pole - v.CoMD).normalized;
        }

        public static Vector3d East(Vessel v)
        {
            Vector3d spin = v.mainBody.getRFrmVel(v.CoMD);
            Vector3d up = Up(v);
            if (spin.sqrMagnitude > 1e-6) return Vector3d.Exclude(up, spin).normalized;
            return Vector3d.Cross(up, North(v)).normalized;
        }

        public static Vector3d FromPitchHeading(Vessel v, double pitchDeg, double headingDeg)
        {
            double p = pitchDeg * Math.PI / 180.0, h = headingDeg * Math.PI / 180.0;
            return (Math.Cos(p) * (Math.Cos(h) * North(v) + Math.Sin(h) * East(v)) + Math.Sin(p) * Up(v)).normalized;
        }

        public static Vector3d RadialOut(Vessel v)
        {
            Vector3d r = Vector3d.Exclude(v.obt_velocity, Up(v));
            return r.sqrMagnitude > 1e-9 ? r.normalized : Up(v);
        }

        public static Vector3d OrbitNormal(Vessel v) => -Vector3d.Cross(RadialOut(v), v.obt_velocity.normalized);

        /// <summary>World direction the nose should point for the autopilot's current mode; zero when undefined.</summary>
        public static Vector3d Direction(Vessel v, Autopilot ap)
        {
            switch (ap.Mode)
            {
                case AttitudeMode.Prograde: return v.obt_velocity.normalized;
                case AttitudeMode.Retrograde: return -v.obt_velocity.normalized;
                case AttitudeMode.Normal: return OrbitNormal(v);
                case AttitudeMode.AntiNormal: return -OrbitNormal(v);
                case AttitudeMode.RadialOut: return RadialOut(v);
                case AttitudeMode.RadialIn: return -RadialOut(v);
                case AttitudeMode.Up: return Up(v);
                case AttitudeMode.SurfacePrograde: return v.srfSpeed > 0.5 ? v.srf_velocity.normalized : Up(v);
                case AttitudeMode.SurfaceRetrograde: return v.srfSpeed > 0.5 ? -v.srf_velocity.normalized : Up(v);
                case AttitudeMode.PitchHeading: return FromPitchHeading(v, ap.Pitch, ap.Heading);
                case AttitudeMode.Fixed: return ap.FixedDirection;
                case AttitudeMode.Target:
                case AttitudeMode.AntiTarget:
                {
                    ITargetable t = v.targetObject;
                    if (t == null || t.GetTransform() == null) return Vector3d.zero;
                    Vector3d d = ((Vector3d)t.GetTransform().position - v.CoMD).normalized;
                    return ap.Mode == AttitudeMode.Target ? d : -d;
                }
                case AttitudeMode.TargetPrograde:
                case AttitudeMode.TargetRetrograde:
                {
                    ITargetable t = v.targetObject;
                    if (t == null) return Vector3d.zero;
                    Vector3d rel = v.obt_velocity - t.GetObtVelocity();
                    if (rel.sqrMagnitude < 1e-4) return Vector3d.zero;
                    return ap.Mode == AttitudeMode.TargetPrograde ? rel.normalized : -rel.normalized;
                }
                case AttitudeMode.Maneuver:
                {
                    PatchedConicSolver solver = v.patchedConicSolver;
                    if (solver == null || solver.maneuverNodes.Count == 0) return Vector3d.zero;
                    Vector3d burn = solver.maneuverNodes[0].GetBurnVector(v.orbit);
                    return burn.sqrMagnitude > 1e-6 ? burn.normalized : Vector3d.zero;
                }
                default: return Vector3d.zero;
            }
        }

        /// <summary>Navball angles: pitch above the horizon, compass heading, roll.</summary>
        public static void Attitude(Vessel v, out double pitch, out double heading, out double roll)
        {
            Quaternion surface = Quaternion.LookRotation((Vector3)North(v), (Vector3)Up(v));
            Quaternion rel = Quaternion.Inverse(Quaternion.Euler(90f, 0f, 0f) * Quaternion.Inverse(v.ReferenceTransform.rotation) * surface);
            Vector3 e = rel.eulerAngles;
            heading = e.y;
            pitch = e.x > 180f ? 360f - e.x : -e.x;
            roll = e.z > 180f ? e.z - 360f : e.z;
        }

        public static double LocalGravity(Vessel v)
        {
            double r = (v.CoMD - v.mainBody.position).magnitude;
            return v.mainBody.gravParameter / (r * r);
        }

        /// <summary>Thrust the currently ignited engines produce at full throttle here, and their combined Isp.</summary>
        public static void EngineStats(Vessel v, out double maxThrust, out double currentThrust, out double isp, out int burning, out int flamedOut)
        {
            maxThrust = currentThrust = 0;
            burning = flamedOut = 0;
            double flow = 0;
            double pressureAtm = v.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres;
            foreach (Part p in v.parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleEngines e) || !e.EngineIgnited || !e.isEnabled) continue;
                    if (e.flameout) { flamedOut++; continue; }
                    burning++;
                    double ispHere = e.atmosphereCurve.Evaluate((float)pressureAtm);
                    double ispVac = e.atmosphereCurve.Evaluate(0f);
                    if (ispHere <= 0 || ispVac <= 0) continue;
                    double thrust = e.maxThrust * (e.thrustPercentage / 100.0) * (ispHere / ispVac);
                    maxThrust += thrust;
                    flow += thrust / (ispHere * G0);
                    currentThrust += e.finalThrust;
                }
            }
            isp = flow > 0 ? maxThrust / (flow * G0) : 0;
        }

        /// <summary>
        /// The engines' greatest thrust, counted as EngineStats counts it, at another air pressure (atmospheres): what they will
        /// give lower down. An engine's thrust goes with its Isp at the pressure it works in; one made for a vacuum gives a quarter
        /// of its thrust at Kerbin's sea level (the Terrier: 14.8 kN of 60).
        /// </summary>
        public static double ThrustAt(Vessel v, double pressureAtm)
        {
            double sum = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleEngines e) || !e.EngineIgnited || !e.isEnabled || e.flameout) continue;
                    double ispThere = e.atmosphereCurve.Evaluate((float)pressureAtm), ispVac = e.atmosphereCurve.Evaluate(0f);
                    if (ispThere <= 0 || ispVac <= 0) continue;
                    sum += e.maxThrust * (e.thrustPercentage / 100.0) * (ispThere / ispVac);
                }
            return sum;
        }

        /// <summary>Seconds of full-throttle burn needed for a velocity change, from the rocket equation.</summary>
        public static double BurnTime(Vessel v, double deltaV)
        {
            EngineStats(v, out double thrust, out double _, out double isp, out int _, out int _);
            if (thrust <= 0 || isp <= 0) return double.NaN;
            double ve = isp * G0;
            double m0 = v.totalMass;
            return m0 * ve / thrust * (1.0 - Math.Exp(-deltaV / ve));
        }
    }
}
