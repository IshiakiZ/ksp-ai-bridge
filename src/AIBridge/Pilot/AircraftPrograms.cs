using System;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>A strip to land on: where it starts, which way it runs and how high it is.</summary>
    public sealed class Runway
    {
        public string Name = "runway";
        public double Lat, Lon, Heading, Altitude, Length = 2500, Width = 40;
    }

    /// <summary>Keeps an aircraft pointing along a line while its wheels are on the ground, with rudder and nose wheel together.</summary>
    public sealed class GroundSteering
    {
        double wheelSign = -1, checkUT = -1, errorAtCheck, lastHeading = double.NaN, headingRate;
        /// <summary>Degrees off the wanted heading, and how fast the nose is swinging (degrees a second).</summary>
        public double Error { get; private set; }
        public double HeadingRate => headingRate;

        public void Apply(Autopilot ap, Vessel v, double targetHeading)
        {
            double ut = Planetarium.GetUniversalTime(), dt = Math.Max(0.005, TimeWarp.fixedDeltaTime);
            double heading = Aero.NoseHeading(v);
            if (!double.IsNaN(lastHeading)) headingRate += (Aero.Wrap(heading - lastHeading) / dt - headingRate) * Math.Min(1.0, dt / 0.15);
            lastHeading = heading;
            double error = Aero.Wrap(targetHeading - heading);
            Error = error;
            double speed = Math.Max(2.0, v.srfSpeed);

            // Ask for a turn rate, never more than the tyres can hold without skidding.
            double rateLimit = Math.Min(20.0, 2.5 / speed * Aero.Rad2Deg);
            double wantedRate = OrbitMath.Clamp(error * 1.0, -rateLimit, rateLimit);
            double rateCommand = wantedRate + 0.5 * (wantedRate - headingRate);

            // The game steers by the angle of an imaginary front wheel: turn rate = speed x tan(angle) / wheelbase.
            // At take-off speed a fraction of a degree is plenty, which is why steering by feel oscillates.
            SteeringGeometry(v, out double wheelbase, out double range);
            double angle = Math.Atan(rateCommand * Aero.Deg2Rad * wheelbase / speed) * Aero.Rad2Deg;
            ap.WheelSteer = (float)(wheelSign * OrbitMath.Clamp(angle / range, -1, 1));
            // The rudder helps in the same direction; it is aerodynamic, so weak when slow.
            ap.YawOverride = (float)OrbitMath.Clamp(error / 10.0 - headingRate / 15.0, -0.6, 0.6);

            // Wheels are not all wired the same way round: steering that makes the error grow gets reversed.
            if (v.srfSpeed > 3 && v.srfSpeed < 25 && Math.Abs(error) > 1.5)
            {
                if (checkUT < 0) { checkUT = ut; errorAtCheck = Math.Abs(error); }
                else if (ut - checkUT > 1.5)
                {
                    if (Math.Abs(error) > errorAtCheck + 2.0) wheelSign = -wheelSign;
                    checkUT = -1;
                }
            }
            else checkUT = -1;
        }

        static void SteeringGeometry(Vessel v, out double wheelbase, out double range)
        {
            wheelbase = 0;
            range = 0;
            Vector3 forward = v.ReferenceTransform.up;
            foreach (Part p in v.parts)
            {
                var steer = p.FindModuleImplementing<ModuleWheels.ModuleWheelSteering>();
                if (steer == null || !steer.steeringEnabled) continue;
                double ahead = Math.Abs(Vector3.Dot(p.transform.position - v.CurrentCoM, forward));
                if (ahead > wheelbase) { wheelbase = ahead; range = steer.steeringRange; }
            }
            if (wheelbase < 0.3) wheelbase = 3;
            if (range < 1) range = 30;
        }

        public static void Release(Autopilot ap)
        {
            ap.YawOverride = null;
            ap.WheelSteer = null;
        }
    }

    /// <summary>Runway take-off: hold the centreline, rotate, climb away, retract the gear and level off.</summary>
    public sealed class TakeoffProgram : Program
    {
        public override string Name => "take-off";

        public double TargetAltitude;
        public double? HeadingAfter, CruiseSpeed, RotateSpeed;
        public double RotatePitch = 10;

        enum State { Roll, Rotate, Climb }
        State state = State.Roll;
        bool started, gearUp;
        double runwayHeading, startAltitude, startUT, liftSpeed, rotateAt, airborneUT = -1;
        readonly GroundSteering steering = new GroundSteering();

        public override void Tick(Autopilot ap, Vessel v)
        {
            double ut = Planetarium.GetUniversalTime();
            if (!started)
            {
                started = true;
                startUT = ut;
                runwayHeading = Aero.NoseHeading(v);
                startAltitude = v.altitude;
                liftSpeed = Aero.LiftSpeed(v, 10);
                rotateAt = RotateSpeed ?? (double.IsNaN(liftSpeed) ? 45 : 0.8 * liftSpeed);
                ap.Plane.Disengage();
                ap.SetAttitude(AttitudeMode.Off);
                ap.UseTrim = true;
                v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                v.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
                Guidance.EngineStats(v, out double _, out double _, out double _, out int burning, out int _);
                if (burning == 0) KSP.UI.Screens.StageManager.ActivateNextStage();
            }
            if (!v.IsControllable) { Fail("lost control of the aircraft"); return; }
            // (rotated by airspeed, which is what lifts: into a headwind that comes sooner over the ground)
            double airspeed = Aero.Airspeed(v);

            switch (state)
            {
                case State.Roll:
                    Phase = "rolling, " + Math.Round(airspeed) + " m/s airspeed (rotating at " + Math.Round(rotateAt) + ")";
                    ap.SetThrottle(1);
                    steering.Apply(ap, v, runwayHeading);
                    if (airspeed >= rotateAt) state = State.Rotate;
                    else if (ut - startUT > 25 && v.srfSpeed < 5) Fail("the aircraft is not moving at full throttle (no thrust, or the brakes or gear are stuck)");
                    else if (ut - startUT > 150) Fail("still on the ground after 150 s at " + Math.Round(airspeed) + " m/s airspeed");
                    break;

                case State.Rotate:
                    Phase = "rotating, " + Math.Round(airspeed) + " m/s airspeed";
                    ap.SetThrottle(1);
                    ap.RollTarget = 0;
                    ap.SetPitchHeading(RotatePitch, runwayHeading);
                    if (v.Landed) { steering.Apply(ap, v, runwayHeading); airborneUT = -1; }
                    else
                    {
                        GroundSteering.Release(ap);
                        if (airborneUT < 0) airborneUT = ut;
                        if (ut - airborneUT > 0.6 && v.altitude - startAltitude > 2.5)
                        {
                            state = State.Climb;
                            AircraftHold hold = ap.Plane;
                            hold.Engage(v);
                            hold.Heading = runwayHeading;
                            hold.Altitude = TargetAltitude;
                            hold.VerticalSpeed = null;
                            hold.Speed = CruiseSpeed ?? DefaultCruise(v);
                            hold.ThrottleSetting = 1;
                            hold.MinSpeed = double.IsNaN(liftSpeed) ? 0 : 1.1 * liftSpeed;
                            ap.Log.Add("airborne at " + Math.Round(airspeed) + " m/s airspeed after " + Math.Round(ut - startUT) + " s");
                        }
                    }
                    if (ut - startUT > 180) Fail("could not lift off: " + Math.Round(airspeed) + " m/s airspeed and still on the ground (not enough wing, or the elevators cannot raise the nose)");
                    break;

                case State.Climb:
                {
                    double gained = v.altitude - startAltitude;
                    Phase = "climbing through " + Math.Round(v.altitude) + " m at " + Math.Round(v.verticalSpeed, 1) + " m/s";
                    if (!gearUp && gained > 25 && v.verticalSpeed > 0) { Gear.Set(v, false); gearUp = true; }
                    if (gained > 120 && HeadingAfter.HasValue) ap.Plane.Heading = HeadingAfter.Value;
                    if (v.LandedOrSplashed && ut - airborneUT > 3) { Fail("settled back onto the ground after lifting off"); return; }
                    if (v.altitude >= TargetAltitude - 25 || (Math.Abs(TargetAltitude - v.altitude) < 60 && Math.Abs(v.verticalSpeed) < 3))
                    {
                        if (HeadingAfter.HasValue) ap.Plane.Heading = HeadingAfter.Value;
                        Finish("airborne and level at " + Math.Round(v.altitude) + " m; " + ap.Plane.Describe());
                    }
                    break;
                }
            }
        }

        /// <summary>A comfortable cruising speed for an aircraft nobody gave a speed for: well clear of the stall, far from supersonic.</summary>
        public static double DefaultCruise(Vessel v)
        {
            double lift = Aero.LiftSpeed(v, 10);
            return double.IsNaN(lift) ? 120 : OrbitMath.Clamp(2.5 * lift, 80, 250);
        }

        public override JObj Detail()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return null;
            return new JObj().Add("speed", Math.Round(Aero.Airspeed(v), 1)).Add("ground_speed", Math.Round(v.srfSpeed, 1)).Add("altitude_m", Math.Round(v.altitude)).Add("vertical_speed", Math.Round(v.verticalSpeed, 1));
        }

        public override void OnEnd(Autopilot ap)
        {
            GroundSteering.Release(ap);
            // A take-off abandoned on the ground must not leave the engine at full power.
            Vessel v = FlightGlobals.ActiveVessel;
            if (Failed && v != null && v.Landed && !ap.HandingOver)
            {
                ap.SetThrottle(0);
                ap.Throttle = null;
                ap.SetAttitude(AttitudeMode.Off);
                v.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
            }
        }
    }

    /// <summary>Fly to a point on the map and keep going on the same heading once it is reached.</summary>
    public sealed class FlyToProgram : Program
    {
        public override string Name => "fly to " + Label;

        public double TargetLat, TargetLon, Radius = 1500;
        public string Label = "the target";
        double closest = double.MaxValue;

        public override void Tick(Autopilot ap, Vessel v)
        {
            if (!ap.Plane.Enabled) { Fail("the aircraft hold was switched off"); return; }
            if (v.LandedOrSplashed) { Fail("the aircraft is on the ground"); return; }
            double distance = Api.SurfaceApi.GroundDistance(v.mainBody, v.latitude, v.longitude, TargetLat, TargetLon);
            ap.Plane.Heading = Api.SurfaceApi.Bearing(v.latitude, v.longitude, TargetLat, TargetLon);
            Phase = Math.Round(distance / 1000.0, 1) + " km to go, " + Math.Round(v.altitude) + " m, " + Math.Round(Aero.Airspeed(v)) + " m/s airspeed";
            if (distance < closest) closest = distance;
            if (distance <= Radius || (closest < 6000 && distance > closest + 400))
                Finish("reached " + Label + " (passed " + Math.Round(closest) + " m from it); " + ap.Plane.Describe());
        }

        public override JObj Detail()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return null;
            return new JObj().Add("distance_m", Math.Round(Api.SurfaceApi.GroundDistance(v.mainBody, v.latitude, v.longitude, TargetLat, TargetLon)))
                .Add("altitude_m", Math.Round(v.altitude)).Add("speed", Math.Round(Aero.Airspeed(v), 1)).Add("ground_speed", Math.Round(v.srfSpeed, 1));
        }
    }

    /// <summary>
    /// Runway landing: fly to a point on the extended centreline, follow the glide slope down, flare,
    /// touch down on the main wheels and brake to a stop on the centreline.
    /// </summary>
    public sealed class RunwayLandProgram : Program
    {
        public override string Name => "landing at " + Strip.Name;

        public Runway Strip;
        public double Glideslope = 4.0, FinalDistance = 8000;
        public double? ApproachSpeed;

        enum State { Intercept, Final, Flare, Rollout, GoAround }
        State state = State.Intercept;
        bool started, gearDown, circling;
        int attempts = 1;
        double approachSpeed, startAltitude, gearOffset = 1.5, gearAge = -1e9, touchdownUT = -1, touchdownX, floatingSince = -1, flareStartUT, steadySince = -1;
        double x, y;
        readonly GroundSteering steering = new GroundSteering();
        const double AimPoint = 150;

        void Locate(Vessel v)
        {
            double d = Api.SurfaceApi.GroundDistance(v.mainBody, Strip.Lat, Strip.Lon, v.latitude, v.longitude);
            double b = Api.SurfaceApi.Bearing(Strip.Lat, Strip.Lon, v.latitude, v.longitude);
            double rel = (b - Strip.Heading) * Aero.Deg2Rad;
            x = d * Math.Cos(rel);
            y = d * Math.Sin(rel);
        }

        public override void Tick(Autopilot ap, Vessel v)
        {
            double ut = Planetarium.GetUniversalTime();
            AircraftHold hold = ap.Plane;
            if (!started)
            {
                started = true;
                startAltitude = v.altitude;
                double lift = Aero.LiftSpeed(v, 8);
                approachSpeed = ApproachSpeed ?? (double.IsNaN(lift) ? 65 : OrbitMath.Clamp(1.2 * lift, 30, 140));
                hold.Engage(v);
                hold.MinSpeed = double.IsNaN(lift) ? 0 : 0.85 * lift;
                if (v.LandedOrSplashed) { Fail("the aircraft is already on the ground"); return; }
            }
            if (!v.IsControllable) { Fail("lost control of the aircraft"); return; }
            Locate(v);
            double tanGs = Math.Tan(Glideslope * Aero.Deg2Rad);
            if (ut - gearAge > 2.0 && gearDown) { gearOffset = LandProgram.LowestPointBelowCoM(v); gearAge = ut; }
            double height = v.altitude - Strip.Altitude - gearOffset;
            double wheelHeight = Aero.HeightAboveSurface(v) - gearOffset;

            switch (state)
            {
                case State.Intercept:
                {
                    Api.SurfaceApi.Spot fix = Api.SurfaceApi.Offset(v.mainBody, Strip.Lat, Strip.Lon, Strip.Heading + 180, FinalDistance);
                    double toFix = Api.SurfaceApi.GroundDistance(v.mainBody, v.latitude, v.longitude, fix.Lat, fix.Lon);
                    double interceptAltitude = Strip.Altitude + (FinalDistance + AimPoint) * tanGs;
                    // (Since 2026-10-20: from high up, the height the way to the fix cannot lose is lost first in a descending circle
                    // round the fix, as a pilot coming home high would. Asked to land from 11 km up and 30 km out (a spaceplane home
                    // from orbit), it flew straight at the fix, came over the runway 8 km up, went round twice and spiralled down
                    // for eight minutes.)
                    double allowed = interceptAltitude + Math.Max(0, toFix - 3000) * 0.10;
                    if (!circling && v.altitude - allowed > 600 && toFix < 15000)
                    {
                        circling = true;
                        ap.Log.Add("too high for the approach: losing " + Math.Round(v.altitude - interceptAltitude) + " m in a circle round the approach fix first");
                    }
                    if (circling && v.altitude < interceptAltitude + 300)
                    {
                        circling = false;
                        hold.MaxSink = 25;
                        ap.Log.Add("down to " + Math.Round(v.altitude) + " m: to the approach fix");
                    }
                    if (circling)
                    {
                        // (the fix kept on the left at a radius the aircraft can turn at a 25-degree bank, turning in or out by how far off it is)
                        double radius = Math.Max(2500, v.srfSpeed * v.srfSpeed / (9.81 * Math.Tan(25 * Aero.Deg2Rad)));
                        double off = OrbitMath.Clamp((toFix - radius) / radius, -1, 1);
                        hold.WingsLevel = false;
                        hold.PitchFloor = null;
                        hold.BankLimit = 30;
                        hold.MaxSink = 40;
                        hold.Heading = Api.SurfaceApi.Bearing(v.latitude, v.longitude, fix.Lat, fix.Lon) + 90 - 60 * off;
                        hold.VerticalSpeed = -OrbitMath.Clamp((v.altitude - interceptAltitude) / 25, 8, 40);
                        hold.Speed = approachSpeed * 1.4;
                        Phase = "losing height round the approach fix, " + Math.Round(v.altitude) + " m, down to " + Math.Round(interceptAltitude + 300) + " m";
                        break;
                    }
                    hold.WingsLevel = false;
                    hold.VerticalSpeed = null;
                    hold.PitchFloor = null;
                    hold.Altitude = Math.Max(interceptAltitude, Math.Min(startAltitude, interceptAltitude + Math.Max(0, toFix - 3000) * 0.10));
                    hold.Heading = Api.SurfaceApi.Bearing(v.latitude, v.longitude, fix.Lat, fix.Lon);
                    hold.Speed = approachSpeed * 1.4;
                    Phase = "flying to the approach fix, " + Math.Round(toFix / 1000.0, 1) + " km away";
                    bool inCone = x < -2500 && x > -FinalDistance * 1.5 && Math.Abs(y) < 0.35 * Math.Abs(x) && Math.Abs(Aero.Wrap(hold.Track - Strip.Heading)) < 50;
                    if (toFix < Math.Max(1200, 10 * v.srfSpeed) || inCone)
                    {
                        state = State.Final;
                        ap.Log.Add("on final for " + Strip.Name + ", " + Math.Round(-x / 1000.0, 1) + " km out at " + Math.Round(height) + " m above it");
                    }
                    break;
                }

                case State.Final:
                {
                    if (!gearDown && x > -6000) { Gear.Set(v, true); gearDown = true; gearAge = ut - 1.0; v.ActionGroups.SetGroup(KSPActionGroup.Brakes, false); }
                    double lookAhead = OrbitMath.Clamp(0.35 * Math.Abs(x), 350, 2500);
                    hold.Heading = Strip.Heading - Math.Atan2(y, lookAhead) * Aero.Rad2Deg;
                    hold.Speed = approachSpeed;
                    hold.WingsLevel = false;
                    hold.BankLimit = height < 60 ? 12 : 30;
                    double wanted = Math.Max(0, AimPoint - x) * tanGs;
                    double groundSpeed = v.horizontalSrfSpeed;
                    hold.VerticalSpeed = OrbitMath.Clamp(-groundSpeed * tanGs + 0.25 * (wanted - height), -16, 4);
                    Phase = "final, " + Math.Round(-x) + " m to the threshold, " + Math.Round(height) + " m up, " + Math.Round(y) + " m " + (y >= 0 ? "right" : "left") + " of the centreline";

                    if (wheelHeight < 7.0 + 0.5 * Math.Max(0, -v.verticalSpeed) && x > -900)
                    {
                        bool wide = Math.Abs(y) > Strip.Width * 0.5 + 5;
                        if (wide && attempts < 3) { BeginGoAround(ap, v, "lined up " + Math.Round(Math.Abs(y)) + " m off the centreline"); break; }
                        state = State.Flare;
                        flareStartUT = ut;
                    }
                    else if (x > Strip.Length * 0.5 && height > 15 && attempts < 3) BeginGoAround(ap, v, "too high over the runway");
                    else if (x > 400 && height < 3 && wheelHeight > 20) { Fail("the ground here is far below the runway height given (" + Math.Round(Strip.Altitude) + " m)"); return; }
                    break;
                }

                case State.Flare:
                    Phase = "flare, " + Math.Round(Math.Max(0, wheelHeight), 1) + " m, sinking " + Math.Round(-v.verticalSpeed, 1) + " m/s";
                    hold.WingsLevel = true;
                    hold.Heading = Strip.Heading - OrbitMath.Clamp(y * 0.15, -4, 4);
                    hold.Speed = null;
                    hold.ThrottleSetting = 0;
                    hold.MinSpeed = 0;
                    hold.PitchFloor = 2;
                    hold.MaxPitch = 14;
                    hold.VerticalSpeed = -OrbitMath.Clamp(wheelHeight * 0.4, 0.6, 5);
                    if (v.Landed)
                    {
                        state = State.Rollout;
                        touchdownUT = ut;
                        touchdownX = x;
                        ap.Log.Add("touchdown at " + Math.Round(v.srfSpeed) + " m/s, " + Math.Round(x) + " m past the threshold, " + Math.Round(Math.Abs(y)) + " m from the centreline");
                        hold.Disengage();
                    }
                    else if (v.Splashed) { Fail("came down in the water"); return; }
                    else if (ut - flareStartUT > 45) { BeginGoAround(ap, v, "floated for 45 s without touching down"); }
                    break;

                case State.Rollout:
                {
                    Phase = "rolling out, " + Math.Round(v.srfSpeed) + " m/s";
                    ap.SetThrottle(0);
                    // Wings level with the ailerons and a touch of up elevator: braking pushes the nose down, and
                    // an aircraft riding on its nose wheel swaps ends.
                    ap.UseTrim = false;
                    ap.RollTarget = 0;
                    ap.SetPitchHeading(1.5, Strip.Heading);
                    steering.Apply(ap, v, Strip.Heading - OrbitMath.Clamp(y * 0.2, -6, 6));

                    // Anti-skid: no brakes until the nose wheel has been down for a second, and none whenever
                    // the aircraft starts to swing, dip or lean. Held steady, full braking is safe at any speed.
                    double bank = Aero.Bank(v);
                    bool unsteady = Math.Abs(steering.Error) > 3 || Math.Abs(steering.HeadingRate) > 6 || Aero.Pitch(v) < -3 || (!double.IsNaN(bank) && Math.Abs(bank) > 5);
                    if (unsteady) steadySince = -1; else if (steadySince < 0) steadySince = ut;
                    bool brake = v.Landed && ut - touchdownUT > 1.0 && steadySince >= 0 && ut - steadySince > 0.4;
                    if (v.ActionGroups[KSPActionGroup.Brakes] != brake) v.ActionGroups.SetGroup(KSPActionGroup.Brakes, brake);

                    if (!v.Landed)
                    {
                        if (floatingSince < 0) floatingSince = ut;
                        if (ut - floatingSince > 2.5 && wheelHeight > 3)
                        {
                            // Bounced back into the air: fly it down again.
                            GroundSteering.Release(ap);
                            hold.Engage(v);
                            state = State.Flare;
                            flareStartUT = ut;
                        }
                    }
                    else floatingSince = -1;
                    if (v.Landed && v.srfSpeed < 0.15)
                    {
                        v.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                        Finish("landed at " + Strip.Name + ": touched down " + Math.Round(touchdownX) + " m past the threshold and stopped after " + Math.Round(x - touchdownX) + " m, " +
                               Math.Round(Math.Abs(y), 1) + " m from the centreline" + (attempts > 1 ? " (attempt " + attempts + ")" : "") + "; brakes are on");
                    }
                    break;
                }

                case State.GoAround:
                    Phase = "going around, " + Math.Round(height) + " m";
                    hold.WingsLevel = false;
                    hold.PitchFloor = null;
                    hold.MaxPitch = 20;
                    hold.BankLimit = 30;
                    hold.VerticalSpeed = null;
                    hold.Altitude = Strip.Altitude + 450;
                    hold.Speed = null;
                    hold.ThrottleSetting = 1;
                    hold.Heading = Strip.Heading;
                    if (height > 60 && gearDown) { Gear.Set(v, false); gearDown = false; }
                    if (height > 300)
                    {
                        startAltitude = v.altitude;
                        state = State.Intercept;
                    }
                    break;
            }
        }

        void BeginGoAround(Autopilot ap, Vessel v, string why)
        {
            attempts++;
            state = State.GoAround;
            ap.Plane.Engage(v);
            ap.Log.Add("going around: " + why);
        }

        public override JObj Detail() => new JObj().Add("along_m", Math.Round(x)).Add("off_centreline_m", Math.Round(y, 1)).Add("attempt", attempts);

        public override void OnEnd(Autopilot ap)
        {
            GroundSteering.Release(ap);
            ap.Plane.MaxSink = 25;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null && v.Landed)
            {
                ap.Plane.Disengage();
                if (!ap.HandingOver) ap.SetThrottle(0);
                ap.Throttle = null;
                ap.RollTarget = null;
                ap.SetAttitude(AttitudeMode.Off);
            }
            else if (ap.Plane.Enabled)
            {
                // Abandoned in the air: stop descending and keep flying.
                ap.Plane.WingsLevel = false;
                ap.Plane.PitchFloor = null;
                ap.Plane.VerticalSpeed = null;
                ap.Plane.BankLimit = 30;
                ap.Plane.MaxPitch = 20;
                if (v != null) ap.Plane.Altitude = Math.Max(v.altitude, Strip.Altitude + 300);
                ap.Plane.Speed = null;
                ap.Plane.ThrottleSetting = 1;
            }
        }
    }
}
