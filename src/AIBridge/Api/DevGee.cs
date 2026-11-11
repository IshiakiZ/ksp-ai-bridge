#if DEV
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// (Development build: the g the crew feel, frame by frame, for the jitter of the crew's heads in their portraits while a craft
    /// set down somewhere sits there, 2026-10-27.) Each frame, after Smooth Portraits has stepped its ride: what the game says the
    /// craft feels (its geeForce, and its perturbation along the craft and across it, smoothed and not), what its gravity is
    /// multiplied by and whether either of the game's eases is on, whether it is landed or packed, how fast it goes and turns, how
    /// far it has drifted from where it was, how many parts it has; and the push Smooth Portraits' ride takes from it, and the
    /// lean of each head it gives.
    ///
    /// Through dev_call: Start("seconds"), then Report("a .csv to write every frame to"), which says it in a few lines. Nothing
    /// is left in the game afterwards.
    /// </summary>
    public static class DevGee
    {
        static GameObject holder;
        static float began, until;
        static int steps;
        static readonly List<float[]> rows = new List<float[]>();
        static readonly StringBuilder changes = new StringBuilder();
        static Type ride;
        static CelestialBody body;
        static double lat0, lon0, alt0;
        static string lastSituation;
        static float lastGravity = float.NaN;
        static int lastEase = -1, lastPhysicsEase = -1, lastParts = -1, lastPacked = -1;
        static readonly List<string> heads = new List<string>();

        const int Heads = 3;
        static readonly string[] Columns = { "t", "dt", "steps", "gee", "gee_immediate", "along_g", "across_g", "along_immediate_g", "across_immediate_g",
            "gravity_x", "ease_to_surface", "physics_ease", "landed", "packed", "srf_speed", "vertical_speed", "turning_rad_s", "height_m", "drift_m", "parts",
            "ride_push_g", "ride_buzz", "nod1", "side1", "nod2", "side2", "nod3", "side3",
            "accel_ms2", "accel_immediate_ms2", "ignore_g_frames", "bob1_deg", "bob2_deg", "bob3_deg", "pose1_deg", "pose2_deg", "pose3_deg",
            "outer_lights", "outer_light", "outer_light_d", "outer_which", "outer_light_r", "head_from_part_m", "cabin_own", "cabin_sun", "portrait_lamp", "lamps_on_craft", "lamps_on_craft_light",
            "pic_sun", "pic_sun_least", "pic_ambient", "pictures", "sun_00", "sun_01" };
        // (the light the portraits are drawn in, at the moment each is drawn since the last row: the cabin's sun, the least of it, and the
        // light from all round (the ambient's first term, as a brightness), and how many were drawn; and the scene's sun as each of its two
        // cameras drew it. For the light on the crew's faces flickering between two looks under a storm, 2026-10-28)
        static float picSun = float.NaN, picSunLeast = float.NaN, picAmbient = float.NaN, sun00 = float.NaN, sun01 = float.NaN;
        static int pictures;
        static Light cabinSun;
        static int cabinSunLooked = -100;
        // (the lights on the cabin, for the warm light from one side seen on the crew's faces after a crash, 2026-10-27: a light of the
        // world's that also lights the cabin's layers, the insides of cockpits and the crew in them; how much of it reaches the first
        // head, by Unity's own fall of a lamp's light with distance; which of them is strongest, by its name's place in `outer`)
        const int CabinLayers = (1 << 16) | (1 << 20), WorldLayers = (1 << 0) | (1 << 15);
        static readonly List<string> outer = new List<string>();
        static Light[] lights = new Light[0];
        static int lightsLooked = -100;
        static bool shut;
        static readonly Dictionary<Light, int> shutWas = new Dictionary<Light, int>();
        static readonly FieldInfo ignoring = typeof(Vessel).GetField("ignoreGeeforceFrames", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        static readonly Quaternion?[] lastPose = new Quaternion?[Heads];

        public static string Start(string arg)
        {
            Stop();
            float seconds = 8f;
            float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            if (seconds <= 0f) seconds = 8f;
            rows.Clear(); changes.Clear(); heads.Clear(); outer.Clear();
            for (int i = 0; i < Heads; i++) lastPose[i] = null;
            lastSituation = null; lastGravity = float.NaN; lastEase = lastPhysicsEase = lastParts = lastPacked = -1;
            steps = 0;
            began = Time.realtimeSinceStartup;
            until = began + Mathf.Min(seconds, 600f);
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "no vessel";
            body = v.mainBody;
            lat0 = body.GetLatitude(v.CoMD); lon0 = body.GetLongitude(v.CoMD); alt0 = body.GetAltitude(v.CoMD);
            // (the copy of Smooth Portraits' ride that is running: where the development build has swapped its own in beside the
            // installed one, the one stepped most lately)
            ride = null;
            int latest = int.MinValue;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType("SmoothPortraits.Ride", false);
                FieldInfo f = t?.GetField("steppedFrame", BindingFlags.NonPublic | BindingFlags.Static);
                int frame = f != null ? (int)f.GetValue(null) : int.MinValue + 1;
                if (t != null && frame > latest) { latest = frame; ride = t; }
            }
            holder = new GameObject("AIBridge gee watch");
            UnityEngine.Object.DontDestroyOnLoad(holder);
            holder.AddComponent<GeeWatch>();
            picSun = picSunLeast = picAmbient = sun00 = sun01 = float.NaN; pictures = 0;
            Camera.onPreRender += Drawn;
            return "watching for " + seconds.ToString("F1", CultureInfo.InvariantCulture) + " s" + (ride != null ? ", the ride of " + ride.Assembly.GetName().Name : ", no Smooth Portraits ride found");
        }

        static void Stop()
        {
            Camera.onPreRender -= Drawn;
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null;
        }

        /// <summary>As any camera draws: a portrait's (one that sees the cabin's layers and not the scene's, other than the cockpit's own view), the light it is drawn in; the scene's two, the sun's strength.</summary>
        static void Drawn(Camera cam)
        {
            if (cam == null) return;
            if (cam.name == "Camera 00") { sun00 = Sun.Instance != null && Sun.Instance.sunLight != null ? Sun.Instance.sunLight.intensity : float.NaN; return; }
            if (cam.name == "Camera 01") { sun01 = Sun.Instance != null && Sun.Instance.sunLight != null ? Sun.Instance.sunLight.intensity : float.NaN; return; }
            if ((cam.cullingMask & CabinLayers) == 0 || (cam.cullingMask & (1 << 15)) != 0) return;
            if (InternalCamera.Instance != null && cam.gameObject == InternalCamera.Instance.gameObject) return;
            if (cabinSun == null && Time.frameCount - cabinSunLooked > 30)
            {
                cabinSunLooked = Time.frameCount;
                IVASun s = UnityEngine.Object.FindObjectOfType<IVASun>();
                cabinSun = s != null ? s.GetComponent<Light>() : null;
            }
            float strength = cabinSun != null && cabinSun.isActiveAndEnabled ? cabinSun.intensity : 0f;
            UnityEngine.Rendering.SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
            picSun = strength;
            picSunLeast = float.IsNaN(picSunLeast) ? strength : Mathf.Min(picSunLeast, strength);
            picAmbient = 0.2126f * sh[0, 0] + 0.7152f * sh[1, 0] + 0.0722f * sh[2, 0];
            pictures++;
        }

        internal static void Step() => steps++;

        /// <summary>One frame's row, once the frame's scripts have all run.</summary>
        internal static void Frame()
        {
            float t = Time.realtimeSinceStartup - began;
            if (Time.realtimeSinceStartup > until) { Stop(); return; }
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            float g0 = (float)PhysicsGlobals.GravitationalAcceleration;
            Vector3 axis = v.ReferenceTransform != null ? v.ReferenceTransform.up : v.transform.up;
            Vector3 p = (Vector3)v.perturbation / g0, pi = (Vector3)v.perturbation_immediate / g0;
            float along = Vector3.Dot(p, axis), alongNow = Vector3.Dot(pi, axis);
            double drift = body != null && v.mainBody == body ? (body.GetWorldSurfacePosition(lat0, lon0, alt0) - v.CoMD).magnitude : double.NaN;
            var row = new float[Columns.Length];
            for (int i = 0; i < row.Length; i++) row[i] = float.NaN;
            row[0] = t; row[1] = Time.deltaTime; row[2] = steps; steps = 0;
            row[3] = (float)v.geeForce; row[4] = (float)v.geeForce_immediate;
            row[5] = along; row[6] = (p - along * axis).magnitude; row[7] = alongNow; row[8] = (pi - alongNow * axis).magnitude;
            row[9] = (float)v.gravityMultiplier; row[10] = v.easingInToSurface ? 1 : 0; row[11] = v.precalc != null && v.precalc.isEasingGravity ? 1 : 0;
            row[12] = v.Landed ? 1 : 0; row[13] = v.packed ? 1 : 0;
            row[14] = (float)v.srfSpeed; row[15] = (float)v.verticalSpeed; row[16] = v.angularVelocity.magnitude; row[17] = v.heightFromTerrain;
            row[18] = (float)drift; row[19] = v.parts.Count;
            row[28] = (float)v.acceleration.magnitude; row[29] = (float)v.acceleration_immediate.magnitude;
            if (ignoring != null) row[30] = Convert.ToSingle(ignoring.GetValue(v));
            Ride(row);
            Cabin(v, row, t);
            row[48] = picSun; row[49] = picSunLeast; row[50] = picAmbient; row[51] = pictures; row[52] = sun00; row[53] = sun01;
            picSunLeast = float.NaN; pictures = 0;
            rows.Add(row);
            // (and when anything of the state changes, said with its time)
            string situation = v.situation.ToString();
            if (situation != lastSituation) { changes.Append(t.ToString("F2")).Append(" s: ").Append(situation).Append('\n'); lastSituation = situation; }
            if (!(Mathf.Abs(row[9] - lastGravity) < 1e-4f)) { changes.Append(t.ToString("F2")).Append(" s: gravity x").Append(row[9].ToString("F3")).Append('\n'); lastGravity = row[9]; }
            if ((int)row[10] != lastEase) { changes.Append(t.ToString("F2")).Append(" s: the set position's ease ").Append(row[10] > 0 ? "on" : "off").Append('\n'); lastEase = (int)row[10]; }
            if ((int)row[11] != lastPhysicsEase) { changes.Append(t.ToString("F2")).Append(" s: the physics ease ").Append(row[11] > 0 ? "on" : "off").Append('\n'); lastPhysicsEase = (int)row[11]; }
            if ((int)row[13] != lastPacked) { changes.Append(t.ToString("F2")).Append(" s: ").Append(row[13] > 0 ? "packed" : "unpacked").Append('\n'); lastPacked = (int)row[13]; }
            if ((int)row[19] != lastParts) { changes.Append(t.ToString("F2")).Append(" s: ").Append((int)row[19]).Append(" parts\n"); lastParts = (int)row[19]; }
        }

        /// <summary>Smooth Portraits' ride as it stands this frame: its push, its buzz, and the first three heads' lean (by name).</summary>
        static void Ride(float[] row)
        {
            if (ride == null) return;
            const BindingFlags any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            object push = ride.GetField("push", any)?.GetValue(null), buzz = ride.GetField("buzz", any)?.GetValue(null);
            if (push is Vector3 pushed) row[20] = pushed.magnitude;
            if (buzz is float buzzing) row[21] = buzzing;
            if (!(ride.GetField("heads", any)?.GetValue(null) is IDictionary all)) return;
            var named = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var kerbals = new Dictionary<string, Kerbal>();
            foreach (DictionaryEntry e in all)
                if (e.Key is Kerbal one && one != null && e.Value != null) { named[one.crewMemberName ?? one.name] = e.Value; kerbals[one.crewMemberName ?? one.name] = one; }
            int n = 0;
            foreach (KeyValuePair<string, object> kv in named)
            {
                if (n >= Heads) break;
                if (!heads.Contains(kv.Key) && heads.Count < Heads) heads.Add(kv.Key);
                Type h = kv.Value.GetType();
                row[22 + 2 * n] = Convert.ToSingle(h.GetField("nod").GetValue(kv.Value));
                row[23 + 2 * n] = Convert.ToSingle(h.GetField("side").GetValue(kv.Value));
                // (the game's own bob of the portrait's camera, as its last picture left it: the seat camera turned from where the seat
                // has it, by the craft's acceleration and turning; and how far the game's own animation moved the head since the last frame)
                Kerbal k = kerbals[kv.Key];
                InternalSeat seat = k.protoCrewMember != null ? k.protoCrewMember.seat : null;
                if (seat != null && seat.portraitCamera != null) row[31 + n] = Quaternion.Angle(seat.portraitCameraInitialRotation, seat.portraitCamera.transform.localRotation);
                if (h.GetField("was")?.GetValue(kv.Value) is Quaternion was)
                {
                    Quaternion pose = Quaternion.Inverse(k.transform.rotation) * was;
                    if (lastPose[n].HasValue) row[34 + n] = Quaternion.Angle(lastPose[n].Value, pose);
                    lastPose[n] = pose;
                }
                n++;
            }
        }

        /// <summary>
        /// The lights on the cabin this frame, at the first head (by name): those of the world's that light the cabin's layers too (how
        /// many reach the head, the strongest one's light there, how far off it is and how far it reaches, which it is), how far the head
        /// is drawn from its part, the cabin's own lamps (their strengths summed) and its sun, and whether Smooth Portraits' fire lamp is
        /// alive; and the world's lamps that reach the craft itself (how many, the strongest one's light on it). With Shut on, the cabin's
        /// layers are first taken out of what each light of the world's lights.
        /// </summary>
        static void Cabin(Vessel v, float[] row, float t)
        {
            Kerbal k = null;
            foreach (ProtoCrewMember m in v.GetVesselCrew())
                if (m != null && m.KerbalRef != null && (k == null || string.CompareOrdinal(m.name, k.crewMemberName) < 0)) k = m.KerbalRef;
            if (Time.frameCount - lightsLooked >= 5) { lights = UnityEngine.Object.FindObjectsOfType<Light>(); lightsLooked = Time.frameCount; }
            Vector3 head = k == null ? Vector3.zero : k.headTransform != null ? k.headTransform.position : k.transform.position;
            int reaching = 0;
            float strongest = 0f, own = 0f, sun = 0f;
            Light best = null;
            bool lamp = false;
            int onCraft = 0;
            float onCraftLight = 0f;
            foreach (Light l in lights)
            {
                if (l == null) continue;
                bool portraits = l.name == "SmoothPortraits fire light";
                if (portraits && l.isActiveAndEnabled) lamp = true;
                // (and the world's lamps on the craft itself, whether they light the cabin or not: a fire beside it lights it still)
                if (l.type != LightType.Directional && l.isActiveAndEnabled && l.intensity > 0f && (l.cullingMask & WorldLayers) != 0 && !portraits)
                {
                    float off = (float)(l.transform.position - (Vector3)v.CoMD).magnitude;
                    if (off < l.range) { onCraft++; onCraftLight = Mathf.Max(onCraftLight, l.intensity / (1f + 25f * (off / l.range) * (off / l.range))); }
                }
                if ((l.cullingMask & CabinLayers) == 0) continue;
                bool world = (l.cullingMask & WorldLayers) != 0 && !portraits;
                if (world && shut)
                {
                    if (!shutWas.ContainsKey(l)) shutWas[l] = l.cullingMask;
                    l.cullingMask &= ~CabinLayers;
                    continue;
                }
                if (!l.isActiveAndEnabled || l.intensity <= 0f) continue;
                if (!world)
                {
                    if (l.GetComponent<IVASun>() != null) sun = l.intensity; else if (!portraits) own += l.intensity;
                    continue;
                }
                if (k == null) continue;
                // (Unity's fall of a lamp's light: 1 / (1 + 25 (d / range)²), and nothing past its range)
                float d = Vector3.Distance(l.transform.position, head), there;
                if (l.type == LightType.Directional) there = l.intensity;
                else if (d >= l.range) continue;
                else there = l.intensity / (1f + 25f * (d / l.range) * (d / l.range));
                reaching++;
                if (there > strongest) { strongest = there; best = l; }
            }
            row[37] = reaching; row[38] = strongest;
            if (best != null)
            {
                string name = best.name + (best.transform.parent != null ? " under " + best.transform.parent.name : "") + " (" + best.type + ", mask " + best.cullingMask + ")";
                int at = outer.IndexOf(name);
                if (at < 0) { at = outer.Count; outer.Add(name); changes.Append(t.ToString("F2")).Append(" s: first on the cabin: ").Append(at).Append(" = ").Append(name).Append('\n'); }
                row[39] = Vector3.Distance(best.transform.position, head); row[40] = at; row[41] = best.type == LightType.Directional ? float.PositiveInfinity : best.range;
            }
            if (k != null && k.InPart != null) row[42] = Vector3.Distance(head, k.InPart.transform.position);
            row[43] = own; row[44] = sun; row[45] = lamp ? 1 : 0; row[46] = onCraft; row[47] = onCraftLight;
        }

        /// <summary>
        /// "on": while watched, every light of the world's that also lights the cabin's layers has them taken out of what it lights (does
        /// the light on the crew's faces come from it?); "off": each given them back as it had them.
        /// </summary>
        public static string Shut(string arg)
        {
            shut = (arg ?? "").Trim() == "on";
            if (holder != null) changes.Append((Time.realtimeSinceStartup - began).ToString("F2")).Append(" s: the world's lights ").Append(shut ? "kept off the cabin" : "let onto it again").Append('\n');
            if (shut) return "the world's lights kept off the cabin while watched";
            int back = 0;
            foreach (KeyValuePair<Light, int> kv in shutWas) if (kv.Key != null) { kv.Key.cullingMask = kv.Value; back++; }
            shutWas.Clear();
            return "the cabin's layers given back to " + back + " lights";
        }

        /// <summary>
        /// "on": every copy of Smooth Portraits that is loaded takes its last moments' pictures ahead all the time, as if a crash were
        /// near (its TestDanger, which a release build has no command for); "off": as it comes.
        /// </summary>
        public static string Near(string arg)
        {
            bool on = (arg ?? "").Trim() == "on";
            var b = new StringBuilder();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                FieldInfo f = a.GetType("SmoothPortraits.LastMoments", false)?.GetField("TestDanger", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null) continue;
                f.SetValue(null, on);
                b.Append(b.Length > 0 ? ", " : "").Append(a.GetName().Name);
            }
            return (on ? "taken ahead all the time: " : "taken ahead near a crash: ") + (b.Length > 0 ? b.ToString() : "no Smooth Portraits loaded");
        }

        /// <summary>
        /// Smooth Portraits' last moments as they stand: how many times its pictures have been taken ahead (while it reckons a crash may
        /// be near), until when it reckons so, and each of the things it reckons by, as they are now for the craft flown.
        /// </summary>
        public static string Moments(string arg)
        {
            const BindingFlags any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            var b = new StringBuilder();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType("SmoothPortraits.LastMoments", false);
                if (t == null) continue;
                b.Append(a.GetName().Name).Append(": taken ahead ").Append(t.GetField("Prepared", any)?.GetValue(null))
                 .Append(" times, danger until ").Append(t.GetField("dangerUntil", any)?.GetValue(null)).Append(" (now ").Append(Time.unscaledTime.ToString("F1"))
                 .Append("), kept ready for ").Append((t.GetField("ready", any)?.GetValue(null) as IDictionary)?.Count).Append('\n');
            }
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return b.Append("no vessel").ToString();
            b.Append("flown: packed ").Append(v.packed).Append(", crew ").Append(v.GetCrewCount()).Append(", srf ").Append(v.srfSpeed.ToString("F2")).Append(" m/s, radar alt ")
             .Append(v.radarAltitude.ToString("F1")).Append(" m\n");
            foreach (Vessel o in FlightGlobals.VesselsLoaded)
            {
                if (o == null || o == v) continue;
                float closing = (float)(v.obt_velocity - o.obt_velocity).magnitude, apart = (float)(o.CoMD - v.CoMD).magnitude;
                bool counts = !o.packed && closing > 8f && apart < closing * 2.5f + 40f;
                b.Append(counts ? "  ** " : "     ").Append(o.GetDisplayName()).Append(": ").Append(o.situation).Append(o.packed ? ", packed" : "").Append(", closing ").Append(closing.ToString("F1"))
                 .Append(" m/s, ").Append(apart.ToString("F0")).Append(" m off, its srf ").Append(o.srfSpeed.ToString("F1")).Append('\n');
            }
            double hottest = 0; string which = "";
            foreach (Part part in v.parts)
            {
                if (part == null) continue;
                double r = Math.Max(part.temperature / part.maxTemp, part.skinMaxTemp > 0 ? part.skinTemperature / part.skinMaxTemp : 0);
                if (r > hottest) { hottest = r; which = part.partInfo != null ? part.partInfo.title : part.name; }
            }
            return b.Append("hottest part ").Append(which).Append(" at ").Append((100 * hottest).ToString("F0")).Append("% of what it breaks at").ToString();
        }

        /// <summary>What was watched, in a few lines; every frame to the .csv given, where one is.</summary>
        public static string Report(string arg)
        {
            Stop();
            if (rows.Count < 2) return "nothing watched";
            if (!string.IsNullOrEmpty(arg))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(arg)));
                var csv = new StringBuilder(string.Join(",", Columns)).Append('\n');
                foreach (float[] row in rows)
                {
                    for (int i = 0; i < row.Length; i++) csv.Append(i > 0 ? "," : "").Append(float.IsNaN(row[i]) ? "" : row[i].ToString("G6", CultureInfo.InvariantCulture));
                    csv.Append('\n');
                }
                File.WriteAllText(arg, csv.ToString());
            }
            var b = new StringBuilder();
            float span = rows[rows.Count - 1][0] - rows[0][0];
            b.Append(rows.Count).Append(" frames over ").Append(span.ToString("F1")).Append(" s").Append(heads.Count > 0 ? "; heads: " + string.Join(", ", heads) : "").Append('\n');
            b.Append(changes);
            // (each measure over the frames the craft was not packed: its mean, spread, least and most, and how much it changes from
            // one frame to the next, the middle of those changes, the 95th in a hundred, and the most)
            void Say(string name, int column, string format)
            {
                var values = new List<float>();
                var moves = new List<float>();
                float last = float.NaN;
                foreach (float[] row in rows)
                {
                    if (row[13] > 0 || float.IsNaN(row[column])) { last = float.NaN; continue; }
                    values.Add(row[column]);
                    if (!float.IsNaN(last)) moves.Add(Mathf.Abs(row[column] - last));
                    last = row[column];
                }
                if (values.Count == 0) return;
                double mean = 0, spread = 0;
                foreach (float x in values) mean += x;
                mean /= values.Count;
                foreach (float x in values) spread += (x - mean) * (x - mean);
                spread = Math.Sqrt(spread / values.Count);
                values.Sort(); moves.Sort();
                b.Append("  ").Append(name.PadRight(22)).Append(" mean ").Append(mean.ToString(format)).Append(", spread ").Append(spread.ToString(format))
                 .Append(", ").Append(values[0].ToString(format)).Append(" to ").Append(values[values.Count - 1].ToString(format));
                if (moves.Count > 0) b.Append("; a frame's change ").Append(moves[moves.Count / 2].ToString(format)).Append(" (middle), ").Append(moves[(int)(0.95 * (moves.Count - 1))].ToString(format)).Append(" (95%), ").Append(moves[moves.Count - 1].ToString(format)).Append(" (most)");
                b.Append('\n');
            }
            Say("gee", 3, "F3");
            Say("gee, unsmoothed", 4, "F3");
            Say("along the craft, g", 5, "F3");
            Say("across the craft, g", 6, "F3");
            Say("across, unsmoothed, g", 8, "F3");
            Say("srf speed, m/s", 14, "F3");
            Say("turning, rad/s", 16, "F4");
            Say("drift, m", 18, "F3");
            Say("ride's push, g", 20, "F3");
            Say("ride's buzz", 21, "F3");
            Say("acceleration, m/s2", 28, "F3");
            Say("accel., unsmoothed", 29, "F3");
            Say("g frames ignored", 30, "F0");
            for (int n = 0; n < heads.Count; n++)
            {
                Say(heads[n] + "'s nod, deg", 22 + 2 * n, "F2");
                Say(heads[n] + "'s side, deg", 23 + 2 * n, "F2");
                Say(heads[n] + "'s camera bob, deg", 31 + n, "F2");
                Say(heads[n] + "'s own pose moved, deg", 34 + n, "F2");
            }
            Say("world lights on a head", 37, "F0");
            Say("their strongest there", 38, "F3");
            Say("it from the head, m", 39, "F1");
            Say("the head from its part, m", 42, "F1");
            Say("the cabin's own lamps", 43, "F2");
            Say("the cabin's sun", 44, "F2");
            Say("Portraits' fire lamp", 45, "F0");
            Say("world lamps on the craft", 46, "F0");
            Say("their strongest there", 47, "F3");
            Say("cabin sun in the pictures", 48, "F3");
            Say("the least of it", 49, "F3");
            Say("the light all round them", 50, "F3");
            Say("pictures since a row", 51, "F0");
            Say("the sun as Camera 00 drew", 52, "F3");
            Say("as Camera 01 drew", 53, "F3");
            return b.ToString();
        }
    }

    [DefaultExecutionOrder(32000)]
    sealed class GeeWatch : MonoBehaviour
    {
        void FixedUpdate() => DevGee.Step();
        void LateUpdate() => DevGee.Frame();
    }
}
#endif
