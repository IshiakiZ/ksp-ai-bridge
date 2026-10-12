using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// Not sitting in silence. An AI takes seconds to answer and minutes to do what it was asked, and through most
    /// of that it says nothing: the player has spoken, and the game looks as if nobody heard. So three things:
    ///
    ///   at once   when the player says something to the AI (typed in the panel, or spoken) the bridge answers that
    ///             moment with what is true: that the AI has it, that it will have it when its present command
    ///             returns, or that no AI is connected to hear it;
    ///   the AI    is told, on what it reads next, to say a line of its own now, and again whenever it has been
    ///             quiet for this long while it works (Talk says when; the MCP server puts it on the tool's result);
    ///   failing   that (an AI that is thinking, or inside one long command, cannot speak), the bridge says what it
    ///             can see the AI doing: the commands it has run since the last line, the one it is in, the
    ///             program that is flying. Never what it cannot see.
    ///
    /// Only an AI counts: the MCP server says when one of its tools begins and ends (chat_work). Commands sent to
    /// the bridge any other way (a script, a test) start nothing here.
    ///
    /// The bridge's own lines are marked as its own in the panel's conversation (the AI's begin "AI:"), go to the
    /// middle of the screen as the AI's do, and are read out by the ship's voice when the voice edition is running.
    /// </summary>
    public static class Company
    {
        /// <summary>The player's switch (in the panel), kept from one run of the game to the next.</summary>
        public static bool On = true;
        /// <summary>How long a silence may last, in real seconds, before something is said.</summary>
        public static float Every = 20f;

        static BridgeHost host;
        static bool atWork;                       // something asked of the AI is in hand
        static bool unseen;                       // the player's words have not been handed to an AI yet
        static float beganAt, askedAt = -1000f, saidAt = -1000f, aiSaidAt = -1000f, aiDidAt = -1000f, aiSeenAt = -1000f;
        static int own;                           // lines of the bridge's own since the AI last said or began anything
        static readonly Dictionary<string, string> running = new Dictionary<string, string>();   // the AI's commands that have begun and not ended: what each is, in words
        static readonly Dictionary<string, float> runningSince = new Dictionary<string, float>();
        static readonly List<string> did = new List<string>();          // what it has done since anything was last said, in order, each once
        static string settingsFile;

        static float Now => Time.realtimeSinceStartup;

        /// <summary>Whether something asked of the AI is in hand just now.</summary>
        public static bool AtWork => atWork;

        public static void Register(BridgeHost h)
        {
            host = h;
            atWork = false; unseen = false; own = 0;
            running.Clear(); runningSince.Clear(); did.Clear();
            settingsFile = BridgeHost.DataDir + "talk.txt";
            try
            {
                if (File.Exists(settingsFile))
                {
                    string[] kept = File.ReadAllText(settingsFile).Trim().Split(' ');
                    On = kept[0] != "off";
                    if (kept.Length > 1 && float.TryParse(kept[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float every)) Every = Mathf.Clamp(every, 8f, 120f);
                }
            }
            catch (Exception) { }
            Rpc.Sync("chat_work", "An AI's tool has begun or ended (sent by the MCP server, so that the game knows an AI is at work and on what). params: {id, tool, doing: what it is in a few words, as 'reading the ship's instruments', begins: bool}", Work).AlwaysAllowed = true;
            Rpc.Sync("chat_talk", "Whether the AI should say something to the player now, and the setting. params: {on: bool, every_s: seconds of quiet allowed (8 to 120)} to change it", a =>
            {
                if (a.Has("on") || a.Has("every_s")) Set(a.Has("on") ? a.Bool("on") : On, a.Has("every_s") ? (float)a.Num("every_s", Every) : Every);
                return Talk();
            }).AlwaysAllowed = true;
        }

        /// <summary>The player's switch and the length of a silence, kept.</summary>
        public static void Set(bool on, float every)
        {
            On = on;
            Every = Mathf.Clamp(every, 8f, 120f);
            try { File.WriteAllText(settingsFile, (On ? "on " : "off ") + Every.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)); }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ what happens

        /// <summary>The player has said something to the AI (typed in the panel, or spoken). Answered at once.</summary>
        internal static void Asked()
        {
            float now = Now;
            askedAt = now;
            unseen = true;
            own = 0;
            if (!atWork) { atWork = true; beganAt = now; did.Clear(); }
            if (!On) return;
            string line;
            if (ChatApi.Listening || VoiceApi.Collecting) line = "Got it: the AI has your message now.";
            else if (running.Count > 0) line = "Got it: the AI will see this as soon as its present command returns.";
            else if (now - aiSeenAt < 30f) line = "Got it: the AI will see this with its next command.";
            else line = "Got it, but no AI is connected just now. It will see this when one is.";
            Say(line);
            own = 0;                              // (the answer at once is not one of the lines that count a silence)
        }

        /// <summary>What the player said has been handed to an AI.</summary>
        internal static void Given()
        {
            unseen = false;
            aiSeenAt = Now;
        }

        /// <summary>The AI has said something to the player (its answer, a message on the screen, a spoken line).</summary>
        internal static void Spoke()
        {
            aiSaidAt = saidAt = aiSeenAt = Now;
            own = 0;
            did.Clear();
        }

        /// <summary>An AI's server has just asked after the player's words: one is there.</summary>
        internal static void Seen() { aiSeenAt = Now; }

        /// <summary>The ship's voice has said something of its own (a call-out of the flight): there is no silence to fill just now.</summary>
        internal static void Sounded() { saidAt = Now; }

        static object Work(Args a)
        {
            float now = Now;
            aiSeenAt = now;
            string id = a.Str("id") ?? a.Str("tool") ?? "tool";
            if (a.Bool("begins", true))
            {
                string doing = (a.Str("doing") ?? "").Trim();
                if (doing.Length == 0) doing = "using " + (a.Str("tool") ?? "a command");
                if (doing.Length > 80) doing = doing.Substring(0, 80);
                if (!atWork) { atWork = true; beganAt = now; did.Clear(); own = 0; }
                running[id] = doing;
                runningSince[id] = now;
                aiDidAt = now;
            }
            else
            {
                if (running.TryGetValue(id, out string was))
                {
                    running.Remove(id); runningSince.Remove(id);
                    did.Remove(was);
                    did.Add(was);
                    while (did.Count > 6) did.RemoveAt(0);
                }
                aiDidAt = now;
            }
            return Talk();
        }

        /// <summary>
        /// For the AI: whether it should say a line to the player now. It should when something asked of it is in hand
        /// and it has told the player nothing since that began, or nothing for as long as a silence may last.
        /// </summary>
        public static JObj Talk()
        {
            float now = Now;
            var o = new JObj().Add("on", On).Add("every_s", Math.Round(Every, 1)).Add("at_work", atWork);
            if (!On || !atWork) return o.Add("say_now", false);
            bool nothingYet = aiSaidAt < beganAt;
            float quiet = now - Mathf.Max(aiSaidAt, beganAt);
            bool due = nothingYet || quiet >= Every;
            o.Add("say_now", due);
            if (!due) return o;
            o.Add("quiet_s", Math.Round(quiet, 0));
            o.Add("why", nothingYet ? "You have begun on something for the player and have told them nothing yet."
                                    : "It is " + Math.Round(quiet, 0) + " s since you last said anything to the player.");
            return o;
        }

        // ------------------------------------------------------------------ the bridge's own lines

        /// <summary>Every frame: say something when a silence has lasted long enough, and notice that the work is over.</summary>
        public static void Update()
        {
            if (!atWork) return;
            float now = Now;
            // (a command the AI began and never ended: its server has gone)
            if (running.Count > 0 && now - aiSeenAt > 1800f) { running.Clear(); runningSince.Clear(); }
            bool busy = running.Count > 0;
            // Over: it has told the player something since it was asked, and has done nothing more for a while.
            if (!unseen && !busy && aiSaidAt > Mathf.Max(askedAt, beganAt) && now - Mathf.Max(aiDidAt, aiSaidAt) > 0.75f * Every) { End(); return; }
            if (!On || now - Mathf.Max(saidAt, beganAt) < Every) return;
            string line = Line(now, busy);
            if (line == null) { End(); return; }
            Say(line);
        }

        static void End()
        {
            atWork = false; unseen = false; own = 0;
            did.Clear();
        }

        /// <summary>What the bridge can truthfully say now; nothing when there is no more to say (and the work is taken to be over).</summary>
        static string Line(float now, bool busy)
        {
            string present = null; float presentSince = now;
            foreach (KeyValuePair<string, string> one in running)
                if (present == null || runningSince[one.Key] > presentSince) { present = one.Value; presentSince = runningSince[one.Key]; }
            if (unseen)
            {
                // (what the player said is still waiting to be read)
                if (busy) return own == 0 ? "The AI has not seen your message yet: it is still " + present + "." : null;
                return own == 0 ? "Your message is still waiting: no AI has come for it." : null;
            }
            bool since = aiDidAt > Mathf.Max(askedAt, beganAt) || busy;       // it has run something since it was asked
            if (!since)
            {
                float waited = now - Mathf.Max(askedAt, beganAt);
                if (own == 0) return "The AI has your message. Nothing back from it yet.";
                if (own == 1) return "Still nothing from the AI: " + Seconds(waited) + ".";
                if (own == 2) return Seconds(waited) + " and nothing from the AI. It may have stopped: have a look at its window.";
                return null;
            }
            string flying = null;
            try { flying = Pilot.Autopilot.Describe(); } catch (Exception) { }
            string ship = string.IsNullOrEmpty(flying) ? "" : " The ship: " + flying + ".";
            if (did.Count == 0)
            {
                // (nothing new since the last line)
                if (busy) return "The AI is still " + present + ": " + Seconds(now - presentSince) + "." + ship;
                // (a silence of two lengths between commands is thinking; of three, it has finished without a word, or stopped)
                float idle = now - aiDidAt;
                if (idle < 2.5f * Every) return "The AI is between commands: nothing new for " + Seconds(idle) + "." + ship;
                return idle < 3.5f * Every ? "Nothing more from the AI for " + Seconds(idle) + ": it has finished or stopped. Its own window says which." : null;
            }
            // (what it has finished is said as done, and only the command it is in as going on)
            var parts = new List<string>();
            for (int n = Mathf.Max(0, did.Count - (busy ? 2 : 3)); n < did.Count; n++) if (did[n] != present) parts.Add(did[n]);
            if (parts.Count == 0) return "The AI is still " + present + ": " + Seconds(now - presentSince) + "." + ship;
            string list = "";
            for (int n = 0; n < parts.Count; n++) list += (n == 0 ? "" : n == parts.Count - 1 ? " and " : ", ") + parts[n];
            return "The AI has been " + list + (busy ? ", and is now " + present : "") + "." + ship;
        }

        static string Seconds(float s) => s < 120f ? Mathf.RoundToInt(s) + " seconds" : Mathf.RoundToInt(s / 60f) + " minutes";

        static void Say(string line)
        {
            saidAt = Now;
            own++;
            did.Clear();
            ChatApi.FromBridge(line);
            try { ScreenMessages.PostScreenMessage("<color=#86b6c8>" + line.Replace("<", "‹").Replace(">", "›") + "</color>", Mathf.Clamp(2.5f + 0.32f * line.Split(' ').Length, 4f, 10f), ScreenMessageStyle.UPPER_CENTER); } catch (Exception) { }
            // (read out by the ship's voice when somebody is collecting its lines; not worth saying late)
            try { Pilot.Autopilot.I.Narrator.Say(line, 1, Pilot.Narrator.Quiet, 8f, "company", 0f); } catch (Exception) { }
            if (host != null) host.Note("bridge said: " + line, false);
        }

#if DEV
        /// <summary>For the development build: what this class holds just now.</summary>
        public static string Dev()
        {
            float now = Now;
            string text = (On ? "on" : "off") + " every " + Every + " s, " + (atWork ? "at work for " + Mathf.RoundToInt(now - beganAt) + " s" : "idle") + (unseen ? ", message unseen" : "")
                          + ", own lines " + own + ", AI said " + Mathf.RoundToInt(now - aiSaidAt) + " s ago, did " + Mathf.RoundToInt(now - aiDidAt) + " s ago, seen " + Mathf.RoundToInt(now - aiSeenAt) + " s ago; running:";
            foreach (KeyValuePair<string, string> one in running) text += " [" + one.Value + "]";
            text += "; did:";
            foreach (string one in did) text += " [" + one + "]";
            return text;
        }
#endif
    }
}
