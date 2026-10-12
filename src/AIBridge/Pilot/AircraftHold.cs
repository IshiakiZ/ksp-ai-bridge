using System;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// The aircraft flight director: holds a ground track, an altitude (or a rate of climb) and a speed by
    /// banking, pitching and moving the throttle. It keeps flying after the program that set it up has
    /// finished, the way an airliner's autopilot holds its last clearance.
    /// </summary>
    public sealed class AircraftHold
    {
        public bool Enabled { get; private set; }

        public double Heading, Altitude;
        /// <summary>Climb rate to hold instead of an altitude (approach and flare).</summary>
        public double? VerticalSpeed;
        /// <summary>Speed to hold with the throttle; without it the throttle stays at <see cref="ThrottleSetting"/>.</summary>
        public double? Speed;
        public double ThrottleSetting = 1.0;
        public double BankLimit = 30, MaxPitch = 20, MinPitch = -15, MaxAoA = 13, MaxClimb = 30, MaxSink = 25;
        /// <summary>Lowest nose attitude allowed (the flare keeps the nose wheel off the ground).</summary>
        public double? PitchFloor;
        /// <summary>Keep the wings level and point the nose along <see cref="Heading"/> with the rudder (flare).</summary>
        public bool WingsLevel;
        /// <summary>Below this speed the nose is lowered whatever the altitude target says.</summary>
        public double MinSpeed;
        /// <summary>
        /// Height to keep above the ground ahead. The hold looks along its track and climbs above its
        /// set altitude when the terrain demands it, coming back down afterwards. Zero switches this off.
        /// </summary>
        public double TerrainClearance = 300;
        /// <summary>The altitude the terrain ahead currently demands (0 when the set altitude is enough).</summary>
        public double TerrainFloor { get; private set; }
        double terrainCheckUT = -1, terrainClimbRate;
        bool terrainLogged;

        // What the controller is doing, for telemetry.
        public double Aoa, Bank, PitchCommand, BankCommand, Track;

        double aoaFiltered, throttleIntegral, lastSpeed = double.NaN, accelFiltered;

        public void Engage(Vessel v)
        {
            if (!Enabled)
            {
                aoaFiltered = Aero.AngleOfAttack(v);
                throttleIntegral = v.ctrlState != null ? v.ctrlState.mainThrottle : 0.5;
                lastSpeed = double.NaN;
                accelFiltered = 0;
            }
            Enabled = true;
            Autopilot.I.UseTrim = true;
        }

        public void Disengage()
        {
            Enabled = false;
            VerticalSpeed = null;
            PitchFloor = null;
            WingsLevel = false;
            Speed = null;
            ThrottleSetting = 1.0;
            MinSpeed = 0;
            BankLimit = 30;
            TerrainFloor = 0;
            terrainLogged = steepLogged = false;
        }

        public void Tick(Autopilot ap, Vessel v)
        {
            double dt = Math.Max(0.005, TimeWarp.fixedDeltaTime);
            // Flown by the air, not the ground: speed is airspeed, and the climb angle the one through the air
            // (they are the same without a weather mod's wind).
            Vector3d air = Aero.AirVelocity(v);
            double speed = air.magnitude;
            double rising = v.verticalSpeed - Vector3d.Dot(air, Guidance.Up(v));     // the air's own rise
            double gamma = speed > 5 ? Math.Asin(OrbitMath.Clamp((v.verticalSpeed - rising) / speed, -1, 1)) * Aero.Rad2Deg : 0;
            Aoa = Aero.AngleOfAttack(v);
            aoaFiltered += (Aoa - aoaFiltered) * Math.Min(1.0, dt / 1.5);
            Track = Aero.Track(v);
            double bank = Aero.Bank(v);
            Bank = double.IsNaN(bank) ? 0 : bank;

            // Vertical: turn the wanted climb rate into a flight-path angle, then aim the nose at that angle
            // plus the angle of attack the wing is flying at.
            double vsWanted;
            if (VerticalSpeed.HasValue) { vsWanted = VerticalSpeed.Value; TerrainFloor = 0; }
            else
            {
                double climb = Math.Min(MaxClimb, 0.2 * speed), sink = Math.Min(MaxSink, 0.2 * speed);
                LookAtTerrain(ap, v);
                double target = Math.Max(Altitude, TerrainFloor);
                vsWanted = OrbitMath.Clamp(0.10 * (target - v.altitude), -sink, climb);
                // Start the climb in time: the hill has to be cleared by the time the aircraft reaches it.
                if (terrainClimbRate > 0) vsWanted = Math.Max(vsWanted, Math.Min(climb, terrainClimbRate * 1.3));
            }
            double gammaWanted = Math.Asin(OrbitMath.Clamp((vsWanted - rising) / Math.Max(speed, 25.0), -0.5, 0.5)) * Aero.Rad2Deg;
            double pitch = gammaWanted + aoaFiltered + 1.0 * (gammaWanted - gamma);
            pitch = Math.Min(pitch, gamma + MaxAoA);
            if (MinSpeed > 0 && speed < MinSpeed)
                pitch = Math.Min(pitch, gamma + aoaFiltered - (MinSpeed - speed) * 0.6);   // too slow: give up height for speed
            pitch = OrbitMath.Clamp(pitch, MinPitch, MaxPitch);
            if (PitchFloor.HasValue) pitch = Math.Max(pitch, PitchFloor.Value);
            PitchCommand = pitch;

            // Lateral: bank toward the wanted track over the ground, and keep the nose in line with the airflow
            // (in a crosswind that is off the track: the aircraft crabs into the wind).
            double headingError = Aero.Wrap(Heading - Track);
            BankCommand = WingsLevel ? 0 : OrbitMath.Clamp(headingError * 1.5, -BankLimit, BankLimit);
            double noseHeading = WingsLevel ? Heading : Aero.AirTrack(v);
            ap.FixedDirection = Guidance.FromPitchHeading(v, pitch, noseHeading);
            ap.RollTarget = BankCommand;
            ap.SetAttitude(AttitudeMode.Fixed);

            if (Speed.HasValue)
            {
                if (!double.IsNaN(lastSpeed)) accelFiltered += ((speed - lastSpeed) / dt - accelFiltered) * Math.Min(1.0, dt / 0.6);
                lastSpeed = speed;
                double error = Speed.Value - speed;
                throttleIntegral = OrbitMath.Clamp(throttleIntegral + error * 0.006 * dt, 0, 1);
                ap.SetThrottle(OrbitMath.Clamp(0.08 * error + throttleIntegral - 0.12 * accelFiltered, 0, 1));
            }
            else ap.SetThrottle(ThrottleSetting);
        }

        /// <summary>Sample the ground along the track for the next minute or so of flight and work out how high the aircraft has to be.</summary>
        void LookAtTerrain(Autopilot ap, Vessel v)
        {
            if (TerrainClearance <= 0) { TerrainFloor = 0; terrainClimbRate = 0; return; }
            double ut = Planetarium.GetUniversalTime();
            if (ut - terrainCheckUT < 0.5) return;
            terrainCheckUT = ut;
            CelestialBody body = v.mainBody;
            double ground = Math.Max(30.0, v.horizontalSrfSpeed);
            double floor = 0, rate = 0;
            // Look along the wanted heading as well as the present track, so a turn does not lead into a hillside.
            foreach (double heading in new[] { Track, Track - 6, Track + 6, Heading })
            {
                foreach (double seconds in new[] { 4.0, 9.0, 15.0, 23.0, 33.0, 45.0, 60.0, 80.0, 105.0, 135.0, 170.0 })
                {
                    Api.SurfaceApi.Spot spot = Api.SurfaceApi.Offset(body, v.latitude, v.longitude, heading, ground * seconds);
                    double needed = Math.Max(0, body.TerrainAltitude(spot.Lat, spot.Lon, true)) + TerrainClearance;
                    if (needed > floor) floor = needed;
                    if (needed > v.altitude) rate = Math.Max(rate, (needed - v.altitude) / seconds);
                }
            }
            terrainClimbRate = rate;
            if (floor > Altitude + 5)
            {
                TerrainFloor = floor;
                if (!terrainLogged)
                {
                    terrainLogged = true;
                    ap.Log.Add("high ground ahead: climbing to " + Math.Round(floor) + " m to stay " + Math.Round(TerrainClearance) + " m above it (set altitude " + Math.Round(Altitude) + " m)");
                }
                double canClimb = Math.Min(MaxClimb, 0.2 * Aero.Airspeed(v));
                if (rate > canClimb * 1.5 && !steepLogged) { steepLogged = true; ap.Log.Add("warning: the ground ahead rises faster than this aircraft climbs (" + Math.Round(rate) + " m/s needed); turn away or slow down"); }
            }
            else
            {
                if (terrainLogged) ap.Log.Add("clear of the high ground: back to " + Math.Round(Altitude) + " m");
                terrainLogged = steepLogged = false;
                TerrainFloor = 0;
            }
        }
        bool steepLogged;

        public string Describe() =>
            "aircraft hold: heading " + Math.Round(Heading) + ", " + (VerticalSpeed.HasValue ? "vertical speed " + Math.Round(VerticalSpeed.Value, 1) + " m/s" : "altitude " + Math.Round(Altitude) + " m") +
            (Speed.HasValue ? ", speed " + Math.Round(Speed.Value) + " m/s" : ", throttle " + Math.Round(ThrottleSetting * 100) + "%");

        public JObj Status(Vessel v)
        {
            var o = new JObj();
            o.Add("heading", Math.Round(Heading, 1));
            if (VerticalSpeed.HasValue) o.Add("vertical_speed", Math.Round(VerticalSpeed.Value, 1)); else o.Add("altitude_m", Math.Round(Altitude));
            if (Speed.HasValue) o.Add("speed", Math.Round(Speed.Value, 1)); else o.Add("throttle", Math.Round(ThrottleSetting, 2));
            if (TerrainFloor > 0 && !VerticalSpeed.HasValue) o.Add("terrain_floor_m", Math.Round(TerrainFloor));
            if (v.radarAltitude < 20000) o.Add("height_above_ground_m", Math.Round(v.radarAltitude));
            o.Add("now", new JObj().Add("track", Math.Round(Track, 1)).Add("altitude_m", Math.Round(v.altitude)).Add("speed", Math.Round(Aero.Airspeed(v), 1)).Add("ground_speed", Math.Round(v.srfSpeed, 1)).Add("vertical_speed", Math.Round(v.verticalSpeed, 1))
                .Add("bank", Math.Round(Bank, 1)).Add("pitch", Math.Round(Aero.Pitch(v), 1)).Add("angle_of_attack", Math.Round(Aoa, 1)));
            return o;
        }
    }
}
