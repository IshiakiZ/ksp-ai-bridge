using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIBridge.Pilot
{
    public enum AttitudeMode
    {
        Off, Prograde, Retrograde, Normal, AntiNormal, RadialOut, RadialIn,
        SurfacePrograde, SurfaceRetrograde, Up, Target, AntiTarget,
        TargetPrograde, TargetRetrograde, Maneuver, PitchHeading, Fixed
    }

    /// <summary>
    /// Everything that has to react faster than an AI can think: holding an attitude, holding a throttle,
    /// staging when engines burn out, and running multi-minute flight programs. It runs once per physics
    /// frame and writes its commands through the vessel's fly-by-wire hook, the same way stock SAS does.
    /// </summary>
    public sealed class Autopilot
    {
        public static Autopilot I { get; private set; } = new Autopilot();

        // Static entry points used by the host object.
        public static bool Engaged => I.IsEngaged;
        public static void FixedTick() => I.Tick();
        public static string Describe() => I.DescribeState();
        /// <summary>Hand the vessel back to the player exactly as it is, throttle included.</summary>
        public static void Disengage(string reason) => I.Release(reason, false);
        public static void Shutdown() { I.Release("AI Bridge unloaded", true); EvaControl.ReleaseAll(); Api.VoiceApi.Shutdown(); I.Unbind(); I.Log.Unhook(); }

        public Vessel Vessel { get; private set; }
        public readonly FlightLog Log = new FlightLog();
        public readonly AutoStager Stager = new AutoStager();
        /// <summary>The ship's-computer commentary read out by the voice edition.</summary>
        public readonly Narrator Narrator = new Narrator();
        /// <summary>Aircraft heading/altitude/speed hold; outlives the program that engaged it.</summary>
        public readonly AircraftHold Plane = new AircraftHold();

        // --- attitude target ---
        public AttitudeMode Mode = AttitudeMode.Off;
        public double Pitch, Heading;
        public Vector3d FixedDirection;
        public double? RollTarget;
        /// <summary>
        /// Roll by reference instead of by bank angle: turn about the nose until RollFrom (a direction fixed
        /// to this vessel, in world axes) lines up with RollToward. Used where there is no horizon, as in docking.
        /// </summary>
        public Vector3d? RollFrom, RollToward;
        public double AngleError { get; private set; } = double.NaN;
        public double MaxTurnRate = 0.30;

        // --- throttle ---
        public double? Throttle;

        // --- wheel controls held by a program (rovers, taxiing aircraft) ---
        public float? WheelThrottle, WheelSteer;
        /// <summary>Rudder command added on top of the attitude controller (runway steering).</summary>
        public float? YawOverride;

        /// <summary>RCS translation held by a program, as an acceleration direction in world axes (length 0..1 = thruster effort).</summary>
        public Vector3d? Translation;

        // --- raw stick input, held until an expiry time ---
        public float InPitch, InYaw, InRoll, InX, InY, InZ, InWheelSteer, InWheelThrottle;
        float inputUntil = -1f;

        /// <summary>Set while a program is being stopped for the player, so it leaves the throttle where it is.</summary>
        public bool HandingOver { get; private set; }

        // --- high-level program ---
        public Program Program { get; private set; }
        public string LastProgramSummary;
        public bool LastProgramFailed { get; private set; }
        public int ProgramsFinished { get; private set; }

        /// <summary>Flight recorder: a few samples a second of attitude and the controls being applied, for tuning and post-mortems.</summary>
        public readonly List<string> Trace = new List<string>();
        public const string TraceColumns = "t,state,speed,alt,vs,pitch,bank,heading,track,aoa,c_pitch,c_roll,c_yaw,c_wheel,throttle,phase";
        double lastTraceUT = -1;

        /// <summary>Let the attitude controller build up a holding ("trim") stick position; switched on for aircraft.</summary>
        public bool UseTrim;
        const float TrimTime = 1.0f;
        Vector3 trim;

        Vector3 torque = Vector3.one;
        int torqueAge = 1000;
        Vector3 lastCmd;
        FlightInputCallback hook;

        public bool IsEngaged => Mode != AttitudeMode.Off || Throttle.HasValue || Program != null || Stager.Enabled || Plane.Enabled || Translation.HasValue || Time.realtimeSinceStartup < inputUntil;

        // ------------------------------------------------------------------ lifecycle

        void Bind(Vessel v)
        {
            Vessel = v;
            if (v == null) return;
            hook = OnFlyByWire;
            v.OnFlyByWire += hook;
            torqueAge = 1000;
            Log.Hook();
        }

        void Unbind()
        {
            if (Vessel != null && hook != null)
            {
                try { Vessel.OnFlyByWire -= hook; } catch { }
            }
            Vessel = null;
            hook = null;
        }

        void Tick()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready || FlightGlobals.ActiveVessel == null)
            {
                if (Vessel != null || IsEngaged)
                {
                    if (IsEngaged) Release("left the flight scene", false);
                    Unbind();
                }
                return;
            }
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != Vessel)
            {
                bool hadVessel = Vessel != null;
                Unbind();
                Bind(active);
                if (hadVessel && IsEngaged)
                {
                    // Staging can hand focus to a new vessel object, and so can docking; anything else means
                    // the player switched ships.
                    if (Program != null && Program.SurvivesVesselChange(active)) Log.Add("active vessel is now '" + active.GetDisplayName() + "'");
                    else if (Stager.SecondsSinceStaging < 3.0) Log.Add("active vessel is now '" + active.GetDisplayName() + "' after staging");
                    else Release("active vessel changed to '" + active.GetDisplayName() + "'", false);
                }
            }

            if (Vessel.packed) return;

            if (Program != null)
            {
                try
                {
                    Program.Tick(this, Vessel);
                }
                catch (Exception ex)
                {
                    BridgeHost.LogWarn("program " + Program.Name + " failed: " + ex);
                    Program.Fail("internal error: " + ex.Message);
                }
                if (Program != null && Program.Done) EndProgram();
            }
            if (Plane.Enabled)
            {
                try { Plane.Tick(this, Vessel); }
                catch (Exception ex) { BridgeHost.LogWarn("aircraft hold failed: " + ex); Plane.Disengage(); }
            }
            Stager.Tick(this, Vessel);

            if (Throttle.HasValue && FlightInputHandler.state != null)
                FlightInputHandler.state.mainThrottle = (float)Throttle.Value;
        }

        // ------------------------------------------------------------------ commands

        public void SetAttitude(AttitudeMode mode)
        {
            RequireVessel();
            if (mode != AttitudeMode.Off && Mode == AttitudeMode.Off)
            {
                // Stock SAS would fight the bridge's own controller.
                Vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            }
            if (mode == AttitudeMode.Off && Mode != AttitudeMode.Off) HandBackToSas();
            if (mode == Mode) return;
            if (mode == AttitudeMode.Off || Mode == AttitudeMode.Off) trim = Vector3.zero;
            Mode = mode;
            AngleError = double.NaN;
            lastCmd = Vector3.zero;
        }

        public void SetPitchHeading(double pitch, double heading)
        {
            Pitch = Math.Max(-90, Math.Min(90, pitch));
            Heading = ((heading % 360) + 360) % 360;
            SetAttitude(AttitudeMode.PitchHeading);
        }

        public void SetThrottle(double? value)
        {
            RequireVessel();
            Throttle = value.HasValue ? Math.Max(0, Math.Min(1, value.Value)) : (double?)null;
            if (value.HasValue && FlightInputHandler.state != null) FlightInputHandler.state.mainThrottle = (float)Throttle.Value;
        }

        public void HoldInput(float seconds)
        {
            RequireVessel();
            inputUntil = Time.realtimeSinceStartup + Mathf.Clamp(seconds, 0.05f, 120f);
        }

        public void ClearInput()
        {
            InPitch = InYaw = InRoll = InX = InY = InZ = InWheelSteer = InWheelThrottle = 0f;
            inputUntil = -1f;
        }

        public void Start(Program program)
        {
            RequireVessel();
            if (Program != null)
            {
                Program.Abort("replaced by " + program.Name);
                EndProgram();
            }
            Program = program;
            program.StartedUT = Planetarium.GetUniversalTime();
            Log.Add("program started: " + program.Name);
            Narrator.OnProgramStart(program);
            BridgeHost.Instance?.Note("autopilot: " + program.Name);
            Announce("AI autopilot: " + program.Name);
        }

        /// <summary>Stop the running program now (its clean-up included), leaving holds such as the aircraft hold engaged.</summary>
        public void AbortProgram(string reason)
        {
            if (Program == null) return;
            Program.Abort(reason);
            EndProgram();
        }

        void EndProgram()
        {
            Program p = Program;
            Program = null;
            if (p == null) return;
            try { p.OnEnd(this); } catch (Exception ex) { BridgeHost.LogWarn("program cleanup failed: " + ex.Message); }
            // Being replaced by the next program is not a failure of this one, and should not read like one.
            bool replaced = p.Failed && p.Result.StartsWith("aborted (replaced by ", StringComparison.Ordinal);
            LastProgramSummary = replaced ? p.Name + " stopped: " + p.Result.Substring(9).TrimEnd(')')
                               : p.Name + (p.Failed ? " FAILED: " : " finished: ") + p.Result;
            LastProgramFailed = p.Failed;
            ProgramsFinished++;
            Log.Add("program " + LastProgramSummary);
            Narrator.OnProgramEnd(p);
            BridgeHost.Instance?.Note("autopilot: " + LastProgramSummary);
            Announce("AI autopilot: " + (p.Failed ? "<color=#ff8060>" + p.Name + " failed</color>" : p.Name + " complete"));
        }

        /// <summary>Drop every command the bridge is holding and give the vessel back to the player.</summary>
        public void Release(string reason, bool cutThrottle)
        {
            bool was = IsEngaged;
            if (Program != null)
            {
                HandingOver = !cutThrottle;
                Program.Abort(reason);
                EndProgram();
                HandingOver = false;
            }
            Stager.Enabled = false;
            Plane.Disengage();
            UseTrim = false;
            RollTarget = null;
            RollFrom = RollToward = null;
            WheelThrottle = WheelSteer = YawOverride = null;
            Translation = null;
            ClearInput();
            if (Throttle.HasValue)
            {
                if (cutThrottle && FlightInputHandler.state != null) FlightInputHandler.state.mainThrottle = 0f;
                Throttle = null;
            }
            if (Mode != AttitudeMode.Off)
            {
                Mode = AttitudeMode.Off;
                HandBackToSas();
            }
            if (was) Log.Add("autopilot released: " + reason);
        }

        static void Announce(string text)
        {
            try { ScreenMessages.PostScreenMessage(text, 5f, ScreenMessageStyle.UPPER_CENTER); } catch { }
        }

        void HandBackToSas()
        {
            if (Vessel == null) return;
            try
            {
                if (Vessel.Autopilot != null && Vessel.Autopilot.CanSetMode(VesselAutopilot.AutopilotMode.StabilityAssist))
                {
                    Vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                    Vessel.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
                }
            }
            catch { }
        }

        void RequireVessel()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || FlightGlobals.ActiveVessel == null)
                throw new ApiException("No vessel is being flown. Launch one or switch to one first.");
            if (Vessel == null) Bind(FlightGlobals.ActiveVessel);
        }

        // ------------------------------------------------------------------ fly-by-wire

        void OnFlyByWire(FlightCtrlState s)
        {
            if (Vessel == null) return;
            if (Throttle.HasValue) s.mainThrottle = (float)Throttle.Value;
            if (Mode != AttitudeMode.Off) DriveAttitude(s);
            if (WheelThrottle.HasValue) s.wheelThrottle = WheelThrottle.Value;
            if (WheelSteer.HasValue) s.wheelSteer = WheelSteer.Value;
            if (YawOverride.HasValue) s.yaw = YawOverride.Value;
            if (Translation.HasValue)
            {
                // RCS thrusters fire along the stick vector and so push the vessel the other way.
                Vector3 local = Vessel.ReferenceTransform.InverseTransformDirection(-(Vector3)Translation.Value);
                s.X = Mathf.Clamp(local.x, -1f, 1f);
                s.Z = Mathf.Clamp(local.y, -1f, 1f);
                s.Y = Mathf.Clamp(local.z, -1f, 1f);
            }
            if (Time.realtimeSinceStartup < inputUntil)
            {
                if (InPitch != 0f) s.pitch = InPitch;
                if (InYaw != 0f) s.yaw = InYaw;
                if (InRoll != 0f) s.roll = InRoll;
                if (InX != 0f) s.X = InX;
                if (InY != 0f) s.Y = InY;
                if (InZ != 0f) s.Z = InZ;
                if (InWheelSteer != 0f) s.wheelSteer = InWheelSteer;
                if (InWheelThrottle != 0f) s.wheelThrottle = InWheelThrottle;
            }
            if (IsEngaged) Record(s);
        }

        void Record(FlightCtrlState s)
        {
            double ut = Planetarium.GetUniversalTime();
            if (ut - lastTraceUT < 0.2) return;
            lastTraceUT = ut;
            Vessel v = Vessel;
            double bank = Aero.Bank(v);
            Trace.Add(string.Join(",", new[]
            {
                Math.Round(v.missionTime, 1).ToString(), v.Landed ? "L" : v.Splashed ? "S" : "F", Math.Round(v.srfSpeed, 1).ToString(), Math.Round(v.altitude, 1).ToString(), Math.Round(v.verticalSpeed, 1).ToString(),
                Math.Round(Aero.Pitch(v), 1).ToString(), double.IsNaN(bank) ? "" : Math.Round(bank, 1).ToString(), Math.Round(Aero.NoseHeading(v), 1).ToString(), Math.Round(Aero.Track(v), 1).ToString(), Math.Round(Aero.AngleOfAttack(v), 1).ToString(),
                Math.Round(s.pitch, 2).ToString(), Math.Round(s.roll, 2).ToString(), Math.Round(s.yaw, 2).ToString(), Math.Round(s.wheelSteer, 2).ToString(), Math.Round(s.mainThrottle, 2).ToString(),
                Program != null ? Program.Phase.Replace(',', ';') : Plane.Enabled ? "hold" : ""
            }));
            if (Trace.Count > 6000) Trace.RemoveRange(0, Trace.Count - 6000);
        }

        void DriveAttitude(FlightCtrlState s)
        {
            Vector3d wanted = Guidance.Direction(Vessel, this);
            if (wanted.sqrMagnitude < 1e-12) { AngleError = double.NaN; return; }

            Transform t = Vessel.ReferenceTransform;
            Vector3 nose = t.up;
            Vector3 target = ((Vector3)wanted).normalized;
            float angle = Vector3.Angle(nose, target) * Mathf.Deg2Rad;
            AngleError = angle * Mathf.Rad2Deg;
            Vector3 axis = Vector3.Cross(nose, target);
            if (axis.sqrMagnitude < 1e-10f) axis = angle > 1f ? t.right : Vector3.zero;
            else axis.Normalize();
            Vector3 err = t.InverseTransformDirection(axis) * angle;

            if (++torqueAge > 10) { torque = AvailableTorque(Vessel); torqueAge = 0; }
            Vector3 moi = Vessel.MOI;
            Vector3 w = Vessel.angularVelocity;
            float dt = Mathf.Max(TimeWarp.fixedDeltaTime, 0.02f);

            // Roll is held as a bank angle against the horizon (positive = right wing down). Rolling right is
            // a negative turn about the nose axis, hence the sign.
            float rollError = 0f;
            if (RollFrom.HasValue && RollToward.HasValue)
            {
                Vector3 from = Vector3.ProjectOnPlane((Vector3)RollFrom.Value, nose), to = Vector3.ProjectOnPlane((Vector3)RollToward.Value, nose);
                if (from.sqrMagnitude > 0.01f && to.sqrMagnitude > 0.01f) rollError = Vector3.SignedAngle(from, to, nose) * Mathf.Deg2Rad;
            }
            else if (RollTarget.HasValue)
            {
                double bank = Aero.Bank(Vessel);
                if (!double.IsNaN(bank)) rollError = -(float)(Aero.Wrap(RollTarget.Value - bank) * Aero.Deg2Rad);
            }

            Vector3 cmd;
            cmd.x = AxisCommand(err.x, w.x, torque.x, moi.x, dt);
            cmd.y = AxisCommand(rollError, w.y, torque.y, moi.y, dt);
            cmd.z = AxisCommand(err.z, w.z, torque.z, moi.z, dt);
            if (UseTrim)
            {
                // Aircraft carry steady loads (the wing's pitching moment, the weight on the nose wheel at
                // rotation) that a controller reacting only to error never quite overcomes, so remember
                // the stick position that holds them.
                for (int n = 0; n < 3; n++)
                {
                    float total = cmd[n] + trim[n];
                    if (Mathf.Abs(total) < 1f || Mathf.Sign(cmd[n]) != Mathf.Sign(total))
                        trim[n] = Mathf.Clamp(trim[n] + cmd[n] * dt / TrimTime, -1f, 1f);
                    cmd[n] = Mathf.Clamp(cmd[n] + trim[n], -1f, 1f);
                }
            }
            else trim = Vector3.zero;
            cmd = Vector3.Lerp(lastCmd, cmd, 0.6f);
            lastCmd = cmd;
            s.pitch = cmd.x;
            s.roll = cmd.y;
            s.yaw = cmd.z;
        }

        /// <summary>
        /// One axis of the attitude controller. Pick a turn rate that can still be stopped before reaching
        /// the target given the torque actually available, then push toward that rate.
        /// </summary>
        float AxisCommand(float error, float rate, float torqueAvail, float inertia, float dt)
        {
            float accel = Mathf.Max(torqueAvail / Mathf.Max(inertia, 1e-3f), 1e-3f);
            float mag = Mathf.Abs(error);
            float wanted = Mathf.Min(mag * 1.5f, 0.7f * Mathf.Sqrt(2f * accel * mag));
            wanted = Mathf.Min(wanted, (float)MaxTurnRate) * Mathf.Sign(error);
            float response = Mathf.Max(0.25f, 2.5f * dt);
            float alpha = (wanted - rate) / response;
            // Reaction wheels and gimbals apply torque of the opposite sign to the stick value.
            return -Mathf.Clamp(alpha / accel, -1f, 1f);
        }

        public static Vector3 AvailableTorque(Vessel v)
        {
            Vector3 total = Vector3.zero;
            foreach (Part p in v.parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    if (m is ModuleControlSurface surface)
                    {
                        // The stock estimate for control surfaces multiplies the torque by the lever arm a
                        // second time, so work it out from the lift each deflection can add.
                        try
                        {
                            if (p.Rigidbody == null || p.ShieldedFromAirstream) continue;
                            Vector3 arm = p.Rigidbody.worldCenterOfMass - v.CurrentCoM;
                            Vector3 a = v.ReferenceTransform.InverseTransformDirection(Vector3.Cross(arm, surface.GetPotentialLift(true)));
                            Vector3 b = v.ReferenceTransform.InverseTransformDirection(Vector3.Cross(arm, surface.GetPotentialLift(false)));
                            if (!surface.ignorePitch) total.x += Mathf.Max(Mathf.Abs(a.x), Mathf.Abs(b.x));
                            if (!surface.ignoreRoll) total.y += Mathf.Max(Mathf.Abs(a.y), Mathf.Abs(b.y));
                            if (!surface.ignoreYaw) total.z += Mathf.Max(Mathf.Abs(a.z), Mathf.Abs(b.z));
                        }
                        catch { }
                        continue;
                    }
                    if (!(m is ITorqueProvider tp)) continue;
                    try
                    {
                        tp.GetPotentialTorque(out Vector3 pos, out Vector3 neg);
                        total.x += (Mathf.Abs(pos.x) + Mathf.Abs(neg.x)) * 0.5f;
                        total.y += (Mathf.Abs(pos.y) + Mathf.Abs(neg.y)) * 0.5f;
                        total.z += (Mathf.Abs(pos.z) + Mathf.Abs(neg.z)) * 0.5f;
                    }
                    catch { }
                }
            }
            return total;
        }

        // ------------------------------------------------------------------ status

        string DescribeState()
        {
            if (Program != null) return Program.Name + ": " + Program.Phase;
            if (Plane.Enabled) return Plane.Describe();
            if (Mode != AttitudeMode.Off) return "holding " + ModeName(Mode) + (Throttle.HasValue ? ", throttle " + Math.Round(Throttle.Value * 100) + "%" : "");
            if (Throttle.HasValue) return "throttle " + Math.Round(Throttle.Value * 100) + "%";
            if (Stager.Enabled) return "auto-staging";
            return null;
        }

        public JObj Status()
        {
            var o = new JObj();
            o.Add("engaged", IsEngaged);
            if (Program != null)
            {
                o.Add("program", Program.Name);
                o.Add("phase", Program.Phase);
                o.Add("running_s", Math.Round(Planetarium.GetUniversalTime() - Program.StartedUT, 1));
                JObj detail = Program.Detail();
                if (detail != null && detail.Count > 0) o.Add("detail", detail);
            }
            else if (LastProgramSummary != null) o.Add("last_program", LastProgramSummary);
            o.Add("attitude", ModeName(Mode) + (Mode == AttitudeMode.PitchHeading ? " (pitch " + Math.Round(Pitch, 1) + ", heading " + Math.Round(Heading, 1) + ")" : ""));
            if (Mode != AttitudeMode.Off && !double.IsNaN(AngleError)) o.Add("pointing_error_deg", Math.Round(AngleError, 1));
            if (Throttle.HasValue) o.Add("throttle_hold", Math.Round(Throttle.Value, 3));
            if (Stager.Enabled) o.Add("autostage", "on, stops before stage " + Stager.StopAtStage);
            if (Plane.Enabled && Vessel != null) o.Add("aircraft_hold", Plane.Status(Vessel));
            return o;
        }

        public static string ModeName(AttitudeMode m)
        {
            switch (m)
            {
                case AttitudeMode.Off: return "off";
                case AttitudeMode.AntiNormal: return "anti_normal";
                case AttitudeMode.RadialOut: return "radial_out";
                case AttitudeMode.RadialIn: return "radial_in";
                case AttitudeMode.SurfacePrograde: return "surface_prograde";
                case AttitudeMode.SurfaceRetrograde: return "surface_retrograde";
                case AttitudeMode.AntiTarget: return "anti_target";
                case AttitudeMode.TargetPrograde: return "target_prograde";
                case AttitudeMode.TargetRetrograde: return "target_retrograde";
                case AttitudeMode.PitchHeading: return "pitch_heading";
                default: return m.ToString().ToLowerInvariant();
            }
        }

        public static AttitudeMode ParseMode(string name)
        {
            string n = (name ?? "").ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            foreach (AttitudeMode m in Enum.GetValues(typeof(AttitudeMode)))
                if (ModeName(m) == n) return m;
            switch (n)
            {
                case "antinormal": return AttitudeMode.AntiNormal;
                case "radial": case "radial_plus": return AttitudeMode.RadialOut;
                case "anti_radial": case "radial_minus": return AttitudeMode.RadialIn;
                case "node": case "maneuver_node": return AttitudeMode.Maneuver;
                case "srf_prograde": return AttitudeMode.SurfacePrograde;
                case "srf_retrograde": return AttitudeMode.SurfaceRetrograde;
                case "none": case "release": return AttitudeMode.Off;
            }
            throw new ApiException("Unknown attitude mode '" + name + "'. Use one of: off, prograde, retrograde, normal, anti_normal, radial_out, radial_in, surface_prograde, surface_retrograde, up, target, anti_target, target_prograde, target_retrograde, maneuver, pitch_heading.");
        }
    }

    /// <summary>Base class for a multi-minute flight routine stepped every physics frame.</summary>
    public abstract class Program
    {
        public abstract string Name { get; }
        public string Phase = "starting";
        public bool Done, Failed;
        public string Result = "";
        public double StartedUT;

        public abstract void Tick(Autopilot ap, Vessel v);
        public virtual JObj Detail() => null;
        public virtual void OnEnd(Autopilot ap) { }
        /// <summary>Called when the game makes a different vessel the active one; return true to carry on (docking merges two vessels into one).</summary>
        public virtual bool SurvivesVesselChange(Vessel now) => false;

        public void Finish(string message) { Done = true; Result = message; }
        public void Fail(string message) { Done = true; Failed = true; Result = message; }
        public void Abort(string reason) { if (!Done) { Done = true; Failed = true; Result = "aborted (" + reason + ")"; } }
    }
}
