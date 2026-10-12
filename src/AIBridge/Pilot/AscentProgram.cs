using System;
using KSP.UI.Screens;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Launch to a circular orbit: straight up, a gravity turn that follows a pitch-versus-altitude curve
    /// while staying close to the airflow, engine cut-off when the apoapsis reaches the target, coast,
    /// then a horizontal burn around apoapsis. Staging is automatic throughout.
    /// </summary>
    public sealed class AscentProgram : Program
    {
        public override string Name => "ascent to " + Math.Round(TargetAltitude / 1000.0) + " km orbit";

        public double TargetAltitude = 80000;
        public double Inclination;
        public double TurnStartSpeed = 60;
        public double TurnEndAltitude = double.NaN;
        public double TurnShape = 0.45;
        public double MaxAoA = 5;
        public double MaxQ = 45;
        public bool UseWarp = true;

        enum State { Prelaunch, Vertical, Turn, CoastInAtmosphere, CoastToApoapsis, Circularize }
        State state = State.Prelaunch;

        double heading = 90, turnStartAltitude, stateSince, startedAt = -1, lastAp, lastApUT, apRate, startMass;
        int liftoffStagings;
        double noThrustSince = -1;
        bool warping;

        public override void Tick(Autopilot ap, Vessel v)
        {
            CelestialBody body = v.mainBody;
            double ut = Planetarium.GetUniversalTime();
            if (startedAt < 0) Begin(ap, v, ut);
            Orbit orbit = v.orbit;
            double alt = v.altitude;
            bool hasAir = body.atmosphere && alt < body.atmosphereDepth;

            if (ut - lastApUT > 0.25)
            {
                double rate = (orbit.ApA - lastAp) / (ut - lastApUT);
                apRate = apRate * 0.5 + rate * 0.5;
                lastAp = orbit.ApA;
                lastApUT = ut;
            }

            if (state != State.Prelaunch && (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED) && ut - startedAt > 10)
            {
                Fail("the vessel is back on the surface");
                return;
            }
            if (!v.IsControllable && state != State.Prelaunch)
            {
                Fail("lost control of the vessel (no crew, power or signal)");
                return;
            }

            switch (state)
            {
                case State.Prelaunch:
                    Phase = "ignition";
                    ap.SetPitchHeading(90, heading);
                    ap.SetThrottle(1);
                    if (v.srfSpeed > 1.0 && v.verticalSpeed > 0.5) { Enter(State.Vertical, ut); break; }
                    if (ut - stateSince > 1.0 + liftoffStagings * 3.0)
                    {
                        if (liftoffStagings >= 4 || v.currentStage <= 0)
                        {
                            Guidance.EngineStats(v, out double thrust, out double _, out double _, out int _, out int _);
                            Fail("could not lift off (thrust " + Math.Round(thrust) + " kN for " + Math.Round(v.totalMass, 1) + " t; TWR " + Math.Round(thrust / (v.totalMass * Guidance.LocalGravity(v)), 2) + ")");
                            break;
                        }
                        if (StageManager.CanSeparate) { ap.Stager.Fire(ap, "ignition"); liftoffStagings++; }
                    }
                    break;

                case State.Vertical:
                    Phase = "vertical climb";
                    ap.SetPitchHeading(90, heading);
                    ap.SetThrottle(ThrottleFor(v, orbit, 1.0));
                    if (v.srfSpeed >= TurnStartSpeed || (!body.atmosphere && alt - turnStartAltitude > 50))
                    {
                        turnStartAltitude = alt;
                        Enter(State.Turn, ut);
                    }
                    break;

                case State.Turn:
                {
                    double frac = OrbitMath.Clamp((alt - turnStartAltitude) / Math.Max(1.0, TurnEndAltitude - turnStartAltitude), 0, 1);
                    double pitch = 90.0 * (1.0 - Math.Pow(frac, TurnShape));
                    Phase = "gravity turn, pitch " + Math.Round(pitch) + "°";
                    Vector3d wanted = Guidance.FromPitchHeading(v, pitch, heading);
                    if (hasAir && v.srfSpeed > 40 && v.dynamicPressurekPa > 0.5)
                    {
                        // Stay near the airflow while the air is thick, or the rocket tumbles. (The airflow: the craft's
                        // own motion through the air, which with a weather mod's wind is not its motion over the ground.)
                        Vector3d wind = (v.srf_velocity - Api.WeatherApi.WindOn(v)).normalized;
                        double limit = MaxAoA * OrbitMath.Clamp(15.0 / v.dynamicPressurekPa, 1.0, 9.0);
                        double off = Vector3d.Angle(wanted, wind);
                        if (off > limit) wanted = OrbitMath.Slerp(wind, wanted, limit / off);
                    }
                    // Dropping boosters in thick air: fly dead straight into the airflow and keep accelerating,
                    // otherwise the spent boosters slide back into the core.
                    bool separating = hasAir && v.dynamicPressurekPa > 3.0 && v.srfSpeed > 40 &&
                                      (ap.Stager.Pending || ap.Stager.SecondsSinceStaging < 1.5 || AutoStager.TimeToBurnout(v) < 1.5);
                    if (separating) wanted = (v.srf_velocity - Api.WeatherApi.WindOn(v)).normalized;
                    ap.FixedDirection = wanted;
                    ap.SetAttitude(AttitudeMode.Fixed);

                    if (orbit.ApA >= TargetAltitude)
                    {
                        ap.SetThrottle(0);
                        Enter(hasAir ? State.CoastInAtmosphere : State.CoastToApoapsis, ut);
                        break;
                    }
                    double timeToTarget = (TargetAltitude - orbit.ApA) / Math.Max(apRate, 1.0);
                    double throttle = OrbitMath.Clamp(timeToTarget / 2.5, 0.05, 1.0);
                    ap.SetThrottle(separating ? throttle : ThrottleFor(v, orbit, throttle));
                    CheckPropulsion(ap, v, ut, "during the gravity turn");
                    break;
                }

                case State.CoastInAtmosphere:
                    Phase = "coasting out of the atmosphere";
                    ap.SetAttitude(v.altitude > body.atmosphereDepth * 0.6 ? AttitudeMode.Prograde : AttitudeMode.SurfacePrograde);
                    if (orbit.ApA < TargetAltitude - 300)
                    {
                        // Drag pulled the apoapsis down: nudge it back up.
                        ap.SetAttitude(AttitudeMode.Prograde);
                        ap.SetThrottle(ap.AngleError < 10 ? OrbitMath.Clamp((TargetAltitude - orbit.ApA) / 4000.0, 0.05, 0.5) : 0);
                        CheckPropulsion(ap, v, ut, "while holding the apoapsis");
                    }
                    else if (orbit.ApA > TargetAltitude + 100) ap.SetThrottle(0);
                    SetPhysicsWarp(alt > body.atmosphereDepth * 0.55 && ap.Throttle.GetValueOrDefault() == 0 && orbit.timeToAp > 25);
                    if (alt >= body.atmosphereDepth)
                    {
                        SetPhysicsWarp(false);
                        ap.SetThrottle(0);
                        Enter(State.CoastToApoapsis, ut);
                    }
                    else if (v.verticalSpeed < 0 && orbit.ApA < body.atmosphereDepth)
                        Fail("fell back before reaching space (apoapsis " + Math.Round(orbit.ApA / 1000, 1) + " km)");
                    break;

                case State.CoastToApoapsis:
                {
                    double burn = CircularizeBurnTime(v, orbit, out double dv);
                    Phase = "coasting to apoapsis, " + Math.Round(orbit.timeToAp) + " s (burn " + Math.Round(dv) + " m/s)";
                    ap.FixedDirection = HorizontalPrograde(v);
                    ap.SetAttitude(AttitudeMode.Fixed);
                    ap.SetThrottle(0);
                    if (double.IsNaN(burn))
                    {
                        // No running engine: let the stager light the next stage, or give up when nothing is left.
                        CheckPropulsion(ap, v, ut, "before the circularisation burn");
                        if (!Done && noThrustSince >= 0 && ut - noThrustSince > 1.5 && StageManager.CanSeparate && v.currentStage > 0) ap.Stager.Fire(ap, "ignite upper stage");
                        break;
                    }
                    noThrustSince = -1;
                    double lead = burn / 2.0 + 1.0;
                    bool pastApoapsis = v.verticalSpeed < 0 && orbit.timeToAp > orbit.period / 2.0;
                    if (orbit.timeToAp <= lead || pastApoapsis)
                    {
                        StopWarp();
                        Enter(State.Circularize, ut);
                        break;
                    }
                    double wait = orbit.timeToAp - lead;
                    if (UseWarp && !warping && wait > 40 && ap.AngleError < 5)
                    {
                        TimeWarp.fetch.WarpTo(ut + wait - 12);
                        warping = true;
                    }
                    break;
                }

                case State.Circularize:
                {
                    double r = (v.CoMD - body.position).magnitude;
                    Vector3d up = Guidance.Up(v);
                    Vector3d horizontal = HorizontalPrograde(v);
                    double horizontalSpeed = Vector3d.Dot(v.obt_velocity, horizontal);
                    double remaining = OrbitMath.CircularSpeed(body, r) - horizontalSpeed;
                    Phase = "circularising, " + Math.Round(Math.Max(0, remaining)) + " m/s to go";
                    // Lean against any climb or sink so the orbit ends up round rather than tilted.
                    double lean = OrbitMath.Clamp(-v.verticalSpeed / 60.0, -0.3, 0.3);
                    ap.FixedDirection = (horizontal + up * lean).normalized;
                    ap.SetAttitude(AttitudeMode.Fixed);

                    Guidance.EngineStats(v, out double maxThrust, out double _, out double _, out int burning, out int _);
                    double accel = maxThrust / Math.Max(1e-6, v.totalMass);
                    bool aligned = double.IsNaN(ap.AngleError) || ap.AngleError < 12;
                    ap.SetThrottle(aligned ? OrbitMath.Clamp(remaining / Math.Max(0.1, accel * 1.2), 0.03, 1.0) : 0.0);

                    if (remaining <= 0.3 || (orbit.PeA > TargetAltitude - 1500 && orbit.eccentricity < 0.002))
                    {
                        ap.SetThrottle(0);
                        Finish("in orbit around " + body.bodyName + ": apoapsis " + Math.Round(orbit.ApA / 1000, 1) + " km, periapsis " + Math.Round(orbit.PeA / 1000, 1) + " km, inclination " + Math.Round(orbit.inclination, 2) + "°");
                        break;
                    }
                    CheckPropulsion(ap, v, ut, "during circularisation (periapsis " + Math.Round(orbit.PeA / 1000, 1) + " km)");
                    break;
                }
            }
        }

        void Begin(Autopilot ap, Vessel v, double ut)
        {
            startedAt = stateSince = ut;
            startMass = v.totalMass;
            CelestialBody body = v.mainBody;
            lastAp = v.orbit.ApA;
            lastApUT = ut;
            turnStartAltitude = v.altitude;
            if (double.IsNaN(TurnEndAltitude))
                TurnEndAltitude = body.atmosphere ? body.atmosphereDepth * 0.72 : Math.Min(TargetAltitude * 0.6, Math.Max(6000, body.Radius * 0.04));
            if (!body.atmosphere) TurnStartSpeed = Math.Min(TurnStartSpeed, 15);
            heading = LaunchHeading(v, Inclination, TargetAltitude);
            ap.Stager.Enabled = true;
            if (v.situation != Vessel.Situations.PRELAUNCH && v.situation != Vessel.Situations.LANDED && v.situation != Vessel.Situations.SPLASHED)
            {
                // Started in mid-air: join the profile where the vessel already is.
                turnStartAltitude = 0;
                state = State.Turn;
            }
        }

        void Enter(State next, double ut)
        {
            state = next;
            stateSince = ut;
            noThrustSince = -1;
        }

        /// <summary>Compass heading at lift-off that ends up at the requested inclination, allowing for the planet's spin.</summary>
        static double LaunchHeading(Vessel v, double inclination, double targetAltitude)
        {
            CelestialBody body = v.mainBody;
            double lat = v.latitude * Math.PI / 180.0;
            double inc = Math.Abs(inclination) * Math.PI / 180.0;
            double cosRatio = Math.Cos(inc) / Math.Max(1e-6, Math.Cos(lat));
            double beta = Math.Abs(cosRatio) >= 1.0 ? (cosRatio > 0 ? Math.PI / 2 : -Math.PI / 2) : Math.Asin(cosRatio);
            if (inclination < 0) beta = Math.PI - beta;
            double orbitSpeed = OrbitMath.CircularSpeed(body, body.Radius + targetAltitude);
            double spin = body.rotates && body.rotationPeriod != 0 ? 2.0 * Math.PI * body.Radius * Math.Cos(lat) / body.rotationPeriod : 0;
            double east = orbitSpeed * Math.Sin(beta) - spin;
            double north = orbitSpeed * Math.Cos(beta);
            double h = Math.Atan2(east, north) * 180.0 / Math.PI;
            return (h + 360.0) % 360.0;
        }

        double ThrottleFor(Vessel v, Orbit orbit, double wanted)
        {
            if (MaxQ > 0 && v.dynamicPressurekPa > MaxQ)
                wanted = Math.Min(wanted, OrbitMath.Clamp(1.0 - (v.dynamicPressurekPa - MaxQ) / (0.35 * MaxQ), 0.35, 1.0));
            return wanted;
        }

        static Vector3d HorizontalPrograde(Vessel v)
        {
            Vector3d h = Vector3d.Exclude(Guidance.Up(v), v.obt_velocity);
            return h.sqrMagnitude > 1e-6 ? h.normalized : Guidance.East(v);
        }

        double CircularizeBurnTime(Vessel v, Orbit orbit, out double dv)
        {
            CelestialBody body = v.mainBody;
            double rAp = orbit.ApR;
            dv = Math.Max(0, OrbitMath.CircularSpeed(body, rAp) - OrbitMath.SpeedAt(body, rAp, orbit.semiMajorAxis));
            return Guidance.BurnTime(v, dv);
        }

        /// <summary>Fail the program once thrust is needed but nothing is burning and no stage can fix that.</summary>
        void CheckPropulsion(Autopilot ap, Vessel v, double ut, string when)
        {
            Guidance.EngineStats(v, out double _, out double _, out double _, out int burning, out int _);
            if (burning > 0) { noThrustSince = -1; return; }
            if (noThrustSince < 0) { noThrustSince = ut; return; }
            if (ut - noThrustSince < 6.0) return;
            Orbit o = v.orbit;
            Fail("out of propellant or engines " + when + ": apoapsis " + Math.Round(o.ApA / 1000, 1) + " km, periapsis " + Math.Round(o.PeA / 1000, 1) + " km, stage " + v.currentStage);
        }

        void SetPhysicsWarp(bool on)
        {
            if (!UseWarp) return;
            int wanted = on ? 2 : 0;
            if (TimeWarp.WarpMode == TimeWarp.Modes.LOW && TimeWarp.CurrentRateIndex != wanted) TimeWarp.SetRate(wanted, true, false);
        }

        void StopWarp()
        {
            if (warping || TimeWarp.CurrentRateIndex != 0)
            {
                TimeWarp.fetch.CancelAutoWarp();
                TimeWarp.SetRate(0, true, false);
            }
            warping = false;
        }

        public override JObj Detail()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return null;
            return new JObj()
                .Add("target_altitude_m", TargetAltitude)
                .Add("heading", Math.Round(heading, 1))
                .Add("apoapsis_m", Math.Round(v.orbit.ApA))
                .Add("periapsis_m", Math.Round(v.orbit.PeA))
                .Add("altitude_m", Math.Round(v.altitude));
        }

        public override void OnEnd(Autopilot ap)
        {
            StopWarp();
            if (!ap.HandingOver) ap.SetThrottle(0);
            ap.Throttle = null;
            ap.Stager.Enabled = false;
            ap.SetAttitude(AttitudeMode.Off);
        }
    }
}
