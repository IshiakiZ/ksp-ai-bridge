using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AIBridge
{
    /// <summary>
    /// The mod's single long-lived object. Owns the local server, runs every request on Unity's main
    /// thread (KSP's API is not thread-safe), steps multi-frame operations, and draws the in-game panel.
    /// </summary>
    public sealed class BridgeHost : MonoBehaviour
    {
        public const string Version = "0.9.0";
        public const int DefaultPort = 5770;

        public static BridgeHost Instance { get; private set; }

        /// <summary>The player's master switch. When false, every command that changes the game is refused.</summary>
        public bool AiEnabled = true;

        HttpServer server;
        readonly Queue<Call> inbox = new Queue<Call>();
        readonly List<Routine> routines = new List<Routine>();
        readonly List<string> activity = new List<string>();
        readonly List<string> shown = new List<string>();         // the last few of those, as the panel shows them
        string lastActivity = "waiting for an AI to connect";
        float lastCallAt = -1000f;
        long callCount;
        string startupError;

        Rect window = new Rect(-1, 96, 268, 0);
        bool collapsed;
        bool hudVisible = true;
        float panelHigh;                                          // how tall the panel was when it was last drawn
        bool carried; Vector2 carriedBy;                          // the panel is held by the pointer, and where on it the pointer has hold
        string draft = "";                                        // what the player is typing to the AI
        bool typingLocked;
        const string SayBox = "aibridge:say", TypingLock = "AIBridgeTyping", PanelLock = "AIBridgePanel";
        static readonly int GripHint = "AIBridge grip".GetHashCode();
        const float PanelWide = 268f;
        bool interfaceHidden;                                     // the player has put the game's whole interface away (F2): the panel goes with it
        const int WindowId = 0x41494272;

        sealed class Routine
        {
            public Call Call;
            public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
            public float ResumeAt;
            public bool Blocked;
            public bool Finished;
        }

        // ---- paths ----

        public static string ModDir => KSPUtil.ApplicationRootPath + "GameData/AIBridge/";
        public static string DataDir => ModDir + "PluginData/";

        // ---- logging ----

        public static void Log(string msg) => Debug.Log("[AIBridge] " + msg);
        public static void LogWarn(string msg) => Debug.LogWarning("[AIBridge] " + msg);

        /// <summary>Record something the AI did so the player can see it in the panel and the AI can read it back.</summary>
        public void Note(string text) => Note(text, true);

        /// <summary>(What is said between the player and the AI is recorded too, but the panel shows it as a conversation, not among these lines.)</summary>
        public void Note(string text, bool inPanel)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            activity.Add(stamp + "  " + text);
            if (activity.Count > 300) activity.RemoveRange(0, activity.Count - 300);
            if (!inPanel) return;
            lastActivity = text;
            shown.Add(stamp + "  " + text);
            if (shown.Count > 3) shown.RemoveAt(0);
        }

        public IList<string> Activity => activity;

        // ---- lifecycle ----

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            GameEvents.onHideUI.Add(InterfaceHidden);
            GameEvents.onShowUI.Add(InterfaceShown);
            GameEvents.onGameSceneLoadRequested.Add(SceneLeft);
            try { hudVisible = !File.Exists(DataDir + "panel.txt") || File.ReadAllText(DataDir + "panel.txt").Trim() != "hidden"; }
            catch (Exception) { }
            try
            {
                Directory.CreateDirectory(DataDir);
                Rpc.Clear();
                Api.Registry.RegisterAll(this);
                string token = NewToken();
                server = new HttpServer(this, token);
                server.Start(DefaultPort, 10);
                WriteInfoFile(token);
                Log("v" + Version + " listening on 127.0.0.1:" + server.Port);
            }
            catch (Exception ex)
            {
                startupError = ex.Message;
                Debug.LogError("[AIBridge] failed to start: " + ex);
            }
        }

        /// <summary>Whether the game's whole interface is put away just now (F2, or a mod that does it, such as Cinema Camera's viewfinder).</summary>
        internal static bool InterfaceIsHidden => Instance != null && Instance.interfaceHidden;

        /// <summary>
        /// The navball kept minimised (the tab at the bottom of the flight screen that folds it away): asked for by whoever is
        /// testing with the game on screen, so that it is not in the way of what is being looked at (see the "navball" command).
        /// The game brings it back at every new flight and on leaving the map, so it is put down again whenever it is found up.
        /// On in the development build, which is what tests are run on; off otherwise, until asked for.
        /// </summary>
#if DEV
        internal static bool NavballDown = true;
#else
        internal static bool NavballDown;
#endif

        static void KeepNavball()
        {
            if (!NavballDown || !HighLogic.LoadedSceneIsFlight) return;
            KSP.UI.Screens.Flight.NavBallToggle ball = KSP.UI.Screens.Flight.NavBallToggle.Instance;
            if (ball != null && ball.panel != null && ball.panel.expanded) ball.panel.Collapse();
        }

        void InterfaceHidden() => interfaceHidden = true;
        void InterfaceShown() => interfaceHidden = false;
        void SceneLeft(GameScenes to) => interfaceHidden = false;          // (the game brings its interface back with every new scene)

        void OnDestroy()
        {
            TypingLocked(false);
            InputLockManager.RemoveControlLock(PanelLock);
            GameEvents.onHideUI.Remove(InterfaceHidden);
            GameEvents.onShowUI.Remove(InterfaceShown);
            GameEvents.onGameSceneLoadRequested.Remove(SceneLeft);
            try { if (launcher != null && KSP.UI.Screens.ApplicationLauncher.Instance != null) KSP.UI.Screens.ApplicationLauncher.Instance.RemoveModApplication(launcher); } catch { }
            if (launcherIcon != null) Destroy(launcherIcon);
            try { server?.Stop(); } catch { }
            try { Pilot.Autopilot.Shutdown(); } catch { }
            try { SceneWatch.Unhook(); } catch { }
            try { File.Delete(DataDir + "bridge.json"); } catch { }
            lock (inbox)
            {
                while (inbox.Count > 0) { Call c = inbox.Dequeue(); c.Error = "AIBridge is shutting down"; c.Done.Set(); }
            }
            foreach (Routine r in routines) { r.Call.Error = "AIBridge is shutting down"; r.Call.Done.Set(); }
            routines.Clear();
            if (Instance == this) Instance = null;
            Log("stopped");
        }

        static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new StringBuilder(48);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        void WriteInfoFile(string token)
        {
            var info = new JObj()
                .Add("port", server.Port)
                .Add("token", token)
                .Add("version", Version)
                .Add("pid", System.Diagnostics.Process.GetCurrentProcess().Id)
                .Add("started", DateTime.UtcNow.ToString("o"));
            File.WriteAllText(DataDir + "bridge.json", Json.Write(info));
        }

        // ---- request pump ----

        /// <summary>Called from network threads.</summary>
        public void Submit(Call call)
        {
            lock (inbox) inbox.Enqueue(call);
        }

        /// <summary>Run a multi-frame job that no network request is waiting for (flight plans).</summary>
        public void RunBackground(IEnumerator job, string name)
        {
            var routine = new Routine { Call = new Call { Method = name } };
            routine.Stack.Push(job);
            routines.Add(routine);
        }

        void Update()
        {
            try { Api.BuildShow.Watch(); } catch (Exception) { }
            try { KeepNavball(); } catch (Exception) { }
            while (true)
            {
                Call call;
                lock (inbox)
                {
                    if (inbox.Count == 0) break;
                    call = inbox.Dequeue();
                }
                Begin(call);
            }

            for (int n = routines.Count - 1; n >= 0; n--)
            {
                Routine r = routines[n];
                if (r.Finished) { routines.RemoveAt(n); continue; }
                if (r.Blocked || Time.realtimeSinceStartup < r.ResumeAt) continue;
                Step(r);
                if (r.Finished) routines.RemoveAt(n);
            }

            try { Api.VoiceApi.Update(); }
            catch (Exception ex) { LogWarn("voice update failed: " + ex.Message); }
            try { Api.Company.Update(); }
            catch (Exception ex) { LogWarn("company update failed: " + ex.Message); }
            Pointer();

            if (Input.GetKeyDown(KeyCode.F10) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))) ShowPanel(!hudVisible);
            try { Launcher(); }
            catch (Exception ex) { LogWarn("toolbar button: " + ex.Message); }
        }

        // ---- the panel's button on the game's toolbar ----

        KSP.UI.Screens.ApplicationLauncherButton launcher;
        Texture2D launcherIcon;

        /// <summary>
        /// The panel can be put away: its own × (or Alt+F10) closes it, and a button on the game's toolbar (the
        /// strip of buttons down the side of the screen in flight) brings it back. Whether it was open is
        /// remembered from one run of the game to the next.
        /// </summary>
        public bool PanelShown => hudVisible;

        public void ShowPanel(bool shown)
        {
            if (hudVisible == shown) return;
            hudVisible = shown;
            if (launcher != null)
            {
                if (shown) launcher.SetTrue(false);
                else launcher.SetFalse(false);
            }
            try { File.WriteAllText(DataDir + "panel.txt", shown ? "shown" : "hidden"); }
            catch (Exception) { }
        }

        void Launcher()
        {
            if (launcher != null) return;
            if (HighLogic.LoadedScene == GameScenes.LOADING || HighLogic.LoadedScene == GameScenes.LOADINGBUFFER) return;
            if (!KSP.UI.Screens.ApplicationLauncher.Ready || KSP.UI.Screens.ApplicationLauncher.Instance == null) return;
            if (launcherIcon == null) launcherIcon = LauncherIcon();
            launcher = KSP.UI.Screens.ApplicationLauncher.Instance.AddModApplication(() => ShowPanel(true), () => ShowPanel(false), null, null, null, null,
                                                                                     KSP.UI.Screens.ApplicationLauncher.AppScenes.ALWAYS, launcherIcon);
            if (hudVisible) launcher.SetTrue(false);
        }

        /// <summary>The button's picture: the letters A I, drawn here so that the mod needs no picture file.</summary>
        static Texture2D LauncherIcon()
        {
            const int size = 38;
            // (each row of a letter, from the top, a bit for each of its five columns)
            int[] a = { 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 }, i = { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x1F };
            var picture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            var ink = new Color32(170, 235, 190, 255);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // two letters, each five columns by seven rows of three-pixel squares, with a gap of two columns between
                    int column = (x - 2) / 3, row = (size - 1 - y - 8) / 3;
                    bool on = false;
                    if (x >= 2 && row >= 0 && row < 7 && size - 1 - y >= 8)
                    {
                        if (column < 5) on = (a[row] >> (4 - column) & 1) != 0;
                        else if (column >= 6 && column < 11) on = (i[row] >> (10 - column) & 1) != 0;
                    }
                    pixels[y * size + x] = on ? ink : new Color32(0, 0, 0, 0);
                }
            picture.SetPixels32(pixels);
            picture.Apply();
            return picture;
        }

        void FixedUpdate()
        {
            try { Pilot.Autopilot.FixedTick(); }
            catch (Exception ex) { LogWarn("autopilot tick failed: " + ex); }
            // (how hard the craft is being strained, the worst of which is kept for the AI to ask about)
            try { Api.StructureApi.FixedTick(); }
            catch (Exception) { }
            // (and what the air is doing to it)
            try { Api.AirApi.FixedTick(); }
            catch (Exception) { }
        }

        void Begin(Call call)
        {
            RpcEntry entry = Rpc.Find(call.Method);
            if (entry == null)
            {
                call.Error = "Unknown method '" + call.Method + "'. Call 'help' for the list.";
                call.Done.Set();
                return;
            }
            if (!AiEnabled && !entry.AlwaysAllowed)
            {
                call.Error = "The player has paused AI control in the AI Bridge panel. Ask them to press Resume AI.";
                call.Done.Set();
                return;
            }
            callCount++;
            lastCallAt = Time.realtimeSinceStartup;
            try
            {
                if (entry.Sync != null)
                {
                    call.Result = entry.Sync(call.Args);
                    call.Done.Set();
                    return;
                }
                var routine = new Routine { Call = call };
                routine.Stack.Push(entry.Async(call));
                routines.Add(routine);
                Step(routine);
            }
            catch (ApiException ex) { call.Error = ex.Message; call.Done.Set(); }
            catch (Exception ex)
            {
                LogWarn(call.Method + " threw: " + ex);
                call.Error = ex.GetType().Name + ": " + ex.Message;
                call.Done.Set();
            }
        }

        void Step(Routine r)
        {
            try
            {
                while (true)
                {
                    if (r.Stack.Count == 0) { Finish(r); return; }
                    IEnumerator top = r.Stack.Peek();
                    if (!top.MoveNext()) { r.Stack.Pop(); continue; }
                    object y = top.Current;
                    if (y is IEnumerator nested) { r.Stack.Push(nested); continue; }
                    if (y is WaitSec w) { r.ResumeAt = Time.realtimeSinceStartup + w.Seconds; return; }
                    if (y is WaitFrameEnd) { r.Blocked = true; StartCoroutine(ResumeAtFrameEnd(r)); return; }
                    return;
                }
            }
            catch (ApiException ex) { r.Call.Error = ex.Message; Finish(r); }
            catch (Exception ex)
            {
                LogWarn(r.Call.Method + " threw: " + ex);
                r.Call.Error = ex.GetType().Name + ": " + ex.Message;
                Finish(r);
            }
        }

        IEnumerator ResumeAtFrameEnd(Routine r)
        {
            yield return new WaitForEndOfFrame();
            r.Blocked = false;
            if (!r.Finished) Step(r);
        }

        static void Finish(Routine r)
        {
            r.Finished = true;
            r.Call.Done.Set();
        }

        // ---- in-game panel ----

#if DEV
        /// <summary>For the development build: how often the panel has been drawn, and how often with no height (the blink it had while dragged), and where it is.</summary>
        static int DevDrawn, DevBlank;
        public static string DevPanel() => Instance == null ? "not running" : "drawn " + DevDrawn + " times, " + DevBlank + " with no height; at " + Instance.window.x + "," + Instance.window.y + ", " + Instance.panelHigh + " high; cameras " + (InputLockManager.lockStack.ContainsKey(PanelLock) ? "held" : "free");

        /// <summary>Text drawn large along the bottom of the screen while a video is being recorded.</summary>
        public static string DevCaption;
        public static bool DevHidePanel;
        static GUIStyle captionStyle;

        static void DrawCaption()
        {
            if (captionStyle == null) captionStyle = new GUIStyle { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, richText = true };
            captionStyle.fontSize = Mathf.RoundToInt(Screen.height / 24f);
            // (a caption that begins with ^ goes along the top instead: for a picture with the game's own interface along the bottom)
            bool top = DevCaption.StartsWith("^");
            string words = top ? DevCaption.Substring(1) : DevCaption;
            var where = new Rect(0, Screen.height * (top ? 0.105f : 0.87f), Screen.width, Screen.height * 0.09f);
            if (top) { GUI.color = new Color(0f, 0f, 0f, 0.55f); GUI.DrawTexture(new Rect(0, where.y, Screen.width, where.height), Texture2D.whiteTexture); GUI.color = Color.white; }
            captionStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(where.x + 2, where.y + 2, where.width, where.height), words, captionStyle);
            captionStyle.normal.textColor = Color.white;
            GUI.Label(where, words, captionStyle);
        }
#endif

        void OnGUI()
        {
#if DEV
            if (!string.IsNullOrEmpty(DevCaption)) DrawCaption();
            if (DevHidePanel) return;
#endif
            if (!hudVisible || interfaceHidden) { TypingLocked(false); return; }
            if (HighLogic.LoadedScene == GameScenes.LOADING || HighLogic.LoadedScene == GameScenes.LOADINGBUFFER) return;
            GUISkin skin = HighLogic.Skin;
            if (skin != null) GUI.skin = skin;
            if (window.x < 0) window.x = Screen.width - PanelWide - 56;
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - PanelWide));
            window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - 40));
            // How tall the panel is to be is found afresh in every laying out (a height of nought asks for that), and
            // there only. Asked in every event, as it first was, the nought came back as the panel's height in every
            // frame in which the game had itself moved it (GUI.DragWindow), and it was drawn with no height: it blinked
            // for as long as it was dragged. (Seen and counted in Keystone's window, which did the same.) So the panel
            // is carried by hand too: see Pointer.
            window.width = PanelWide;
            window.height = Event.current.type == EventType.Layout ? 0 : panelHigh;
            Rect given = GUILayout.Window(WindowId, window, DrawWindow, "AI Bridge", GUILayout.Width(PanelWide));
            if (Event.current.type == EventType.Repaint && given.height > 0) panelHigh = given.height;
#if DEV
            if (Event.current.type == EventType.Repaint) { DevDrawn++; if (given.height <= 0) DevBlank++; }
#endif
            // While the player is typing to the AI the keys are for the panel: an M must not open the map. A click
            // anywhere else gives them back.
            bool typing = GUI.GetNameOfFocusedControl() == SayBox;
            if (typing && Event.current.type == EventType.MouseDown && !new Rect(window.x, window.y, PanelWide, panelHigh).Contains(Event.current.mousePosition)) { GUIUtility.keyboardControl = 0; typing = false; }
            TypingLocked(typing);
        }

        /// <summary>
        /// The pointer and the panel, once a frame and before the game's cameras look at the wheel: a panel that is held
        /// goes with the pointer, and while the pointer is over the panel (or holds it) a click or the wheel is for the
        /// panel and not for what lies under it.
        /// </summary>
        void Pointer()
        {
            bool shows = hudVisible && !interfaceHidden && panelHigh > 0f && HighLogic.LoadedScene != GameScenes.LOADING && HighLogic.LoadedScene != GameScenes.LOADINGBUFFER;
#if DEV
            if (DevHidePanel) shows = false;
#endif
            var pointer = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (carried && (!shows || !Input.GetMouseButton(0))) carried = false;
            if (carried)
            {
                window.x = Mathf.Clamp(pointer.x - carriedBy.x, 0, Mathf.Max(0, Screen.width - PanelWide));
                window.y = Mathf.Clamp(pointer.y - carriedBy.y, 0, Mathf.Max(0, Screen.height - 40));
            }
            bool over = shows && (carried || new Rect(window.x, window.y, PanelWide, panelHigh).Contains(pointer));
            // (asked of the game itself, not remembered: the game lets every hold go at times, from its pause menu for one)
            bool held = InputLockManager.lockStack.ContainsKey(PanelLock);
            if (over && !held) InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS | ControlTypes.KSC_ALL | ControlTypes.EDITOR_SOFT_LOCK, PanelLock);
            else if (!over && held) InputLockManager.RemoveControlLock(PanelLock);
        }

        void TypingLocked(bool on)
        {
            if (on == typingLocked) return;
            typingLocked = on;
            if (on) InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, TypingLock);
            else InputLockManager.RemoveControlLock(TypingLock);
        }

        /// <summary>The player sends what they have typed to the AI.</summary>
        void SayToAi(string kind)
        {
            string text = (draft ?? "").Trim();
            if (text.Length == 0) return;
            try { Api.ChatApi.Said(text, kind); } catch (Exception) { }
            draft = "";
        }

        void DrawWindow(int id)
        {
            bool recent = Time.realtimeSinceStartup - lastCallAt < 15f;
            string state = startupError != null ? "<color=#ff6060>OFFLINE</color>"
                : !AiEnabled ? "<color=#ffb040>AI paused by you</color>"
                : recent ? "<color=#60ff80>AI connected</color>"
                : "<color=#a0a0a0>idle</color>";
            GUILayout.BeginHorizontal();
            GUILayout.Label(state + (server != null ? "  <size=10>port " + server.Port + "</size>" : ""), RichLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(collapsed ? "+" : "–", GUILayout.Width(24))) collapsed = !collapsed;
            if (GUILayout.Button(new GUIContent("×", "Put this panel away. The AI button on the toolbar brings it back."), GUILayout.Width(24))) ShowPanel(false);
            GUILayout.EndHorizontal();

            if (!collapsed)
            {
                if (startupError != null) GUILayout.Label(startupError, RichLabel);
                string pilot = Pilot.Autopilot.Describe();
                if (pilot != null) GUILayout.Label("<color=#80c8ff>" + pilot + "</color>", RichLabel);
                Api.VoiceApi.DrawPanel(RichLabel);
                if (shown.Count == 0) GUILayout.Label("<size=11>" + lastActivity + "</size>", RichLabel);
                for (int n = 0; n < shown.Count; n++)
                {
                    string line = shown[n];
                    if (line.Length > 96) line = line.Substring(0, 96) + "…";
                    GUILayout.Label("<size=10>" + (n == shown.Count - 1 ? line : "<color=#b0b0b0>" + line + "</color>") + "</size>", RichLabel);
                }
                // ---- talking to the AI: the end of the conversation, and a box to type into
                IList<Api.ChatApi.Line> said = Api.ChatApi.Lines;
                for (int n = Mathf.Max(0, said.Count - 5); n < said.Count; n++)
                {
                    Api.ChatApi.Line one = said[n];
                    string words = one.Text.Length > 260 ? one.Text.Substring(0, 260) + "…" : one.Text;
                    words = words.Replace("<", "‹").Replace(">", "›");
                    // (a line of the bridge's own, about the AI, is not the AI's and is not shown as its)
                    if (one.Kind == "bridge") GUILayout.Label("<size=10><color=#86b6c8>· " + words + "</color></size>", RichLabel);
                    else GUILayout.Label("<size=11>" + (one.Player ? "<color=#ffe08a>You" + (one.Kind == "ask" ? " asked" : one.Kind == "tell" ? " told it" : "") + ":</color> " : "<color=#8fd8ff>AI:</color> ") + words + "</size>", RichLabel);
                }
                if (Api.ChatApi.Waiting > 0)
                    GUILayout.Label("<size=10><color=#ffb040>" + (recent ? "the AI will see this in a moment" : "the AI is not connected just now: it will see this when it next does something") + "</color></size>", RichLabel);
                Event key = Event.current;
                bool mine = GUI.GetNameOfFocusedControl() == SayBox;
                if (mine && key.type == EventType.KeyDown && (key.keyCode == KeyCode.Return || key.keyCode == KeyCode.KeypadEnter || key.character == '\n')) { key.Use(); SayToAi("say"); }
                else if (mine && key.type == EventType.KeyDown && key.keyCode == KeyCode.Escape) { key.Use(); GUIUtility.keyboardControl = 0; }
                GUILayout.BeginHorizontal();
                GUI.SetNextControlName(SayBox);
                draft = GUILayout.TextField(draft ?? "", 400, GUILayout.Width(170));
                if (string.IsNullOrEmpty(draft) && !mine && Event.current.type == EventType.Repaint)
                {
                    Rect box = GUILayoutUtility.GetLastRect();
                    GUI.Label(new Rect(box.x + 5, box.y + 2, box.width, box.height), "<size=11><color=#808080>say something to the AI</color></size>", RichLabel);
                }
                if (GUILayout.Button(new GUIContent("Ask", "A question: the AI answers and goes on with what it is doing."))) SayToAi("ask");
                if (GUILayout.Button(new GUIContent("Tell", "An instruction: the AI does it, changing what it is doing if need be."))) SayToAi("tell");
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(AiEnabled ? "Pause AI" : "Resume AI"))
                {
                    AiEnabled = !AiEnabled;
                    if (!AiEnabled) Pilot.Autopilot.Disengage("player paused AI control");
                    Note(AiEnabled ? "player resumed AI control" : "player paused AI control");
                }
                if (Pilot.Autopilot.Engaged && GUILayout.Button("Take controls"))
                {
                    Pilot.Autopilot.Disengage("player took the controls");
                    Note("player took the controls");
                }
                GUILayout.EndHorizontal();
                // (the AI is not to leave the player in silence: see Api.Company)
                GUILayout.BeginHorizontal();
                bool talks = GUILayout.Toggle(Api.Company.On, "", GUILayout.Width(22));
                GUILayout.Label("<size=11>never silent: says what it is doing, about every " + Mathf.RoundToInt(Api.Company.Every) + " s</size>", RichLabel);
                GUILayout.EndHorizontal();
                if (talks != Api.Company.On) Api.Company.Set(talks, Api.Company.Every);
                GUILayout.Label("<size=10><color=#909090>× puts this panel away (the AI button on the toolbar, or Alt+F10, brings it back) · " + callCount + " commands</color></size>", RichLabel);
            }
            // Carried by any part of it that is not a button or the box (Pointer does the carrying). By hand: the game's
            // own way of it, GUI.DragWindow, is what made the panel blink while it was dragged (see where it is drawn).
            Event e = Event.current;
            int grip = GUIUtility.GetControlID(GripHint, FocusType.Passive);
            switch (e.GetTypeForControl(grip))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && new Rect(0, 0, PanelWide, Mathf.Max(panelHigh, 24f)).Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = grip;
                        GUIUtility.keyboardControl = 0;
                        carriedBy = new Vector2(Input.mousePosition.x - window.x, Screen.height - Input.mousePosition.y - window.y);
                        carried = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == grip) e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == grip) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
            }
        }

        static GUIStyle richLabel;
        static GUIStyle RichLabel
        {
            get
            {
                if (richLabel == null)
                {
                    richLabel = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
                    richLabel.margin = new RectOffset(2, 2, 1, 1);
                    richLabel.padding = new RectOffset(0, 0, 0, 0);
                }
                return richLabel;
            }
        }
    }
}
