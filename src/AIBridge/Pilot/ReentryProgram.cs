using System;
using System.Collections.Generic;
using AIBridge.Api;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Landing on a world with air: de-orbit if needed, drop the parts that are not coming along, hold the
    /// heat shield into the wind, open the parachutes the moment it is safe to, and finish on the engine
    /// when parachutes alone would hit too hard (or there are none).
    /// </summary>
    public sealed class ReentryProgram : Program
    {
        public override string Name => "atmospheric landing";

        /// <summary>true/false, or null to decide from the craft: a capsule sheds its service module, a lander keeps everything.</summary>
        public bool? StageBeforeEntry;
        public bool UseChutes = true, UseWarp = true, AllowEngines = true;
        public double TouchdownSpeed = 2.0;
        /// <summary>The de-orbit burn, when the vessel starts in orbit.</summary>
        public Program Prelude;
        /// <summary>
        /// A spot the de-orbit burn was aimed at. Everything after the burn then has to be predictable:
        /// the service module is dropped sideways-on so that its push does not shift the landing point,
        /// and the parachutes wait until the capsule is low and falling straight.
        /// </summary>
        public bool HasTarget;
        public double TargetLat, TargetLon;
        public string TargetLabel = "the target";
        public double PredictedMiss = double.NaN;
        /// <summary>
        /// Aiming through air too thin to slow the vessel (Duna): the engine brakes it to a stop over the
        /// spot, as on an airless world, and the parachutes open for the last stretch straight down.
        /// </summary>
        public bool PoweredAim;
        public double ArriveHeight = 2500;

        enum State { Coast, Entry, BrakeForChutes, Powered, Settled }
        State state = State.Coast;
        bool started, stagerWasOn, entryLogged, chutesLogged, railsWarping, staged, physicsWarping, stagingAttitude;
        double stageTurnSince = -1;
        double lastStageUT = -100, settledSince = -1, peakG, touchdownSpeed = double.NaN, lastSpeed, startUT;
        int chuteCount, partsAtStart;
        LandProgram powered;

        static List<ModuleParachute> Chutes(Vessel v)
        {
            var list = new List<ModuleParachute>();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleParachute c) list.Add(c);
            return list;
        }

        static bool HasLegsOrWheels(Vessel v)
        {
            foreach (Part p in v.parts) if (p.FindModuleImplementing<ModuleWheelBase>() != null) return true;
            return false;
        }

        public static bool HasUsableEngine(Vessel v)
        {
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleEngines e && e.isEnabled && !e.flameout && !e.throttleLocked && e.maxThrust > 0)
                    {
                        // A dry engine is no use: check the first propellant can actually be drawn.
                        bool dry = false;
                        foreach (Propellant pr in e.propellants) if (pr.totalResourceAvailable <= 0.01 && !pr.ignoreForIsp) { dry = true; break; }
                        if (!dry || e.EngineIgnited) return true;
                    }
            return false;
        }

        /// <summary>Acceleration every usable engine could give at full throttle in the air here, lit or not.</summary>
        public static double PotentialThrustAccel(Vessel v)
        {
            double thrust = 0;
            double pressure = v.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleEngines e && e.isEnabled && !e.flameout && !e.throttleLocked && e.maxThrust > 0)
                    {
                        double vac = e.atmosphereCurve.Evaluate(0f), here = e.atmosphereCurve.Evaluate((float)pressure);
                        if (vac > 0 && here > 0) thrust += e.maxThrust * (e.thrustPercentage / 100.0) * (here / vac);
                    }
            return thrust / Math.Max(1e-6, v.totalMass);
        }

        /// <summary>True when a parachute is still packed and would be torn off if opened at this speed.</summary>
        bool ChutesWaitingForSpeed(Vessel v)
        {
            if (!UseChutes) return false;
            foreach (ModuleParachute c in Chutes(v))
                if (c.deploymentState == ModuleParachute.deploymentStates.STOWED && c.deploymentSafeState != ModuleParachute.deploymentSafeStates.SAFE) return true;
            return false;
        }

        static void IgniteEngines(Vessel v)
        {
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleEngines e && e.isEnabled && !e.EngineIgnited && !e.throttleLocked && e.maxThrust > 0) e.Activate();
        }

        public override void Tick(Autopilot ap, Vessel v)
        {
            double ut = Planetarium.GetUniversalTime();
            CelestialBody body = v.mainBody;
            if (Prelude != null)
            {
                Prelude.Tick(ap, v);
                Phase = "de-orbit burn: " + Prelude.Phase;
                if (!Prelude.Done) return;
                Prelude.OnEnd(ap);
                if (Prelude.Failed) { Fail("de-orbit burn failed: " + Prelude.Result); return; }
                ap.Log.Add("de-orbit burn done: " + Prelude.Result);
                Prelude = null;
                return;
            }
            if (!started)
            {
                started = true;
                startUT = ut;
                stagerWasOn = ap.Stager.Enabled;
                ap.Stager.Enabled = false;
                partsAtStart = v.parts.Count;
                chuteCount = Chutes(v).Count;
                if (!body.atmosphere) { Fail(Places.Name(body) + " has no atmosphere"); return; }
                if (v.orbit.PeA > body.atmosphereDepth && v.altitude > body.atmosphereDepth)
                {
                    Fail("the periapsis (" + Math.Round(v.orbit.PeA / 1000.0, 1) + " km) is above the atmosphere (" + Math.Round(body.atmosphereDepth / 1000.0) + " km); a de-orbit burn is needed first");
                    return;
                }
                if (chuteCount == 0 && !(AllowEngines && HasUsableEngine(v)))
                    ap.Log.Add("warning: no parachutes and no usable engine; this will be a hard landing");
                // In thin air the stock opening pressure keeps parachutes shut until the last few kilometres;
                // lower it, as a player would with the slider in the editor.
                double surfaceAtm = body.GetPressure(0) * PhysicsGlobals.KpaToAtmospheres;
                if (UseChutes && surfaceAtm < 0.25)
                {
                    int changed = 0;
                    foreach (ModuleParachute c in Chutes(v))
                        if (c.deploymentState == ModuleParachute.deploymentStates.STOWED && c.minAirPressureToOpen > 0.0105f) { c.minAirPressureToOpen = 0.01f; changed++; }
                    if (changed > 0) ap.Log.Add("thin air on " + Places.Name(body) + ": set " + changed + " parachute" + (changed > 1 ? "s" : "") + " to open at 0.01 atm instead of the default");
                }
            }

            double speed = v.srfSpeed;
            if (v.geeForce > peakG) peakG = v.geeForce;
            if (!v.LandedOrSplashed) lastSpeed = speed;
            bool inAir = v.altitude < body.atmosphereDepth;

            if (v.LandedOrSplashed && state != State.Settled)
            {
                state = State.Settled;
                settledSince = ut;
                touchdownSpeed = lastSpeed;
                StopWarp();
                if (powered != null) { powered.OnEnd(ap); powered = null; }
                ap.SetThrottle(0);
            }

            switch (state)
            {
                case State.Coast:
                {
                    double entryIn = TimeToAltitude(v, body.atmosphereDepth);
                    Phase = "coasting to the atmosphere" + (double.IsNaN(entryIn) ? "" : ", entry in " + Math.Round(Math.Max(0, entryIn)) + " s");
                    ap.SetThrottle(0);
                    DropWhatStaysBehind(ap, v, ut, entryIn);
                    if (v.IsControllable && !stagingAttitude) ap.SetAttitude(AttitudeMode.Retrograde);
                    if (UseWarp && !railsWarping && !double.IsNaN(entryIn) && entryIn > 60 && TimeWarp.CurrentRateIndex == 0 && ut - startUT > 2 && ut - lastStageUT > 3 && (staged || !HasTarget))
                    {
                        TimeWarp.fetch.WarpTo(ut + entryIn - 25);
                        railsWarping = true;
                    }
                    if (inAir) { state = State.Entry; StopWarp(); }
                    break;
                }

                case State.Entry:
                {
                    if (!entryLogged) { entryLogged = true; ap.Log.Add("entered the atmosphere at " + Math.Round(speed) + " m/s"); }
                    DropWhatStaysBehind(ap, v, ut, 0);
                    if (v.IsControllable) ap.SetAttitude(AttitudeMode.SurfaceRetrograde);
                    ap.SetThrottle(0);
                    int open = DeployChutes(ap, v);
                    Phase = (open > 0 ? "under " + open + " parachute" + (open > 1 ? "s" : "") : "descending") + ", " + Math.Round(v.altitude) + " m, " + Math.Round(speed) + " m/s";

                    // Gentle time acceleration through the long fall; never while it matters.
                    bool calm = v.dynamicPressurekPa < 4 && v.geeForce < 2.5 && v.radarAltitude > 3000 && open == 0;
                    SetPhysicsWarp(UseWarp && calm);

                    // Engines: needed when there are no parachutes, when the parachutes will not slow it
                    // enough, when it is still too fast for them to open with the ground coming up (thin air),
                    // and as a last resort whenever the ground is nearer than the distance it takes to stop.
                    if (PoweredAim && AllowEngines && powered == null && HasUsableEngine(v) &&
                        LandProgram.NearBrakingPoint(v, TargetLat, TargetLon, PotentialThrustAccel(v), 40, out double toSpot))
                    {
                        SetPhysicsWarp(false);
                        IgniteEngines(v);
                        powered = new LandProgram { TouchdownSpeed = OrbitMath.Clamp(TouchdownSpeed, 0.5, 6), UseWarp = false, AutoStage = false, ThinAir = true, ArriveHeight = ArriveHeight,
                                                    HasTarget = true, TargetLat = TargetLat, TargetLon = TargetLon, TargetLabel = TargetLabel };
                        state = State.Powered;
                        ap.Log.Add("thin air: the engine will stop the vessel over " + TargetLabel + " (" + Math.Round(toSpot / 1000.0, 1) + " km ahead, " + Math.Round(speed) + " m/s)");
                    }
                    else if (AllowEngines && powered == null && HasUsableEngine(v))
                    {
                        double gravity = Guidance.LocalGravity(v);
                        double net = PotentialThrustAccel(v) - gravity;
                        double sink = -v.verticalSpeed, room = v.radarAltitude - 10;
                        bool noChutes = chuteCount == 0 || !UseChutes;
                        bool chutesTooWeak = open > 0 && AllChutesFullyOpen(v) && v.radarAltitude < 1500 && sink > Math.Max(6.0, TouchdownSpeed * 2.5);
                        bool noChutesAndSlowEnough = noChutes && v.verticalSpeed < -1 && (speed < 400 || v.radarAltitude < 8000);
                        bool mustLand = false, brakeForChutes = false;
                        if (net > 0.5 && sink > 20)
                        {
                            // Seconds until the ground if nothing slows the fall from here (drag will, so this
                            // errs early), against the seconds the engine needs. On a shallow entry the
                            // vessel is high for a long time, which is why this compares times, not distances.
                            double toGround = (-sink + Math.Sqrt(sink * sink + 2.0 * gravity * Math.Max(0, room))) / gravity;
                            double toStop = speed / net;
                            bool waiting = open == 0 && ChutesWaitingForSpeed(v);
                            // Packed parachutes that are waiting for the speed to drop: slow down just enough for
                            // them, late, and let them do the rest. Open parachutes hold the sink rate steady,
                            // so the time to the ground is simply height over sink rate.
                            brakeForChutes = waiting && toGround < Math.Max(0, speed - 220) / net * 1.2 + 12;
                            if (open > 0) mustLand = room / sink < toStop * 1.5 + 3;
                            else if (!waiting) mustLand = toGround < toStop * 1.8 + 5;
                        }
                        if (chutesTooWeak || noChutesAndSlowEnough || (mustLand && !brakeForChutes))
                        {
                            SetPhysicsWarp(false);
                            IgniteEngines(v);
                            powered = new LandProgram { TouchdownSpeed = OrbitMath.Clamp(TouchdownSpeed, 0.5, 6), UseWarp = false, AutoStage = false, InAtmosphere = true,
                                                        HasTarget = HasTarget, TargetLat = TargetLat, TargetLon = TargetLon, TargetLabel = TargetLabel };
                            state = State.Powered;
                            ap.Log.Add(chutesTooWeak ? "parachutes leave " + Math.Round(sink, 1) + " m/s of sink: finishing on the engine"
                                       : noChutes ? "no parachutes: landing on the engine"
                                       : "too fast and too low for the parachutes (" + Math.Round(speed) + " m/s at " + Math.Round(v.radarAltitude) + " m): landing on the engine");
                        }
                        else if (brakeForChutes)
                        {
                            SetPhysicsWarp(false);
                            IgniteEngines(v);
                            state = State.BrakeForChutes;
                            ap.Log.Add("too fast for the parachutes (" + Math.Round(speed) + " m/s at " + Math.Round(v.radarAltitude) + " m): slowing down with the engine first");
                        }
                    }
                    break;
                }

                case State.BrakeForChutes:
                {
                    // Burn against the motion until the parachutes report that they can open, then let them work.
                    int open = DeployChutes(ap, v);
                    Phase = "slowing for the parachutes, " + Math.Round(v.altitude) + " m, " + Math.Round(speed) + " m/s";
                    ap.SetAttitude(AttitudeMode.SurfaceRetrograde);
                    bool aligned = !double.IsNaN(ap.AngleError) && ap.AngleError < 20.0;
                    ap.SetThrottle(aligned ? 1.0 : 0.0);
                    if (open > 0 || !ChutesWaitingForSpeed(v) || speed < 60 || !HasUsableEngine(v))
                    {
                        ap.SetThrottle(0);
                        state = State.Entry;
                        if (open > 0) ap.Log.Add("engine off at " + Math.Round(speed) + " m/s with the parachutes out");
                    }
                    break;
                }

                case State.Powered:
                    DeployChutes(ap, v);
                    powered.Tick(ap, v);
                    Phase = "engine landing: " + powered.Phase;
                    if (powered.Done)
                    {
                        if (powered.Failed)
                        {
                            // Out of fuel or thrust: fall back on whatever parachutes there are.
                            ap.Log.Add("engine landing ended: " + powered.Result);
                            powered.OnEnd(ap);
                            powered = null;
                            AllowEngines = false;
                            state = State.Entry;
                        }
                    }
                    break;

                case State.Settled:
                    Phase = "touchdown";
                    ap.SetThrottle(0);
                    if (!v.LandedOrSplashed) { state = State.Entry; break; }
                    if (ut - settledSince > 3.0)
                    {
                        int lost = partsAtStart - v.parts.Count;
                        Finish((v.Splashed ? "splashed down on " : "landed on ") + Places.Name(body) + " at latitude " + Math.Round(v.latitude, 3) + ", longitude " + Math.Round(v.longitude, 3) +
                               (HasTarget ? ", " + Describe(Api.SurfaceApi.GroundDistance(body, v.latitude, v.longitude, TargetLat, TargetLon)) + " from " + TargetLabel : "") +
                               (double.IsNaN(touchdownSpeed) ? "" : ", touching down at " + Math.Round(touchdownSpeed, 1) + " m/s") + "; peak " + Math.Round(peakG, 1) + " g" +
                               (lost > 0 ? "; " + lost + " part" + (lost > 1 ? "s" : "") + " jettisoned or lost on the way down" : ""));
                    }
                    break;
            }
        }

        /// <summary>
        /// A capsule coming home drops its service module before the air gets thick: fire stages until the
        /// next one to go is the parachutes. A lander with legs keeps its engine unless told otherwise.
        /// </summary>
        void DropWhatStaysBehind(Autopilot ap, Vessel v, double ut, double entryIn)
        {
            if (staged) return;
            bool wanted = StageBeforeEntry ?? (chuteCount > 0 && !HasLegsOrWheels(v));
            if (!wanted) { staged = true; return; }
            if (HasTarget)
            {
                // Aimed landing: separate straight away, pointing across the orbit, so the kick from the
                // decoupler goes sideways (a few hundred metres at most) instead of along the track (kilometres).
                if (v.altitude < v.mainBody.atmosphereDepth) { /* too late to be fussy */ }
                else
                {
                    ap.SetAttitude(AttitudeMode.Normal);
                    stagingAttitude = true;
                    if (stageTurnSince < 0) stageTurnSince = ut;
                    bool square = !double.IsNaN(ap.AngleError) && ap.AngleError < 4.0 && v.angularVelocity.magnitude < 0.03;
                    if (!square && ut - stageTurnSince < 45 && (double.IsNaN(entryIn) || entryIn > 40)) return;
                }
            }
            else if (!double.IsNaN(entryIn) && entryIn > 45) return;          // not yet: keep the engine for corrections
            if (ut - lastStageUT < 1.5 || TimeWarp.CurrentRateIndex != 0) return;
            int chuteStage = -1;
            foreach (ModuleParachute c in Chutes(v)) if (c.part.inverseStage > chuteStage) chuteStage = c.part.inverseStage;
            if (chuteStage < 0 || v.currentStage - 1 <= chuteStage) { staged = true; stagingAttitude = false; return; }
            int before = v.parts.Count;
            KSP.UI.Screens.StageManager.ActivateNextStage();
            lastStageUT = ut;
            ap.Log.Add("staged before entry (stage " + (v.currentStage) + "), " + before + " parts before");
        }

        int DeployChutes(Autopilot ap, Vessel v)
        {
            int open = 0;
            if (!UseChutes) return 0;
            double pressureAtm = v.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres;
            foreach (ModuleParachute c in Chutes(v))
            {
                if (c.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED || c.deploymentState == ModuleParachute.deploymentStates.DEPLOYED) { open++; continue; }
                if (c.deploymentState != ModuleParachute.deploymentStates.STOWED) continue;
                // Aiming for a spot: a parachute opened high would drag the capsule short, so wait until it is low.
                if (HasTarget && v.radarAltitude > 3500) continue;
                // Braking to a stop over the spot: keep them packed until the vessel is coming straight down.
                if (PoweredAim && AllowEngines && (powered == null || !powered.OverTheSpot) && HasUsableEngine(v)) continue;
                if (c.deploymentSafeState == ModuleParachute.deploymentSafeStates.SAFE && pressureAtm >= c.minAirPressureToOpen && v.verticalSpeed < 0)
                {
                    c.Deploy();
                    if (!chutesLogged) { chutesLogged = true; ap.Log.Add("parachutes out at " + Math.Round(v.altitude) + " m and " + Math.Round(v.srfSpeed) + " m/s"); }
                }
            }
            return open;
        }

        static string Describe(double metres) => metres >= 2000 ? Math.Round(metres / 1000.0, 1) + " km" : Math.Round(metres) + " m";

        static bool AllChutesFullyOpen(Vessel v)
        {
            bool any = false;
            foreach (ModuleParachute c in Chutes(v))
            {
                if (c.deploymentState == ModuleParachute.deploymentStates.DEPLOYED) any = true;
                else if (c.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED || c.deploymentState == ModuleParachute.deploymentStates.STOWED || c.deploymentState == ModuleParachute.deploymentStates.ACTIVE) return false;
            }
            return any;
        }

        /// <summary>Seconds until the orbit first comes down to an altitude, or NaN if it never does.</summary>
        public static double TimeToAltitude(Vessel v, double altitude)
        {
            Orbit o = v.orbit;
            CelestialBody body = o.referenceBody;
            if (v.altitude <= altitude) return 0;
            if (o.PeA >= altitude) return double.NaN;
            double now = Planetarium.GetUniversalTime();
            double toPe = o.timeToPe;
            if (o.eccentricity < 1 && toPe > o.period / 2 && v.verticalSpeed < 0) toPe -= o.period;   // defensive: already past apoapsis
            double lo = now, hi = now + Math.Max(1, toPe);
            for (int n = 0; n < 50; n++)
            {
                double mid = (lo + hi) / 2.0;
                if (o.getRelativePositionAtUT(mid).magnitude - body.Radius > altitude) lo = mid; else hi = mid;
            }
            return hi - now;
        }

        void SetPhysicsWarp(bool on)
        {
            if (TimeWarp.WarpMode != TimeWarp.Modes.LOW) return;
            int wanted = on ? 2 : 0;
            if (!on && !physicsWarping) return;
            if (TimeWarp.CurrentRateIndex != wanted) TimeWarp.SetRate(wanted, true, false);
            physicsWarping = on;
        }

        void StopWarp()
        {
            if (TimeWarp.CurrentRateIndex != 0)
            {
                TimeWarp.fetch.CancelAutoWarp();
                TimeWarp.SetRate(0, true, false);
            }
            physicsWarping = false;
        }

        public override JObj Detail()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return null;
            if (Prelude != null) return Prelude.Detail();
            return new JObj().Add("altitude_m", Math.Round(v.altitude)).Add("speed", Math.Round(v.srfSpeed, 1)).Add("vertical_speed", Math.Round(v.verticalSpeed, 1)).Add("g", Math.Round(v.geeForce, 1));
        }

        public override void OnEnd(Autopilot ap)
        {
            if (Prelude != null) { try { Prelude.Abort("landing stopped"); Prelude.OnEnd(ap); } catch { } Prelude = null; }
            if (powered != null) { try { powered.OnEnd(ap); } catch { } powered = null; }
            StopWarp();
            if (!ap.HandingOver) ap.SetThrottle(0);
            ap.Throttle = null;
            ap.Stager.Enabled = stagerWasOn;
            ap.SetAttitude(AttitudeMode.Off);
        }
    }
}
