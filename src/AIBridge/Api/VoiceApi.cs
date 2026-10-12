using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using AIBridge.Pilot;

namespace AIBridge.Api
{
    /// <summary>
    /// The ship's voice, and the player's. Speech is made and understood outside the game (the plugin has
    /// no speech engine); this side plays the finished audio with a ship's-intercom sound, hands out the
    /// lines the ship's computer wants said, watches the push-to-talk key, and passes on what the player
    /// said to whichever AI is flying.
    /// </summary>
    public static class VoiceApi
    {
        sealed class Line
        {
            public AudioClip Clip;
            public string Text, Effect;
            public float Volume, Seconds;
        }

        static BridgeHost host;
        static GameObject speaker;
        static AudioSource source;
        static AudioLowPassFilter lowPass;
        static AudioHighPassFilter highPass;
        static AudioReverbFilter reverb;
        static AudioDistortionFilter distortion;
        static readonly Queue<Line> queue = new Queue<Line>();
        static bool pumping;
        static string speaking, lastSpoken;
        static int spokenCount;

        // The player's side of the conversation.
        static KeyCode talkKey = KeyCode.Y;
        static bool talkDown;
        static int talkChanges;
        static float listenerSeenAt = -1000f;
        static ScreenMessage listeningNote;
        static readonly Queue<string> heard = new Queue<string>();
        static int collecting;               // how many calls are waiting for the player's next spoken words just now
        /// <summary>Whether somebody is waiting for the player's next spoken words this moment: what is said now reaches them at once.</summary>
        internal static bool Collecting => collecting > 0;
        static int heardTotal;
        static string feedClient;
        static float feedSeenAt = -1000f;

        static Narrator Narrator => Autopilot.I.Narrator;
        static bool ListenerAlive => Time.realtimeSinceStartup - listenerSeenAt < 8f;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("voice_play", "Say a line in the game. params: {wav_base64: 16-bit PCM WAV (leave out to show the words only), text: the words (shown as a caption), effect: ship|radio|none, chirp: bool (the intercom tone in front), volume, caption: bool, interrupt: bool, by: ai when the line is the AI's own words to the player}", Play).AlwaysAllowed = true;
            Rpc.Sync("voice_status", "The ship's voice: speaking or not, lines waiting, whether someone is narrating and listening.", a => Status()).AlwaysAllowed = true;
            Rpc.Sync("voice_stop", "Stop speaking and drop the lines that are waiting.", a => { Stop(); return Status(); }).AlwaysAllowed = true;
            Rpc.Async("voice_feed", "Lines the ship's computer wants said (staging, orbit, warnings, call-outs). Blocks until there is one. params: {since: last id seen (leave out to start from now), timeout_s, detail: quiet|normal|chatty, client}", Feed).AlwaysAllowed = true;
            Rpc.Sync("voice_config", "Voice settings kept by the game. params: {key: push-to-talk key name such as Y or RightControl, detail: quiet|normal|chatty}", Config).AlwaysAllowed = true;
            Rpc.Async("voice_key", "Block until the push-to-talk key is pressed or let go. params: {seen: the 'changes' value from the last answer, timeout_s}", Key).AlwaysAllowed = true;
            collecting = 0;
            Rpc.Sync("voice_heard", "Pass on something the player said. params: {text, queue: keep it for voice_next (default true), caption: show it on screen (default true)}", Heard).AlwaysAllowed = true;
            Rpc.Async("voice_next", "Block until the player has said something, then return it. params: {timeout_s}", Next).AlwaysAllowed = true;
#if DEV
            Rpc.Sync("dev_voice_key", "Development build only: press or release the push-to-talk key. params: {down}", a => { devKeyDown = a.Bool("down"); return Status(); });
            Rpc.Sync("dev_voice_tap", "Development build only: record what comes out of the voice's effects. params: {on: bool, save: path of a WAV file to write}", Tap);
            Rpc.Sync("dev_voice_panel", "Development build only: what the panel's voice buttons do. params: {voice_step: 1 or -1, pick: true (the talk key button), key: a key name, as if pressed while picking, cancel: true}", DevPanel);
#endif
            LookAtSettings(true);
        }

        static JObj Status() => new JObj()
            .Add("speaking", speaking)
            .Add("waiting", queue.Count)
            .Add("lines_spoken", spokenCount)
            .Add("last", lastSpoken)
            .Add("narrating", Narrator.Active)
            .Add("narrator", Narrator.Active ? feedClient : null)
            .Add("detail", Narrator.Named(Narrator.Detail))
            .Add("listening", ListenerAlive)
            .Add("talk_key", talkKey.ToString())
            .Add("talk_key_down", talkDown)
            .Add("voice", voiceName)
            .Add("heard_waiting", heard.Count)
            .Add("heard_total", heardTotal)
            .Add("failed_starts", failedStarts)
#if DEV
            .Add("silent_lines", silentLines)
#endif
            ;

        /// <summary>One line for the in-game panel, or null when the voice edition is not in use.</summary>
        public static string PanelLine()
        {
            bool talking = Narrator.Active, hearing = ListenerAlive;
            if (!talking && !hearing) return null;
            return (talking ? "ship's voice on" : "") + (talking && hearing ? " · " : "") + (hearing ? (talkDown ? "listening…" : "hold " + talkKey + " to talk") : "");
        }

        public static void Stop()
        {
            queue.Clear();
            if (source != null) source.Stop();
            speaking = null;
        }

        /// <summary>Called when the bridge is unloaded (hot reload or exit).</summary>
        public static void Shutdown()
        {
            Stop();
            if (speaker != null) UnityEngine.Object.Destroy(speaker);
            speaker = null;
            source = null;
            pumping = false;
            pickingKey = false;
            Unlock();
        }

        /// <summary>Every frame: let the narrator look at the flight and watch the push-to-talk key.</summary>
        public static void Update()
        {
            Narrator.Tick();
            LookAtSettings();
            if (pickingKey) PickKey();
            else if (pickLocked && (heldAfterPick == KeyCode.None || !Input.GetKey(heldAfterPick))) Unlock();
            if (previewAt > 0f && Time.realtimeSinceStartup >= previewAt)
            {
                // A narrator that is running says something in the voice just chosen (it reads the new voice from the file first).
                previewAt = -1f;
                Narrator.Say("This is the ship's computer.", 2, Narrator.Quiet, 5f, "voice-check", 0f);
            }
            bool down = ListenerAlive && Input.GetKey(talkKey) && !Typing() && !pickLocked;
#if DEV
            down |= ListenerAlive && devKeyDown;
#endif
            if (down == talkDown) return;
            talkDown = down;
            talkChanges++;
            try
            {
                if (listeningNote != null) { ScreenMessages.RemoveMessage(listeningNote); listeningNote = null; }
                if (down) listeningNote = ScreenMessages.PostScreenMessage("<color=#ffe08a>[ listening ]</color>", 60f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch { }
        }

        /// <summary>True while the player is typing into a text box, where the talk key is just a letter.</summary>
        static bool Typing()
        {
            if (GUIUtility.keyboardControl != 0) return true;
            UnityEngine.EventSystems.EventSystem system = UnityEngine.EventSystems.EventSystem.current;
            GameObject focus = system != null ? system.currentSelectedGameObject : null;
            return focus != null && (focus.GetComponent<TMPro.TMP_InputField>() != null || focus.GetComponent<UnityEngine.UI.InputField>() != null);
        }

        // ------------------------------------------------------------------ the voice's settings, and its part of the panel
        //
        // The voice edition keeps its settings in ~/.ksp-ai-bridge/voice.json (%USERPROFILE%\.ksp-ai-bridge on Windows),
        // the file ksp_voice.py reads; the narrator and the listener take a change up before their next line. The
        // game takes the push-to-talk key from there, and the panel writes the key and the voice there.

        // Kokoro's English voices, the 28 of the voices file the setup installs (the other 26 are for other
        // languages and read English with their accent): a American, b British; then f a woman, m a man.
        static readonly string[] Voices = {
            "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
            "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
            "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george", "bm_lewis" };
        const string DefaultVoice = "bf_emma", DefaultKey = "Y";      // (ksp_voice.py's defaults, which it leaves out of the file)
        const string PickLock = "AIBridgeTalkKey";

        static string settingsFile, voiceFolder;
        static DateTime settingsWritten;
        static float settingsLookedAt = -1000f, pickingSince, previewAt = -1f;
        static string voiceName = DefaultVoice, keyAlsoDoes;
        static KeyCode keyLookedUp = KeyCode.None;
        static bool voiceInstalled, hearingInstalled, pickingKey, pickLocked;
        static KeyCode heldAfterPick = KeyCode.None;
        static Array keyCodes;
        static GUIStyle smallButton, centred;

        static void Paths()
        {
            if (settingsFile != null) return;
            string home = Environment.GetEnvironmentVariable(Application.platform == RuntimePlatform.WindowsPlayer ? "USERPROFILE" : "HOME");
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dir = Path.Combine(home, ".ksp-ai-bridge");
            settingsFile = Path.Combine(dir, "voice.json");
            string models = Environment.GetEnvironmentVariable("KSP_VOICE_DIR");
            voiceFolder = string.IsNullOrEmpty(models) ? Path.Combine(dir, "voice") : models;
        }

        /// <summary>Read the settings file again when it has changed (looked at every few seconds).</summary>
        static void LookAtSettings(bool now = false)
        {
            if (!now && Time.realtimeSinceStartup - settingsLookedAt < 3f) return;
            settingsLookedAt = Time.realtimeSinceStartup;
            try
            {
                Paths();
                voiceInstalled = File.Exists(Path.Combine(voiceFolder, "voices-v1.0.bin"));
                hearingInstalled = Directory.Exists(Path.Combine(voiceFolder, "whisper"));
                DateTime written = File.Exists(settingsFile) ? File.GetLastWriteTimeUtc(settingsFile) : DateTime.MinValue;
                if (written == settingsWritten && !now) return;
                settingsWritten = written;
                Dictionary<string, object> stored = ReadSettings();
                object value = null;
                string voice = stored != null && stored.TryGetValue("voice", out value) ? value as string : null;
                voiceName = string.IsNullOrEmpty(voice) ? DefaultVoice : voice;
                string key = stored != null && stored.TryGetValue("key", out value) ? value as string : null;
                if (ParseKey(string.IsNullOrEmpty(key) ? DefaultKey : key, out KeyCode parsed)) talkKey = parsed;
            }
            catch (Exception ex) { BridgeHost.LogWarn("voice settings: " + ex.Message); }
        }

        static Dictionary<string, object> ReadSettings()
        {
            if (!File.Exists(settingsFile)) return null;
            try { return Json.Parse(File.ReadAllText(settingsFile)) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        /// <summary>One setting into the file, keeping the others. A default is left out, as ksp_voice.py does.</summary>
        static void WriteSetting(string name, string value, string fallback)
        {
            try
            {
                Paths();
                Dictionary<string, object> stored = ReadSettings();
                if (stored == null && File.Exists(settingsFile)) File.Copy(settingsFile, settingsFile + ".unreadable", true);   // (kept, not lost)
                if (stored == null) stored = new Dictionary<string, object>();
                if (value == fallback) stored.Remove(name);
                else stored[name] = value;
                Directory.CreateDirectory(Path.GetDirectoryName(settingsFile));
                // (written whole beside it and then put in its place, so that ksp_voice.py never reads half a file)
                string written = settingsFile + ".game.tmp";
                File.WriteAllText(written, Json.Write(stored));
                if (!File.Exists(settingsFile)) File.Move(written, settingsFile);
                else
                {
                    try { File.Replace(written, settingsFile, null); }
                    catch (Exception) { File.Copy(written, settingsFile, true); File.Delete(written); }
                }
                settingsWritten = File.GetLastWriteTimeUtc(settingsFile);
            }
            catch (Exception ex) { BridgeHost.LogWarn("could not keep the voice setting '" + name + "': " + ex.Message); }
        }

        static bool ParseKey(string name, out KeyCode key)
        {
            key = KeyCode.None;
            try { key = (KeyCode)Enum.Parse(typeof(KeyCode), name, true); }
            catch (Exception) { return false; }
            return key != KeyCode.None;
        }

        /// <summary>
        /// The voice's part of the in-game panel: what it is doing, and, when the voice edition is set up on this
        /// computer, the voice (when Kokoro is installed) and the push-to-talk key (when speech recognition is).
        /// </summary>
        public static void DrawPanel(GUIStyle label)
        {
            string line = PanelLine();
            if (line != null) GUILayout.Label("<size=11><color=#9fdcff>" + line + "</color></size>", label);
            if (!voiceInstalled && !hearingInstalled && !ListenerAlive) return;
            if (smallButton == null)
            {
                smallButton = new GUIStyle(GUI.skin.button) { fontSize = 11 };
                centred = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            }
            if (voiceInstalled)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("<size=11>voice</size>", label, GUILayout.Width(52));
                if (GUILayout.Button("<", GUILayout.Width(24))) StepVoice(-1);
                GUILayout.Label("<size=11>" + Described(voiceName) + "</size>", centred, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(">", GUILayout.Width(24))) StepVoice(1);
                GUILayout.EndHorizontal();
            }
            if (hearingInstalled || ListenerAlive)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("<size=11>talk key</size>", label, GUILayout.Width(52));
                if (GUILayout.Button(pickingKey ? "press a key (Esc keeps " + talkKey + ")" : talkKey.ToString(), smallButton) && !pickingKey) StartPicking();
                GUILayout.EndHorizontal();
                if (keyLookedUp != talkKey) { keyLookedUp = talkKey; keyAlsoDoes = GameUse(talkKey); }
                if (keyAlsoDoes != null && !pickingKey)
                    GUILayout.Label("<size=10><color=#ffb040>" + talkKey + " is also the game's key for " + keyAlsoDoes + ": both happen</color></size>", label);
            }
        }

        /// <summary>"bf_emma" as "Emma, British woman".</summary>
        static string Described(string voice)
        {
            if (string.IsNullOrEmpty(voice) || voice.Length < 4 || voice[2] != '_' || (voice[0] != 'a' && voice[0] != 'b')) return voice;
            return char.ToUpperInvariant(voice[3]) + voice.Substring(4) + ", " + (voice[0] == 'a' ? "American" : "British") +
                   (voice[1] == 'f' ? " woman" : voice[1] == 'm' ? " man" : "");
        }

        static void StepVoice(int step)
        {
            int at = Array.IndexOf(Voices, voiceName);
            at = at < 0 ? (step > 0 ? 0 : Voices.Length - 1) : (at + step + Voices.Length) % Voices.Length;
            voiceName = Voices[at];
            WriteSetting("voice", voiceName, DefaultVoice);
            previewAt = Time.realtimeSinceStartup + 0.9f;       // (once the clicking has stopped)
        }

        static void StartPicking()
        {
            pickingKey = true;
            pickingSince = Time.realtimeSinceStartup;
            GUIUtility.keyboardControl = 0;                      // (the key is not typed into the panel's text box)
            // The key pressed is for the panel only: Space must not stage the rocket, M must not open the map.
            if (!pickLocked) InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, PickLock);
            pickLocked = true;
            heldAfterPick = KeyCode.None;
        }

        /// <summary>While picking: the first key, extra mouse button or controller button pressed becomes the talk key.</summary>
        static void PickKey()
        {
            if (Time.realtimeSinceStartup - pickingSince > 15f) { EndPicking(KeyCode.None, KeyCode.None); return; }
            if (!Input.anyKeyDown) return;
            foreach (KeyCode cancel in new[] { KeyCode.Escape, KeyCode.Mouse0, KeyCode.Mouse1 })
                if (Input.GetKeyDown(cancel)) { EndPicking(KeyCode.None, cancel); return; }
            if (keyCodes == null) keyCodes = Enum.GetValues(typeof(KeyCode));
            foreach (KeyCode key in keyCodes)
                if (key != KeyCode.None && Input.GetKeyDown(key)) { EndPicking(key, key); return; }
        }

        /// <summary>Stop picking; the game's keys stay locked until the key that ended it is let go.</summary>
        static void EndPicking(KeyCode chosen, KeyCode pressed)
        {
            pickingKey = false;
            heldAfterPick = pressed;
            if (chosen == KeyCode.None) return;
            talkKey = chosen;
            WriteSetting("key", chosen.ToString(), DefaultKey);
            host?.Note("talk key set to " + chosen);
        }

        static void Unlock()
        {
            if (pickLocked) InputLockManager.RemoveControlLock(PickLock);
            pickLocked = false;
            heldAfterPick = KeyCode.None;
        }

        /// <summary>What the game itself does with a key, if anything (B is the brakes): both happen when it is held to talk.</summary>
        static string GameUse(KeyCode key)
        {
            try
            {
                foreach (System.Reflection.FieldInfo field in typeof(GameSettings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (field.FieldType != typeof(KeyBinding)) continue;
                    KeyBinding binding = field.GetValue(null) as KeyBinding;
                    if (binding == null) continue;
                    if ((binding.primary != null && binding.primary.code == key) || (binding.secondary != null && binding.secondary.code == key))
                        return field.Name.Replace('_', ' ').ToLowerInvariant();
                }
            }
            catch (Exception) { }
            return null;
        }

#if DEV
        static object DevPanel(Args a)
        {
            if (a.Has("voice_step")) StepVoice(a.Int("voice_step") < 0 ? -1 : 1);
            if (a.Bool("pick")) StartPicking();
            if (a.Has("key"))
            {
                if (!ParseKey(a.Str("key"), out KeyCode key)) throw new ApiException("Unknown key '" + a.Str("key") + "'.");
                if (!pickingKey) StartPicking();
                EndPicking(key, KeyCode.None);
            }
            if (a.Bool("cancel") && pickingKey) EndPicking(KeyCode.None, KeyCode.None);
            return Status().Add("picking", pickingKey).Add("locked", pickLocked).Add("voice_installed", voiceInstalled).Add("hearing_installed", hearingInstalled)
                .Add("settings_file", settingsFile).Add("key_also_does", GameUse(talkKey));
        }
#endif

        // ------------------------------------------------------------------ what the ship says

        static IEnumerator Feed(Call c)
        {
            Args a = c.Args;
            string client = a.Str("client") ?? "voice";
            if (feedClient != null && feedClient != client && Time.realtimeSinceStartup - feedSeenAt < 12f)
            {
                // Two narrators would talk over each other: the first one keeps the job.
                c.Result = new JObj().Add("busy", true).Add("holder", feedClient).Add("last_id", Narrator.LastId);
                yield break;
            }
            feedClient = client;
            string detail = a.Str("detail");
            if (detail != null) Narrator.Detail = Narrator.ParseDetail(detail);
            bool fresh = !a.Has("since");
            int since = fresh ? Narrator.LastId : a.Int("since");
            float until = Time.realtimeSinceStartup + Mathf.Clamp((float)a.Num("timeout_s", 20), 0f, 40f);
            while (true)
            {
                feedSeenAt = Time.realtimeSinceStartup;
                Narrator.Touch();
                if (fresh || Narrator.LastId > since || Time.realtimeSinceStartup >= until || c.Abandoned) break;
                yield return null;
            }
            c.Result = new JObj().Add("lines", Narrator.Since(since)).Add("last_id", Narrator.LastId).Add("detail", Narrator.Named(Narrator.Detail));
        }

        static object Config(Args a)
        {
            string key = a.Str("key");
            if (key != null)
            {
                KeyCode parsed;
                try { parsed = (KeyCode)Enum.Parse(typeof(KeyCode), key, true); }
                catch { throw new ApiException("Unknown key '" + key + "'. Use a Unity key name such as Y, B, RightControl, F8 or Mouse3."); }
                if (parsed == KeyCode.None) throw new ApiException("Pick a real key.");
                talkKey = parsed;
            }
            string detail = a.Str("detail");
            if (detail != null) Narrator.Detail = Narrator.ParseDetail(detail);
            return Status();
        }

        // ------------------------------------------------------------------ what the player says

        static IEnumerator Key(Call c)
        {
            Args a = c.Args;
            int seen = a.Int("seen", -1);
            float until = Time.realtimeSinceStartup + Mathf.Clamp((float)a.Num("timeout_s", 20), 0f, 40f);
            while (true)
            {
                listenerSeenAt = Time.realtimeSinceStartup;
                if (talkChanges != seen || Time.realtimeSinceStartup >= until || c.Abandoned) break;
                yield return null;
            }
            c.Result = new JObj().Add("down", talkDown).Add("changes", talkChanges).Add("key", talkKey.ToString());
        }

        static object Heard(Args a)
        {
            string text = (a.Require("text") ?? "").Trim();
            if (text.Length == 0) throw new ApiException("Nothing was said.");
            if (text.Length > 600) text = text.Substring(0, 600);
            listenerSeenAt = Time.realtimeSinceStartup;
            heardTotal++;
            if (a.Bool("caption", true))
            {
                try { ScreenMessages.PostScreenMessage("<color=#ffe08a>You: " + text.Replace("<", "‹").Replace(">", "›") + "</color>", 6f, ScreenMessageStyle.UPPER_CENTER); } catch { }
            }
            if (a.Bool("queue", true))
            {
                heard.Enqueue(text);
                while (heard.Count > 8) heard.Dequeue();
            }
            host.Note("player said: " + text);
            // (answered at once, as what is typed is: see Company. Words that are not kept for voice_next are with whoever passed them on already.)
            Company.Asked();
            if (!a.Bool("queue", true)) Company.Given();
            return new JObj().Add("queued", heard.Count);
        }

        static IEnumerator Next(Call c)
        {
            float until = Time.realtimeSinceStartup + Mathf.Clamp((float)c.Args.Num("timeout_s", 20), 0f, 40f);
            Company.Seen();
            collecting++;
            try
            {
                while (heard.Count == 0 && Time.realtimeSinceStartup < until && !c.Abandoned) yield return null;
            }
            finally { collecting--; }
            var o = new JObj();
            if (heard.Count > 0 && !c.Abandoned) { o.Add("heard", heard.Dequeue()); Company.Given(); }
            else o.Add("heard", null);
            o.Add("waiting", heard.Count).Add("listening", ListenerAlive).Add("talk_key", talkKey.ToString());
            c.Result = o;
        }

        // ------------------------------------------------------------------ playing speech

        static void EnsureSpeaker()
        {
            if (speaker != null) return;
            speaker = new GameObject("AIBridgeVoice");
            UnityEngine.Object.DontDestroyOnLoad(speaker);
            source = speaker.AddComponent<AudioSource>();
            source.spatialBlend = 0f;            // in the player's head, not somewhere in the scene
            source.playOnAwake = false;
            source.ignoreListenerPause = true;   // keeps talking in the pause menu and the editors
            source.priority = 0;
            highPass = speaker.AddComponent<AudioHighPassFilter>();
            lowPass = speaker.AddComponent<AudioLowPassFilter>();
            distortion = speaker.AddComponent<AudioDistortionFilter>();
            reverb = speaker.AddComponent<AudioReverbFilter>();
#if DEV
            speaker.AddComponent<VoiceTap>();    // after the effects, so it hears what the player hears
#endif
        }

        static void ApplyEffect(string effect)
        {
            switch (effect)
            {
                case "radio":
                    // A small speaker a long way away: narrow band and a little grit.
                    highPass.enabled = lowPass.enabled = distortion.enabled = true;
                    reverb.enabled = false;
                    highPass.cutoffFrequency = 420f;
                    lowPass.cutoffFrequency = 3100f;
                    distortion.distortionLevel = 0.22f;
                    break;
                case "none":
                    highPass.enabled = lowPass.enabled = distortion.enabled = reverb.enabled = false;
                    break;
                default:
                    // The ship's computer: full voice, slightly metallic, in a room that hums.
                    highPass.enabled = lowPass.enabled = reverb.enabled = true;
                    distortion.enabled = false;
                    highPass.cutoffFrequency = 130f;
                    lowPass.cutoffFrequency = 7800f;
                    reverb.reverbPreset = AudioReverbPreset.User;
                    reverb.dryLevel = 0f;
                    reverb.room = -900f;
                    reverb.roomHF = -1400f;
                    reverb.decayTime = 0.9f;
                    reverb.decayHFRatio = 0.5f;
                    reverb.reflectionsLevel = -1300f;
                    reverb.reflectionsDelay = 0.012f;
                    reverb.reverbLevel = -700f;
                    reverb.reverbDelay = 0.02f;
                    reverb.diffusion = 80f;
                    reverb.density = 90f;
                    break;
            }
        }

        static object Play(Args a)
        {
            string effect = (a.Str("effect") ?? "ship").ToLowerInvariant();
            if (effect != "ship" && effect != "radio" && effect != "none") throw new ApiException("effect must be ship, radio or none.");
            string text = a.Str("text");
            AudioClip clip = null;
            float seconds;
            if (a.Has("wav_base64"))
            {
                byte[] wav;
                try { wav = Convert.FromBase64String(a.Str("wav_base64")); }
                catch { throw new ApiException("wav_base64 is not valid base64."); }
                clip = Decode(wav, a.Bool("chirp", effect != "none"));
                seconds = clip.length;
            }
            else
            {
                // No voice installed: the words still appear, for about as long as they would take to say.
                if (string.IsNullOrEmpty(text)) throw new ApiException("Give wav_base64 (speech) or text (a caption), or both.");
                seconds = Mathf.Clamp(text.Length * 0.065f, 1.5f, 12f);
            }
            // (a line of the AI's own is the AI speaking to the player; any other is the ship's computer, and still not silence)
            if (a.Str("by") == "ai") Company.Spoke(); else Company.Sounded();
            if (a.Bool("interrupt", false)) Stop();
            if (queue.Count >= 12) throw new ApiException("The ship's voice has 12 lines waiting already; let it catch up (voice_status) or use voice_stop.");
            queue.Enqueue(new Line { Clip = clip, Text = a.Bool("caption", true) ? text : null, Effect = effect, Seconds = seconds, Volume = Mathf.Clamp01((float)a.Num("volume", 1.0)) });
            if (!pumping)
            {
                pumping = true;
                host.RunBackground(Pump(), "voice");
            }
            return new JObj().Add("seconds", Math.Round(seconds, 2)).Add("waiting", queue.Count).Add("speaking", speaking).Add("sound", clip != null);
        }

        static int failedStarts;
#if DEV
        static int silentLines;
#endif

        static string AudioState()
        {
            AudioListener[] listeners = UnityEngine.Object.FindObjectsOfType<AudioListener>();
            int enabled = 0;
            foreach (AudioListener l in listeners) if (l.isActiveAndEnabled) enabled++;
            AudioConfiguration config = AudioSettings.GetConfiguration();
            return "playing " + (source != null && source.isPlaying) + ", listeners " + enabled + "/" + listeners.Length + ", listener paused " + AudioListener.pause +
                   ", time scale " + Time.timeScale + ", warp " + TimeWarp.CurrentRate + ", real voices " + config.numRealVoices + ", scene " + HighLogic.LoadedScene;
        }

        static IEnumerator Pump()
        {
            EnsureSpeaker();
            while (queue.Count > 0)
            {
                Line line = queue.Dequeue();
                speaking = line.Text ?? "(speech)";
                lastSpoken = speaking;
                spokenCount++;
                if (line.Clip != null)
                {
                    ApplyEffect(line.Effect);
                    source.clip = line.Clip;
                    // The game's master and voice sliders both apply; a voice slider at zero means "no voices".
                    float voices = Mathf.Clamp01(GameSettings.VOICE_VOLUME);
                    source.volume = line.Volume * Mathf.Clamp01(GameSettings.MASTER_VOLUME) * (voices <= 0f ? 0f : Mathf.Lerp(0.7f, 1f, voices));
                    source.Play();
                }
                if (!string.IsNullOrEmpty(line.Text))
                {
                    try { ScreenMessages.PostScreenMessage("<color=#9fdcff>" + line.Text.Replace("<", "‹").Replace(">", "›") + "</color>", Mathf.Max(2.5f, line.Seconds + 0.8f), ScreenMessageStyle.UPPER_CENTER); } catch { }
                }
                float started = Time.realtimeSinceStartup;
                float until = started + line.Seconds + 0.35f;
                bool checkedStart = line.Clip == null;
#if DEV
                int soundBefore = VoiceTap.SoundBlocks;
                bool wasVirtual = false;
#endif
                // A line cut short by voice_stop ends at once; otherwise wait for it to finish.
                while (source != null && Time.realtimeSinceStartup < until && speaking != null &&
                       (line.Clip == null || source.isPlaying || Time.realtimeSinceStartup - started < 0.3f))
                {
                    if (!checkedStart && Time.realtimeSinceStartup - started > 0.2f)
                    {
                        checkedStart = true;
                        if (!source.isPlaying)
                        {
                            // The audio engine now and then refuses a sound it has just been given; ask once more.
                            failedStarts++;
                            BridgeHost.LogWarn("voice: \"" + speaking + "\" did not start (" + AudioState() + "); trying again");
                            source.Play();
                        }
                    }
#if DEV
                    wasVirtual |= source.isVirtual;
#endif
                    yield return null;
                }
#if DEV
                if (line.Clip != null && speaking != null && VoiceTap.SoundBlocks == soundBefore)
                {
                    silentLines++;
                    BridgeHost.LogWarn("voice: \"" + speaking + "\" made no sound (virtual " + wasVirtual + ", " + AudioState() + ")");
                }
#endif
                if (line.Clip != null) UnityEngine.Object.Destroy(line.Clip);
                if (source == null) break;
            }
            speaking = null;
            pumping = false;
        }

        /// <summary>Turn a 16-bit PCM WAV file into a clip, optionally with an intercom chirp in front of it.</summary>
        static AudioClip Decode(byte[] wav, bool chirp)
        {
            if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' || wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
                throw new ApiException("That is not a WAV file.");
            int channels = 0, rate = 0, bits = 0, format = 0, dataAt = -1, dataLength = 0;
            int at = 12;
            while (at + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, at, 4);
                int size = BitConverter.ToInt32(wav, at + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(wav, at + 8);
                    channels = BitConverter.ToInt16(wav, at + 10);
                    rate = BitConverter.ToInt32(wav, at + 12);
                    bits = BitConverter.ToInt16(wav, at + 22);
                }
                else if (id == "data")
                {
                    dataAt = at + 8;
                    // Some writers leave the length open (0 or -1) when they stream the file out.
                    dataLength = size <= 0 ? wav.Length - dataAt : Math.Min(size, wav.Length - dataAt);
                    break;
                }
                if (size < 0) break;
                at += 8 + size + (size & 1);
            }
            if (dataAt < 0 || channels < 1 || channels > 2 || rate < 8000 || rate > 96000) throw new ApiException("Cannot read that WAV file (need 1 or 2 channels at 8 to 96 kHz).");
            if (format != 1 || bits != 16) throw new ApiException("The WAV must be 16-bit PCM (it is format " + format + ", " + bits + " bits).");
            int frames = dataLength / (2 * channels);
            if (frames < rate / 50) throw new ApiException("The WAV holds no sound.");
            if (frames > rate * 60) throw new ApiException("A single line may be at most 60 seconds long.");

            int lead = chirp ? (int)(rate * 0.22f) : 0;
            var samples = new float[(frames + lead) * channels];
            if (chirp)
            {
                // Two short rising tones, the "attention" sound of a ship's intercom.
                for (int n = 0; n < lead; n++)
                {
                    float t = n / (float)rate;
                    float tone = t < 0.07f ? 880f : t < 0.09f ? 0f : t < 0.16f ? 1320f : 0f;
                    float edge = t < 0.07f ? Mathf.Min(t, 0.07f - t) : Mathf.Min(Mathf.Max(0f, t - 0.09f), Mathf.Max(0f, 0.16f - t));
                    float value = tone > 0f ? Mathf.Sin(2f * Mathf.PI * tone * t) * 0.16f * Mathf.Clamp01(edge / 0.008f) : 0f;
                    for (int c = 0; c < channels; c++) samples[n * channels + c] = value;
                }
            }
            int offset = lead * channels;
            float peak = 0f;
            for (int n = 0; n < frames * channels; n++)
            {
                float value = BitConverter.ToInt16(wav, dataAt + n * 2) / 32768f;
                samples[offset + n] = value;
                peak = Mathf.Max(peak, Mathf.Abs(value));
            }
            // Speech engines differ a lot in level; bring every line to the same loudness.
            if (peak > 0.02f)
            {
                float gain = Mathf.Min(0.9f / peak, 6f);
                for (int n = offset; n < samples.Length; n++) samples[n] *= gain;
            }
            AudioClip clip = AudioClip.Create("ai-bridge-voice", frames + lead, channels, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

#if DEV
        static bool devKeyDown;

        /// <summary>Sits after the voice's effects and copies what passes through, so a test can check the result is still speech.</summary>
        sealed class VoiceTap : MonoBehaviour
        {
            public static readonly List<float> Samples = new List<float>();
            public static volatile bool On;
            public static int Channels = 2, Calls, SoundBlocks;
            int quietBlocks;

            void OnAudioFilterRead(float[] data, int channels)
            {
                Calls++;
                bool sound = false;
                for (int n = 0; n < data.Length && !sound; n += 16) sound = Mathf.Abs(data[n]) > 1e-4f;
                if (sound) SoundBlocks++;
                if (!On) return;
                Channels = channels;
                // Keep only the stretches with sound in them, so a recording of a whole flight holds its speech and little else.
                if (sound) quietBlocks = 0; else if (++quietBlocks > 12) return;
                lock (Samples) { if (Samples.Count < 48000 * 2 * 240) Samples.AddRange(data); }
            }
        }

        static object Tap(Args a)
        {
            if (a.Has("on"))
            {
                if (a.Bool("on")) lock (VoiceTap.Samples) VoiceTap.Samples.Clear();     // a new recording; stopping keeps it for saving
                VoiceTap.On = a.Bool("on");
            }
            var o = new JObj().Add("recording", VoiceTap.On).Add("rate", AudioSettings.outputSampleRate).Add("channels", VoiceTap.Channels);
            o.Add("callbacks", VoiceTap.Calls).Add("listener_volume", AudioListener.volume).Add("listener_paused", AudioListener.pause)
             .Add("master_volume", GameSettings.MASTER_VOLUME).Add("voice_volume", GameSettings.VOICE_VOLUME);
            if (source != null)
                o.Add("source", new JObj().Add("playing", source.isPlaying).Add("time", Math.Round(source.time, 2)).Add("volume", Math.Round(source.volume, 3))
                    .Add("enabled", source.enabled).Add("mute", source.mute).Add("active", speaker.activeInHierarchy)
                    .Add("clip", source.clip != null ? source.clip.length + " s, " + source.clip.loadState : null));
            string path = a.Str("save");
            float[] samples;
            lock (VoiceTap.Samples) samples = VoiceTap.Samples.ToArray();
            o.Add("seconds", Math.Round(samples.Length / (double)Math.Max(1, VoiceTap.Channels) / AudioSettings.outputSampleRate, 2));
            double peak = 0;
            foreach (float s in samples) peak = Math.Max(peak, Math.Abs(s));
            o.Add("peak", Math.Round(peak, 3));
            if (path != null)
            {
                int channels = Math.Max(1, VoiceTap.Channels), rate = AudioSettings.outputSampleRate;
                using (var f = new System.IO.BinaryWriter(System.IO.File.Create(path)))
                {
                    f.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                    f.Write(36 + samples.Length * 2);
                    f.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                    f.Write(16); f.Write((short)1); f.Write((short)channels); f.Write(rate); f.Write(rate * channels * 2); f.Write((short)(channels * 2)); f.Write((short)16);
                    f.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                    f.Write(samples.Length * 2);
                    foreach (float s in samples) f.Write((short)Mathf.Clamp(s * 32767f, -32768f, 32767f));
                }
                o.Add("saved", path);
            }
            return o;
        }
#endif
    }
}
