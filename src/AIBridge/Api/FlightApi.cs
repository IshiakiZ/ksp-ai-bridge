using System;
using System.Collections.Generic;
using AIBridge.Pilot;
using KSP.UI.Screens;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>Flight telemetry and direct controls for the vessel being flown.</summary>
    public static class FlightApi
    {
        static BridgeHost host;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("trace", "Flight recorder: recent samples of attitude and control outputs while the autopilot was engaged. params: {last, every, clear}", a =>
            {
                Autopilot ap = Autopilot.I;
                int last = Math.Max(1, Math.Min(6000, a.Int("last", 150))), every = Math.Max(1, a.Int("every", 1));
                var rows = new List<object>();
                for (int n = Math.Max(0, ap.Trace.Count - last); n < ap.Trace.Count; n += every) rows.Add(ap.Trace[n]);
                if (a.Bool("clear", false)) ap.Trace.Clear();
                return new JObj().Add("columns", Autopilot.TraceColumns).Add("rows", rows);
            }).AlwaysAllowed = true;
            Rpc.Sync("vessel", "Telemetry for the active vessel. params: {full}", a => VesselSummary(Active(), a.Bool("full", false))).AlwaysAllowed = true;
            Rpc.Sync("control", "Direct controls. params: {throttle, stage, sas, sas_mode, rcs, gear, lights, brakes, abort, action_group, pitch, yaw, roll, x, y, z, wheel_steer, wheel_throttle, seconds}", Control);
            Rpc.Sync("attitude", "Point the vessel and hold. params: {mode, pitch, heading, roll, max_turn_rate}", Attitude);
            Rpc.Sync("autostage", "Automatic staging when engines burn out. params: {on, stop_at_stage}", AutoStage);
            Rpc.Sync("events", "Flight events since an id (staging, explosions, SOI changes...). params: {since}", Events).AlwaysAllowed = true;
            Rpc.Sync("pilot_status", "What the autopilot is doing right now.", a => Autopilot.I.Status()).AlwaysAllowed = true;
            Rpc.Sync("release", "Release every autopilot hold and hand the controls back. params: {cut_throttle}", Release);
            Rpc.Async("revert", "Undo the current flight. params: {to: launch|editor}", Revert);
            Rpc.Async("quicksave", "Save the flight under a name so it can be restored. params: {name}", QuickSave);
            Rpc.Async("quickload", "Restore a flight saved with quicksave. params: {name}", QuickLoad);
            Rpc.Async("recover", "Recover the landed or splashed-down vessel on the home planet and return to the Space Center.", Recover);
        }

        public static Vessel Active()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) throw new ApiException("Not in flight (current scene: " + GameApi.SceneName(HighLogic.LoadedScene) + "). Launch a craft or switch to a vessel first.");
            if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null) throw new ApiException("The flight scene is still loading; try again in a moment.");
            return FlightGlobals.ActiveVessel;
        }

        /// <summary>After a flight scene loads, wait for physics to start and the stock delta-v figures to be computed.</summary>
        public static System.Collections.IEnumerator Settle(float seconds = 4f)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            yield return new WaitSec(0.8f);
            while (Time.realtimeSinceStartup < deadline)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null) yield break;
                VesselDeltaV dv = v.VesselDeltaV;
                if (!v.packed && (dv == null || (dv.IsReady && !dv.SimulationRunning && dv.TotalDeltaVVac > 0))) yield break;
                yield return null;
            }
        }

        static double R(double v, int digits = 1) => double.IsNaN(v) || double.IsInfinity(v) ? double.NaN : Math.Round(v, digits);

        // ---------------------------------------------------------------- telemetry

        public static JObj VesselSummary(Vessel v, bool full = false)
        {
            if (v == null) throw new ApiException("No active vessel.");
            CelestialBody body = v.mainBody;
            var o = new JObj();
            o.Add("name", v.GetDisplayName());
            o.Add("type", v.vesselType.ToString().ToLowerInvariant());
            o.Add("situation", v.situation.ToString());
            o.Add("body", body.bodyName);
            o.Add("ut", R(Planetarium.GetUniversalTime(), 1));
            o.Add("met_s", R(v.missionTime, 1));
            o.Add("controllable", v.IsControllable);
            if (v.packed) o.Add("on_rails", true);
            int crew = v.GetCrewCount();
            if (crew > 0 || full) o.Add("crew", crew);

            o.Add("altitude_m", R(v.altitude, 0));
            if (v.radarAltitude < 100000 && !v.Splashed) o.Add("height_above_ground_m", R(v.radarAltitude, 1));
            o.Add("lat", R(v.latitude, 4));
            o.Add("lon", R(v.longitude, 4));
            o.Add("speed", new JObj()
                .Add("surface", R(v.srfSpeed, 1))
                .Add("orbital", R(v.obt_speed, 1))
                .Add("vertical", R(v.verticalSpeed, 1))
                .Add("horizontal", R(v.horizontalSrfSpeed, 1)));
            Guidance.Attitude(v, out double pitch, out double heading, out double roll);
            o.Add("attitude", new JObj().Add("pitch", R(pitch, 1)).Add("heading", R(heading, 1)).Add("roll", R(roll, 1)));

            if (!v.LandedOrSplashed) o.Add("orbit", OrbitInfo(v.orbit, true));
            if (body.atmosphere && v.altitude < body.atmosphereDepth)
                o.Add("atmosphere", new JObj().Add("dynamic_pressure_kpa", R(v.dynamicPressurekPa, 2)).Add("mach", R(v.mach, 2)).Add("top_m", body.atmosphereDepth));
            o.Add("g_force", R(v.geeForce, 2));

            Guidance.EngineStats(v, out double maxThrust, out double thrust, out double isp, out int burning, out int flamedOut);
            double g = Guidance.LocalGravity(v);
            var prop = new JObj();
            prop.Add("mass_t", R(v.totalMass, 3));
            prop.Add("throttle", R(v.ctrlState.mainThrottle, 2));
            prop.Add("thrust_kn", R(thrust, 1));
            prop.Add("max_thrust_kn", R(maxThrust, 1));
            prop.Add("twr", R(maxThrust / Math.Max(1e-9, v.totalMass * g), 2));
            if (isp > 0) prop.Add("isp", R(isp, 0));
            prop.Add("engines_burning", burning);
            if (flamedOut > 0) prop.Add("engines_flamed_out", flamedOut);
            prop.Add("current_stage", v.currentStage);
            VesselDeltaV dv = v.VesselDeltaV;
            if (dv != null && dv.IsReady)
            {
                prop.Add("dv_total", R(dv.TotalDeltaVActual, 0));
                prop.Add("dv_total_vac", R(dv.TotalDeltaVVac, 0));
                var stages = new List<object>();
                var infos = new List<DeltaVStageInfo>(dv.OperatingStageInfo);
                infos.Sort((x, y) => y.stage.CompareTo(x.stage));
                foreach (DeltaVStageInfo s in infos)
                    if (s.deltaVActual > 0.5f || s.deltaVinVac > 0.5f)
                        stages.Add(new JObj().Add("stage", s.stage).Add("dv", R(s.deltaVActual, 0)).Add("dv_vac", R(s.deltaVinVac, 0)).Add("twr", R(s.TWRActual, 2)).Add("burn_s", R(s.stageBurnTime, 0)));
                if (stages.Count > 0) prop.Add("stages", stages);
            }
            o.Add("propulsion", prop);

            o.Add("resources", Resources(v));

            var c = new JObj();
            c.Add("sas", v.ActionGroups[KSPActionGroup.SAS]);
            if (v.ActionGroups[KSPActionGroup.SAS] && v.Autopilot != null) c.Add("sas_mode", v.Autopilot.Mode.ToString());
            c.Add("rcs", v.ActionGroups[KSPActionGroup.RCS]);
            c.Add("gear", Gear.IsDown(v) && v.ActionGroups[KSPActionGroup.Gear]);
            c.Add("brakes", v.ActionGroups[KSPActionGroup.Brakes]);
            c.Add("lights", v.ActionGroups[KSPActionGroup.Light]);
            o.Add("controls", c);

            if (v.targetObject != null) o.Add("target", TargetInfo(v));
            PatchedConicSolver solver = v.patchedConicSolver;
            if (solver != null && solver.maneuverNodes.Count > 0)
            {
                ManeuverNode node = solver.maneuverNodes[0];
                double remaining = node.GetBurnVector(v.orbit).magnitude;
                var n = new JObj().Add("count", solver.maneuverNodes.Count).Add("next_dv", R(remaining, 1)).Add("next_in_s", R(node.UT - Planetarium.GetUniversalTime(), 1));
                double burn = Guidance.BurnTime(v, remaining);
                if (!double.IsNaN(burn)) n.Add("next_burn_s", R(burn, 1));
                o.Add("maneuver_nodes", n);
            }

            if (TimeWarp.CurrentRate > 1.01f) o.Add("time_warp", new JObj().Add("rate", TimeWarp.CurrentRate).Add("mode", TimeWarp.WarpMode == TimeWarp.Modes.HIGH ? "on_rails" : "physics"));
            if (FlightDriver.Pause) o.Add("paused", true);
            JObj ap = Autopilot.I.Status();
            if (Autopilot.I.IsEngaged || ap["last_program"] != null) o.Add("autopilot", ap);
            o.Add("last_event_id", Autopilot.I.Log.LastId);

            if (full)
            {
                var names = new List<object>();
                foreach (ProtoCrewMember k in v.GetVesselCrew()) names.Add(k.name + " (" + k.trait + ")");
                if (names.Count > 0) o.Add("crew_names", names);
                o.Add("biome", ScienceUtil.GetExperimentBiome(body, v.latitude, v.longitude));
                if (v.connection != null) o.Add("comms", new JObj().Add("connected", v.connection.IsConnected).Add("signal", R(v.connection.SignalStrength, 2)));
                o.Add("part_count", v.parts.Count);
                o.Add("terrain_altitude_m", R(v.terrainAltitude, 0));
                o.Add("local_gravity", R(g, 3));
                o.Add("in_sunlight", v.directSunlight);
                if (body.atmosphere && v.altitude < body.atmosphereDepth) o.Add("static_pressure_kpa", R(v.staticPressurekPa, 3));
                Vector3 torque = Autopilot.AvailableTorque(v);
                o.Add("turn_authority", new JObj().Add("pitch_torque_knm", R(torque.x, 1)).Add("yaw_torque_knm", R(torque.z, 1)).Add("roll_torque_knm", R(torque.y, 1)));
            }
            return o;
        }

        public static JObj OrbitInfo(Orbit orbit, bool withNext) => OrbitInfo(orbit, withNext ? 3 : 0);

        /// <summary>Describe an orbit and up to <paramref name="follow"/> of the trajectory segments after it.</summary>
        public static JObj OrbitInfo(Orbit orbit, int follow)
        {
            var o = new JObj();
            CelestialBody body = orbit.referenceBody;
            o.Add("body", body.bodyName);
            bool closed = orbit.eccentricity < 1.0;
            if (closed) o.Add("apoapsis_m", R(orbit.ApA, 0));
            o.Add("periapsis_m", R(orbit.PeA, 0));
            bool current = orbit.StartUT <= Planetarium.GetUniversalTime() + 1.0 || orbit.patchStartTransition == Orbit.PatchTransitionType.INITIAL;
            if (current)
            {
                if (closed) o.Add("time_to_apoapsis_s", R(orbit.timeToAp, 1));
                o.Add("time_to_periapsis_s", R(orbit.timeToPe, 1));
            }
            o.Add("inclination_deg", R(orbit.inclination, 3));
            o.Add("eccentricity", R(orbit.eccentricity, 5));
            if (closed) o.Add("period_s", R(orbit.period, 1));
            o.Add("sma_m", R(orbit.semiMajorAxis, 0));
            if (orbit.PeA < 0) o.Add("note", "periapsis is below the surface: this trajectory hits the ground");
            else if (body.atmosphere && orbit.PeA < body.atmosphereDepth) o.Add("note", "periapsis is inside the atmosphere");
            if (follow > 0 && orbit.patchEndTransition != Orbit.PatchTransitionType.FINAL && orbit.nextPatch != null && orbit.nextPatch.activePatch)
            {
                var n = new JObj();
                n.Add("transition", orbit.patchEndTransition.ToString().ToLowerInvariant());
                n.Add("in_s", R(orbit.EndUT - Planetarium.GetUniversalTime(), 0));
                n.Add("then", OrbitInfo(orbit.nextPatch, follow - 1));
                o.Add("next", n);
            }
            return o;
        }

        static JObj Resources(Vessel v)
        {
            var amounts = new Dictionary<string, double[]>();
            var order = new List<string>();
            foreach (Part p in v.parts)
                foreach (PartResource r in p.Resources)
                {
                    if (!amounts.TryGetValue(r.resourceName, out double[] pair)) { amounts[r.resourceName] = pair = new double[2]; order.Add(r.resourceName); }
                    pair[0] += r.amount;
                    pair[1] += r.maxAmount;
                }
            var o = new JObj();
            foreach (string name in order) o.Add(name, R(amounts[name][0], 1) + "/" + R(amounts[name][1], 1));
            return o;
        }

        public static JObj TargetInfo(Vessel v)
        {
            ITargetable t = v.targetObject;
            var o = new JObj();
            o.Add("name", t.GetName());
            o.Add("kind", t is CelestialBody ? "body" : t is Vessel ? "vessel" : "part");
            Transform tr = t.GetTransform();
            if (tr != null) o.Add("distance_m", R(((Vector3d)tr.position - v.CoMD).magnitude, 0));
            Vector3d rel = v.obt_velocity - t.GetObtVelocity();
            if (!(t is CelestialBody) || ((CelestialBody)t) != v.mainBody) o.Add("relative_speed", R(rel.magnitude, 1));
            return o;
        }

        // ---------------------------------------------------------------- controls

        static object Control(Args a)
        {
            Vessel v = Active();
            Autopilot ap = Autopilot.I;
            var done = new List<string>();

            if (a.Has("throttle"))
            {
                double t = a.Num("throttle");
                if (t > 1.0001) t /= 100.0;
                ap.SetThrottle(t);
                done.Add("throttle " + Math.Round(Math.Max(0, Math.Min(1, t)) * 100) + "%");
            }
            if (a.Has("sas"))
            {
                bool on = a.Bool("sas");
                if (on && ap.Mode != AttitudeMode.Off) ap.Mode = AttitudeMode.Off;
                v.ActionGroups.SetGroup(KSPActionGroup.SAS, on);
                done.Add("SAS " + (on ? "on" : "off"));
            }
            if (a.Has("sas_mode"))
            {
                string name = a.Str("sas_mode").Replace("_", "").Replace(" ", "");
                VesselAutopilot.AutopilotMode mode;
                try { mode = (VesselAutopilot.AutopilotMode)Enum.Parse(typeof(VesselAutopilot.AutopilotMode), name, true); }
                catch { throw new ApiException("Unknown sas_mode. Use: StabilityAssist, Prograde, Retrograde, Normal, Antinormal, RadialIn, RadialOut, Target, AntiTarget, Maneuver."); }
                if (ap.Mode != AttitudeMode.Off) ap.Mode = AttitudeMode.Off;
                v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                if (!v.Autopilot.CanSetMode(mode)) throw new ApiException("This vessel's pilot or probe core cannot hold " + mode + ". Use the 'attitude' command instead; it works on any controllable vessel.");
                v.Autopilot.SetMode(mode);
                done.Add("SAS mode " + mode);
            }
            Toggle(a, v, "rcs", KSPActionGroup.RCS, done);
            if (a.Has("gear"))
            {
                bool down = a.Bool("gear");
                int legs = Gear.Set(v, down);
                done.Add("gear " + (down ? "down" : "up") + (legs > 0 ? " (" + legs + " legs/wheels)" : ""));
            }
            Toggle(a, v, "lights", KSPActionGroup.Light, done);
            Toggle(a, v, "brakes", KSPActionGroup.Brakes, done);
            if (a.Bool("abort", false)) { v.ActionGroups.ToggleGroup(KSPActionGroup.Abort); done.Add("ABORT group fired"); }
            if (a.Has("action_group"))
            {
                int n = a.Int("action_group");
                if (n < 1 || n > 10) throw new ApiException("action_group must be 1 to 10.");
                v.ActionGroups.ToggleGroup((KSPActionGroup)Enum.Parse(typeof(KSPActionGroup), "Custom" + n.ToString("00")));
                done.Add("action group " + n + " toggled");
            }

            bool anyInput = false;
            foreach (string key in new[] { "pitch", "yaw", "roll", "x", "y", "z", "wheel_steer", "wheel_throttle" }) anyInput |= a.Has(key);
            if (anyInput)
            {
                ap.InPitch = Stick(a, "pitch"); ap.InYaw = Stick(a, "yaw"); ap.InRoll = Stick(a, "roll");
                ap.InX = Stick(a, "x"); ap.InY = Stick(a, "y"); ap.InZ = Stick(a, "z");
                ap.InWheelSteer = Stick(a, "wheel_steer"); ap.InWheelThrottle = Stick(a, "wheel_throttle");
                float seconds = (float)a.Num("seconds", 1.0);
                ap.HoldInput(seconds);
                done.Add("stick input held for " + seconds + " s");
            }

            if (a.Bool("stage", false))
            {
                if (TimeWarp.CurrentRate > 1.01f && TimeWarp.WarpMode == TimeWarp.Modes.HIGH) throw new ApiException("Cannot stage during on-rails time warp. Set warp to 1x first.");
                if (StageManager.Instance == null || !StageManager.CanSeparate) throw new ApiException("Staging is locked right now (cool-down after the last stage, or locked by the player). Try again in a second.");
                if (v.currentStage <= 0) throw new ApiException("No stages left to activate.");
                int firing = v.currentStage - 1;
                ap.Stager.Fire(ap, "commanded");
                done.Add("activated stage " + firing);
            }

            if (done.Count == 0) throw new ApiException("Nothing to do. Give at least one of: throttle, stage, sas, sas_mode, rcs, gear, lights, brakes, abort, action_group, pitch/yaw/roll/x/y/z.");
            host.Note(string.Join(", ", done.ToArray()));
            return new JObj().Add("done", done).Add("current_stage", v.currentStage).Add("throttle", R(v.ctrlState.mainThrottle, 2));
        }

        static float Stick(Args a, string key) => (float)Math.Max(-1, Math.Min(1, a.Num(key, 0)));

        static void Toggle(Args a, Vessel v, string key, KSPActionGroup group, List<string> done)
        {
            if (!a.Has(key)) return;
            bool on = a.Bool(key);
            v.ActionGroups.SetGroup(group, on);
            done.Add(key + " " + (on ? "on" : "off"));
        }

        static object Attitude(Args a)
        {
            Vessel v = Active();
            Autopilot ap = Autopilot.I;
            string modeName = a.Str("mode");
            if (string.IsNullOrEmpty(modeName)) modeName = a.Has("pitch") || a.Has("heading") ? "pitch_heading" : null;
            if (modeName == null) throw new ApiException("Give 'mode' (e.g. prograde, retrograde, maneuver, target, up, pitch_heading, off).");
            AttitudeMode mode = Autopilot.ParseMode(modeName);
            if (!v.IsControllable && mode != AttitudeMode.Off) throw new ApiException("The vessel is not controllable (no crew, no powered probe core, or no signal).");
            if (a.Has("max_turn_rate")) ap.MaxTurnRate = Math.Max(0.02, Math.Min(1.5, a.Num("max_turn_rate") * Math.PI / 180.0));
            ap.RollTarget = a.Has("roll") ? a.Num("roll") : (double?)null;

            if (mode == AttitudeMode.PitchHeading)
            {
                Guidance.Attitude(v, out double curPitch, out double curHeading, out double _);
                ap.SetPitchHeading(a.Num("pitch", curPitch), a.Num("heading", curHeading));
            }
            else
            {
                if ((mode == AttitudeMode.Target || mode == AttitudeMode.AntiTarget || mode == AttitudeMode.TargetPrograde || mode == AttitudeMode.TargetRetrograde) && v.targetObject == null)
                    throw new ApiException("No target is set. Use set_target first.");
                if (mode == AttitudeMode.Maneuver && (v.patchedConicSolver == null || v.patchedConicSolver.maneuverNodes.Count == 0))
                    throw new ApiException("There is no maneuver node to point at.");
                ap.SetAttitude(mode);
            }
            host.Note("attitude: " + Autopilot.ModeName(ap.Mode));
            return ap.Status();
        }

        static object AutoStage(Args a)
        {
            Active();
            Autopilot ap = Autopilot.I;
            ap.Stager.Enabled = a.Bool("on", true);
            if (a.Has("stop_at_stage")) ap.Stager.StopAtStage = Math.Max(0, a.Int("stop_at_stage"));
            host.Note("auto-staging " + (ap.Stager.Enabled ? "on" : "off"));
            return ap.Status();
        }

        static object Events(Args a)
        {
            int since = a.Int("since", Math.Max(0, Autopilot.I.Log.LastId - 30));
            return new JObj().Add("events", Autopilot.I.Log.Since(since, 100)).Add("last_event_id", Autopilot.I.Log.LastId);
        }

        static System.Collections.IEnumerator Revert(Call c)
        {
            Active();
            string to = (c.Args.Str("to", "launch") ?? "launch").ToLowerInvariant();
            Autopilot.I.Release("flight reverted", true);
            if (to == "launch")
            {
                if (!FlightDriver.CanRevertToPostInit) throw new ApiException("This flight cannot be reverted to launch (reverting is disabled, or the flight was resumed from a save).");
                host.Note("reverting flight to launch");
                FlightDriver.RevertToLaunch();
                yield return SceneWatch.WaitFor(GameScenes.FLIGHT, 180f);
                yield return Settle();
                c.Result = VesselSummary(FlightGlobals.ActiveVessel);
            }
            else if (to == "editor" || to == "vab" || to == "sph")
            {
                if (!FlightDriver.CanRevertToPrelaunch) throw new ApiException("This flight cannot be reverted to the editor.");
                EditorFacility facility = to == "sph" ? EditorFacility.SPH : to == "vab" ? EditorFacility.VAB : ShipConstruction.ShipType;
                if (facility == EditorFacility.None) facility = EditorFacility.VAB;
                host.Note("reverting flight to the editor");
                FlightDriver.RevertToPrelaunch(facility);
                yield return SceneWatch.WaitFor(GameScenes.EDITOR, 180f);
                c.Result = GameApi.Status(null);
            }
            else throw new ApiException("'to' must be launch or editor.");
        }

        static string SaveFile(Args a) => "ai_" + KSPUtil.SanitizeString(a.Str("name", "quicksave") ?? "quicksave", '_', true);

        static List<string> SavedFlights()
        {
            var names = new List<string>();
            string dir = KSPUtil.ApplicationRootPath + "saves/" + HighLogic.SaveFolder;
            if (System.IO.Directory.Exists(dir))
                foreach (string f in System.IO.Directory.GetFiles(dir, "ai_*.sfs")) names.Add(System.IO.Path.GetFileNameWithoutExtension(f).Substring(3));
            return names;
        }

        static System.Collections.IEnumerator QuickSave(Call c)
        {
            Active();
            // The game refuses to save for a moment after a burn or while a vessel is still rolling to a
            // stop, so give it a few seconds before giving up.
            float deadline = Time.realtimeSinceStartup + 12f;
            ClearToSaveStatus clear = FlightGlobals.ClearToSave();
            while (clear != ClearToSaveStatus.CLEAR && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                clear = FlightGlobals.ClearToSave();
            }
            if (clear != ClearToSaveStatus.CLEAR)
                throw new ApiException("KSP cannot save right now (" + clear + "). It refuses while under acceleration, moving fast in atmosphere or about to hit the ground.");
            string file = SaveFile(c.Args);
            GameEvents.onGameAboutToQuicksave.Fire();
            Game game = HighLogic.CurrentGame.Updated();
            game.startScene = GameScenes.FLIGHT;
            GamePersistence.SaveGame(game, file, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            game.startScene = GameScenes.SPACECENTER;
            host.Note("saved flight as '" + file.Substring(3) + "'");
            c.Result = new JObj().Add("saved", file.Substring(3)).Add("ut", R(Planetarium.GetUniversalTime(), 1));
        }

        static System.Collections.IEnumerator QuickLoad(Call c)
        {
            if (!HighLogic.LoadedSceneIsGame) throw new ApiException("No game is loaded.");
            string file = SaveFile(c.Args);
            ConfigNode node = GamePersistence.LoadSFSFile(file, HighLogic.SaveFolder);
            if (node == null) throw new ApiException("No saved flight named '" + file.Substring(3) + "'. Saved flights: " + (SavedFlights().Count > 0 ? string.Join(", ", SavedFlights().ToArray()) : "none"));
            Game game = GamePersistence.LoadGameCfg(node, file, true, false);
            if (game == null || game.flightState == null) throw new ApiException("That save could not be loaded.");
            Autopilot.I.Release("loading a saved flight", true);
            GamePersistence.UpdateScenarioModules(game);
            GameEvents.onGameStatePostLoad.Fire(node);
            host.Note("restoring saved flight '" + file.Substring(3) + "'");
            FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
            yield return SceneWatch.WaitFor(GameScenes.FLIGHT, 180f);
            yield return Settle();
            c.Result = VesselSummary(FlightGlobals.ActiveVessel);
        }

        static System.Collections.IEnumerator Recover(Call c)
        {
            Vessel v = Active();
            if (!v.LandedOrSplashed) throw new ApiException("The vessel must be landed or splashed down to be recovered (it is " + v.situation + ").");
            if (!v.IsRecoverable) throw new ApiException("This vessel cannot be recovered here; recovery only works on " + FlightGlobals.GetHomeBodyName() + ".");
            string name = v.GetDisplayName();
            Autopilot.I.Release("vessel recovered", true);
            host.Note("recovering '" + name + "'");
            GameEvents.OnVesselRecoveryRequested.Fire(v);
            yield return SceneWatch.WaitFor(GameScenes.SPACECENTER, 180f);
            yield return new WaitSec(1.0f);
            JObj status = GameApi.Status(null);
            status.Add("recovered", name);
            c.Result = status;
        }

        static object Release(Args a)
        {
            Autopilot.I.Release("released by the AI", a.Bool("cut_throttle", false));
            return Autopilot.I.Status();
        }
    }
}
