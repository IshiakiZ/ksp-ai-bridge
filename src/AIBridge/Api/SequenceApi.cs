using System;
using System.Collections;
using System.Collections.Generic;
using AIBridge.Pilot;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// Flight plans: a list of commands the game carries out by itself, one after another, waiting for each
    /// autopilot program to finish before starting the next. A whole mission leg ("launch, reach orbit,
    /// transfer to the Mun, capture") becomes one request, so nothing depends on how long a single
    /// request from the AI is allowed to stay open.
    /// </summary>
    public static class SequenceApi
    {
        sealed class Step
        {
            public string Do;
            public Args Params;
            public Args Until;
            public float TimeoutS;
            public bool Optional;
            public string State = "pending";
            public string Note = "";
        }

        sealed class Plan
        {
            public int Id;
            public string Name;
            public readonly List<Step> Steps = new List<Step>();
            public int Index;
            public string State = "running";
            public string Error;
            public bool AbortRequested;
            public float StartedReal;
            public double StartedUT;
        }

        static BridgeHost host;
        static Plan current;
        static int nextId = 1;
        static readonly HashSet<string> reserved = new HashSet<string> { "do", "until", "timeout_s", "optional" };
        static readonly HashSet<string> forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sequence_start", "sequence_abort", "quit" };

        public static bool Running => current != null && current.State == "running";

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("sequence_start", "Run a flight plan: a list of steps carried out in order inside the game. params: {name, steps:[{do, ...params, until, timeout_s, optional}]}", Start);
            Rpc.Sync("sequence_status", "Progress of the current or last flight plan.", a => Status()).AlwaysAllowed = true;
            Rpc.Sync("sequence_abort", "Stop the running flight plan and its autopilot program.", Abort);
        }

        static object Start(Args a)
        {
            if (Running) throw new ApiException("A flight plan is already running (step " + (current.Index + 1) + " of " + current.Steps.Count + ": " + current.Steps[current.Index].Do + "). Abort it first or wait for it.");
            List<object> raw = a.List("steps");
            if (raw == null || raw.Count == 0) throw new ApiException("Give 'steps': a list like [{\"do\": \"ascent\", \"altitude_km\": 80}, {\"do\": \"plan\", \"maneuver\": \"circularize\"}, {\"do\": \"execute_node\"}].");
            if (raw.Count > 60) throw new ApiException("A flight plan can have at most 60 steps.");

            var plan = new Plan { Id = nextId++, Name = a.Str("name", "flight plan") };
            for (int n = 0; n < raw.Count; n++)
            {
                var s = new Args(raw[n]);
                string what = s.Str("do");
                if (string.IsNullOrEmpty(what)) throw new ApiException("steps[" + n + "] needs \"do\": the command to run (any bridge command, or \"wait\").");
                if (forbidden.Contains(what)) throw new ApiException("steps[" + n + "]: '" + what + "' cannot be used inside a flight plan.");
                if (what != "wait" && Rpc.Find(what) == null) throw new ApiException("steps[" + n + "]: unknown command '" + what + "'. Use the command names listed by 'help' (e.g. launch, ascent, set_target, plan, execute_node, warp, land, quicksave, control, attitude, part_do, science_run, wait).");
                var p = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in s.Map) if (!reserved.Contains(kv.Key)) p[kv.Key] = kv.Value;
                var step = new Step { Do = what, Params = new Args(p), Until = s.Obj("until"), Optional = s.Bool("optional", false) };
                step.TimeoutS = (float)OrbitMath.Clamp(s.Num("timeout_s", 1800), 5, 6 * 3600);
                if (what == "wait" && step.Until == null && !s.Has("timeout_s")) throw new ApiException("steps[" + n + "]: a wait step needs \"until\": {...} conditions (or timeout_s for a plain pause).");
                if (step.Until != null) NavApi.CheckConditions(step.Until);
                plan.Steps.Add(step);
            }
            plan.StartedReal = Time.realtimeSinceStartup;
            plan.StartedUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0;
            current = plan;
            host.Note("flight plan '" + plan.Name + "' started (" + plan.Steps.Count + " steps)");
            host.RunBackground(Run(plan), "flight plan");
            return Status();
        }

        static object Abort(Args a)
        {
            if (!Running) throw new ApiException("No flight plan is running.");
            current.AbortRequested = true;
            Autopilot.I.Release("flight plan aborted", true);
            return Status();
        }

        public static JObj Status()
        {
            var o = new JObj();
            if (current == null) { o.Add("state", "none"); return o; }
            o.Add("id", current.Id).Add("name", current.Name).Add("state", current.State);
            o.Add("step", Math.Min(current.Index + 1, current.Steps.Count) + " of " + current.Steps.Count);
            if (current.Error != null) o.Add("error", current.Error);
            if (Running && Autopilot.I.Program != null) o.Add("autopilot", Autopilot.I.Program.Name + ": " + Autopilot.I.Program.Phase);
            o.Add("running_real_s", Math.Round(Time.realtimeSinceStartup - current.StartedReal));
            var steps = new List<object>();
            for (int n = 0; n < current.Steps.Count; n++)
            {
                Step s = current.Steps[n];
                var so = new JObj().Add("n", n + 1).Add("do", s.Do).Add("state", s.State);
                if (s.Note.Length > 0) so.Add("result", s.Note.Length > 260 ? s.Note.Substring(0, 260) + "…" : s.Note);
                steps.Add(so);
            }
            o.Add("steps", steps);
            return o;
        }

        /// <summary>One line for the brief telemetry block.</summary>
        public static string Summary()
        {
            if (current == null) return null;
            if (current.State == "running") return "flight plan '" + current.Name + "' step " + (current.Index + 1) + " of " + current.Steps.Count + " (" + current.Steps[current.Index].Do + ")";
            return "flight plan '" + current.Name + "' " + current.State + (current.Error != null ? ": " + current.Error : "");
        }

        static void Finish(Plan plan, string state, string error)
        {
            plan.State = state;
            plan.Error = error;
            string text = "flight plan '" + plan.Name + "' " + state + (error != null ? ": " + error : "");
            Autopilot.I.Log.Add(text);
            host.Note(text);
            try { ScreenMessages.PostScreenMessage("AI " + text, 6f, ScreenMessageStyle.UPPER_CENTER); } catch { }
        }

        static IEnumerator Run(Plan plan)
        {
            for (plan.Index = 0; plan.Index < plan.Steps.Count; plan.Index++)
            {
                Step step = plan.Steps[plan.Index];
                step.State = "running";
                string label = "step " + (plan.Index + 1) + " of " + plan.Steps.Count + " (" + step.Do + ")";
                var call = new Call { Method = step.Do, Args = step.Params };
                float deadline = Time.realtimeSinceStartup + step.TimeoutS;
                string programBefore = Autopilot.I.LastProgramSummary;
                int programsBefore = Autopilot.I.ProgramsFinished;

                if (step.Do != "wait")
                {
                    // Let scene changes and vessel loading settle before issuing the next command.
                    while (SceneWatch.Loading && Time.realtimeSinceStartup < deadline) yield return null;
                    RpcEntry entry = Rpc.Find(step.Do);
                    if (step.Do == "warp" && !step.Params.Has("timeout_s")) step.Params.Map["timeout_s"] = (double)Math.Min(step.TimeoutS, 590f);
                    if (entry.Sync != null)
                    {
                        try { call.Result = entry.Sync(step.Params); }
                        catch (ApiException ex) { call.Error = ex.Message; }
                        catch (Exception ex) { call.Error = ex.GetType().Name + ": " + ex.Message; BridgeHost.LogWarn("flight plan step failed: " + ex); }
                    }
                    else yield return Guarded(entry.Async(call), call);

                    // A command that starts an autopilot program is only finished when the program is.
                    while (call.Error == null && Autopilot.I.Program != null)
                    {
                        if (Interrupted(plan, deadline, out string why)) { call.Error = why; Autopilot.I.Release("flight plan: " + why, true); break; }
                        yield return null;
                    }
                    if (call.Error == null && Autopilot.I.ProgramsFinished != programsBefore && Autopilot.I.LastProgramFailed)
                        call.Error = Autopilot.I.LastProgramSummary;
                    if (call.Error == null) step.Note = Autopilot.I.ProgramsFinished != programsBefore ? Autopilot.I.LastProgramSummary : Brief(call.Result);
                }

                if (call.Error == null && step.Until != null)
                {
                    string met = null;
                    while (met == null)
                    {
                        Vessel v = HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                        met = NavApi.Met(step.Until, v);
                        if (met == null && Interrupted(plan, deadline, out string why)) { call.Error = "waiting for " + string.Join("/", new List<string>(step.Until.Map.Keys).ToArray()) + ": " + why; break; }
                        if (met == null) yield return null;
                    }
                    if (met != null) step.Note = (step.Note.Length > 0 ? step.Note + "; " : "") + met;
                }
                else if (call.Error == null && step.Do == "wait")
                {
                    while (Time.realtimeSinceStartup < deadline && !plan.AbortRequested && host.AiEnabled) yield return null;
                    step.Note = "paused " + Math.Round(step.TimeoutS) + " s";
                }

                if (plan.AbortRequested) { step.State = "aborted"; Finish(plan, "aborted", "stopped at " + label); yield break; }
                if (call.Error != null)
                {
                    step.Note = call.Error;
                    if (step.Optional) { step.State = "skipped"; Autopilot.I.Log.Add("flight plan " + label + " failed but is optional: " + call.Error); continue; }
                    step.State = "failed";
                    Finish(plan, "failed", label + ": " + call.Error);
                    yield break;
                }
                step.State = "done";
                Autopilot.I.Log.Add("flight plan " + label + " done" + (step.Note.Length > 0 ? ": " + (step.Note.Length > 160 ? step.Note.Substring(0, 160) + "…" : step.Note) : ""));
            }
            plan.Index = plan.Steps.Count - 1;
            Finish(plan, "done", null);
        }

        static bool Interrupted(Plan plan, float deadline, out string why)
        {
            why = null;
            if (plan.AbortRequested) why = "aborted";
            else if (!host.AiEnabled) why = "the player paused AI control";
            else if (Time.realtimeSinceStartup > deadline) why = "timed out";
            return why != null;
        }

        static string Brief(object result)
        {
            if (result == null) return "";
            try
            {
                string json = Json.Write(result);
                return json.Length > 220 ? json.Substring(0, 220) + "…" : json;
            }
            catch { return ""; }
        }

        /// <summary>Run a command's coroutine to the end, turning any exception into the call's error instead of unwinding the plan.</summary>
        static IEnumerator Guarded(IEnumerator inner, Call call)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(inner);
            while (stack.Count > 0)
            {
                bool moved;
                object yielded = null;
                try
                {
                    moved = stack.Peek().MoveNext();
                    if (moved) yielded = stack.Peek().Current;
                }
                catch (ApiException ex) { call.Error = ex.Message; yield break; }
                catch (Exception ex)
                {
                    BridgeHost.LogWarn("flight plan step threw: " + ex);
                    call.Error = ex.GetType().Name + ": " + ex.Message;
                    yield break;
                }
                if (!moved) { stack.Pop(); continue; }
                if (yielded is IEnumerator nested) { stack.Push(nested); continue; }
                yield return yielded;
            }
        }
    }
}
