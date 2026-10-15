using System;
using System.Collections.Generic;
using AIBridge.Api;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// The ship's computer's commentary: short lines about what the vessel is doing, written to be read
    /// out by a voice. It watches the flight itself, so it speaks whether the AI or the player is flying,
    /// and it listens to the flight log for what the autopilot programs report. Lines are only produced
    /// while a voice client is collecting them, so a game without the voice edition pays nothing.
    /// </summary>
    public sealed class Narrator
    {
        /// <summary>How much it says: warnings and milestones only, the normal call-outs, or everything.</summary>
        public const int Quiet = 0, Normal = 1, Chatty = 2;

        sealed class Line { public int Id; public float At; public string Text; public int Priority; public float Ttl; }

        readonly List<Line> lines = new List<Line>();
        readonly Dictionary<string, float> lastSaid = new Dictionary<string, float>();
        int nextId = 1;
        float listenedAt = -1000f, nextTick, nextHeatCheck;

        public int Detail = Normal;
        public int LastId => nextId - 1;
        public bool Active => Time.realtimeSinceStartup - listenedAt < 30f;
        public void Touch() { listenedAt = Time.realtimeSinceStartup; }

        // What was true at the last look, to notice changes.
        bool haveVessel;
        Guid vesselId;
        Vessel.Situations situation, pendingSituation;
        float pendingSince;
        string bodyName;
        double maxQ, lastRadar = double.NaN, lastToBurn = double.NaN, nodeUT = double.NaN, lastRange = double.NaN;
        bool maxQCalled, supersonic, fuelArmed, powerArmed, wasConnected, wasBurning;
        float connectionChangedAt = -1f, burningSince = -1f, highGSince = -1f, lastStageAt = -1000f, adoptedAt = -1000f, orbitSince = -1f;
        int chutesOut, chutesFull, lostParts;
        float lostSince = -1f;
        string phaseHead;
        float phaseSince;
        bool phaseSaid;

        static readonly int[] Heights = { 10000, 5000, 2000, 1000, 500, 200, 100, 50, 20, 10 };
        static readonly string[] HeightWords = { "10,000 metres", "5,000 metres", "2,000 metres", "1,000 metres", "500 metres", "200", "100", "50", "20", "10" };
        static readonly int[] Ranges = { 10000, 5000, 1000, 500, 100, 50, 20, 10 };

        // ------------------------------------------------------------------ the feed

        /// <summary>
        /// Queue a line. priority: 3 warning (cuts in), 2 milestone, 1 call-out. level: how talkative the
        /// narrator must be set for this to be said. ttl: seconds after which it is no longer worth saying.
        /// key + cooldown: the same kind of line is not repeated within that many seconds.
        /// </summary>
        public void Say(string text, int priority, int level, float ttl, string key = null, float cooldown = 4f)
        {
            if (!Active || Detail < level || string.IsNullOrEmpty(text)) return;
            float now = Time.realtimeSinceStartup;
            key = key ?? text;
            if (lastSaid.TryGetValue(key, out float at) && now - at < cooldown) return;
            lastSaid[key] = now;
            lines.Add(new Line { Id = nextId++, At = now, Text = text, Priority = priority, Ttl = ttl });
            if (lines.Count > 60) lines.RemoveRange(0, lines.Count - 60);
        }

        public List<object> Since(int id)
        {
            var list = new List<object>();
            float now = Time.realtimeSinceStartup;
            foreach (Line l in lines)
            {
                if (l.Id <= id || now - l.At > l.Ttl) continue;
                list.Add(new JObj().Add("id", l.Id).Add("text", l.Text).Add("priority", l.Priority).Add("ttl_s", Math.Round(l.Ttl, 1)).Add("age_s", Math.Round(now - l.At, 2)));
            }
            return list;
        }

        public static string Named(int detail) => detail <= Quiet ? "quiet" : detail >= Chatty ? "chatty" : "normal";

        public static int ParseDetail(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "quiet": return Quiet;
                case "normal": return Normal;
                case "chatty": return Chatty;
            }
            throw new ApiException("detail must be quiet, normal or chatty.");
        }

        static string Place(CelestialBody body) => body == null ? "the surface" : Places.Spoken(body);

        static string Km(double metres)
        {
            double km = metres / 1000.0;
            return (Math.Abs(km) >= 100 ? Math.Round(km).ToString("0") : Math.Round(km, 1).ToString("0.#")) + " km";
        }

        static string FirstClause(string text, int limit = 140)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int cut = text.Length;
            foreach (string stop in new[] { ". ", "; ", " (" })
            {
                int at = text.IndexOf(stop, StringComparison.Ordinal);
                if (at > 12 && at < cut) cut = at;
            }
            text = text.Substring(0, Math.Min(cut, limit)).TrimEnd('.', ' ', ',', ';', ':');
            return text;
        }

        static string Sentence(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return text;
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);
            return ".!?".IndexOf(text[text.Length - 1]) >= 0 ? text : text + ".";
        }

        // ------------------------------------------------------------------ what the autopilot reports

        public void OnProgramStart(Program p)
        {
            // A call-out, not a milestone: "Ignition" and "Lift-off" go first when they arrive together.
            Say("Autopilot: " + p.Name + ".", 1, Normal, 14f, "program", 1f);
        }

        public void OnProgramEnd(Program p)
        {
            if (p.Failed)
            {
                if (p.Result.StartsWith("aborted (", StringComparison.Ordinal))
                {
                    if (p.Result.IndexOf("replaced by", StringComparison.Ordinal) < 0) Say("Autopilot disengaged.", 2, Normal, 10f, "released", 5f);
                    return;
                }
                Say("Warning. " + Sentence(p.Name + " failed: " + FirstClause(p.Result)), 3, Quiet, 25f, "program-end", 1f);
                return;
            }
            string line;
            switch (p.GetType().Name)
            {
                case "AscentProgram": line = "Ascent complete."; break;
                case "NodeProgram": line = "Burn complete."; break;
                case "LandProgram": case "ReentryProgram": line = "Landing complete."; break;
                case "RunwayLandProgram": line = "Landing complete. Full stop."; break;
                case "DockProgram": line = "Docking complete."; break;
                case "DriveProgram": line = "We have arrived."; break;
                case "FlyToProgram": line = "Destination reached."; break;
                case "TakeoffProgram": line = null; break;
                default: line = Sentence(p.Name + " complete"); break;
            }
            if (line != null) Say(line, 2, Normal, 20f, "program-end", 1f);
        }

        /// <summary>A flight-log entry was written; say it in the ship's words when it is worth saying.</summary>
        public void OnLog(string text)
        {
            if (!Active || string.IsNullOrEmpty(text)) return;
            try { Translate(text); } catch (Exception ex) { BridgeHost.LogWarn("narrator: " + ex.Message); }
        }

        void Translate(string t)
        {
            if (t.StartsWith("stage ", StringComparison.Ordinal))
            {
                lastStageAt = Time.realtimeSinceStartup;
                Vessel v = FlightGlobals.ActiveVessel;
                if (v != null && v.situation == Vessel.Situations.PRELAUNCH) Say("Ignition.", 2, Quiet, 8f, "ignition", 8f);
                else Say("Staging.", 2, Quiet, 6f, "stage", 1.5f);
            }
            else if (t == "lift-off") Say("Lift-off.", 2, Quiet, 10f, "liftoff", 10f);
            else if (t.StartsWith("part destroyed: ", StringComparison.Ordinal))
            {
                float now = Time.realtimeSinceStartup;
                if (lostSince < 0f || now - lostSince > 6f) { lostSince = now; lostParts = 0; }
                lostParts++;
                // One warning per mishap: a crash, the break-up and the loss of a part are the same piece of
                // news, and each new warning would cut off the one before it.
                if (lostParts == 1) Say("Warning. " + t.Substring(16) + " destroyed.", 3, Quiet, 10f, "damage", 4f);
            }
            else if (t.StartsWith("crash: ", StringComparison.Ordinal)) Say("Warning. Collision.", 3, Quiet, 8f, "damage", 4f);
            else if (t.StartsWith("crew member killed: ", StringComparison.Ordinal)) Say("Warning. We have lost " + t.Substring(20) + ".", 3, Quiet, 30f);
            else if (t.StartsWith("entered the sphere of influence of ", StringComparison.Ordinal))
            {
                string name = t.Substring(35), left = null;
                int end = name.IndexOf(" (left ", StringComparison.Ordinal);
                if (end > 0) { left = name.Substring(end + 7).TrimEnd(')'); name = name.Substring(0, end); }
                // Climbing out to the star is leaving somewhere, not arriving.
                if (name == "Sun" && left != null) Say("We have left the sphere of influence of " + (left == "Mun" ? "the Mun" : left) + ". Now in orbit around the Sun.", 2, Quiet, 30f, "soi", 5f);
                else Say("Entering the sphere of influence of " + (name == "Mun" ? "the Mun" : name) + ".", 2, Quiet, 30f, "soi", 5f);
            }
            else if (t.StartsWith("docked: ", StringComparison.Ordinal)) Say("Docking confirmed.", 2, Quiet, 20f, "docked", 8f);
            else if (t.StartsWith("structural failure at ", StringComparison.Ordinal)) Say("Warning. Structural failure at the " + t.Substring(22) + ".", 3, Quiet, 12f, "damage", 4f);
            else if (t.StartsWith("autopilot released: ", StringComparison.Ordinal)) Say("Autopilot disengaged.", 2, Normal, 10f, "released", 5f);
            else if (t.StartsWith("flight plan '", StringComparison.Ordinal))
            {
                int quote = t.IndexOf("' ", 13, StringComparison.Ordinal);
                string rest = quote > 0 ? t.Substring(quote + 2) : "";
                if (rest.StartsWith("finished", StringComparison.Ordinal) || rest.StartsWith("done", StringComparison.Ordinal) || rest.StartsWith("complete", StringComparison.Ordinal))
                    Say("Flight plan complete.", 2, Quiet, 30f, "plan", 3f);
                else if (rest.StartsWith("failed", StringComparison.Ordinal))
                {
                    // "failed: step 3 (ascent): ..." -- the program that failed has already said why.
                    string where = rest.Length > 8 ? rest.Substring(8) : "";
                    int end = where.IndexOfAny(new[] { '(', ':' });
                    if (end > 0) where = where.Substring(0, end).Trim();
                    Say("Warning. Flight plan failed" + (where.StartsWith("step", StringComparison.Ordinal) ? " at " + where : "") + ".", 3, Quiet, 30f, "plan", 3f);
                }
                else if (rest.StartsWith("aborted", StringComparison.Ordinal) || rest.StartsWith("stopped", StringComparison.Ordinal))
                    Say("Flight plan stopped.", 2, Normal, 20f, "plan", 3f);
            }
            else if (t.StartsWith("warning: ", StringComparison.Ordinal)) Say("Warning. " + Sentence(FirstClause(t.Substring(9), 200)), 3, Quiet, 20f);
            else if (t.StartsWith("airborne at ", StringComparison.Ordinal)) Say("Airborne.", 2, Normal, 8f, "airborne", 15f);
            else if (t.StartsWith("going around: ", StringComparison.Ordinal)) Say("Going around: " + FirstClause(t.Substring(14)) + ".", 2, Normal, 12f, "go-around", 10f);
            else if (t.StartsWith("high ground ahead", StringComparison.Ordinal)) Say("Terrain ahead. Climbing.", 2, Normal, 12f, "terrain", 20f);
            else if (t.StartsWith("clear of the high ground", StringComparison.Ordinal)) Say("Clear of terrain.", 1, Normal, 10f, "terrain-clear", 20f);
            else if (t.StartsWith("the target is turning", StringComparison.Ordinal)) Say("The target is rotating. Following it round.", 1, Normal, 12f);
            else if (t.StartsWith("backing off to line up again", StringComparison.Ordinal)) Say("Backing off to line up again.", 1, Normal, 10f, "backoff", 10f);
            else if (t.StartsWith("de-orbit burn done", StringComparison.Ordinal)) Say("De-orbit burn complete.", 2, Normal, 15f);
            else if (t.StartsWith("stopped ", StringComparison.Ordinal) && t.EndsWith("descending", StringComparison.Ordinal)) Say("Over the landing site. Descending.", 2, Normal, 10f);
            else if (t.StartsWith("parachutes leave ", StringComparison.Ordinal)) Say("The parachutes are not enough. Finishing on the engine.", 2, Quiet, 10f);
            else if (t.StartsWith("too fast for the parachutes", StringComparison.Ordinal)) Say("Too fast for the parachutes. Slowing down with the engine.", 2, Quiet, 10f);
        }

        /// <summary>The steady part of a phase text: "coasting to apoapsis, 43 s (burn 812 m/s)" gives "coasting to apoapsis".</summary>
        static string PhaseHead(string phase)
        {
            if (string.IsNullOrEmpty(phase)) return null;
            int cut = phase.IndexOfAny(new[] { ',', ':', '(' });
            string head = cut >= 0 ? phase.Substring(0, cut) : phase;
            for (int n = 0; n < head.Length; n++)
                if (char.IsDigit(head[n])) { head = head.Substring(0, n); break; }
            head = head.Trim();
            while (true)
            {
                int space = head.LastIndexOf(' ');
                string last = space >= 0 ? head.Substring(space + 1) : head;
                if (last != "at" && last != "to" && last != "through" && last != "from" && last != "of" && last != "in" && last != "for" && last != "the" && last != "under") break;
                head = space >= 0 ? head.Substring(0, space).Trim() : "";
            }
            return head.Length < 4 ? null : head;
        }

        static string PhaseLine(string head)
        {
            switch (head)
            {
                case "starting": case "vertical climb": case "waiting for burn start": case "falling": case "touchdown": case "descending":
                case "finding which way is forward": case "finding which way the steering turns": case "getting back up":
                case "at the hatch": case "taking hold of the ladder": case "in the air": case "ignition": case "going around":
                    return null;
                case "gravity turn": return "Beginning gravity turn.";
                case "coasting out of the atmosphere": return "Engine cut-off. Coasting out of the atmosphere.";
                case "circularising": return "Circularisation burn.";
                case "turning to the burn direction": return "Turning to burn attitude.";
                case "burning": return "Burn started.";
                case "no active engine": case "no engine running": return "Warning. No engine is running. Waiting for staging.";
                case "braking": return "Braking burn.";
                case "coasting to the atmosphere": return "Coasting to atmospheric entry.";
                case "engine landing": return "Landing on the engine.";
                case "rolling": return "Take-off roll.";
                case "rotating": return "Rotate.";
                case "climbing": return "Positive climb.";
                case "final": return "On final approach.";
                case "turning to face the port": return "Turning to face the docking port.";
                case "lining up": return "Lining up with the docking port.";
                case "ports engaged": return "Contact. Ports engaged.";
                case "driving": return "Under way.";
                case "starting the jetpack": return "Jetpack on.";
            }
            return Sentence(head);
        }

        // ------------------------------------------------------------------ watching the flight

        public void Tick()
        {
            if (!Active) { haveVessel = false; return; }
            float now = Time.realtimeSinceStartup;
            if (now < nextTick) return;
            nextTick = now + 0.25f;
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready) { haveVessel = false; return; }
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) { haveVessel = false; return; }
            try
            {
                if (!haveVessel || v.id != vesselId) Adopt(v);
                else Watch(v, now);
            }
            catch (Exception ex) { BridgeHost.LogWarn("narrator: " + ex.Message); }
        }

        /// <summary>A vessel the narrator has not been following: take its state as the starting point, without call-outs.</summary>
        void Adopt(Vessel v)
        {
            bool first = !haveVessel;
            float now = Time.realtimeSinceStartup;
            haveVessel = true;
            vesselId = v.id;
            situation = pendingSituation = v.situation;
            bodyName = v.mainBody.bodyName;
            maxQ = 0;
            maxQCalled = false;
            supersonic = v.mach >= 1.0;
            lastRadar = lastToBurn = nodeUT = lastRange = double.NaN;
            fuelArmed = powerArmed = false;
            wasConnected = v.connection == null || v.connection.IsConnected;
            connectionChangedAt = -1f;
            adoptedAt = now;
            wasBurning = false;
            burningSince = highGSince = -1f;
            CountChutes(v, out chutesOut, out chutesFull);
            phaseHead = null;
            orbitSince = -1f;
            if (now - lastStageAt < 3f || Autopilot.I.Stager.SecondsSinceStaging < 3.0) return;   // staging hands focus to a new vessel object
            if (first) Say("Flight computer online. Vessel: " + v.GetDisplayName() + ".", 1, Normal, 12f, "online", 20f);
            else Say("Now controlling " + v.GetDisplayName() + ".", 1, Normal, 10f, "control", 3f);
        }

        void Watch(Vessel v, float now)
        {
            CelestialBody body = v.mainBody;
            double ut = Planetarium.GetUniversalTime();
            bool fastWarp = TimeWarp.CurrentRate > 4.01f;   // call-outs timed to the second mean nothing at high warp
            bool onGround = v.LandedOrSplashed || v.situation == Vessel.Situations.PRELAUNCH;

            // --- where we are: landed, flying, in orbit...
            if (body.bodyName != bodyName)
            {
                bodyName = body.bodyName;                   // the flight log announces the new sphere of influence
                situation = pendingSituation = v.situation;
            }
            else if (v.situation != situation)
            {
                if (v.situation != pendingSituation) { pendingSituation = v.situation; pendingSince = now; }
                // Wheels and legs bounce between landed and flying; wait until the new state has held.
                bool bouncy = onGround || situation == Vessel.Situations.LANDED || situation == Vessel.Situations.SPLASHED;
                if (now - pendingSince >= (bouncy ? 1.0f : 0.2f))
                {
                    SituationChanged(v, situation, v.situation);
                    situation = v.situation;
                }
            }
            else pendingSituation = situation;

            // --- climbing through the air
            bool inAir = body.atmosphere && v.altitude < body.atmosphereDepth && !onGround;
            if (inAir && v.verticalSpeed > 30)
            {
                if (!supersonic && v.mach >= 1.0) { supersonic = true; Say("Supersonic.", 1, Normal, 4f, "mach", 30f); }
                double q = v.dynamicPressurekPa;
                if (q > maxQ) maxQ = q;
                else if (!maxQCalled && maxQ > 6 && q < maxQ * 0.9) { maxQCalled = true; Say("Passing maximum dynamic pressure.", 1, Normal, 5f, "maxq", 60f); }
            }
            if (v.mach < 0.8) supersonic = false;
            if (onGround) { maxQ = 0; maxQCalled = false; }

            // --- coming down: height call-outs
            double radar = v.radarAltitude;
            bool descending = v.verticalSpeed < -1.5 && !onGround && !v.isEVA;
            Program program = Autopilot.I.Program;
            if (descending && !fastWarp && !double.IsNaN(lastRadar) && v.vesselType != VesselType.Rover)
            {
                bool plane = v.vesselType == VesselType.Plane && inAir;
                bool landing = program is RunwayLandProgram || program is LandProgram || program is ReentryProgram;
                for (int n = Heights.Length - 1; n >= 0; n--)
                {
                    int h = Heights[n];
                    if (!(radar <= h && lastRadar > h)) continue;
                    if (plane && (h > 500 || !(landing || Gear.IsDown(v)))) break;
                    Say(HeightWords[n] + ".", 1, Normal, h >= 1000 ? 4f : 2.2f, "height" + h, 20f);
                    break;
                }
                if (program == null && radar > 0 && -v.verticalSpeed > 15 && radar / -v.verticalSpeed < 3.5 && v.srfSpeed > 20)
                    Say("Warning. Impact imminent.", 3, Normal, 3f, "impact", 8f);
                if (program == null && radar < 120 && v.srfSpeed < 160 && HasDeployableGear(v) && !Gear.IsDown(v))
                    Say("Warning. Landing gear is up.", 3, Normal, 4f, "gear", 20f);
            }
            lastRadar = radar;

            // --- the next maneuver
            PatchedConicSolver solver = v.patchedConicSolver;
            if (solver != null && solver.maneuverNodes.Count > 0)
            {
                ManeuverNode node = solver.maneuverNodes[0];
                double burn = Guidance.BurnTime(v, node.GetBurnVector(v.orbit).magnitude);
                double toBurn = node.UT - (double.IsNaN(burn) ? 0 : burn / 2) - ut;
                if (node.UT != nodeUT) { nodeUT = node.UT; lastToBurn = toBurn; }
                if (!fastWarp)
                {
                    if (toBurn <= 60 && lastToBurn > 60 && toBurn > 56) Say("Burn in 60 seconds.", 1, Normal, 5f, "t60", 30f);
                    else if (toBurn <= 30 && lastToBurn > 30 && toBurn > 27) Say("30 seconds to burn.", 1, Normal, 4f, "t30", 20f);
                    else if (toBurn <= 10 && lastToBurn > 10 && toBurn > 8) Say("Burn in 10 seconds.", 1, Normal, 3f, "t10", 8f);
                }
                lastToBurn = toBurn;
            }
            else nodeUT = double.NaN;

            // --- engines and what they have left to burn
            Engines(v, out int burning, out int flamedOut, out double fraction, out bool moreStages);
            if (burning > 0)
            {
                if (!wasBurning) burningSince = now;
                if (fraction > 0.3) fuelArmed = true;
                else if (fraction < 0.1 && fuelArmed && !moreStages)
                {
                    fuelArmed = false;
                    Say("Warning. Propellant at 10 percent.", 2, Quiet, 8f, "fuel", 30f);
                }
            }
            else if (wasBurning)
            {
                bool longBurn = burningSince >= 0f && now - burningSince > 5f;
                if (flamedOut > 0 && !moreStages && v.ctrlState.mainThrottle > 0.01f) Say("Warning. Propellant exhausted.", 3, Quiet, 10f, "empty", 30f);
                else if (longBurn && v.ctrlState.mainThrottle <= 0.01f && program == null) Say("Engine cut-off.", 1, Chatty, 4f, "cutoff", 20f);
            }
            wasBurning = burning > 0;
            if (orbitSince >= 0f && (burning == 0 || now - orbitSince > 40f))
            {
                orbitSince = -1f;
                if (v.situation == Vessel.Situations.ORBITING) Say("Orbit achieved: " + Km(v.orbit.ApA) + " by " + Km(v.orbit.PeA) + ".", 2, Quiet, 30f, "orbit", 20f);
            }

            // --- electricity
            v.GetConnectedResourceTotals(PartResourceLibrary.ElectricityHashcode, out double charge, out double capacity);
            if (capacity > 0.5)
            {
                double left = charge / capacity;
                if (left > 0.25) powerArmed = true;
                else if (left < 0.1 && powerArmed) { powerArmed = false; Say("Warning. Electric charge below 10 percent.", 3, Quiet, 15f, "power", 60f); }
            }

            // --- contact with home
            if (v.connection != null && CommNet.CommNetScenario.CommNetEnabled)
            {
                bool connected = v.connection.IsConnected;
                if (now - adoptedAt < 6f) wasConnected = connected;       // the network takes a moment to find a new vessel
                if (connected == wasConnected) connectionChangedAt = -1f;
                else if (connectionChangedAt < 0f) connectionChangedAt = now;
                else if (now - connectionChangedAt > 2f)
                {
                    wasConnected = connected;
                    connectionChangedAt = -1f;
                    if (connected) Say("Signal restored.", 1, Normal, 10f, "signal", 10f);
                    else Say(v.IsControllable ? "Signal lost." : "Warning. Signal lost. No control.", 2, Normal, 10f, "signal", 10f);
                }
            }

            // --- parachutes
            CountChutes(v, out int outNow, out int fullNow);
            if (outNow > 0 && chutesOut == 0) Say(outNow > 1 ? "Parachutes out." : "Parachute out.", 2, Quiet, 8f, "chute", 10f);
            if (fullNow > 0 && chutesFull == 0) Say(fullNow > 1 ? "Parachutes fully open." : "Parachute fully open.", 2, Normal, 8f, "chute-full", 10f);
            chutesOut = outNow;
            chutesFull = fullNow;

            // --- heat and load
            if (now >= nextHeatCheck)
            {
                nextHeatCheck = now + 1f;
                Part hottest = null;
                double worst = 0.92;
                foreach (Part p in v.parts)
                {
                    double ratio = Math.Max(p.temperature / Math.Max(1.0, p.maxTemp), p.skinTemperature / Math.Max(1.0, p.skinMaxTemp));
                    if (ratio > worst) { worst = ratio; hottest = p; }
                }
                if (hottest != null) Say("Warning. " + hottest.partInfo.title + " is overheating.", 3, Quiet, 6f, "heat", 12f);
            }
            if (v.geeForce > 8 && v.GetCrewCount() > 0 && !onGround)
            {
                if (highGSince < 0f) highGSince = now;
                else if (now - highGSince > 1.5f) Say("Warning. High g load.", 2, Normal, 5f, "g", 25f);
            }
            else highGSince = -1f;

            // --- closing on a target vessel
            Vessel target = v.targetObject?.GetVessel();
            if (target != null && target != v && !fastWarp)
            {
                double range = (target.CoMD - v.CoMD).magnitude;
                if (!double.IsNaN(lastRange))
                    for (int n = Ranges.Length - 1; n >= 0; n--)
                    {
                        int r = Ranges[n];
                        if (!(range <= r && lastRange > r)) continue;
                        Say("Target range " + (r >= 1000 ? r / 1000 + " km" : r + " m") + ".", 1, Normal, r >= 1000 ? 5f : 3f, "range" + r, 30f);
                        break;
                    }
                lastRange = range;
            }
            else lastRange = double.NaN;

            // --- several parts gone at once
            if (lostSince >= 0f && now - lostSince > 2.5f)
            {
                if (lostParts >= 3) Say("We have lost " + lostParts + " parts.", 3, Quiet, 10f, "lost-many", 5f);
                lostSince = -1f;
                lostParts = 0;
            }

            // --- what the autopilot is doing
            string head = program != null ? PhaseHead(program.Phase) : null;
            if (head != phaseHead) { phaseHead = head; phaseSince = now; phaseSaid = false; }
            else if (head != null && !phaseSaid && now - phaseSince > 0.7f)
            {
                phaseSaid = true;
                string line = PhaseLine(head);
                if (line != null) Say(line, 1, Normal, 6f, "phase:" + head, 20f);
            }
        }

        void SituationChanged(Vessel v, Vessel.Situations from, Vessel.Situations to)
        {
            CelestialBody body = v.mainBody;
            bool fromGround = from == Vessel.Situations.LANDED || from == Vessel.Situations.SPLASHED || from == Vessel.Situations.PRELAUNCH;
            bool hopper = v.isEVA || v.vesselType == VesselType.Rover;
            switch (to)
            {
                case Vessel.Situations.LANDED:
                    if (!fromGround && !hopper) Say(body.isHomeWorld ? "Touchdown." : "Touchdown on " + Place(body) + ".", 2, Quiet, 12f, "down", 10f);
                    break;
                case Vessel.Situations.SPLASHED:
                    if (!fromGround && !hopper) Say("Splashdown.", 2, Quiet, 12f, "down", 10f);
                    break;
                case Vessel.Situations.FLYING:
                    if (from == Vessel.Situations.LANDED || from == Vessel.Situations.SPLASHED) { if (!hopper) Say("Airborne.", 2, Normal, 8f, "airborne", 15f); }
                    else if (!fromGround) Say("Atmospheric entry.", 2, Quiet, 15f, "entry", 60f);
                    break;
                case Vessel.Situations.SUB_ORBITAL:
                    if (fromGround) { if (!hopper) Say("Lift-off.", 2, Quiet, 8f, "liftoff", 10f); }
                    else if (from == Vessel.Situations.FLYING) Say("We have left the atmosphere.", 2, Normal, 15f, "space", 60f);
                    else if (from == Vessel.Situations.ORBITING)
                        Say("Periapsis is below the " + (body.atmosphere ? "atmosphere" : "surface") + ". We are coming down.", 1, Normal, 15f, "deorbit", 30f);
                    break;
                case Vessel.Situations.ORBITING:
                    if (from == Vessel.Situations.ESCAPING) Say("Capture confirmed. We are in orbit around " + Place(body) + ".", 2, Quiet, 30f, "orbit", 20f);
                    else orbitSince = Time.realtimeSinceStartup;      // announced when the burn that got us here ends, with the final figures
                    break;
                case Vessel.Situations.ESCAPING:
                    if (from == Vessel.Situations.ORBITING || from == Vessel.Situations.SUB_ORBITAL) Say("Escape velocity. We are leaving " + Place(body) + ".", 2, Quiet, 30f, "escape", 30f);
                    break;
            }
        }

        static void CountChutes(Vessel v, out int open, out int full)
        {
            open = full = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleParachute chute)
                    {
                        if (chute.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED) open++;
                        else if (chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED) { open++; full++; }
                    }
        }

        static bool HasDeployableGear(Vessel v)
        {
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleWheels.ModuleWheelDeployment) return true;
            return false;
        }

        /// <summary>
        /// Engines that are running, engines that have run dry, how full the tanks feeding the running
        /// ones are (0..1), and whether a later stage still has engines to light.
        /// </summary>
        static void Engines(Vessel v, out int burning, out int flamedOut, out double fraction, out bool moreStages)
        {
            burning = flamedOut = 0;
            fraction = 1.0;
            moreStages = false;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleEngines e)) continue;
                    if (!e.EngineIgnited)
                    {
                        if (!e.flameout && p.inverseStage < v.currentStage) moreStages = true;
                        continue;
                    }
                    if (e.flameout) { flamedOut++; continue; }
                    if (e.finalThrust <= 0f) continue;
                    burning++;
                    foreach (Propellant fuel in e.propellants)
                    {
                        if (fuel.totalResourceCapacity <= 0 || fuel.name == "ElectricCharge" || fuel.name == "IntakeAir") continue;
                        fraction = Math.Min(fraction, fuel.totalResourceAvailable / fuel.totalResourceCapacity);
                    }
                }
        }
    }
}
