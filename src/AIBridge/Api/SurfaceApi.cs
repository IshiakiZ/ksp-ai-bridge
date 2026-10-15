using System;
using System.Collections.Generic;
using AIBridge.Pilot;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>Surface travel: rover driving, and the shared way of naming a spot on the ground.</summary>
    public static class SurfaceApi
    {
        static BridgeHost host;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("drive", "Drive a rover over the ground to a spot. params: {lat, lon | target: vessel name | place: runway|launchpad | heading, distance_m, speed, stop_distance_m}", Drive);
            Rpc.Sync("fly", "Aircraft autopilot. params: {mode: takeoff|cruise|goto|land|off, heading, altitude_m, speed, throttle, lat, lon, target, place, runway, approach_speed, glideslope, final_km, rotate_speed, pitch, bank_limit, radius_m, terrain_clearance_m}", Fly);
            Rpc.Sync("terrain", "The ground at a spot: height, slope, biome and what is standing there. params: {lat, lon | target | place | heading, distance_m}", Terrain).AlwaysAllowed = true;
        }

        /// <summary>A named spot on the ground of a body.</summary>
        public struct Spot
        {
            public double Lat, Lon;
            public string Label;
        }

        public static bool HasSpot(Args a) => a.Has("lat") || a.Has("lon") || a.Has("target") || a.Has("place") || a.Has("distance_m");

        /// <summary>Work out which spot the caller means: coordinates, a vessel, a landmark, or a heading and distance from here.</summary>
        public static Spot Resolve(Args a, Vessel v)
        {
            CelestialBody body = v.mainBody;
            if (a.Has("lat") || a.Has("lon"))
            {
                if (!a.Has("lat") || !a.Has("lon")) throw new ApiException("Give both lat and lon (degrees; west and south are negative).");
                double lat = a.Num("lat"), lon = a.Num("lon");
                if (lat < -90 || lat > 90) throw new ApiException("lat must be between -90 and 90.");
                return new Spot { Lat = lat, Lon = NormLon(lon), Label = "latitude " + Math.Round(lat, 4) + ", longitude " + Math.Round(NormLon(lon), 4) };
            }
            if (a.Has("target"))
            {
                string name = a.Str("target");
                Vessel other = NavApi.FindVessel(name);
                if (other == null) throw new ApiException("No vessel named '" + name + "'. Use vessels to list them.");
                if (other == v) throw new ApiException("That is the vessel being flown.");
                if (other.mainBody != body) throw new ApiException("'" + other.GetDisplayName() + "' is at " + Places.Name(other.mainBody) + ", not " + Places.Name(body) + ".");
                if (!other.LandedOrSplashed) throw new ApiException("'" + other.GetDisplayName() + "' is not on the ground (" + other.situation + ").");
                return new Spot { Lat = other.latitude, Lon = NormLon(other.longitude), Label = other.GetDisplayName() };
            }
            if (a.Has("place"))
            {
                string place = a.Str("place").Trim().Replace(" ", "_");
                if (!body.isHomeWorld) throw new ApiException("Named places exist only on " + Places.Name(FlightGlobals.GetHomeBody()) + ".");
                Dictionary<string, double[]> named = Places.Named();
                if (!named.TryGetValue(place, out double[] ll)) throw new ApiException("Unknown place '" + place + "'. Known: " + string.Join(", ", new List<string>(named.Keys).ToArray()) + ".");
                return new Spot { Lat = ll[0], Lon = ll[1], Label = "the " + place.Replace("_", " ") };
            }
            if (a.Has("distance_m"))
            {
                double heading = a.Num("heading", double.NaN);
                if (double.IsNaN(heading)) throw new ApiException("distance_m needs a heading (compass degrees, 90 = east).");
                Spot s = Offset(body, v.latitude, v.longitude, heading, a.Num("distance_m"));
                s.Label = Math.Round(a.Num("distance_m")) + " m on heading " + Math.Round(heading);
                return s;
            }
            throw new ApiException("Say where: lat and lon, target (a landed vessel), place (" + string.Join(", ", new List<string>(Places.Named().Keys).ToArray()) + "), or heading with distance_m.");
        }

        public static double NormLon(double lon)
        {
            while (lon > 180) lon -= 360;
            while (lon < -180) lon += 360;
            return lon;
        }

        /// <summary>The point a given distance along a compass heading from a start point (great circle).</summary>
        public static Spot Offset(CelestialBody body, double latDeg, double lonDeg, double headingDeg, double distance)
        {
            double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180, th = headingDeg * Math.PI / 180, d = distance / body.Radius;
            double lat2 = Math.Asin(Math.Sin(lat) * Math.Cos(d) + Math.Cos(lat) * Math.Sin(d) * Math.Cos(th));
            double lon2 = lon + Math.Atan2(Math.Sin(th) * Math.Sin(d) * Math.Cos(lat), Math.Cos(d) - Math.Sin(lat) * Math.Sin(lat2));
            return new Spot { Lat = lat2 * 180 / Math.PI, Lon = NormLon(lon2 * 180 / Math.PI) };
        }

        /// <summary>Distance over the ground between two points.</summary>
        public static double GroundDistance(CelestialBody body, double lat1, double lon1, double lat2, double lon2)
        {
            Vector3d a = body.GetRelSurfaceNVector(lat1, lon1), b = body.GetRelSurfaceNVector(lat2, lon2);
            return Math.Acos(OrbitMath.Clamp(Vector3d.Dot(a, b), -1, 1)) * body.Radius;
        }

        /// <summary>Compass heading from one point toward another.</summary>
        public static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180, dl = (lon2 - lon1) * Math.PI / 180;
            double y = Math.Sin(dl) * Math.Cos(p2), x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        }

        // ---------------------------------------------------------------- aircraft

        /// <summary>How far before the threshold (negative) or past it the aircraft is, along the runway's direction.</summary>
        static double Along(Runway r, Vessel v)
        {
            double d = GroundDistance(v.mainBody, r.Lat, r.Lon, v.latitude, v.longitude);
            return d * Math.Cos((Bearing(r.Lat, r.Lon, v.latitude, v.longitude) - r.Heading) * Math.PI / 180);
        }

        static Runway ResolveRunway(Args a, Vessel v)
        {
            Args custom = a.Obj("runway");
            if (custom != null)
            {
                if (!custom.Has("lat") || !custom.Has("lon") || !custom.Has("heading")) throw new ApiException("A custom runway needs lat, lon (where to touch down) and heading (the direction of landing); altitude_m, length_m and width_m are optional.");
                var r = new Runway { Name = custom.Str("name", "the strip"), Lat = custom.Num("lat"), Lon = NormLon(custom.Num("lon")), Heading = ((custom.Num("heading") % 360) + 360) % 360 };
                r.Altitude = custom.Has("altitude_m") ? custom.Num("altitude_m") : Math.Max(0, v.mainBody.TerrainAltitude(r.Lat, r.Lon, true));
                r.Length = custom.Num("length_m", 2000);
                r.Width = custom.Num("width_m", 60);
                return r;
            }
            string name = (a.Str("runway") ?? "ksc").Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
            if (!v.mainBody.isHomeWorld) throw new ApiException("The named runways are on " + Places.Name(FlightGlobals.GetHomeBody()) + ". Elsewhere give runway: {lat, lon, heading, altitude_m}.");
            switch (name)
            {
                case "ksc_09": case "ksc_east": case "09": return Places.Ksc(true);
                case "ksc_27": case "ksc_west": case "27": return Places.Ksc(false);
                case "ksc": case "runway": case "space_center":
                    // Use whichever end the aircraft is already on the approach side of.
                    { Runway east = Places.Ksc(true); return Along(east, v) < east.Length / 2 ? east : Places.Ksc(false); }
                case "island_09": return Places.Island(true);
                case "island_27": return Places.Island(false);
                case "island": case "island_airfield": case "island_runway":
                    { Runway east = Places.Island(true); return Along(east, v) < east.Length / 2 ? east : Places.Island(false); }
            }
            throw new ApiException("Unknown runway '" + name + "'. Use ksc, ksc_09, ksc_27, island, island_09, island_27, or give runway: {lat, lon, heading, altitude_m, length_m}.");
        }

        static void RequireAircraft(Vessel v)
        {
            if (!v.IsControllable) throw new ApiException("The vessel is not controllable (no crew, no powered probe core, or no signal).");
            if (!v.mainBody.atmosphere) throw new ApiException(Places.Name(v.mainBody) + " has no atmosphere; wings do nothing here.");
            if (Aero.WingRating(v, out FloatCurve _) < 0.05) throw new ApiException("This vessel has no wings.");
        }

        static object Fly(Args a)
        {
            Vessel v = FlightApi.Active();
            Autopilot ap = Autopilot.I;
            string mode = (a.Str("mode") ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
            if (mode == "" || mode == "status") return ap.Status();
            if (mode == "off" || mode == "release")
            {
                ap.Release("aircraft autopilot switched off", false);
                return ap.Status();
            }
            RequireAircraft(v);
            AircraftHold hold = ap.Plane;
            bool onGround = v.LandedOrSplashed;

            switch (mode)
            {
                case "takeoff":
                {
                    if (!v.Landed) throw new ApiException(v.Splashed ? "The aircraft is in the water." : "The aircraft is already flying; use mode cruise, goto or land.");
                    var p = new TakeoffProgram { TargetAltitude = a.Has("altitude_m") ? a.Num("altitude_m") : v.altitude + 1000 };
                    if (p.TargetAltitude < v.altitude + 100) throw new ApiException("altitude_m is the height above sea level to climb to; it must be at least 100 m above the runway (" + Math.Round(v.altitude) + " m).");
                    if (a.Has("heading")) p.HeadingAfter = ((a.Num("heading") % 360) + 360) % 360;
                    if (a.Has("speed")) p.CruiseSpeed = Math.Max(20, a.Num("speed"));
                    if (a.Has("rotate_speed")) p.RotateSpeed = Math.Max(10, a.Num("rotate_speed"));
                    p.RotatePitch = OrbitMath.Clamp(a.Num("pitch", 10), 4, 25);
                    ap.Start(p);
                    JObj status = ap.Status();
                    double lift = Aero.LiftSpeed(v, 10);
                    if (!double.IsNaN(lift)) status.Add("estimated_liftoff_speed", Math.Round(lift));
                    return status;
                }

                case "cruise": case "hold":
                {
                    if (onGround) throw new ApiException("The aircraft is on the ground; use mode takeoff.");
                    // End whatever program was flying first, so that its clean-up cannot undo the new settings.
                    ap.AbortProgram("replaced by cruise");
                    bool fresh = !hold.Enabled;
                    hold.Engage(v);
                    if (a.Has("heading")) hold.Heading = ((a.Num("heading") % 360) + 360) % 360; else if (fresh) hold.Heading = Aero.Track(v);
                    if (a.Has("altitude_m")) hold.Altitude = a.Num("altitude_m"); else if (fresh) hold.Altitude = v.altitude;
                    hold.VerticalSpeed = null;
                    hold.WingsLevel = false;
                    hold.PitchFloor = null;
                    ApplySpeed(a, hold, fresh);
                    if (a.Has("bank_limit")) hold.BankLimit = OrbitMath.Clamp(a.Num("bank_limit"), 5, 60);
                    if (a.Has("terrain_clearance_m")) hold.TerrainClearance = OrbitMath.Clamp(a.Num("terrain_clearance_m"), 0, 5000);
                    SetMinSpeed(v, hold);
                    host.Note(hold.Describe());
                    return ap.Status();
                }

                case "goto": case "flyto":
                {
                    if (onGround) throw new ApiException("The aircraft is on the ground; use mode takeoff first.");
                    Spot spot = Resolve(a, v);
                    ap.AbortProgram("replaced by goto");
                    bool fresh = !hold.Enabled;
                    hold.Engage(v);
                    if (a.Has("altitude_m")) hold.Altitude = a.Num("altitude_m"); else if (fresh) hold.Altitude = v.altitude;
                    hold.VerticalSpeed = null;
                    hold.WingsLevel = false;
                    hold.PitchFloor = null;
                    ApplySpeed(a, hold, fresh);
                    SetMinSpeed(v, hold);
                    var p = new FlyToProgram { TargetLat = spot.Lat, TargetLon = spot.Lon, Label = spot.Label, Radius = OrbitMath.Clamp(a.Num("radius_m", 1500), 200, 50000) };
                    ap.Start(p);
                    JObj status = ap.Status();
                    double distance = GroundDistance(v.mainBody, v.latitude, v.longitude, spot.Lat, spot.Lon);
                    status.Add("distance_m", Math.Round(distance));
                    status.Add("bearing", Math.Round(Bearing(v.latitude, v.longitude, spot.Lat, spot.Lon)));
                    if (v.srfSpeed > 20) status.Add("eta_s", Math.Round(distance / v.srfSpeed));
                    return status;
                }

                case "land": case "landing":
                {
                    if (onGround) throw new ApiException("The aircraft is already on the ground.");
                    Runway strip = ResolveRunway(a, v);
                    var p = new RunwayLandProgram
                    {
                        Strip = strip,
                        Glideslope = OrbitMath.Clamp(a.Num("glideslope", 4), 2, 10),
                        FinalDistance = OrbitMath.Clamp(a.Num("final_km", 8), 3, 30) * 1000.0
                    };
                    if (a.Has("approach_speed")) p.ApproachSpeed = Math.Max(15, a.Num("approach_speed"));
                    ap.Start(p);
                    JObj status = ap.Status();
                    status.Add("runway", new JObj().Add("name", strip.Name).Add("lat", strip.Lat).Add("lon", strip.Lon).Add("heading", strip.Heading).Add("altitude_m", Math.Round(strip.Altitude)).Add("length_m", strip.Length));
                    status.Add("distance_m", Math.Round(GroundDistance(v.mainBody, v.latitude, v.longitude, strip.Lat, strip.Lon)));
                    return status;
                }
            }
            throw new ApiException("mode must be takeoff, cruise, goto, land or off.");
        }

        static void ApplySpeed(Args a, AircraftHold hold, bool fresh)
        {
            if (a.Has("speed")) { hold.Speed = Math.Max(15, a.Num("speed")); }
            else if (a.Has("throttle")) { hold.Speed = null; hold.ThrottleSetting = OrbitMath.Clamp(a.Num("throttle"), 0, 1); }
            else if (fresh) { hold.Speed = TakeoffProgram.DefaultCruise(FlightApi.Active()); hold.ThrottleSetting = 1; }
        }

        static void SetMinSpeed(Vessel v, AircraftHold hold)
        {
            double lift = Aero.LiftSpeed(v, 10);
            hold.MinSpeed = double.IsNaN(lift) ? 0 : 1.05 * lift;
        }

        // ---------------------------------------------------------------- the ground

        static object Terrain(Args a)
        {
            Vessel v = HighLogic.LoadedScene == GameScenes.FLIGHT ? FlightGlobals.ActiveVessel : null;
            CelestialBody body;
            Spot spot;
            if (a.Has("body"))
            {
                body = Places.Body(a.Str("body"));
                if (body == null) throw new ApiException("No body named '" + a.Str("body") + "'.");
                if (!a.Has("lat") || !a.Has("lon")) throw new ApiException("Give lat and lon.");
                spot = new Spot { Lat = a.Num("lat"), Lon = NormLon(a.Num("lon")) };
            }
            else
            {
                if (v == null) throw new ApiException("Give body, lat and lon (no vessel is being flown).");
                body = v.mainBody;
                spot = HasSpot(a) ? Resolve(a, v) : new Spot { Lat = v.latitude, Lon = NormLon(v.longitude), Label = "under the vessel" };
            }
            if (!body.hasSolidSurface) throw new ApiException(Places.Name(body) + " has no solid surface.");

            var o = new JObj();
            o.Add("body", Places.Name(body));
            o.Add("lat", Math.Round(spot.Lat, 5));
            o.Add("lon", Math.Round(spot.Lon, 5));
            double height = body.TerrainAltitude(spot.Lat, spot.Lon, true);
            o.Add("terrain_altitude_m", Math.Round(height, 1));
            if (body.ocean && height < 0) o.Add("water", "under " + Math.Round(-height) + " m of sea");

            // Slope from four samples 15 m out.
            double step = 15.0 / body.Radius * 180.0 / Math.PI;
            double north = body.TerrainAltitude(spot.Lat + step, spot.Lon, true), south = body.TerrainAltitude(spot.Lat - step, spot.Lon, true);
            double cosLat = Math.Max(0.05, Math.Cos(spot.Lat * Math.PI / 180));
            double east = body.TerrainAltitude(spot.Lat, spot.Lon + step / cosLat, true), west = body.TerrainAltitude(spot.Lat, spot.Lon - step / cosLat, true);
            double gradient = Math.Sqrt(Math.Pow((north - south) / 30.0, 2) + Math.Pow((east - west) / 30.0, 2));
            double slope = Math.Atan(gradient) * 180.0 / Math.PI;
            o.Add("slope_deg", Math.Round(slope, 1));
            o.Add("landing", slope < 5 ? "flat" : slope < 12 ? "sloping: fine for wide landers, risky for tall ones" : "steep: a lander is likely to tip or slide");
            try
            {
                string biome = ScienceUtil.GetExperimentBiome(body, spot.Lat, spot.Lon);
                if (!string.IsNullOrEmpty(biome)) o.Add("biome", ScienceUtil.GetBiomedisplayName(body, biome));
            }
            catch { }

            if (v != null && body == v.mainBody)
            {
                double distance = GroundDistance(body, v.latitude, v.longitude, spot.Lat, spot.Lon);
                o.Add("distance_m", Math.Round(distance));
                if (distance > 1) o.Add("bearing", Math.Round(Bearing(v.latitude, v.longitude, spot.Lat, spot.Lon), 1));
                // Buildings and runways are not part of the terrain; near the vessel a ray finds them.
                if (distance < 30000)
                {
                    Vector3d top = body.GetWorldSurfacePosition(spot.Lat, spot.Lon, Math.Max(height, 0) + 3000);
                    Vector3d down = (body.position - top).normalized;
                    if (Physics.Raycast(top, down, out RaycastHit hit, 6000f, 1 << 15))
                    {
                        double surface = Math.Max(height, 0) + 3000 - hit.distance;
                        if (Math.Abs(surface - height) > 0.5)
                        {
                            o.Add("surface_altitude_m", Math.Round(surface, 1));
                            string what = hit.collider.name;
                            for (Transform t = hit.collider.transform; t != null; t = t.parent)
                                if (t.GetComponent<PQSCity>() != null || t.GetComponent<PQSCity2>() != null || t.GetComponent<Upgradeables.UpgradeableObject>() != null) { what = t.name + " (" + hit.collider.name + ")"; break; }
                            o.Add("standing_there", what);
                        }
                    }
                }
            }
            return o;
        }

        static object Drive(Args a)
        {
            Vessel v = FlightApi.Active();
            if (!v.IsControllable) throw new ApiException("The vessel is not controllable (no crew, no powered probe core, or no signal).");
            if (!v.Landed) throw new ApiException("The vessel is not standing on the ground (" + v.situation + ").");
            int wheels = 0, motors = 0;
            foreach (Part p in v.parts)
            {
                if (p.FindModuleImplementing<ModuleWheelBase>() != null) wheels++;
                var motor = p.FindModuleImplementing<ModuleWheels.ModuleWheelMotor>();
                if (motor != null && motor.motorEnabled) motors++;
            }
            if (wheels == 0) throw new ApiException("This vessel has no wheels.");
            if (motors == 0) throw new ApiException("None of this vessel's wheels has a working motor (landing gear cannot drive). Aircraft taxi with the fly program instead.");
            Spot spot = Resolve(a, v);
            double distance = GroundDistance(v.mainBody, v.latitude, v.longitude, spot.Lat, spot.Lon);
            var p2 = new DriveProgram
            {
                TargetLat = spot.Lat, TargetLon = spot.Lon, Label = spot.Label,
                Speed = OrbitMath.Clamp(a.Num("speed", 8), 1, 40),
                StopDistance = OrbitMath.Clamp(a.Num("stop_distance_m", a.Has("target") ? 25 : 5), 2, 500)
            };
            Autopilot.I.Start(p2);
            JObj status = Autopilot.I.Status();
            status.Add("distance_m", Math.Round(distance));
            status.Add("bearing", Math.Round(Bearing(v.latitude, v.longitude, spot.Lat, spot.Lon)));
            if (distance > 50000) status.Add("note", "That is " + Math.Round(distance / 1000) + " km: at " + p2.Speed + " m/s it takes about " + Math.Round(distance / p2.Speed / 60) + " minutes of real time (physics warp can be set with warp rate 1-3 on gentle ground).");
            return status;
        }
    }
}
