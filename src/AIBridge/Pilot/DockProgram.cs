using System;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Docking: point this vessel's port back along the target port's axis, fly round to a point straight
    /// out in front of the target port, then slide down that line at a crawl with RCS until the ports
    /// grab each other.
    /// </summary>
    public sealed class DockProgram : Program
    {
        public override string Name => "docking with " + targetName;

        public ModuleDockingNode Mine, Theirs;
        public double MaxSpeed = 2.0, StandoffDistance = 15.0;
        /// <summary>Degrees to turn this port's "up" mark from the target port's; null leaves roll alone unless the target is turning.</summary>
        public double? Roll;

        enum State { Align, Clear, Standoff, Approach, Capture }
        State state = State.Align;
        bool started, rcsWasOn, sasWasOn, docked;
        string targetName = "the target";
        double startUT, alignedSince = -1, captureSince = -1, rcsAccel = 0.2, clearRadius = 6, lastGoodUT, startMono;
        double axial, lateral, relSpeed, alignError, turnRate;
        bool turnLogged;
        // Self-check of which way the thrusters push.
        double sign = 1, checkUT = -1;
        Vector3d checkVelocity, checkCommand;
        Vessel targetVessel;
        int retries;

        public static bool IsDocked(ModuleDockingNode n) => n != null && n.state != null && (n.state.Contains("Docked") || n.state.Contains("PreAttached"));

        public override bool SurvivesVesselChange(Vessel now)
        {
            // Docking merges the two vessels; whichever object survives becomes the active vessel.
            if (Mine != null && Mine.vessel == now) return true;
            return false;
        }

        void Begin(Autopilot ap, Vessel v)
        {
            started = true;
            startUT = lastGoodUT = Planetarium.GetUniversalTime();
            targetVessel = Theirs.vessel;
            targetName = targetVessel.GetDisplayName();
            rcsWasOn = v.ActionGroups[KSPActionGroup.RCS];
            sasWasOn = v.ActionGroups[KSPActionGroup.SAS];
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
            Mine.MakeReferenceTransform();
            try { FlightGlobals.fetch.SetVesselTarget(Theirs, true); } catch { }
            startMono = Monoprop(v);

            // How hard the thrusters can push, to size the corrections.
            double thrust = 0;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleRCS r && r.rcsEnabled && r.isEnabled) thrust += r.thrusterPower * Math.Max(1, r.thrusterTransforms.Count) / 4.0;
            rcsAccel = OrbitMath.Clamp(thrust / Math.Max(0.01, v.totalMass), 0.02, 3.0);

            // Keep clear of the other vessel's hull when flying round it.
            double reach = 0;
            foreach (Part p in targetVessel.parts) reach = Math.Max(reach, (p.transform.position - Theirs.nodeTransform.position).magnitude);
            double own = 0;
            foreach (Part p in v.parts) own = Math.Max(own, (p.transform.position - Mine.nodeTransform.position).magnitude);
            clearRadius = OrbitMath.Clamp(reach * 0.6 + own * 0.6 + 4, 5, 80);
            StandoffDistance = Math.Max(StandoffDistance, Math.Min(40, own * 0.5 + 8));
        }

        static double Monoprop(Vessel v)
        {
            double total = 0;
            foreach (Part p in v.parts)
                foreach (PartResource r in p.Resources)
                    if (r.resourceName == "MonoPropellant" && r.flowState) total += r.amount;
            return total;
        }

        public override void Tick(Autopilot ap, Vessel v)
        {
            double ut = Planetarium.GetUniversalTime();
            if (Mine == null || Mine.part == null || Theirs == null || Theirs.part == null) { Fail("a docking port was lost"); return; }
            if (IsDocked(Mine) && Mine.otherNode != null || docked)
            {
                Finish("docked with " + targetName + "; the combined vessel has " + v.parts.Count + " parts" + UsedNote(v));
                return;
            }
            if (!started) Begin(ap, v);
            if (Theirs.vessel == null || Theirs.vessel == v)
            {
                if (Theirs.vessel == v) { Finish("docked with " + targetName + "; the combined vessel has " + v.parts.Count + " parts" + UsedNote(v)); return; }
                Fail("the target vessel is gone");
                return;
            }
            if (!v.IsControllable) { Fail("lost control of the vessel"); return; }
            targetVessel = Theirs.vessel;

            Transform mine = Mine.nodeTransform, theirs = Theirs.nodeTransform;
            Vector3d axis = theirs.forward;                               // straight out of the target port
            Vector3d offset = (Vector3d)mine.position - (Vector3d)theirs.position;
            axial = Vector3d.Dot(offset, axis);
            Vector3d side = offset - axis * axial;
            lateral = side.magnitude;
            double range = offset.magnitude;
            // A target that is turning carries its port round with it. Close in, fly in the target's own
            // turning frame (speed measured against a point fixed to the target where this port is);
            // far out that point sweeps round too fast to chase, so use plain relative speed there.
            Vector3d relative = v.obt_velocity - targetVessel.obt_velocity;
            turnRate = ((Vector3d)targetVessel.angularVelocity).magnitude * 180.0 / Math.PI;
            Rigidbody theirBody = Theirs.part.Rigidbody, myBody = Mine.part.Rigidbody;
            if (theirBody != null && myBody != null)
            {
                Vector3d inFrame = (Vector3d)myBody.GetPointVelocity(mine.position) - (Vector3d)theirBody.GetPointVelocity(mine.position);
                double blend = OrbitMath.Clamp((90.0 - range) / 45.0, 0, 1);
                relative = relative * (1.0 - blend) + inFrame * blend;
            }
            relSpeed = relative.magnitude;
            alignError = Vector3d.Angle(mine.forward, -axis);
            if (turnRate > 12) { Fail("the target is tumbling at " + Math.Round(turnRate) + "° per second, too fast to follow; steady it first (switch to it and turn SAS on)"); return; }
            if (turnRate > 0.3 && !turnLogged) { turnLogged = true; ap.Log.Add("the target is turning at " + Math.Round(turnRate, 1) + "° per second; following it round"); }

            // The ports have caught hold: hands off and let them pull together.
            bool grabbing = Mine.state != null && Mine.state.Contains("Acquire");
            if (grabbing)
            {
                if (captureSince < 0) { captureSince = ut; ap.Log.Add("docking ports are pulling together"); }
                state = State.Capture;
                Phase = "ports engaged, pulling together";
                ap.Translation = Vector3d.zero;
                ap.SetAttitude(AttitudeMode.Off);
                v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                if (ut - captureSince > 25) { captureSince = -1; Retreat(ap, "the ports held on for 25 s without docking"); }
                return;
            }
            if (state == State.Capture) { state = State.Approach; captureSince = -1; }

            if (range > 2500) { Fail("the target is " + Math.Round(range) + " m away; rendezvous first (get within 2 km, ideally 200 m, at low relative speed)"); return; }
            if (ut - startUT > 1500) { Fail("no docking after 25 minutes; " + Describe()); return; }
            if (Monoprop(v) <= 0.001 && startMono > 0) { Fail("out of monopropellant " + Math.Round(range, 1) + " m from the port"); return; }

            // Attitude: our port looks back down the target port's axis the whole way in. Roll is matched
            // when asked for, and always when the target is turning (or the ports would be twisting past
            // each other as they meet).
            ap.FixedDirection = -axis;
            ap.SetAttitude(AttitudeMode.Fixed);
            if (Roll.HasValue || turnRate > 0.3)
            {
                ap.RollFrom = (Vector3d)mine.up;
                ap.RollToward = QuaternionD.AngleAxis(Roll ?? 0, axis) * (Vector3d)theirs.up;
            }
            else ap.RollFrom = ap.RollToward = null;
            bool aligned = alignError < 6.0;
            if (aligned) { if (alignedSince < 0) alignedSince = ut; } else alignedSince = -1;

            // Where to go next, as a point relative to the target port.
            Vector3d wantedVelocity;
            // Far out, go as fast as the thrusters can comfortably stop from; near the target, the chosen crawl.
            double speedCap = range > 60 ? OrbitMath.Clamp(Math.Sqrt(2.0 * 0.4 * rcsAccel * (range - 40)), MaxSpeed, 8.0) : OrbitMath.Clamp(range / 40.0, 0.25, MaxSpeed);
            Vector3d sideDir = lateral > 0.05 ? side / lateral : Vector3d.Exclude(axis, (Vector3d)theirs.up).normalized;
            switch (state)
            {
                case State.Align:
                    Phase = "turning to face the port, " + Math.Round(alignError) + "° off, " + Math.Round(range, 1) + " m away";
                    wantedVelocity = Vector3d.zero;
                    if (alignedSince >= 0 && ut - alignedSince > 1.5) state = axial > 3 && lateral < Math.Max(1.5, 0.25 * axial) ? State.Approach : State.Clear;
                    break;

                case State.Clear:
                {
                    // Not in front of the port: move out sideways past the hull, then along it.
                    Vector3d goal;
                    if (axial >= StandoffDistance * 0.8) { state = State.Standoff; goto case State.Standoff; }
                    if (lateral < clearRadius) goal = axis * axial + sideDir * (clearRadius + 3);
                    else goal = axis * StandoffDistance + sideDir * lateral;
                    Phase = "flying round to the front of the port, " + Math.Round(range, 1) + " m away";
                    wantedVelocity = Toward(goal - offset, speedCap);
                    break;
                }

                case State.Standoff:
                {
                    Vector3d goal = axis * StandoffDistance;
                    Vector3d gap = goal - offset;
                    Phase = "lining up " + Math.Round(StandoffDistance) + " m in front of the port, " + Math.Round(gap.magnitude, 1) + " m to go";
                    wantedVelocity = Toward(gap, speedCap);
                    if (axial < 2 && lateral > 2) { state = State.Clear; break; }
                    if ((gap.magnitude < 1.5 || (lateral < 1.2 && axial > 2 && axial < StandoffDistance + 4)) && relSpeed < 0.3 && aligned) state = State.Approach;
                    break;
                }

                case State.Approach:
                default:
                {
                    Phase = "closing in, " + Math.Round(axial, 2) + " m out, " + Math.Round(lateral, 2) + " m off the line, " + Math.Round(relSpeed, 2) + " m/s";
                    // Slow down as the gap closes, never quite stopping: the ports need a gentle push to latch.
                    double closing = OrbitMath.Clamp(axial / 15.0, 0.07, Math.Min(1.0, MaxSpeed));
                    if (!aligned || lateral > 0.25 + 0.06 * Math.Max(0, axial)) closing = Math.Min(closing, 0.03);   // off the line: fix that first
                    Vector3d sideways = -side * 0.3;
                    if (sideways.magnitude > 0.4) sideways = sideways.normalized * 0.4;
                    wantedVelocity = -axis * closing + sideways;
                    if (axial < -0.3 || (axial < 4 && lateral > 1.0 + 0.3 * Math.Max(0, axial))) Retreat(ap, "came in " + Math.Round(lateral, 1) + " m off the line");
                    break;
                }
            }

            // Thruster effort: proportional to the speed error, full effort when the error is what a second and a half of thrust would fix.
            Vector3d error = wantedVelocity - relative;
            Vector3d command = error / (rcsAccel * 1.5);
            // Thrusters ignore a feather-light touch, so anything worth correcting gets a firm minimum push.
            if (error.magnitude < 0.015) command = Vector3d.zero;
            else if (command.magnitude < 0.2) command = command.normalized * 0.2;
            if (command.magnitude > 1) command = command.normalized;
            ap.Translation = command * sign;

            // If the vessel is being pushed the wrong way, the thrusters are mounted the other way round from the usual.
            if (command.magnitude > 0.5)
            {
                if (checkUT < 0) { checkUT = ut; checkVelocity = relative; checkCommand = command.normalized; }
                else if (ut - checkUT > 2.0)
                {
                    double gained = Vector3d.Dot(relative - checkVelocity, checkCommand);
                    if (gained < -0.3 * rcsAccel) { sign = -sign; ap.Log.Add("RCS pushes the opposite way on this vessel; reversed the commands"); }
                    checkUT = -1;
                }
            }
            else checkUT = -1;
        }

        static Vector3d Toward(Vector3d gap, double speedCap)
        {
            double distance = gap.magnitude;
            if (distance < 0.05) return Vector3d.zero;
            return gap / distance * Math.Min(speedCap, distance * 0.12 + 0.03);
        }

        void Retreat(Autopilot ap, string why)
        {
            retries++;
            if (retries > 4) { Fail("could not line up after " + retries + " tries; " + Describe()); return; }
            ap.Log.Add("backing off to line up again: " + why);
            state = State.Standoff;
        }

        string Describe() => Math.Round(axial, 1) + " m out along the port's axis, " + Math.Round(lateral, 1) + " m off it, " + Math.Round(relSpeed, 2) + " m/s relative, " + Math.Round(alignError) + "° misaligned";

        string UsedNote(Vessel v)
        {
            double used = startMono - Monoprop(v);
            return used > 0.01 ? "; used " + Math.Round(used, 1) + " units of monopropellant" : "";
        }

        public override JObj Detail() => new JObj().Add("along_axis_m", Math.Round(axial, 2)).Add("off_axis_m", Math.Round(lateral, 2)).Add("relative_speed", Math.Round(relSpeed, 3))
            .Add("misaligned_deg", Math.Round(alignError, 1)).Add("target_turning_deg_s", Math.Round(turnRate, 2)).Add("attempt", retries + 1);

        public override void OnEnd(Autopilot ap)
        {
            ap.Translation = null;
            ap.RollFrom = ap.RollToward = null;
            ap.SetAttitude(AttitudeMode.Off);
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null)
            {
                if (!rcsWasOn) v.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
            }
        }
    }
}
