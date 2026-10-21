using System;
using AIBridge.Api;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Powered landing: cancel the sideways speed while holding altitude, drop straight down, then brake
    /// as late as is safe and settle onto the surface at walking pace. Without a target it lands wherever
    /// the vessel happens to be. With one it first flies a de-orbit burn (the prelude), coasts until the
    /// braking curve says "now", and then stops over the spot before letting itself down.
    /// </summary>
    public sealed class LandProgram : Program
    {
        public override string Name => HasTarget ? "landing at " + TargetLabel : "powered landing";

        public double TouchdownSpeed = 1.5;
        public double SafetyFactor = 1.2;
        public bool UseWarp = true;
        public bool AutoStage = true;

        public bool HasTarget;
        public double TargetLat, TargetLon;
        public string TargetLabel = "the target";
        /// <summary>
        /// Set when landing through air: there is no point fighting the wind to cancel sideways speed (drag
        /// does that), so the vessel falls tail-first and makes one braking burn sized on its whole speed.
        /// </summary>
        public bool InAtmosphere;
        /// <summary>
        /// Set when aiming for a spot through air too thin to do the braking (Duna): the vessel arrives
        /// fast and sinking, so the braking burn has to stop the fall as well as the sideways speed, and
        /// ends at <see cref="ArriveHeight"/> above the spot.
        /// </summary>
        public bool ThinAir;
        public double ArriveHeight = 2500;
        /// <summary>True once the sideways speed is gone and the vessel is coming straight down.</summary>
        public bool OverTheSpot => state == State.Drop || state == State.FinalDescent || state == State.Settled;
        /// <summary>A program to finish first (the de-orbit burn), if any.</summary>
        public Program Prelude;
        /// <summary>When the de-orbit plan expects to be over the target; NaN without a plan.</summary>
        public double PlannedArrivalUT = double.NaN;

        enum State { Coast, Brake, Drop, FinalDescent, Settled }
        State state = State.Brake;
        bool started, stagerWasOn, warping, gearCommanded, railsWarping, nearWarping;
        double bottomOffset, bottomAge = 1e9, settledSince = -1, noThrustSince = -1, startDv, targetAltitude, distance = double.NaN, coastSince = -1;
        Vector3d toTarget;

        /// <summary>Direction over the ground to the target and how far it is, following the curve of the surface.</summary>
        void Locate(Vessel v, Vector3d up)
        {
            CelestialBody body = v.mainBody;
            Vector3d spot = body.GetWorldSurfacePosition(TargetLat, TargetLon, targetAltitude);
            Vector3d spotUp = (spot - body.position).normalized;
            distance = Math.Acos(OrbitMath.Clamp(Vector3d.Dot(up, spotUp), -1, 1)) * (body.Radius + targetAltitude);
            Vector3d flat = Vector3d.Exclude(up, spotUp);
            toTarget = flat.sqrMagnitude > 1e-12 ? flat.normalized : Vector3d.zero;
        }

        public override void Tick(Autopilot ap, Vessel v)
        {
            double ut = Planetarium.GetUniversalTime();
            CelestialBody body = v.mainBody;
            if (Prelude != null)
            {
                // The de-orbit burn runs as a program of its own; this one takes over when it is done.
                Prelude.Tick(ap, v);
                Phase = "de-orbit burn: " + Prelude.Phase;
                if (!Prelude.Done) return;
                Prelude.OnEnd(ap);
                if (Prelude.Failed) { Fail("de-orbit burn failed: " + Prelude.Result); return; }
                ap.Log.Add("de-orbit burn done: " + Prelude.Result);
                Prelude = null;
                return;
            }
            if (!started) Begin(ap, v);
            if (!v.IsControllable) { Fail("lost control of the vessel"); return; }

            if (ut - bottomAge > 2.0) { bottomOffset = LowestPointBelowCoM(v); bottomAge = ut; }
            double height = v.radarAltitude - bottomOffset;
            Vector3d up = Guidance.Up(v);
            Vector3d horizontalVelocity = Vector3d.Exclude(up, v.srf_velocity);
            double horizontalSpeed = horizontalVelocity.magnitude;
            double descentRate = -v.verticalSpeed;
            double gravity = Guidance.LocalGravity(v);

            Guidance.EngineStats(v, out double maxThrust, out double _, out double _, out int burning, out int _);
            double thrustAccel = maxThrust / Math.Max(1e-6, v.totalMass);
            double planAccel = PlanAccel(v, thrustAccel);
            if (HasTarget) Locate(v, up);

            if (v.LandedOrSplashed && state != State.Settled)
            {
                ap.SetThrottle(0);
                state = State.Settled;
                settledSince = ut;
            }

            if (state != State.Settled && state != State.Coast && burning == 0)
            {
                if (noThrustSince < 0) noThrustSince = ut;
                Phase = "no engine running, waiting for staging";
                ap.SetAttitude(AttitudeMode.SurfaceRetrograde);
                if (ut - noThrustSince > 6.0 || (!AutoStage && ut - noThrustSince > 1.0))
                    Fail("no working engine with fuel at " + Math.Round(height) + " m above the ground, descending at " + Math.Round(descentRate, 1) + " m/s");
                return;
            }
            noThrustSince = -1;

            switch (state)
            {
                case State.Coast:
                {
                    // Falling toward the low point of the descent orbit. Brake when the speed still to be
                    // lost needs all of the distance that is left.
                    double r0 = (v.CoMD - body.position).magnitude;
                    double orbital = Vector3d.Exclude(up, v.obt_velocity).magnitude;
                    double holding = Math.Max(0, gravity - orbital * orbital / r0);
                    if (burning > 0 && TooWeak(v, planAccel, 1.02)) return;
                    double usable = Math.Sqrt(Math.Max(0.01, Math.Pow(planAccel * 0.95, 2) - holding * holding));
                    double along = Vector3d.Dot(horizontalVelocity, toTarget);
                    double needed = along > 0 ? along * along / (2.0 * 0.8 * usable) : 0;
                    bool tooLow = false;
                    if (ThinAir && along > 1)
                    {
                        // Arriving on a slant: the burn goes against the direction of travel, so step it
                        // through to see how far it carries and how much height it uses up.
                        StopPoint(along, descentRate, 0.8 * 0.95 * planAccel, holding, out needed, out double drop);
                        tooLow = descentRate > 0 && Math.Min(height, v.altitude - targetAltitude) - drop < 900;
                    }
                    double spare = distance - needed;
                    // The braking estimate only means something once the target is ahead and fairly near;
                    // before that, go by when the de-orbit plan said the vessel would get there.
                    bool closing = along > 0.5 * horizontalSpeed && distance < 0.25 * Math.PI * body.Radius;
                    double eta = closing ? spare / along : double.MaxValue;
                    double arrival = double.IsNaN(PlannedArrivalUT) ? double.NaN : PlannedArrivalUT - ut;
                    Phase = "coasting to the braking point, " + Math.Round(distance / 1000.0, 1) + " km from " + TargetLabel +
                            (closing ? ", braking in " + Math.Round(Math.Max(0, eta)) + " s" : !double.IsNaN(arrival) ? ", overhead in " + Math.Round(arrival) + " s" : "");
                    ap.SetThrottle(0);
                    ap.SetAttitude(AttitudeMode.SurfaceRetrograde);
                    if (coastSince < 0) coastSince = ut;
                    if (burning == 0 && thrustAccel <= 0) { Fail("no working engine for the braking burn"); return; }
                    if (UseWarp && TimeWarp.CurrentRateIndex == 0 && ut - coastSince > 2)
                    {
                        // Skip ahead, first to a few minutes before arrival, then to just before the braking point.
                        if (!closing && !double.IsNaN(arrival) && arrival > 400 && !railsWarping) { TimeWarp.fetch.WarpTo(ut + arrival - 300); railsWarping = true; }
                        else if (closing && eta > 90 && !nearWarping) { TimeWarp.fetch.WarpTo(ut + eta - 45); nearWarping = true; railsWarping = true; }
                    }
                    if ((closing && eta <= 2.0) || (height < 1500 && descentRate > 0) || tooLow)
                    {
                        if (TimeWarp.CurrentRateIndex != 0) { TimeWarp.fetch.CancelAutoWarp(); TimeWarp.SetRate(0, true, false); }
                        state = State.Brake;
                        ap.Log.Add("braking burn started " + Math.Round(distance / 1000.0, 1) + " km from " + TargetLabel + " at " + Math.Round(horizontalSpeed) + " m/s");
                    }
                    else if (ThinAir && along < 0 && ut - coastSince > 5)
                    {
                        // Past the spot already: stop here rather than fly on.
                        state = State.Brake;
                        ap.Log.Add("past " + TargetLabel + " by " + Math.Round(distance / 1000.0, 1) + " km: braking now");
                    }
                    else if (along < 0 && distance > 20000 && ut - coastSince > 30 && (double.IsNaN(arrival) || arrival < -120))
                    {
                        Fail("flying away from " + TargetLabel + " (" + Math.Round(distance / 1000.0) + " km behind); plan the de-orbit again");
                        return;
                    }
                    break;
                }

                case State.Brake:
                {
                    if (HasTarget)
                    {
                        if (ThinAir && v.srfSpeed > 60.0)
                        {
                            BrakeThroughAir(ap, v, up, horizontalVelocity, thrustAccel, planAccel, gravity, height, descentRate);
                            break;
                        }
                        if (BrakeToTarget(ap, v, up, horizontalVelocity, thrustAccel, planAccel, gravity, height, descentRate)) break;
                        return;
                    }
                    if (horizontalSpeed < 2.0)
                    {
                        ap.SetThrottle(0);
                        state = State.Drop;
                        break;
                    }
                    if (TooWeak(v, planAccel, 1.15)) return;
                    Phase = "braking, " + Math.Round(horizontalSpeed) + " m/s sideways, " + Math.Round(height) + " m up";
                    // Tilt the thrust upward just enough that the vessel neither climbs nor sinks while it slows.
                    double r = (v.CoMD - body.position).magnitude;
                    double orbitalHorizontal = Vector3d.Exclude(up, v.obt_velocity).magnitude;
                    double weight = gravity - orbitalHorizontal * orbitalHorizontal / r;
                    double wantedSink = height > 4000 ? Math.Min(40.0, (height - 4000) / 150.0) : 0.0;
                    double upAccel = weight + 0.4 * (descentRate - wantedSink);
                    double sin = OrbitMath.Clamp(upAccel / thrustAccel, -0.15, 0.85);
                    Vector3d direction = (-horizontalVelocity.normalized * Math.Sqrt(1.0 - sin * sin) + up * sin).normalized;
                    ap.FixedDirection = direction;
                    ap.SetAttitude(AttitudeMode.Fixed);
                    bool aligned = !double.IsNaN(ap.AngleError) && ap.AngleError < 8.0;
                    ap.SetThrottle(aligned ? OrbitMath.Clamp(horizontalSpeed / Math.Max(0.5, thrustAccel * 1.5), 0.1, 1.0) : 0.0);
                    if (height < 300 && descentRate > 5)
                    {
                        // Ran out of room: stop worrying about sideways speed and save the ship.
                        state = State.FinalDescent;
                    }
                    break;
                }

                case State.Drop:
                {
                    Phase = "falling, " + Math.Round(height) + " m up at " + Math.Round(descentRate) + " m/s";
                    ap.SetThrottle(0);
                    ap.FixedDirection = SteerUpright(up, horizontalVelocity, descentRate, v, Vector3d.zero);
                    ap.SetAttitude(AttitudeMode.Fixed);
                    if (TooWeak(v, planAccel, 1.02)) return;
                    double net = Math.Max(0.1, planAccel - gravity);
                    double stopping = descentRate > 0 ? descentRate * descentRate / (2.0 * net) : 0.0;
                    double trigger = stopping / 0.7 * SafetyFactor + descentRate * 1.0 + 20.0;
                    if (InAtmosphere && descentRate > 0)
                    {
                        // Through air the burn has to kill the sideways speed as well. It takes speed / net
                        // seconds, during which the vessel keeps sinking at up to its present rate.
                        double burnTime = v.srfSpeed / net;
                        trigger = Math.Max(trigger, descentRate * burnTime * 0.6 / 0.7 * SafetyFactor + descentRate * 2.5 + 30.0);
                    }
                    SetPhysicsWarp(height > trigger + Math.Max(600.0, descentRate * 25.0));
                    if (height <= trigger)
                    {
                        SetPhysicsWarp(false);
                        Gear.Set(v, true);
                        state = State.FinalDescent;
                    }
                    break;
                }

                case State.FinalDescent:
                {
                    Phase = "final descent, " + Math.Round(Math.Max(0, height), 1) + " m up at " + Math.Round(descentRate, 1) + " m/s";
                    SetPhysicsWarp(false);
                    if (!gearCommanded || !Gear.IsDown(v)) { Gear.Set(v, true); gearCommanded = true; }
                    // With a target, keep drifting toward it until the last few metres.
                    Vector3d drift = HasTarget && height > 12 && distance > 2 && distance < 2000 ? toTarget * Math.Min(8.0, 0.12 * distance) : Vector3d.zero;
                    ap.FixedDirection = SteerUpright(up, horizontalVelocity, descentRate, v, drift);
                    ap.SetAttitude(AttitudeMode.Fixed);
                    double net = Math.Max(0.1, planAccel - gravity);
                    // Follow a braking curve: at each height, the fastest descent that 70% of the available
                    // deceleration can still stop a few metres above the ground. Riding the curve needs that
                    // deceleration continuously (the feed-forward term); the last term corrects any error.
                    const double share = 0.7, flare = 5.0;
                    double room = Math.Max(0.0, height - flare);
                    double allowed = Math.Sqrt(2.0 * share * net * room) + TouchdownSpeed;
                    // While still moving fast sideways (coming in through air), brake on the whole speed.
                    double falling = (InAtmosphere || ThinAir) && v.srfSpeed > 25.0 ? Math.Max(descentRate, v.srfSpeed) : descentRate;
                    double onCurve = OrbitMath.Clamp((falling - 0.8 * allowed) / Math.Max(0.5, 0.2 * allowed), 0.0, 1.0);
                    double feedForward = room > 0.0 ? share * net * onCurve : 0.0;
                    double accel = gravity + feedForward + (falling - allowed) / 0.5;
                    ap.SetThrottle(OrbitMath.Clamp(accel / Math.Max(0.1, thrustAccel), 0.0, 1.0));
                    break;
                }

                case State.Settled:
                    Phase = "touchdown";
                    ap.SetThrottle(0);
                    if (!v.LandedOrSplashed) { state = State.FinalDescent; break; }
                    if (ut - settledSince > 2.5)
                    {
                        Finish((v.Splashed ? "splashed down on " : "landed on ") + Places.Name(body) + " at latitude " + Math.Round(v.latitude, 4) + ", longitude " + Math.Round(v.longitude, 4) +
                               ", altitude " + Math.Round(v.altitude) + " m" + (HasTarget ? ", " + Math.Round(distance) + " m from " + TargetLabel : "") + "; " + RemainingDv(v) + " m/s of delta-v left");
                    }
                    break;
            }
        }

        void Begin(Autopilot ap, Vessel v)
        {
            started = true;
            stagerWasOn = ap.Stager.Enabled;
            if (AutoStage) ap.Stager.Enabled = true;
            bottomOffset = LowestPointBelowCoM(v);
            bottomAge = Planetarium.GetUniversalTime();
            Vector3d up = Guidance.Up(v);
            double horizontal = Vector3d.Exclude(up, v.srf_velocity).magnitude;
            state = horizontal > 20.0 && !InAtmosphere ? State.Brake : State.Drop;
            if (ThinAir) InAtmosphere = false;      // flown like an airless landing; what air there is only helps
            if (HasTarget && !InAtmosphere)
            {
                targetAltitude = Math.Max(0, v.mainBody.TerrainAltitude(TargetLat, TargetLon, true));
                Locate(v, up);
                state = distance > 3000 && horizontal > 50 ? State.Coast : State.Brake;
            }
            VesselDeltaV dv = v.VesselDeltaV;
            startDv = dv != null && dv.IsReady ? dv.TotalDeltaVActual : double.NaN;
        }

        /// <summary>
        /// (2026-10-18) The engines' push for each tonne at the bottom of the descent, where the air is thickest, and never more than
        /// now: what the braking is planned on (the throttle is still set on what they give now). Planned on what the engine gave
        /// high up, the stock Kerbal X's upper stage, its Terrier giving a quarter of its thrust at sea level, meant to stop 2.5 km up
        /// and hit the ground at 384 m/s (2026-10-10).
        /// </summary>
        double PlanAccel(Vessel v, double thrustAccel)
        {
            CelestialBody body = v.mainBody;
            if (body == null || !body.atmosphere || thrustAccel <= 0) return thrustAccel;
            double ground = HasTarget && targetAltitude > 0 ? targetAltitude : Math.Max(0, v.terrainAltitude);
            double atm = FlightGlobals.getStaticPressure(ground, body) * PhysicsGlobals.KpaToAtmospheres;
            return Math.Min(thrustAccel, Guidance.ThrustAt(v, atm) / Math.Max(1e-6, v.totalMass));
        }

        /// <summary>
        /// Whether the engines cannot hold the vessel up where it is to land, with a margin (1.15 as the braking always asked; 1.02,
        /// just able to hover there, before it, while falling or coasting), and if so the program ends, saying so and what to do
        /// instead, before the braking begins rather than at the bottom.
        /// </summary>
        bool TooWeak(Vessel v, double planAccel, double margin)
        {
            CelestialBody body = v.mainBody;
            double ground = HasTarget && targetAltitude > 0 ? targetAltitude : Math.Max(0, v.terrainAltitude);
            double g = body.gravParameter / Math.Pow(body.Radius + ground, 2);
            if (planAccel >= g * margin) return false;
            double vacuum = Guidance.ThrustAt(v, 0), there = planAccel * v.totalMass;
            Fail("thrust is too weak to land here: the engines give " + Math.Round(there, 1) + " kN where the vessel would land" +
                 (body.atmosphere && vacuum > there * 1.05 ? " (" + Math.Round(vacuum, 1) + " kN in a vacuum: the air there takes the rest)" : "") +
                 ", " + Math.Round(planAccel, 2) + " m/s² against gravity of " + Math.Round(g, 2) + " m/s²; come down under parachutes (land with stage: true), or land where the air is thinner");
            return true;
        }

        static string RemainingDv(Vessel v)
        {
            VesselDeltaV dv = v.VesselDeltaV;
            return dv != null && dv.IsReady ? Math.Round(dv.TotalDeltaVVac).ToString() : "unknown";
        }

        /// <summary>
        /// Braking with somewhere to be: hold height, shed speed along a curve that reaches zero at the
        /// target, and push sideways against whatever is carrying the vessel off the line to it.
        /// Returns false when the program has ended.
        /// </summary>
        bool BrakeToTarget(Autopilot ap, Vessel v, Vector3d up, Vector3d horizontalVelocity, double thrustAccel, double planAccel, double gravity, double height, double descentRate)
        {
            CelestialBody body = v.mainBody;
            double horizontalSpeed = horizontalVelocity.magnitude;
            if (TooWeak(v, planAccel, 1.15)) return false;
            if (distance < 25 && horizontalSpeed < 1.5)
            {
                ap.SetThrottle(0);
                state = State.Drop;
                ap.Log.Add("stopped " + Math.Round(distance) + " m from " + TargetLabel + ", " + Math.Round(height) + " m up; descending");
                return true;
            }
            if (height < 300 && descentRate > 5) { state = State.FinalDescent; return true; }

            double r = (v.CoMD - body.position).magnitude;
            double orbital = Vector3d.Exclude(up, v.obt_velocity).magnitude;
            double weight = gravity - orbital * orbital / r;
            double along = Vector3d.Dot(horizontalVelocity, toTarget);
            double wantedSink = height > 3000 ? Math.Min(40.0, (height - 3000) / 120.0) : 0.0;
            double upAccel = OrbitMath.Clamp(weight + 0.4 * (descentRate - wantedSink), -0.2 * planAccel, 0.85 * planAccel);
            double usable = Math.Sqrt(Math.Max(0.01, Math.Pow(planAccel * 0.97, 2) - upAccel * upAccel));

            Vector3d across = horizontalVelocity - toTarget * along;
            // Speed still allowed at this distance if 80% of the sideways thrust is to stop the vessel there.
            double decel = 0.8 * usable, room = Math.Max(0, distance - 3);
            double allowed = Math.Sqrt(2.0 * decel * room);
            // A light lander with a strong engine (the usual case after a long burn through thin air) would
            // arrive too fast to stop and swing back and forth over the spot: allow the two seconds it
            // takes to turn the engine round.
            const double swing = 2.0;
            if (ThinAir) allowed = Math.Sqrt(decel * decel * swing * swing + 2.0 * decel * room) - decel * swing;
            double alongAccel;
            if (along > allowed) alongAccel = (allowed - along) / 1.5 - 0.8 * usable * (allowed > 1 ? 1 : 0);          // on or over the curve: ride it down
            else if (along > 25) alongAccel = along > 0.97 * allowed ? -0.8 * usable : 0;                                // under the curve and fast: coast
            else alongAccel = OrbitMath.Clamp((Math.Min(allowed, 25) - along) / 2.0, -usable, Math.Min(2.0, usable));   // slow: creep the rest of the way
            Vector3d wanted = toTarget * alongAccel - across / 2.5;
            if (wanted.magnitude > usable) wanted = wanted.normalized * usable;

            Vector3d thrust = wanted + up * upAccel;
            Phase = "braking, " + Math.Round(horizontalSpeed) + " m/s, " + (distance > 2000 ? Math.Round(distance / 1000.0, 1) + " km" : Math.Round(distance) + " m") + " from " + TargetLabel + ", " + Math.Round(height) + " m up";
            if (thrust.magnitude < 0.03 * thrustAccel)
            {
                ap.SetThrottle(0);
                ap.FixedDirection = (up * 0.3 - horizontalVelocity.normalized).normalized;
                ap.SetAttitude(AttitudeMode.Fixed);
                return true;
            }
            ap.FixedDirection = thrust.normalized;
            ap.SetAttitude(AttitudeMode.Fixed);
            bool aligned = !double.IsNaN(ap.AngleError) && ap.AngleError < 10.0;
            ap.SetThrottle(aligned ? OrbitMath.Clamp(thrust.magnitude / thrustAccel, 0.0, 1.0) : 0.0);
            return true;
        }

        /// <summary>
        /// Where a burn against the direction of travel brings the vessel (nearly) to rest: how far over the
        /// ground it carries and how much height it loses. Gravity bends the path downward as the speed
        /// goes, so this is stepped through rather than worked out in one line. Air is left out; it only
        /// shortens the result.
        /// </summary>
        static void StopPoint(double sideways, double sinking, double decel, double weight, out double ground, out double drop)
        {
            ground = drop = 0;
            const double dt = 0.5;
            for (int n = 0; n < 600; n++)
            {
                double speed = Math.Sqrt(sideways * sideways + sinking * sinking);
                if (speed < 10) break;
                double ax = -decel * sideways / speed, az = weight - decel * sinking / speed;
                ground += sideways * dt + 0.5 * ax * dt * dt;
                drop += sinking * dt + 0.5 * az * dt * dt;
                sideways += ax * dt;
                sinking += az * dt;
            }
        }

        /// <summary>
        /// The fast part of braking toward a spot through thin air. The engine points against the direction
        /// of travel, where the air holds the vessel steady (and where sideways speed and sink rate are
        /// lost together), leaning only a little to stay on the line to the spot; the throttle is set so
        /// that the burn ends over it. Below 60 m/s the ordinary routine takes over.
        /// </summary>
        void BrakeThroughAir(Autopilot ap, Vessel v, Vector3d up, Vector3d horizontalVelocity, double thrustAccel, double planAccel, double gravity, double height, double descentRate)
        {
            CelestialBody body = v.mainBody;
            double r = (v.CoMD - body.position).magnitude;
            double orbital = Vector3d.Exclude(up, v.obt_velocity).magnitude;
            double weight = Math.Max(0, gravity - orbital * orbital / r);
            double horizontalSpeed = horizontalVelocity.magnitude;
            double along = Vector3d.Dot(horizontalVelocity, toTarget);
            Vector3d across = horizontalVelocity - toTarget * along;
            Vector3d retrograde = -v.srf_velocity.normalized;
            // (what the air pushes against: the motion through the air, which with a weather mod's wind is not the motion
            // over the ground; the braking itself is still worked out over the ground, where the spot is)
            Vector3d airRetrograde = -(v.srf_velocity - Api.WeatherApi.WindOn(v)).normalized;

            // Too close to the ground to think about the spot any more: stop the fall.
            StopPoint(horizontalSpeed, descentRate, 0.95 * planAccel, weight, out double _, out double leastDrop);
            if (descentRate > 0 && height - leastDrop < 150) { state = State.FinalDescent; return; }

            // How hard to brake so that the burn ends over the spot: compare where a burn at 80% would end
            // with how far the spot is. Ending too low for comfort overrides that.
            StopPoint(Math.Max(1, along), descentRate, 0.8 * 0.95 * planAccel, weight, out double reach, out double drop);
            double share = along > 1 ? 0.8 * reach / Math.Max(1.0, distance - 15) : 1.0;
            if (descentRate > 0 && Math.Min(height, v.altitude - targetAltitude) - drop < 600) share = 1.0;
            share = OrbitMath.Clamp(share, 0.2, 1.0);

            // Lean against whatever is carrying it off the line, but stay close to tail-first while the air
            // is pushing hard: a lander side-on to a fast airflow cannot be held.
            Vector3d wanted = retrograde * (share * thrustAccel) - across / 3.0;
            double pressure = v.dynamicPressurekPa;
            double cone = OrbitMath.Clamp(40.0 / Math.Max(0.05, pressure), 8.0, 60.0);
            double off = Vector3d.Angle(wanted, airRetrograde);
            // (OrbitMath.Slerp, not the game's Vector3d.Slerp, which is an empty stub that fails when called.)
            Vector3d direction = off > cone ? OrbitMath.Slerp(airRetrograde, wanted.normalized, cone / off) : wanted.normalized;

            Phase = "braking, " + Math.Round(v.srfSpeed) + " m/s, " + (distance > 2000 ? Math.Round(distance / 1000.0, 1) + " km" : Math.Round(distance) + " m") + " from " + TargetLabel + ", " + Math.Round(height) + " m up";
            ap.FixedDirection = direction;
            ap.SetAttitude(AttitudeMode.Fixed);
            // Keep some thrust on even when pointing a little wrong: the engine's swivel is most of the steering.
            double error = double.IsNaN(ap.AngleError) ? 0 : ap.AngleError;
            ap.SetThrottle(share * OrbitMath.Clamp((40.0 - error) / 25.0, 0.3, 1.0));
        }

        /// <summary>
        /// True when a vessel coasting toward a spot is within 'lead' seconds of the point where it must
        /// start braking to stop there. For a program that wants to hand over to this one in good time.
        /// </summary>
        public static bool NearBrakingPoint(Vessel v, double lat, double lon, double thrustAccel, double lead, out double distance)
        {
            CelestialBody body = v.mainBody;
            Vector3d up = Guidance.Up(v);
            double ground = Math.Max(0, body.TerrainAltitude(lat, lon, true));
            Vector3d spotUp = (body.GetWorldSurfacePosition(lat, lon, ground) - body.position).normalized;
            distance = Math.Acos(OrbitMath.Clamp(Vector3d.Dot(up, spotUp), -1, 1)) * (body.Radius + ground);
            Vector3d flat = Vector3d.Exclude(up, spotUp);
            if (flat.sqrMagnitude < 1e-12 || thrustAccel <= 0) return true;
            double along = Vector3d.Dot(Vector3d.Exclude(up, v.srf_velocity), flat.normalized);
            if (along <= 1) return along < 0;           // already past it
            double needed = along * along / (2.0 * 0.8 * thrustAccel * 0.6);       // generous: this only decides when to hand over
            return distance - needed < along * lead;
        }

        /// <summary>
        /// Point against the motion while it is fast; once nearly stopped stand upright and lean gently
        /// toward the sideways speed that is wanted (none, unless there is a target to drift to).
        /// </summary>
        static Vector3d SteerUpright(Vector3d up, Vector3d horizontalVelocity, double descentRate, Vessel v, Vector3d wantedDrift)
        {
            if (v.srfSpeed > 25.0 && descentRate > 5.0 && wantedDrift.sqrMagnitude < 1e-6) return -v.srf_velocity.normalized;
            Vector3d lean = (wantedDrift - horizontalVelocity) * 0.08;
            if (lean.magnitude > 0.35) lean = lean.normalized * 0.35;
            return (up + lean).normalized;
        }

        /// <summary>How far the lowest collider reaches below the centre of mass, measured straight down.</summary>
        public static double LowestPointBelowCoM(Vessel v)
        {
            Vector3d down = -Guidance.Up(v);
            double lowest = 0.5;
            foreach (Part p in v.parts)
            {
                foreach (Collider c in p.GetComponentsInChildren<Collider>())
                {
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    Bounds b = c.bounds;
                    Vector3d centre = (Vector3d)b.center - v.CoMD;
                    double reach = Vector3d.Dot(centre, down) + Math.Abs(down.x) * b.extents.x + Math.Abs(down.y) * b.extents.y + Math.Abs(down.z) * b.extents.z;
                    if (reach > lowest) lowest = reach;
                }
            }
            return Math.Min(lowest, 60.0);
        }

        void SetPhysicsWarp(bool on)
        {
            if (!UseWarp && !warping) return;
            int wanted = on && UseWarp ? 2 : 0;
            if (TimeWarp.WarpMode == TimeWarp.Modes.LOW && TimeWarp.CurrentRateIndex != wanted)
            {
                TimeWarp.SetRate(wanted, true, false);
                warping = wanted != 0;
            }
        }

        public override JObj Detail()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return null;
            if (Prelude != null) return Prelude.Detail();
            JObj o = new JObj()
                .Add("height_above_ground_m", Math.Round(v.radarAltitude - bottomOffset, 1))
                .Add("vertical_speed", Math.Round(v.verticalSpeed, 1))
                .Add("horizontal_speed", Math.Round(v.horizontalSrfSpeed, 1));
            if (HasTarget && !double.IsNaN(distance)) o.Add("distance_to_target_m", Math.Round(distance));
            return o;
        }

        public override void OnEnd(Autopilot ap)
        {
            if (Prelude != null) { try { Prelude.Abort("landing stopped"); Prelude.OnEnd(ap); } catch { } Prelude = null; }
            if (railsWarping && TimeWarp.CurrentRateIndex != 0) { TimeWarp.fetch.CancelAutoWarp(); TimeWarp.SetRate(0, true, false); }
            if (TimeWarp.CurrentRateIndex != 0 && TimeWarp.WarpMode == TimeWarp.Modes.LOW) TimeWarp.SetRate(0, true, false);
            if (!ap.HandingOver) ap.SetThrottle(0);
            ap.Throttle = null;
            ap.Stager.Enabled = stagerWasOn;
            ap.SetAttitude(AttitudeMode.Off);
        }
    }
}
