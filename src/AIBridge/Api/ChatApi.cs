using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// The player and the AI talking to each other while the AI flies. The player types in the panel
    /// (or speaks, in the voice edition); what they say waits here until the AI next looks, and the AI's
    /// answer is shown in the panel's conversation and on the screen.
    ///
    /// The player says which of two things they mean, or leaves it to the AI to tell:
    ///   ask   a question. The AI answers and goes on with exactly what it was doing.
    ///   tell  an instruction. The AI does it, changing what it is doing if need be.
    ///   say   either; the AI judges from the words.
    ///
    /// The AI is only ever "listening" while one of its commands is running, so what the player says is
    /// handed to it three ways: a wait (the command it spends most of a flight in) ends at once with the
    /// message in its result; the MCP server asks after every other command; and chat_next blocks until
    /// there is something.
    ///
    /// So that the player is not left in silence meanwhile, see Company: the bridge answers at once that the
    /// words were heard, the AI is told when to say a line of its own, and the bridge says what it can see
    /// the AI doing when the AI says nothing.
    /// </summary>
    public static class ChatApi
    {
        public sealed class Line
        {
            public int Id;
            public bool Player;
            public string Kind, Text;
            public float At;                 // when it was said, in real seconds since the game started
        }

        static BridgeHost host;
        static readonly List<Line> lines = new List<Line>();
        static readonly Queue<Line> pending = new Queue<Line>();
        static int nextId = 1;
        static int listening;                // how many of the AI's calls are waiting for the player's next words just now

        /// <summary>The conversation so far, oldest first (the panel shows the end of it).</summary>
        public static IList<Line> Lines => lines;

        /// <summary>How many things the player has said that the AI has not been given yet.</summary>
        public static int Waiting => pending.Count;

        /// <summary>Whether a call of the AI's is waiting for the player's next words this moment: what is said now reaches it at once.</summary>
        public static bool Listening => listening > 0;

        public static void Register(BridgeHost h)
        {
            host = h;
            listening = 0;
            Rpc.Sync("chat_send", "Say something to the AI, as the player does in the panel. params: {text, kind: say (default) | ask | tell}", a => Said(a.Require("text"), a.Str("kind"))).AlwaysAllowed = true;
            Rpc.Async("chat_next", "What the player has said to the AI since it last looked. params: {timeout_s: how long to wait for something (default 0: only what is already waiting)}", Next).AlwaysAllowed = true;
            Rpc.Sync("chat_reply", "The AI's answer to the player: shown in the panel's conversation and on the screen. params: {text}", Reply).AlwaysAllowed = true;
        }

        /// <summary>The player says something to the AI.</summary>
        public static JObj Said(string text, string kind)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) throw new ApiException("Nothing was said.");
            if (text.Length > 600) text = text.Substring(0, 600);
            kind = (kind ?? "say").ToLowerInvariant();
            if (kind != "ask" && kind != "tell") kind = "say";
            var line = new Line { Id = nextId++, Player = true, Kind = kind, Text = text, At = Time.realtimeSinceStartup };
            Keep(line);
            pending.Enqueue(line);
            while (pending.Count > 12) pending.Dequeue();
            host.Note("player " + (kind == "ask" ? "asked" : kind == "tell" ? "told the AI" : "said") + ": " + text, false);
            Company.Asked();
            return new JObj().Add("id", line.Id).Add("waiting", pending.Count);
        }

        /// <summary>A line of the bridge's own in the conversation (see Company): not the AI's words, and shown as not.</summary>
        internal static void FromBridge(string text)
        {
            Keep(new Line { Id = nextId++, Player = false, Kind = "bridge", Text = text, At = Time.realtimeSinceStartup });
        }

        static void Keep(Line line)
        {
            lines.Add(line);
            if (lines.Count > 60) lines.RemoveRange(0, lines.Count - 60);
        }

        /// <summary>Everything the player has said that the AI has not been given, as it is given to the AI: taken off the list.</summary>
        public static List<object> Take()
        {
            var list = new List<object>();
            while (pending.Count > 0)
            {
                Line line = pending.Dequeue();
                var o = new JObj().Add("from", "the player").Add("kind", line.Kind == "ask" ? "question" : line.Kind == "tell" ? "instruction" : "message").Add("text", line.Text);
                o.Add("said_s_ago", Math.Round(Time.realtimeSinceStartup - line.At, 0));
                o.Add("what_to_do", (Company.On ? "FIRST, before anything else, one short line with ksp_chat reply so that they are not left in silence (that you heard, and what you will do or look up first). THEN: " : "")
                    + (line.Kind == "ask" ? "Answer with ksp_chat reply. Do not change what you or the ship are doing: everything already running carries on."
                    : line.Kind == "tell" ? "Do it, changing your plan and what the ship is doing if need be. Say what you are doing about it with ksp_chat reply."
                    : "If it is a question, answer with ksp_chat reply and carry on unchanged. If it asks for something to be done or changed, do that and say so with ksp_chat reply."));
                list.Add(o);
            }
            if (list.Count > 0) Company.Given();
            return list;
        }

        static IEnumerator Next(Call c)
        {
            float until = Time.realtimeSinceStartup + Mathf.Clamp((float)c.Args.Num("timeout_s", 0), 0f, 600f);
            Company.Seen();
            listening++;
            try
            {
                while (pending.Count == 0 && Time.realtimeSinceStartup < until && !c.Abandoned) yield return null;
            }
            finally { listening--; }
            if (c.Abandoned) yield break;                      // (nothing is taken off the list for a caller that has gone)
            c.Result = new JObj().Add("messages", Take()).Add("talk", Company.Talk());
        }

        static object Reply(Args a)
        {
            string text = (a.Require("text") ?? "").Trim();
            if (text.Length == 0) throw new ApiException("Give 'text': what to say to the player.");
            if (text.Length > 900) text = text.Substring(0, 900);
            Keep(new Line { Id = nextId++, Player = false, Kind = "reply", Text = text, At = Time.realtimeSinceStartup });
            if (a.Bool("on_screen", true))
            {
                // (long enough to read: a fifth of a second a word, and never less than four)
                float seconds = Mathf.Clamp(2.5f + 0.32f * text.Split(' ').Length, 4f, 16f);
                try { ScreenMessages.PostScreenMessage("<color=#8fd8ff>AI: " + text.Replace("<", "‹").Replace(">", "›") + "</color>", seconds, ScreenMessageStyle.UPPER_CENTER); } catch { }
            }
            host.Note("AI answered: " + text, false);
            Company.Spoke();
            return new JObj().Add("shown", true).Add("player_messages_waiting", pending.Count);
        }
    }
}
