using System;
using System.Reflection;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// The weather, where the Natural Weather mod is installed: the bridge does not need it, and finds what it offers by name
    /// (NaturalWeather.Bridge). Without it the air is still, as the game has it, and every answer says so.
    /// </summary>
    public static class WeatherApi
    {
        public static void Register(BridgeHost h)
        {
            Rpc.Sync("weather", "The weather where the craft is (or at the space centre), from the Natural Weather mod if it is installed: the sky, rain or snow, storms, fog, dust, how far one can see, the clouds in layers, the wind at 10 m and aloft, the gusts, the wind on the craft this moment and its airspeed, and the launch go / no go for the pad with its rules and a three-hour forecast. Without that mod the air is still.", a => Report()).AlwaysAllowed = true;
        }

        static Assembly seenIn;
        static MethodInfo report, brief, wind;

        static void Find()
        {
            // (looked for again when the libraries change: the development build swaps itself in afresh)
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            if (seenIn != null && Array.IndexOf(all, seenIn) >= 0 && report != null) return;
            report = brief = wind = null; seenIn = null;
            for (int n = all.Length - 1; n >= 0; n--)
            {
                Type t;
                try { t = all[n].GetType("NaturalWeather.Bridge", false); } catch (Exception) { continue; }
                if (t == null) continue;
                report = t.GetMethod("Report", BindingFlags.Public | BindingFlags.Static);
                brief = t.GetMethod("Brief", BindingFlags.Public | BindingFlags.Static);
                wind = t.GetMethod("Wind", BindingFlags.Public | BindingFlags.Static);
                seenIn = all[n];
                return;
            }
        }

        static object Report()
        {
            Find();
            if (report == null) return new JObj().Add("installed", false).Add("note", "No weather mod (Natural Weather) is installed: the air is still and the sky clear, as the game has it.");
            try { return report.Invoke(null, new object[] { FlightGlobals.ActiveVessel }); }
            catch (Exception ex) { return new JObj().Add("installed", true).Add("error", (ex.InnerException ?? ex).Message); }
        }

        /// <summary>One line for "status", or null without the weather mod.</summary>
        public static string Brief()
        {
            Find();
            if (brief == null) return null;
            try { return brief.Invoke(null, null) as string; } catch (Exception) { return null; }
        }

        /// <summary>The wind on a craft this moment (world space, m/s), for the autopilots to fly by the air and not the ground; zero without the weather mod.</summary>
        public static Vector3d WindOn(Vessel v)
        {
            Find();
            if (wind == null || v == null) return Vector3d.zero;
            try { return (Vector3d)wind.Invoke(null, new object[] { v }); } catch (Exception) { return Vector3d.zero; }
        }
    }
}
