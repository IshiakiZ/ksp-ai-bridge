#if DEV
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// Development build only: renders a stretch of the game to a folder of numbered pictures, for
    /// making a video. The game is frame-locked while it runs (every picture is exactly one frame of game
    /// time later than the last, however long it took to draw), so the result is smooth at any frame rate
    /// and slow motion is just a higher one. A second pass over the same script, at normal speed and
    /// without pictures, records the sound, which then lines up with the pictures.
    /// </summary>
    public static class DevRecorder
    {
        sealed class Cue { public double At; public Args What; }

        static BridgeHost host;
        static GameObject lookAt;
        static bool running, stopRequested;
        static int framesDone, framesWanted;
        static string folder, lastError, mode;
        static CelestialBody lookBody;
        static double lookLat, lookLon, lookAlt;

        static Vector3d lookFromShip;
        static string lookHold = "";

        static void HoldTarget()
        {
            if (lookAt == null || lookBody == null) return;
            Vessel v = FlightGlobals.ActiveVessel;
            // A place on the planet for a scene on or near the ground; a place beside the ship for one in flight at speed
            // (unless told which: "place" to watch a ship go by and what it leaves behind, "ship" to go along with it).
            bool withShip = lookHold == "ship" || lookHold != "place" && v != null && (v.srfSpeed > 250.0 || v.situation == Vessel.Situations.ORBITING || v.situation == Vessel.Situations.ESCAPING);
            // (beside the ship's own transform, which the game's floating origin moves the moment it shifts the world)
            if (v != null && withShip)
                lookAt.transform.position = (Vector3)((Vector3d)v.transform.position + lookFromShip);
            else lookAt.transform.position = (Vector3)lookBody.GetWorldSurfacePosition(lookLat, lookLon, lookAlt);
        }

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("dev_record", "Development build only: record a scripted stretch of the game. params: {folder, seconds, fps (60), sound: bool (record the sound at normal speed instead of pictures), watch: bool (no pictures and no folder: the camera held so for so long, and the frames a second drawn meanwhile given in dev_record_status as fps), live: bool (pictures of the game as it really runs, as many as it draws, instead of frame-locked; their times go to times.txt), raw: bool (live only: the pixels into one file, frames.rgb, not JPEGs), crop: [x, y, w, h] (live only), hide_ui (true), quality (92), look_at: {east, north, up, surface, hold: place|ship (a place on the planet, or one that goes along with the ship; by default the first unless the ship is fast or in orbit)}, camera: {distance, pitch, heading}, orbit_deg_s, orbit_from and orbit_until (seconds: the camera turned only between them), caption, cues: [{at: seconds, do: explode (at_look: true puts it where the camera looks)|destroy|caption|fx|camera|message|rpc, ...}]}", Start);
            Rpc.Sync("dev_record_status", "Development build only: progress of the recording.", a => Status()).AlwaysAllowed = true;
            Rpc.Sync("dev_record_stop", "Development build only: stop the recording.", a => { stopRequested = true; return Status(); });
            Rpc.Sync("dev_ambient", "Development build only: read Unity's ambient light as the game has it, and optionally hold it at something else for a while (to see what the game's shaders make of it). params: {mode: flat|trilight, sky: [r,g,b], equator: [r,g,b], ground: [r,g,b], hold_s (20)}", Ambient).AlwaysAllowed = true;
        }

        // ---- ambient light, for finding out what the game's shaders do with it
        static UnityEngine.Rendering.AmbientMode heldMode;
        static Color heldSky, heldEquator, heldGround;
        static float heldUntil;
        static bool heldHooked;

        static Color Tint(Args a, string key, Color otherwise)
        {
            var list = a.Raw(key) as System.Collections.IList;
            if (list == null || list.Count < 3) return otherwise;
            return new Color(Convert.ToSingle(list[0]), Convert.ToSingle(list[1]), Convert.ToSingle(list[2]), 1f);
        }

        static void HoldAmbient(Camera cam)
        {
            if (Time.realtimeSinceStartup > heldUntil) return;
            RenderSettings.ambientMode = heldMode;
            RenderSettings.ambientSkyColor = heldSky; RenderSettings.ambientEquatorColor = heldEquator; RenderSettings.ambientGroundColor = heldGround;
            if (heldMode == UnityEngine.Rendering.AmbientMode.Flat) RenderSettings.ambientLight = heldSky;
        }

        static object Ambient(Args a)
        {
            var o = new JObj().Add("mode", RenderSettings.ambientMode.ToString()).Add("light", RenderSettings.ambientLight.ToString("F3")).Add("sky", RenderSettings.ambientSkyColor.ToString("F3"))
                .Add("equator", RenderSettings.ambientEquatorColor.ToString("F3")).Add("ground", RenderSettings.ambientGroundColor.ToString("F3")).Add("intensity", RenderSettings.ambientIntensity)
                .Add("skybox", RenderSettings.skybox != null ? RenderSettings.skybox.name : "none").Add("reflection_mode", RenderSettings.defaultReflectionMode.ToString())
                .Add("pixel_lights", QualitySettings.pixelLightCount).Add("shadow_distance", QualitySettings.shadowDistance).Add("shadow_cascades", QualitySettings.shadowCascades)
                .Add("colour_space", QualitySettings.activeColorSpace.ToString());
            DynamicAmbientLight game = DynamicAmbientLight.Instance;
            if (game != null) o.Add("game", new JObj().Add("vacuum", game.vacuumAmbientColor.ToString("F3")).Add("default", game.defaultAmbientColor.ToString("F3")).Add("boost", game.boostFactor).Add("switched_off", game.disableDynamicAmbient));
            var lights = new List<object>();
            foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
                if (l.isActiveAndEnabled && l.type == LightType.Directional) lights.Add(new JObj().Add("name", l.name).Add("intensity", l.intensity).Add("colour", l.color.ToString("F2")).Add("shadows", l.shadows.ToString()).Add("mask", l.cullingMask));
            o.Add("directional_lights", lights);
            if (a.Has("mode"))
            {
                heldMode = a.Str("mode").ToLowerInvariant() == "flat" ? UnityEngine.Rendering.AmbientMode.Flat : UnityEngine.Rendering.AmbientMode.Trilight;
                heldSky = Tint(a, "sky", Color.black); heldEquator = Tint(a, "equator", Color.black); heldGround = Tint(a, "ground", Color.black);
                heldUntil = Time.realtimeSinceStartup + (float)a.Num("hold_s", 20);
                if (!heldHooked) { Camera.onPreCull += HoldAmbient; heldHooked = true; }
                o.Add("held_for_s", a.Num("hold_s", 20));
            }
            return o;
        }

        static JObj Status() => new JObj().Add("recording", running).Add("mode", mode).Add("frames", framesDone).Add("of", framesWanted)
            .Add("folder", folder).Add("error", lastError).Add("screen", Screen.width + "x" + Screen.height).Add("fps", Math.Round(watchedFps, 1));

        static double watchedFps;

        /// <summary>
        /// No pictures: the camera held where a recording would put it, for so long, while the game runs as it does; and how many
        /// frames a second it drew meanwhile (the first second left out), in the status. For timing what is in view: a thing drawn
        /// by the graphics card costs what it covers of the screen, and the craft's own camera seldom looks at a cloud overhead.
        /// </summary>
        static IEnumerator Watch(Args a, double seconds, List<Cue> cues)
        {
            Prepare(a);
            // (and the camera turned round what it looks at, as in a live recording: for watching what a turning view does)
            float orbit = (float)a.Num("orbit_deg_s", 0), orbitFrom = (float)a.Num("orbit_from", 0), orbitUntil = (float)a.Num("orbit_until", 1e9);
            for (int n = 0; n < 12; n++) yield return null;
            float started = Time.realtimeSinceStartup, counted = -1f;
            int next = 0, frames = 0;
            while (!stopRequested)
            {
                double t = Time.realtimeSinceStartup - started;
                if (t >= seconds) break;
                while (next < cues.Count && cues[next].At <= t) Run(cues[next++].What);
                if (orbit != 0f && t >= orbitFrom && t < orbitUntil && FlightCamera.fetch != null) FlightCamera.fetch.camHdg += orbit * Mathf.Deg2Rad * Time.unscaledDeltaTime;
                HoldTarget();
                framesDone++;
                if (t >= Math.Min(1.0, seconds * 0.3)) { if (counted < 0f) counted = Time.realtimeSinceStartup; else frames++; }
                yield return null;
            }
            if (counted > 0f && frames > 0) watchedFps = frames / Math.Max(0.001, Time.realtimeSinceStartup - counted);
        }

        static object Start(Args a)
        {
            if (running) throw new ApiException("A recording is already running.");
            bool watch = a.Bool("watch", false);
            folder = watch ? a.Str("folder") ?? "" : a.Require("folder");
            if (folder != "") Directory.CreateDirectory(folder);
            double seconds = Pilot.OrbitMath.Clamp(a.Num("seconds", 5), 0.2, 600);
            int fps = (int)Pilot.OrbitMath.Clamp(a.Num("fps", 60), 5, 480);
            var cues = new List<Cue>();
            List<object> raw = a.List("cues");
            if (raw != null) foreach (object o in raw) { var args = new Args(o); cues.Add(new Cue { At = args.Num("at", 0), What = args }); }
            cues.Sort((x, y) => x.At.CompareTo(y.At));
            running = true;
            stopRequested = false;
            lastError = null;
            framesDone = 0;
            bool sound = a.Bool("sound", false), live = a.Bool("live", false);
            mode = watch ? "watch" : sound ? "sound" : live ? "live" : "pictures";
            framesWanted = watch || sound || live ? 0 : (int)Math.Round(seconds * fps);
            watchedFps = 0;
            host.RunBackground(Guarded(watch ? Watch(a, seconds, cues) : sound ? Sound(a, seconds, cues) : live ? Live(a, seconds, cues) : Pictures(a, seconds, fps, cues)), "recording");
            return Status();
        }

        static IEnumerator Guarded(IEnumerator job)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!job.MoveNext()) break;
                    current = job.Current;
                }
                catch (Exception ex)
                {
                    lastError = ex.GetType().Name + ": " + ex.Message;
                    break;
                }
                yield return current;
            }
            Restore();
        }

        static bool hidInterface;

        static void Restore()
        {
            Time.captureFramerate = 0;
            BridgeHost.DevCaption = null;
            BridgeHost.DevHidePanel = false;
            if (hidInterface) try { GameEvents.onShowUI.Fire(); } catch { }
            hidInterface = false;
            if (lookAt != null)
            {
                try { if (FlightGlobals.ActiveVessel != null) FlightCamera.SetTarget(FlightGlobals.ActiveVessel); } catch { }
                UnityEngine.Object.Destroy(lookAt);
                lookAt = null;
                lookBody = null;
            }
            running = false;
        }

        static void Prepare(Args a)
        {
            if (a.Bool("hide_ui", true))
            {
                BridgeHost.DevHidePanel = true;
                // (put away, and brought back after, only if it was showing: see the screenshot's)
                if (!BridgeHost.InterfaceIsHidden) { hidInterface = true; try { GameEvents.onHideUI.Fire(); } catch { } }
            }
            // (the game's own interface left in the picture, and this mod's panel out of it)
            if (a.Bool("hide_panel", false)) BridgeHost.DevHidePanel = true;
            BridgeHost.DevCaption = a.Str("caption");
            // Look at a spot beside the vessel instead of at the vessel, to put an explosion in the middle.
            Args spot = a.Obj("look_at");
            if (spot != null && HighLogic.LoadedSceneIsFlight)
            {
                lookAt = new GameObject("AIBridge camera target");
                Vector3d at = GameApi.DevSpot(spot, out Vector3d _, out Vector3d _, out Vector3d _);
                lookAt.transform.position = (Vector3)at;
                // Kept as a place on the planet: the game slides its whole world about under a moving ship.
                lookBody = FlightGlobals.currentMainBody;
                lookLat = lookBody.GetLatitude(at); lookLon = lookBody.GetLongitude(at); lookAlt = lookBody.GetAltitude(at);
                lookFromShip = FlightGlobals.ActiveVessel != null ? at - (Vector3d)FlightGlobals.ActiveVessel.transform.position : Vector3d.zero;
                lookHold = (spot.Str("hold") ?? "").ToLowerInvariant();
                FlightCamera.SetTarget(lookAt.transform);
            }
            Args camera = a.Obj("camera");
            if (camera != null) CameraApi.Apply(camera);
        }

        static IEnumerator Pictures(Args a, double seconds, int fps, List<Cue> cues)
        {
            Prepare(a);
            int quality = (int)Pilot.OrbitMath.Clamp(a.Num("quality", 92), 40, 100);
            float orbit = (float)a.Num("orbit_deg_s", 0), orbitFrom = (float)a.Num("orbit_from", 0), orbitUntil = (float)a.Num("orbit_until", 1e9);
            for (int n = 0; n < 12; n++) yield return null;       // let the camera and the interface settle
            Time.captureFramerate = fps;
            var picture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            // When each frame was finished, by the clock the game's log is stamped with (clock.txt, one line a frame):
            // so that what the log says happened (an explosion, a mod's note) can be put on the frame it happened in.
            var clock = new System.Text.StringBuilder();
            int next = 0;
            for (int frame = 0; frame < framesWanted && !stopRequested; frame++)
            {
                double t = frame / (double)fps;
                while (next < cues.Count && cues[next].At <= t + 1e-6) Run(cues[next++].What);
                if (orbit != 0f && t >= orbitFrom && t < orbitUntil && FlightCamera.fetch != null) FlightCamera.fetch.camHdg += orbit * Mathf.Deg2Rad / fps;
                HoldTarget();
                yield return WaitFrameEnd.Instance;
                clock.Append(DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                if (picture.width != Screen.width || picture.height != Screen.height) picture.Resize(Screen.width, Screen.height);
                picture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                File.WriteAllBytes(Path.Combine(folder, "f" + frame.ToString("D5") + ".jpg"), picture.EncodeToJPG(quality));
                framesDone = frame + 1;
                // (on to the next frame's Update before the camera's target is put again: put at the end of this frame, it was put
                // before the next frame's physics, and a step that shifted the floating origin left it where the world had been)
                yield return null;
            }
            UnityEngine.Object.Destroy(picture);
            File.WriteAllText(Path.Combine(folder, "clock.txt"), clock.ToString());
        }

        /// <summary>
        /// Pictures of the game as it really runs: nothing is frame-locked, every frame it draws is kept (which
        /// slows it a little), and when each was drawn is written to times.txt. For looking at what the
        /// player sees from one frame to the next, which the frame-locked recording cannot show: there the
        /// game's own timing, and everything that depends on it, is different.
        /// </summary>
        static IEnumerator Live(Args a, double seconds, List<Cue> cues)
        {
            Prepare(a);
            int quality = (int)Pilot.OrbitMath.Clamp(a.Num("quality", 92), 40, 100);
            float orbit = (float)a.Num("orbit_deg_s", 0), orbitFrom = (float)a.Num("orbit_from", 0), orbitUntil = (float)a.Num("orbit_until", 1e9);
            List<object> crop = a.List("crop");
            for (int n = 0; n < 12; n++) yield return null;
            int x = 0, y = 0, w = Screen.width, h = Screen.height;
            if (crop != null && crop.Count == 4)
            {
                x = Mathf.Clamp(Convert.ToInt32(crop[0]), 0, Screen.width - 16); y = Mathf.Clamp(Convert.ToInt32(crop[1]), 0, Screen.height - 16);
                w = Mathf.Clamp(Convert.ToInt32(crop[2]), 16, Screen.width - x); h = Mathf.Clamp(Convert.ToInt32(crop[3]), 16, Screen.height - y);
            }
            var picture = new Texture2D(w, h, TextureFormat.RGB24, false);
            var times = new System.Text.StringBuilder();
            // ("raw": each frame's pixels written as they are, one after another, into frames.rgb (rows from the bottom; its size in
            // size.txt), not made into a JPEG: the encoding cost 10 to 15 ms a frame at 1280 x 720 on this Mac, which held the game
            // back as much as what was being looked at. The reading itself still waits for the card.)
            bool raw = a.Bool("raw", false);
            FileStream rawOut = raw ? new FileStream(Path.Combine(folder, "frames.rgb"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20) : null;
            byte[] rawBytes = raw ? new byte[w * h * 3] : null;
            if (raw) File.WriteAllText(Path.Combine(folder, "size.txt"), w + " " + h + "\n");
            // ("heard": the game's sound taken as well, from the same moment, into sound.wav: a film of something that is never the same
            // twice, a crash, with its own sound)
            Tap tap = null;
            if (a.Bool("heard", false))
            {
                foreach (AudioListener l in UnityEngine.Object.FindObjectsOfType<AudioListener>()) if (l.isActiveAndEnabled) { tap = l.gameObject.AddComponent<Tap>(); break; }
            }
            float started = Time.realtimeSinceStartup;
            int next = 0, frame = 0;
            while (!stopRequested)
            {
                double t = Time.realtimeSinceStartup - started;
                if (t >= seconds) break;
                while (next < cues.Count && cues[next].At <= t) Run(cues[next++].What);
                if (orbit != 0f && t >= orbitFrom && t < orbitUntil && FlightCamera.fetch != null) FlightCamera.fetch.camHdg += orbit * Mathf.Deg2Rad * Time.unscaledDeltaTime;
                HoldTarget();
                yield return WaitFrameEnd.Instance;
                picture.ReadPixels(new Rect(x, Screen.height - y - h, w, h), 0, 0, false);
                if (raw) { picture.GetRawTextureData<byte>().CopyTo(rawBytes); rawOut.Write(rawBytes, 0, rawBytes.Length); }
                else File.WriteAllBytes(Path.Combine(folder, "f" + frame.ToString("D5") + ".jpg"), picture.EncodeToJPG(quality));
                times.Append((Time.realtimeSinceStartup - started).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append(Time.deltaTime.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                framesDone = ++frame;
                // (on to the next frame's Update before the camera's target is put again, as in Pictures: a frame's target put before
                // that frame's physics was left where the world had been by a step that shifted the floating origin, some 140 m about
                // 5 s after a lift-off, and the sky leapt for a frame)
                yield return null;
            }
            UnityEngine.Object.Destroy(picture);
            if (rawOut != null) rawOut.Dispose();
            File.WriteAllText(Path.Combine(folder, "times.txt"), times.ToString());
            if (tap != null)
            {
                tap.On = false;
                float[] samples;
                lock (tap.Samples) samples = tap.Samples.ToArray();
                int channels = Math.Max(1, tap.Channels), rate = AudioSettings.outputSampleRate;
                UnityEngine.Object.Destroy(tap);
                WriteWave(Path.Combine(folder, "sound.wav"), samples, channels, rate);
            }
        }

        static void WriteWave(string path, float[] samples, int channels, int rate)
        {
            using (var f = new BinaryWriter(File.Create(path)))
            {
                f.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                f.Write(36 + samples.Length * 2);
                f.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                f.Write(16); f.Write((short)1); f.Write((short)channels); f.Write(rate); f.Write(rate * channels * 2); f.Write((short)(channels * 2)); f.Write((short)16);
                f.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                f.Write(samples.Length * 2);
                foreach (float s in samples) f.Write((short)Mathf.Clamp(s * 32767f, -32768f, 32767f));
            }
        }

        static IEnumerator Sound(Args a, double seconds, List<Cue> cues)
        {
            Prepare(a);
            AudioListener ears = null;
            foreach (AudioListener l in UnityEngine.Object.FindObjectsOfType<AudioListener>()) if (l.isActiveAndEnabled) { ears = l; break; }
            if (ears == null) throw new ApiException("There is nothing listening in this scene.");
            float music1 = 0f, music2 = 0f;
            bool hadMusic = MusicLogic.fetch != null && MusicLogic.fetch.audio1 != null && MusicLogic.fetch.audio2 != null;
            if (hadMusic) { music1 = MusicLogic.fetch.audio1.volume; music2 = MusicLogic.fetch.audio2.volume; MusicLogic.SetVolume(0f, 0f); }
            for (int n = 0; n < 12; n++) yield return null;
            Tap tap = ears.gameObject.AddComponent<Tap>();
            float started = Time.realtimeSinceStartup;
            int next = 0;
            while (!stopRequested)
            {
                double t = Time.realtimeSinceStartup - started;
                if (t >= seconds) break;
                while (next < cues.Count && cues[next].At <= t) Run(cues[next++].What);
                HoldTarget();
                framesDone++;
                yield return null;
            }
            tap.On = false;
            float[] samples;
            lock (tap.Samples) samples = tap.Samples.ToArray();
            int channels = Math.Max(1, tap.Channels), rate = AudioSettings.outputSampleRate;
            UnityEngine.Object.Destroy(tap);
            if (hadMusic) MusicLogic.SetVolume(music1, music2);
            using (var f = new BinaryWriter(File.Create(Path.Combine(folder, "sound.wav"))))
            {
                f.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                f.Write(36 + samples.Length * 2);
                f.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                f.Write(16); f.Write((short)1); f.Write((short)channels); f.Write(rate); f.Write(rate * channels * 2); f.Write((short)(channels * 2)); f.Write((short)16);
                f.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                f.Write(samples.Length * 2);
                foreach (float s in samples) f.Write((short)Mathf.Clamp(s * 32767f, -32768f, 32767f));
            }
        }

        static void Run(Args cue)
        {
            switch ((cue.Str("do") ?? "").ToLowerInvariant())
            {
                case "explode":
                    // (at_look: where the camera is looking, instead of beside the ship)
                    if (cue.Bool("at_look", false) && lookAt != null) GameApi.DevExplode(cue, (Vector3d)lookAt.transform.position);
                    else GameApi.DevExplode(cue);
                    break;
                case "destroy": host.RunBackground(GameApi.DevDestroy(new Call { Method = "dev_destroy", Args = cue }), "destroy"); break;
                case "caption": BridgeHost.DevCaption = cue.Str("text"); break;
                case "fx": GameApi.DevFxReload(cue); break;
                case "camera": CameraApi.Apply(cue); break;
                case "rpc":
                    // Any other command, e.g. {do: "rpc", method: "control", params: {throttle: 1, stage: true}}.
                    RpcEntry entry = Rpc.Find(cue.Str("method") ?? "");
                    if (entry == null) throw new ApiException("No such command '" + cue.Str("method") + "'.");
                    if (entry.Sync != null) entry.Sync(new Args(cue.Raw("params")));
                    // (one that takes its time, building a craft, say, is started and left to run while the recording goes on)
                    else host.RunBackground(entry.Async(new Call { Method = entry.Name, Args = new Args(cue.Raw("params")) }), entry.Name);
                    break;
                case "message": try { ScreenMessages.PostScreenMessage(cue.Str("text") ?? "", (float)cue.Num("seconds", 3), ScreenMessageStyle.UPPER_CENTER); } catch { } break;
                default: throw new ApiException("Unknown cue '" + cue.Str("do") + "'.");
            }
        }

        /// <summary>Sits on the listener and copies everything the player would hear.</summary>
        sealed class Tap : MonoBehaviour
        {
            public readonly List<float> Samples = new List<float>();
            public volatile bool On = true;
            public int Channels = 2;

            void OnAudioFilterRead(float[] data, int channels)
            {
                if (!On) return;
                Channels = channels;
                lock (Samples) Samples.AddRange(data);
            }
        }
    }
}
#endif
