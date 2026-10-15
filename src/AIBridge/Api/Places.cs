using System;
using System.Collections.Generic;
using AIBridge.Pilot;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// The worlds and the space centre's places as the game has them, not as the stock game has them. A planet pack
    /// (Kopernicus) can show the worlds under other names (Real Worlds shows Kerbin as Earth and the Mun as the Moon) and
    /// move the space centre (Real Worlds puts it at Cape Canaveral, turning its runway six degrees). So a world is found
    /// by either of its names and told by the one the player sees, and the launch pad and the runway are read from the
    /// game's own launch sites; the figures measured in the stock game are kept only for when the game gives none and the
    /// space centre stands where it always has.
    /// </summary>
    public static class Places
    {
        /// <summary>A world's name as the player sees it ("Kerbin" in the stock game, "Earth" under Real Worlds).</summary>
        public static string Name(CelestialBody b) => b == null ? "" : b.displayName.LocalizeRemoveGender();

        /// <summary>A world's name as said in a sentence: "the Mun", "the Moon", "the Sun", otherwise its name.</summary>
        public static string Spoken(CelestialBody b)
        {
            string n = Name(b);
            return n == "Mun" || n == "Moon" || n == "Sun" ? "the " + n : n;
        }

        /// <summary>A world by its shown name or its name in the game's files, in any capitals; null where there is none.</summary>
        public static CelestialBody Body(string name)
        {
            if (string.IsNullOrEmpty(name) || FlightGlobals.Bodies == null) return null;
            name = name.Trim();
            foreach (CelestialBody b in FlightGlobals.Bodies) if (b.bodyName.Equals(name, StringComparison.OrdinalIgnoreCase)) return b;
            foreach (CelestialBody b in FlightGlobals.Bodies) if (Name(b).Equals(name, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        /// <summary>The names of every world, as shown (for messages that list them).</summary>
        public static string AllNames()
        {
            var names = new List<string>();
            if (FlightGlobals.Bodies != null) foreach (CelestialBody b in FlightGlobals.Bodies) names.Add(Name(b));
            return string.Join(", ", names.ToArray());
        }

        /// <summary>A spawn point of one of the space centre's facilities ("LaunchPad", "Runway"), where the game puts a craft.</summary>
        static bool Facility(string facility, string spawn, out double lat, out double lon, out double alt, out double heading)
        {
            lat = lon = alt = heading = double.NaN;
            PSystemSetup ps = PSystemSetup.Instance;
            if (ps == null || ps.SpaceCenterFacilityLaunchSites == null) return false;
            foreach (PSystemSetup.SpaceCenterFacility f in ps.SpaceCenterFacilityLaunchSites)
            {
                if (f == null || f.name != facility || f.spawnPoints == null || f.hostBody == null) continue;
                foreach (PSystemSetup.SpaceCenterFacility.SpawnPoint sp in f.spawnPoints)
                {
                    if (sp == null || sp.name != spawn) continue;
                    if (!sp.latlonaltSet) sp.SetSpawnPointLatLonAlt();
                    if (!sp.latlonaltSet) return false;
                    lat = sp.latitude; lon = sp.longitude; alt = sp.altitude;
                    heading = Heading(f.hostBody, sp.GetSpawnPointTransform());
                    return true;
                }
            }
            return false;
        }

        /// <summary>One of the game's other launch sites (the island airfield, Making History's), where it puts a craft.</summary>
        static bool Site(string name, out double lat, out double lon, out double alt)
        {
            lat = lon = alt = double.NaN;
            PSystemSetup ps = PSystemSetup.Instance;
            if (ps == null || ps.LaunchSites == null) return false;
            foreach (LaunchSite site in ps.LaunchSites)
            {
                if (site == null || site.name != name || site.spawnPoints == null || site.spawnPoints.Length == 0) continue;
                LaunchSite.SpawnPoint sp = site.spawnPoints[0];
                if (!sp.latlonaltSet) sp.SetSpawnPointLatLonAlt();
                if (!sp.latlonaltSet) return false;
                lat = sp.latitude; lon = sp.longitude; alt = sp.altitude;
                return true;
            }
            return false;
        }

        /// <summary>Which way (compass degrees) a spawn point faces over the ground: a craft is put down facing that way.</summary>
        static double Heading(CelestialBody body, Transform t)
        {
            if (body == null || t == null) return double.NaN;
            Vector3d up = ((Vector3d)t.position - body.position).normalized;
            Vector3d east = Vector3d.Cross(up, (Vector3d)body.transform.up).normalized, north = Vector3d.Cross(east, up);
            Vector3d f = t.forward;
            return (Math.Atan2(Vector3d.Dot(f, east), Vector3d.Dot(f, north)) * 180.0 / Math.PI + 360.0) % 360.0;
        }

        /// <summary>The space centre stands where it does in the stock game (its pad within a few hundred metres of it).</summary>
        static bool StockCentre()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || Math.Abs(home.Radius - 600000.0) > 1.0) return false;
            if (!Facility("LaunchPad", "LaunchPad", out double lat, out double lon, out _, out _)) return true;
            return Math.Abs(lat - StockPad[0]) < 0.01 && Math.Abs(lon - StockPad[1]) < 0.01;
        }

        static readonly double[] StockPad = { -0.0972, -74.5577 };

        /// <summary>Where the launch pad is (its spawn point; in the stock game -0.0972, -74.5577).</summary>
        public static bool Pad(out double lat, out double lon)
        {
            if (StockCentre()) { lat = StockPad[0]; lon = StockPad[1]; return true; }
            return Facility("LaunchPad", "LaunchPad", out lat, out lon, out _, out _);
        }

        /// <summary>
        /// The space centre's runway, landing eastbound from its western end or westbound from its eastern. In the stock game,
        /// the figures measured from the game's colliders (2.45 km on heading 90.4, 68.5 m up, 60 m wide), which the autoland
        /// was tuned on. Where a planet pack moved the space centre (its heading and height change with it), the game's own
        /// spawn points at the two ends, "Runway" and "Runway27", facing the way a craft takes off from each.
        /// </summary>
        public static Runway Ksc(bool eastbound)
        {
            if (StockCentre())
                return eastbound
                    ? new Runway { Name = "KSC runway 09", Lat = -0.0486, Lon = -74.7244, Heading = 90.4, Altitude = 68.5, Length = 2450, Width = 60 }
                    : new Runway { Name = "KSC runway 27", Lat = -0.0502, Lon = -74.4905, Heading = 270.4, Altitude = 68.5, Length = 2450, Width = 60 };
            if (Facility("Runway", "Runway", out double wLat, out double wLon, out double wAlt, out double wHdg)
                && Facility("Runway", "Runway27", out double eLat, out double eLon, out double eAlt, out double eHdg))
            {
                CelestialBody home = FlightGlobals.GetHomeBody();
                double length = SurfaceApi.GroundDistance(home, wLat, wLon, eLat, eLon);
                double alt = (wAlt + eAlt) / 2.0;
                if (double.IsNaN(wHdg)) wHdg = SurfaceApi.Bearing(wLat, wLon, eLat, eLon);
                if (double.IsNaN(eHdg)) eHdg = SurfaceApi.Bearing(eLat, eLon, wLat, wLon);
                return eastbound
                    ? new Runway { Name = "KSC runway " + Number(wHdg), Lat = wLat, Lon = wLon, Heading = wHdg, Altitude = alt, Length = length, Width = 60 }
                    : new Runway { Name = "KSC runway " + Number(eHdg), Lat = eLat, Lon = eLon, Heading = eHdg, Altitude = alt, Length = length, Width = 60 };
            }
            throw new ApiException("The game gives no runway here: give runway: {lat, lon, heading, altitude_m}.");
        }

        /// <summary>The old airfield on the island south-east of the space centre: a 1.15 km strip, 133 m up (measured in the
        /// stock game). Only where the island stands where it always has: a planet pack may take it out or leave it elsewhere.</summary>
        public static Runway Island(bool eastbound)
        {
            bool stockIsland = StockCentre();
            if (Site("Island_Airfield", out double lat, out double lon, out _))
                stockIsland = stockIsland && Math.Abs(lat - (-1.54)) < 0.05 && Math.Abs(lon - (-71.91)) < 0.05;
            if (!stockIsland) throw new ApiException("There is no island airfield in this game (the space centre or its worlds were changed by a planet pack).");
            return eastbound
                ? new Runway { Name = "Island airfield 09", Lat = -1.5175, Lon = -71.9650, Heading = 90.1, Altitude = 133, Length = 1150, Width = 60 }
                : new Runway { Name = "Island airfield 27", Lat = -1.5177, Lon = -71.8550, Heading = 270.1, Altitude = 133, Length = 1150, Width = 60 };
        }

        /// <summary>A runway's number from its heading: 84.4 degrees is runway 08.</summary>
        static string Number(double heading)
        {
            int n = (int)Math.Round(heading / 10.0) % 36;
            return (n == 0 ? 36 : n).ToString("00");
        }

        /// <summary>The named places on the home world, as the game has them now: the pad, the runway's middle and ends, and
        /// the island's runway where it is there.</summary>
        public static Dictionary<string, double[]> Named()
        {
            var d = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            if (Pad(out double lat, out double lon)) { d["launchpad"] = new[] { lat, lon }; d["ksc"] = new[] { lat, lon }; }
            try
            {
                Runway w = Ksc(true), e = Ksc(false);
                d["runway"] = new[] { (w.Lat + e.Lat) / 2.0, (w.Lon + e.Lon) / 2.0 };
                d["runway_west"] = new[] { w.Lat, w.Lon };
                d["runway_east"] = new[] { e.Lat, e.Lon };
            }
            catch (ApiException) { }
            try { Runway i = Island(true), j = Island(false); d["island_runway"] = new[] { (i.Lat + j.Lat) / 2.0, (i.Lon + j.Lon) / 2.0 }; }
            catch (ApiException) { }
            return d;
        }
    }
}
