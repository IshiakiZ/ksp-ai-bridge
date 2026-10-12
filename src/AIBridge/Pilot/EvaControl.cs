using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Puppet strings for a kerbal on EVA. The game reads the keyboard at the start of every physics step
    /// and then runs the kerbal's state machine; this slips the bridge's own "keys" in between by wrapping
    /// each state's update, so walking, the jetpack and ladders respond exactly as they do for a player.
    /// </summary>
    public sealed class EvaControl
    {
        public readonly KerbalEVA Eva;
        /// <summary>World position to walk (or swim, or bound) toward; null to stand still.</summary>
        public Vector3d? WalkTarget;
        /// <summary>Jetpack thrust direction in world axes; zero for none.</summary>
        public Vector3d Jet;
        /// <summary>+1 climbs the ladder being held, -1 goes down it.</summary>
        public int Climb;

        readonly Dictionary<KFSMState, KFSMCallback> originals = new Dictionary<KFSMState, KFSMCallback>();
        static readonly BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo packField = typeof(KerbalEVA).GetField("packTgtRPos", hidden);
        static readonly FieldInfo ladderField = typeof(KerbalEVA).GetField("ladderTgtRPos", hidden);
        static readonly FieldInfo airlockField = typeof(KerbalEVA).GetField("currentAirlockPart", hidden);
        static readonly FieldInfo ladderTriggers = typeof(KerbalEVA).GetField("currentLadderTriggers", hidden);
        static EvaControl current;

        EvaControl(KerbalEVA eva) { Eva = eva; }

        public static EvaControl For(KerbalEVA eva)
        {
            if (current != null && current.Eva == eva) return current;
            if (current != null) current.Remove();
            current = new EvaControl(eva);
            current.Install();
            return current;
        }

        public static void ReleaseAll()
        {
            if (current != null) current.Remove();
            current = null;
        }

        void Install()
        {
            foreach (FieldInfo f in typeof(KerbalEVA).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (f.FieldType != typeof(KFSMState)) continue;
                var state = f.GetValue(Eva) as KFSMState;
                if (state == null || originals.ContainsKey(state)) continue;
                KFSMCallback original = state.OnFixedUpdate;
                originals[state] = original;
                state.OnFixedUpdate = () => { Inject(); original?.Invoke(); };
            }
        }

        void Remove()
        {
            foreach (KeyValuePair<KFSMState, KFSMCallback> kv in originals) kv.Key.OnFixedUpdate = kv.Value;
            originals.Clear();
            Stop();
        }

        public void Stop()
        {
            WalkTarget = null;
            Jet = Vector3d.zero;
            Climb = 0;
        }

        void Inject()
        {
            if (Eva == null) return;
            try
            {
                if (WalkTarget.HasValue) Eva.SetWaypoint((Vector3)WalkTarget.Value);
                if (Jet.sqrMagnitude > 1e-6 && packField != null) packField.SetValue(Eva, (Vector3)Jet.normalized);
                if (Climb != 0 && ladderField != null) ladderField.SetValue(Eva, Eva.transform.up * Climb);
            }
            catch { }
        }

        /// <summary>The part whose hatch the kerbal is within reach of, if any.</summary>
        public Part HatchInReach => airlockField != null ? airlockField.GetValue(Eva) as Part : null;

        public bool LadderInReach
        {
            get
            {
                var list = ladderTriggers != null ? ladderTriggers.GetValue(Eva) as List<Collider> : null;
                return list != null && list.Count > 0;
            }
        }

        public string State => Eva.fsm != null && Eva.fsm.Started ? Eva.fsm.currentStateName : "starting";

        public bool In(params KFSMState[] states)
        {
            foreach (KFSMState s in states) if (Eva.fsm.CurrentState == s) return true;
            return false;
        }

        public bool OnGroundAndReady => In(Eva.st_idle_gr, Eva.st_idle_b_gr, Eva.st_walk_acd, Eva.st_walk_fps, Eva.st_run_acd, Eva.st_run_fps, Eva.st_bound_gr_acd, Eva.st_bound_gr_fps, Eva.st_bound_fl, Eva.st_swim_idle, Eva.st_swim_fwd, Eva.st_heading_acquire, Eva.st_land);
    }

    /// <summary>A kerbal going somewhere on foot or by jetpack, and optionally climbing back aboard at the end.</summary>
    public sealed class EvaProgram : Program
    {
        public enum Kind { Walk, Fly, Board }

        public override string Name => Mode == Kind.Board ? "boarding " + Label : (Mode == Kind.Fly ? "jetpack to " : "walking to ") + Label;

        public Kind Mode;
        public KerbalEVA Eva;
        public string Label = "the target";
        public double Lat, Lon, Radius = 1.5, MaxSpeed = 1.5;
        public bool Run = true;
        /// <summary>For flying and boarding: the part to go to (its hatch when it has one).</summary>
        public Part Goal;
        /// <summary>For flying: how far out from the goal to stop.</summary>
        public double StandOff = 0.5;

        EvaControl ctl;
        bool started, boarded, packWasOn;
        float walkSpeedWas;
        double startUT, bestDistance = double.MaxValue, bestUT, duty, distance, arrivedSince = -1, climbSince = -1, endSince = -1;
        string shipName = "";

        public override bool SurvivesVesselChange(Vessel now)
        {
            // Boarding swaps the kerbal for the ship as the vessel being flown.
            if (Mode == Kind.Board && Goal != null && Goal.vessel == now) { boarded = true; return true; }
            return false;
        }

        void Begin(Vessel v)
        {
            started = true;
            startUT = bestUT = Planetarium.GetUniversalTime();
            ctl = EvaControl.For(Eva);
            walkSpeedWas = Eva.walkSpeed;
            packWasOn = Eva.JetpackDeployed;
            if (Goal != null && Goal.vessel != null) shipName = Goal.vessel.GetDisplayName();
        }

        Vector3d GoalPoint(out Vector3d outward)
        {
            if (Goal.airlock != null)
            {
                // The hatch marker's axis runs through the hull; which end is "out" differs between parts, so
                // take the end that points away from the middle of the part.
                Vector3d axis = ((Vector3d)Goal.airlock.forward).normalized;
                Vector3d fromCentre = (Vector3d)Goal.airlock.position - (Vector3d)Goal.transform.TransformPoint(Goal.CoMOffset);
                outward = Vector3d.Dot(axis, fromCentre) >= 0 ? axis : -axis;
                if (fromCentre.magnitude > 0.2 && Math.Abs(Vector3d.Dot(axis, fromCentre.normalized)) < 0.3) outward = fromCentre.normalized;
                return (Vector3d)Goal.airlock.position;
            }
            outward = ((Vector3d)Eva.transform.position - (Vector3d)Goal.transform.position).normalized;
            return (Vector3d)Goal.transform.position + outward * StandOff;
        }

        public override void Tick(Autopilot ap, Vessel v)
        {
            double ut = Planetarium.GetUniversalTime();
            if (boarded || (Mode == Kind.Board && Eva == null)) { Finish("aboard " + shipName); return; }
            if (Eva == null || Eva.vessel != v) { Fail("the kerbal is no longer being controlled"); return; }
            if (!started) Begin(v);
            if (Eva.isRagdoll || ctl.In(Eva.st_ragdoll, Eva.st_recover))
            {
                // A fallen kerbal only gets up when asked to move, so keep asking.
                ctl.Jet = Vector3d.zero;
                ctl.Climb = 0;
                ctl.WalkTarget = (Vector3d)Eva.transform.position + (Vector3d)Eva.transform.forward * 3.0;
                Phase = "getting back up";
                bestUT = ut;
                return;
            }
            if (ut - startUT > 1800) { Fail("gave up after 30 minutes, " + Math.Round(distance, 1) + " m away"); return; }

            switch (Mode)
            {
                case Kind.Walk: Walk(v, ut); break;
                case Kind.Fly: Fly(v, ut, false); break;
                case Kind.Board: Board(v, ut); break;
            }
        }

        void Walk(Vessel v, double ut)
        {
            CelestialBody body = v.mainBody;
            if (Eva.OnALadder) { Fail("the kerbal is holding a ladder; let go first"); return; }
            Vector3d up = Guidance.Up(v);
            Vector3d spot = body.GetWorldSurfacePosition(Lat, Lon, v.altitude);
            Vector3d flat = Vector3d.Exclude(up, spot - v.CoMD);
            distance = flat.magnitude;
            Phase = Math.Round(distance, 1) + " m to go (" + ctl.State + ")";
            if (distance <= Radius)
            {
                ctl.Stop();
                if (arrivedSince < 0) arrivedSince = ut;
                if (ut - arrivedSince > 0.8 || v.srfSpeed < 0.2) Finish("arrived " + Math.Round(distance, 1) + " m from " + Label + " at latitude " + Math.Round(v.latitude, 5) + ", longitude " + Math.Round(v.longitude, 5));
                return;
            }
            arrivedSince = -1;
            if (!v.LandedOrSplashed && !ctl.OnGroundAndReady) { ctl.Stop(); Phase = "in the air, " + Math.Round(distance, 1) + " m to go"; bestUT = ut; return; }
            Eva.walkSpeed = Run ? Eva.runSpeed : walkSpeedWas;
            ctl.WalkTarget = v.CoMD + flat.normalized * Math.Max(3.0, distance);
            if (distance < bestDistance - 0.5) { bestDistance = distance; bestUT = ut; }
            if (ut - bestUT > 25) Fail("stuck " + Math.Round(distance, 1) + " m from " + Label + " (something is in the way, or the slope is too steep)");
        }

        /// <summary>Jetpack flight to the goal. Returns true when in position.</summary>
        bool Fly(Vessel v, double ut, bool forBoarding)
        {
            if (Goal == null || Goal.vessel == null) { Fail("the target is gone"); return false; }
            if (!Eva.HasJetpack) { Fail("this kerbal has no jetpack"); return false; }
            if (Eva.Fuel <= 0.0001) { Fail("the jetpack is out of propellant, " + Math.Round(distance, 1) + " m from " + Label); return false; }
            if (Eva.OnALadder) { Eva.fsm.RunEvent(Eva.On_ladderLetGo); return false; }
            if (!Eva.JetpackDeployed)
            {
                if (ctl.In(Eva.st_idle_gr, Eva.st_idle_b_gr, Eva.st_idle_fl)) Eva.ToggleJetpack();
                Phase = "starting the jetpack";
                return false;
            }

            Vector3d goal = GoalPoint(out Vector3d outward);
            Vector3d here = Eva.transform.position;
            Vector3d gap = goal - here;
            distance = gap.magnitude;
            bool surface = v.mainBody != null && (v.LandedOrSplashed || v.radarAltitude < 300 || Goal.vessel.LandedOrSplashed);
            // Speed relative to the hatch itself, so that a ship turning slowly is followed round.
            Vector3d velocity;
            Rigidbody mine = Eva.part.Rigidbody, theirs = Goal.Rigidbody;
            if (mine != null && theirs != null) velocity = (Vector3d)mine.velocity - (Vector3d)theirs.GetPointVelocity((Vector3)goal);
            else velocity = surface ? v.srf_velocity - Goal.vessel.srf_velocity : v.obt_velocity - Goal.vessel.obt_velocity;
            double power = Eva.linPower / Math.Max(0.01, v.totalMass);
            Vector3d up = Guidance.Up(v);
            double gravity = surface ? Guidance.LocalGravity(v) : 0;
            if (surface && power < gravity * 1.15) { Fail("the jetpack cannot lift a kerbal in " + v.mainBody.bodyName + "'s gravity; use a ladder"); return false; }

            Phase = Math.Round(distance, 1) + " m from " + Label + " at " + Math.Round(velocity.magnitude, 2) + " m/s";
            Part reach = ctl.HatchInReach;
            bool atHatch = reach != null && reach.vessel == Goal.vessel;
            if (!forBoarding && ((distance <= Math.Max(0.3, Radius) && velocity.magnitude < 0.15) || (atHatch && velocity.magnitude < 0.3)))
            {
                ctl.Stop();
                Finish((atHatch ? "at the hatch of " + Goal.vessel.GetDisplayName() : "holding " + Math.Round(distance, 1) + " m from " + Label) + " with " + Math.Round(Eva.Fuel, 2) + " units of jetpack propellant left");
                return true;
            }

            // A hatch can only be reached from in front of it. Coming from the side or from behind, swing
            // wide round the hull to a point out along the hatch's line, then slide onto that line and go in.
            Vector3d aim = gap;
            if (Goal.airlock != null)
            {
                Vector3d rel = here - goal;
                double front = Vector3d.Dot(rel, outward);
                Vector3d side = rel - outward * front;
                double off = side.magnitude;
                Vector3d sideDir = off > 0.05 ? side / off : Vector3d.Exclude(outward, up).normalized;
                if (front < 1.0 && distance > 1.0) aim = goal + outward * 3.0 + sideDir * Math.Max(off, 3.5) - here;
                else if (off > 0.4 + 0.3 * Math.Max(0, front)) aim = goal + outward * Math.Max(front, 2.0) - here;
            }
            double cap = Math.Min(MaxSpeed, 0.3 * Math.Min(distance, aim.magnitude + 0.5) + 0.08);
            Vector3d wanted = aim.normalized * cap;
            Vector3d push = (wanted - velocity) / 1.2 + up * gravity;
            double effort = push.magnitude / power;
            // The pack is either on or off, so thrust for a share of the frames.
            duty += Math.Min(1.0, effort);
            if (duty >= 1.0 && effort > 0.03) { duty -= 1.0; ctl.Jet = push.normalized; }
            else ctl.Jet = Vector3d.zero;

            if (distance < bestDistance - 0.2) { bestDistance = distance; bestUT = ut; }
            if (ut - bestUT > 120) { Fail("not getting closer than " + Math.Round(bestDistance, 1) + " m to " + Label + " (something is in the way of the hatch)"); return false; }
            return distance < 0.8;
        }

        void Board(Vessel v, double ut)
        {
            if (Goal == null || Goal.vessel == null) { Fail("the ship is gone"); return; }
            Part hatch = ctl.HatchInReach;
            if (hatch != null && hatch.vessel == Goal.vessel && hatch.protoModuleCrew.Count < hatch.CrewCapacity)
            {
                // At a hatch of the right ship: get in. (Any part of it with a free seat will do.)
                ctl.Stop();
                Goal = hatch;
                if (ctl.In(Eva.st_idle_fl, Eva.st_idle_gr, Eva.st_idle_b_gr, Eva.st_ladder_idle, Eva.st_swim_idle))
                {
                    Phase = "climbing in";
                    Eva.BoardPart(hatch);
                }
                else Phase = "at the hatch (" + ctl.State + ")";
                return;
            }

            // Holding a ladder: climb it to the hatch.
            if (Eva.OnALadder)
            {
                if (climbSince < 0) climbSince = ut;
                Vector3d toHatch = Goal.airlock != null ? (Vector3d)Goal.airlock.position - (Vector3d)Eva.transform.position : Vector3d.zero;
                ctl.WalkTarget = null;
                ctl.Jet = Vector3d.zero;
                ctl.Climb = Vector3d.Dot(toHatch, Eva.transform.up) >= 0 ? 1 : -1;
                distance = toHatch.magnitude;
                Phase = "on the ladder, " + Math.Round(distance, 1) + " m from the hatch";
                // At the end of the rungs with the hatch still out of reach, more climbing will not help.
                if (ctl.In(Eva.st_ladder_end_reached)) { if (endSince < 0) endSince = ut; } else endSince = -1;
                if (endSince >= 0 && ut - endSince > 4) Fail("the ladder ends " + Math.Round(distance, 1) + " m short of the hatch; the ship needs a longer ladder (or one more rung next to the hatch)");
                else if (ut - climbSince > 120) Fail("the ladder does not lead to a hatch with a free seat");
                return;
            }
            climbSince = -1;
            ctl.Climb = 0;

            bool surface = v.LandedOrSplashed || v.radarAltitude < 50;
            double power = Eva.linPower / Math.Max(0.01, v.totalMass);
            bool canFly = Eva.HasJetpack && Eva.Fuel > 0.001 && (!surface || power > Guidance.LocalGravity(v) * 1.15);
            if (canFly) { ctl.WalkTarget = null; Fly(v, ut, true); return; }

            // Too heavy to fly: walk under the hatch and take any ladder that comes within reach.
            if (ctl.LadderInReach && ctl.In(Eva.st_idle_gr, Eva.st_idle_b_gr, Eva.st_idle_fl, Eva.st_swim_idle, Eva.st_walk_acd, Eva.st_run_acd))
            {
                ctl.Stop();
                Eva.fsm.RunEvent(Eva.On_ladderGrabStart);
                Phase = "taking hold of the ladder";
                return;
            }
            Vector3d up = Guidance.Up(v);
            Vector3d goal = Goal.airlock != null ? (Vector3d)Goal.airlock.position : (Vector3d)Goal.transform.position;
            Vector3d flat = Vector3d.Exclude(up, goal - (Vector3d)Eva.transform.position);
            distance = flat.magnitude;
            double climb = Vector3d.Dot(goal - (Vector3d)Eva.transform.position, up);
            Phase = "walking to " + shipName + ", " + Math.Round(distance, 1) + " m to go";
            Eva.walkSpeed = Run && distance > 4 ? Eva.runSpeed : walkSpeedWas;
            ctl.WalkTarget = distance > 0.3 ? (Vector3d)Eva.transform.position + flat.normalized * 3.0 : (Vector3d?)null;
            if (distance < bestDistance - 0.3) { bestDistance = distance; bestUT = ut; }
            if (ut - bestUT > 20)
                Fail(distance < 2.5 ? "the hatch is " + Math.Round(climb, 1) + " m overhead with no ladder in reach, and the jetpack cannot lift a kerbal here" : "stuck " + Math.Round(distance, 1) + " m from " + shipName);
        }

        public override JObj Detail() => new JObj().Add("distance_m", Math.Round(distance, 2)).Add("state", ctl != null ? ctl.State : "starting");

        public override void OnEnd(Autopilot ap)
        {
            if (ctl != null) ctl.Stop();
            if (Eva != null) Eva.walkSpeed = walkSpeedWas > 0 ? walkSpeedWas : Eva.walkSpeed;
        }
    }
}
