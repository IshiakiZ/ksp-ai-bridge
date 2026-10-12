using System;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Rover autopilot: drive over the surface to a latitude/longitude. It first nudges the wheels to learn
    /// which way this particular rover counts as "forward" and which way its steering turns, then holds a
    /// speed, steers at the target, slows for turns and for arrival, and stops with the brakes on.
    /// </summary>
    public sealed class DriveProgram : Program
    {
        public override string Name => "drive to " + (Label ?? Math.Round(TargetLat, 3) + ", " + Math.Round(TargetLon, 3));

        public double TargetLat, TargetLon;
        public string Label;
        public double Speed = 8;
        public double StopDistance = 12;

        enum State { FindForward, FindSteering, Drive, Reverse, Stop }
        State state = State.FindForward;
        bool started;
        double stateSince, startUT, lastMovingUT, startDistance, airborneSince = -1;
        Vector3 forwardAxisLocal = Vector3.up;   // in the vessel's reference frame
        double steerSign = 1, headingAtProbe, integral;

        static double Wrap(double deg) { while (deg > 180) deg -= 360; while (deg < -180) deg += 360; return deg; }

        Vector3d TargetVector(Vessel v) => VectorTo(v, TargetLat, TargetLon);

        static Vector3d VectorTo(Vessel v, double lat, double lon)
        {
            Vector3d target = v.mainBody.GetWorldSurfacePosition(lat, lon, v.altitude);
            return Vector3d.Exclude(Guidance.Up(v), target - v.CoMD);
        }

        // Going round an obstacle: an intermediate point to drive to first, and which side was chosen.
        bool detouring;
        double detourLat, detourLon, lastPlanUT = -100;
        int detourSide, detours, reverses;
        string lastObstacle;
        Vector3d reverseFrom;

        double HeadingOf(Vessel v, Vector3d horizontal)
        {
            return Math.Atan2(Vector3d.Dot(horizontal, Guidance.East(v)), Vector3d.Dot(horizontal, Guidance.North(v))) * 180.0 / Math.PI;
        }

        Vector3d Forward(Vessel v) => Vector3d.Exclude(Guidance.Up(v), (Vector3d)(v.ReferenceTransform.rotation * forwardAxisLocal)).normalized;

        public override void Tick(Autopilot ap, Vessel v)
        {
            double ut = Planetarium.GetUniversalTime();
            if (!started)
            {
                started = true;
                startUT = stateSince = lastMovingUT = ut;
                startDistance = TargetVector(v).magnitude;
                v.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
                if (v.ActionGroups[KSPActionGroup.SAS]) v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            }
            if (!v.IsControllable) { Fail("lost control of the rover (no power, crew or signal)"); return; }

            Vector3d up = Guidance.Up(v);
            Vector3d toFinal = TargetVector(v);
            Vector3d toTarget = detouring ? VectorTo(v, detourLat, detourLon) : toFinal;
            double distance = toFinal.magnitude;
            Vector3d groundVelocity = Vector3d.Exclude(up, v.srf_velocity);

            // Wheels only work on the ground; ease off while airborne after a bump.
            if (!v.Landed && !v.Splashed)
            {
                if (airborneSince < 0) airborneSince = ut;
                ap.WheelThrottle = 0; ap.WheelSteer = 0;
                if (ut - airborneSince > 20) Fail("the rover has been off the ground for 20 s (" + v.situation + ")");
                return;
            }
            airborneSince = -1;
            if (v.Splashed) { Fail("the rover is in water"); return; }

            switch (state)
            {
                case State.FindForward:
                {
                    Phase = "finding which way is forward";
                    ap.WheelThrottle = 0.7f; ap.WheelSteer = 0;
                    if (groundVelocity.magnitude > 0.8)
                    {
                        // Whichever body axis lines up with the motion is this rover's forward.
                        Vector3 local = Quaternion.Inverse(v.ReferenceTransform.rotation) * (Vector3)groundVelocity.normalized;
                        Vector3 best = Vector3.up; float bestDot = -1f;
                        foreach (Vector3 axis in new[] { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
                        {
                            float d = Vector3.Dot(local, axis);
                            if (d > bestDot) { bestDot = d; best = axis; }
                        }
                        forwardAxisLocal = best;
                        headingAtProbe = HeadingOf(v, Forward(v));
                        state = State.FindSteering; stateSince = ut;
                    }
                    else if (ut - stateSince > 8)
                    {
                        Fail("the wheels do not move the rover (" + Power(v) + ")");
                    }
                    break;
                }

                case State.FindSteering:
                {
                    Phase = "finding which way the steering turns";
                    ap.WheelThrottle = 0.5f; ap.WheelSteer = 0.5f;
                    double turned = Wrap(HeadingOf(v, Forward(v)) - headingAtProbe);
                    if (Math.Abs(turned) > 2.0 || ut - stateSince > 4)
                    {
                        // A positive stick that turned the rover left (heading decreasing) is the usual case.
                        steerSign = turned > 0 ? -1 : 1;
                        state = State.Drive; stateSince = ut; integral = 0;
                    }
                    break;
                }

                case State.Drive:
                {
                    double heading = HeadingOf(v, Forward(v));
                    double bearing = HeadingOf(v, toTarget);
                    double error = Wrap(bearing - heading);
                    double forwardSpeed = Vector3d.Dot(groundVelocity, Forward(v));
                    Phase = (detouring ? "going round " + (lastObstacle ?? "an obstacle") + ", " : "driving, ") + Math.Round(distance) + " m to go at " + Math.Round(forwardSpeed, 1) + " m/s";

                    if (distance <= StopDistance) { state = State.Stop; stateSince = ut; break; }
                    if (detouring && toTarget.magnitude < 6.0) detouring = false;      // reached the way-point: try for the target again

                    // Look along the way to the aim point, and straight ahead once actually heading that way.
                    // An obstacle means picking a way round: the open direction nearest the target, keeping
                    // to one side; boxed in, back up and look again.
                    if (ut - lastLookUT > 0.25)
                    {
                        lastLookUT = ut;
                        bool turning = Math.Abs(error) > 25;
                        double stopping = Math.Max(14.0, forwardSpeed * forwardSpeed / 4.0 + forwardSpeed + 6.0);
                        string hazard = LookAhead(v, up, toTarget.normalized, 0, Math.Min(toTarget.magnitude, stopping));
                        if (hazard == null && !turning) hazard = LookAhead(v, up, Forward(v), Math.Max(0, forwardSpeed), toTarget.magnitude);
                        string nose = turning && forwardSpeed > 0.3 ? LookAhead(v, up, Forward(v), 0, 5) : null;
                        if (hazard != null || nose != null)
                        {
                            bool planned = hazard == null || (ut - lastPlanUT > 1.2 && PlanDetour(v, up, toFinal, hazard));
                            if (planned && nose == null) lastPlanUT = Math.Max(lastPlanUT, ut - 0.01);
                            if (nose != null || (!planned && ut - lastPlanUT > 1.2))
                            {
                                // Nose against something, or no way on from here: reverse a few metres and look again.
                                if (reverses >= 6)
                                {
                                    blockedBy = (hazard ?? nose) + " (boxed in after going round " + detours + " obstacle" + (detours == 1 ? "" : "s") + ")";
                                    state = State.Stop;
                                    stateSince = ut;
                                    break;
                                }
                                reverses++;
                                reverseFrom = v.CoMD;
                                state = State.Reverse;
                                stateSince = ut;
                                detouring = false;
                                break;
                            }
                        }
                        // Past the obstacle and the direct line is open again: forget which side was being followed.
                        else if (!detouring && detourSide != 0 && LookAhead(v, up, toFinal.normalized, 0, Math.Min(distance, 70)) == null) detourSide = 0;
                    }

                    // Slow down for sharp turns and for the approach; fast, hard steering flips rovers.
                    double wanted = Speed * OrbitMath.Clamp(1.0 - Math.Abs(error) / 80.0, 0.25, 1.0);
                    if (detouring) wanted = Math.Min(wanted, Math.Abs(error) > 25 ? 2.5 : 5.0);
                    wanted = Math.Min(wanted, Math.Sqrt(2.0 * 1.2 * Math.Max(0, distance - StopDistance)) + 1.5);
                    double speedError = wanted - forwardSpeed;
                    integral = OrbitMath.Clamp(integral + speedError * TimeWarp.fixedDeltaTime * 0.15, -0.5, 0.5);
                    ap.WheelThrottle = (float)OrbitMath.Clamp(0.35 * speedError + integral, -1, 1);
                    bool tooFast = forwardSpeed > wanted + 2.5;
                    if (v.ActionGroups[KSPActionGroup.Brakes] != tooFast) v.ActionGroups.SetGroup(KSPActionGroup.Brakes, tooFast);

                    double authority = OrbitMath.Clamp(6.0 / Math.Max(1.0, Math.Abs(forwardSpeed)), 0.12, 1.0);
                    ap.WheelSteer = (float)(steerSign * -OrbitMath.Clamp(error / 25.0, -1, 1) * authority);

                    if (Math.Abs(forwardSpeed) > 0.3) lastMovingUT = ut;
                    if (ut - lastMovingUT > 20) { Fail("stuck: not moving for 20 s with " + Math.Round(distance) + " m to go (" + Power(v) + ")"); return; }
                    if (Vector3.Angle((Vector3)up, v.ReferenceTransform.rotation * UpAxisGuess()) > 70f && ut - stateSince > 3)
                    {
                        Fail("the rover has tipped over " + Math.Round(distance) + " m from the target");
                        return;
                    }
                    break;
                }

                case State.Reverse:
                {
                    Phase = "backing away from " + (lastObstacle ?? "an obstacle");
                    double moved = (v.CoMD - reverseFrom).magnitude;
                    if (v.ActionGroups[KSPActionGroup.Brakes]) v.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
                    ap.WheelSteer = 0;
                    ap.WheelThrottle = -0.7f;
                    lastMovingUT = ut;
                    if (moved > 9 || ut - stateSince > 7)
                    {
                        ap.WheelThrottle = 0;
                        state = State.Drive;
                        stateSince = ut;
                        lastPlanUT = -100;
                    }
                    break;
                }

                case State.Stop:
                    Phase = "stopping";
                    ap.WheelThrottle = 0; ap.WheelSteer = 0;
                    if (!v.ActionGroups[KSPActionGroup.Brakes]) v.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                    if (v.srfSpeed < 0.3 || ut - stateSince > 20)
                    {
                        if (blockedBy != null)
                            Fail("stopped " + Math.Round(distance) + " m short of the target at latitude " + Math.Round(v.latitude, 4) + ", longitude " + Math.Round(v.longitude, 4) + ": " + blockedBy +
                                 ". Brakes are on");
                        else
                            Finish("arrived " + Math.Round(distance) + " m from the target at latitude " + Math.Round(v.latitude, 4) + ", longitude " + Math.Round(v.longitude, 4) +
                                   " after " + Math.Round(ut - startUT) + " s" + (detours > 0 ? ", going round " + detours + " obstacle" + (detours > 1 ? "s" : "") : "") + "; brakes are on");
                    }
                    break;
            }
        }

        double lastLookUT = -1;
        string blockedBy;

        /// <summary>
        /// Choose a way round whatever is ahead: the open direction closest to the line to the target, on
        /// the side already being followed if there is one. Sets a way-point about 35 m that way.
        /// </summary>
        bool PlanDetour(Vessel v, Vector3d up, Vector3d toFinal, string hazard)
        {
            if (detours >= 40) return false;
            Vector3d line = toFinal.normalized;
            // Long legs first; in a tight spot settle for a short one.
            foreach (double leg in new[] { 40.0, 24.0, 14.0 })
            {
                foreach (double size in new[] { 35.0, 60.0, 85.0, 110.0, 140.0, 165.0 })
                {
                    foreach (int sign in new[] { 1, -1 })
                    {
                        if (detourSide != 0 && sign != detourSide) continue;
                        Vector3d direction = QuaternionD.AngleAxis(size * sign, up) * line;
                        if (LookAhead(v, up, direction, 0, leg + 8) != null) continue;
                        Vector3d spot = v.CoMD + direction * leg;
                        detourLat = v.mainBody.GetLatitude(spot);
                        detourLon = v.mainBody.GetLongitude(spot);
                        detouring = true;
                        detourSide = sign;
                        detours++;
                        int cut = hazard.IndexOf(" is ", StringComparison.Ordinal);
                        string what = cut > 0 ? hazard.Substring(0, cut) : "an obstacle";
                        if (what != lastObstacle || detours == 1) Autopilot.I.Log.Add("going round " + what);
                        lastObstacle = what;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Feel along a direction for a wall, another vessel or a drop. With a speed it looks as far as the
        /// rover needs to stop; with a fixed reach it checks a whole stretch of ground. Returns a
        /// description of what is in the way, or null when the path is clear.
        /// </summary>
        string LookAhead(Vessel v, Vector3d up, Vector3d direction, double speed, double limit)
        {
            Vector3 forward = ((Vector3)direction).normalized;
            Vector3 upward = (Vector3)up;
            bool stretch = speed <= 0;
            float reach = (float)(stretch ? limit : Math.Min(limit, Math.Max(10.0, speed * speed / (2.0 * 2.0) + speed * 1.0 + 6.0)));
            if (reach < 1f) return null;
            const int mask = (1 << 15) | (1 << 0) | (1 << 19);
            foreach (float lift in new[] { 0.5f, 1.4f })
            {
                Vector3 origin = (Vector3)v.CoMD + upward * lift;
                RaycastHit[] hits = Physics.RaycastAll(origin, forward, reach, mask);
                float nearest = float.MaxValue;
                string what = null;
                foreach (RaycastHit hit in hits)
                {
                    Part part = hit.collider.GetComponentInParent<Part>();
                    if (part != null && part.vessel == v) continue;
                    // Ground that rises gently is not an obstacle; a wall or a steep bank is.
                    if (part == null && Vector3.Angle(hit.normal, upward) < 40f) continue;
                    if (hit.distance >= nearest) continue;
                    nearest = hit.distance;
                    if (part != null) what = "'" + part.vessel.GetDisplayName() + "' is " + Math.Round(hit.distance) + " m ahead";
                    else
                    {
                        string name = null;
                        for (Transform t = hit.collider.transform; t != null; t = t.parent)
                            if (t.GetComponent<PQSCity>() != null || t.GetComponent<PQSCity2>() != null || t.GetComponent<Upgradeables.UpgradeableObject>() != null) { name = t.name; break; }
                        what = (name != null ? "a building (" + name + ")" : "a steep bank or cliff face") + " is " + Math.Round(hit.distance) + " m ahead";
                    }
                }
                if (what != null) return what;
            }

            // Drops: the ground a little way along is well below the wheels.
            double wheelHeight = v.heightFromTerrain >= 0 ? v.heightFromTerrain : 1.0;
            var probes = new System.Collections.Generic.List<float>();
            if (stretch) for (float d = 8f; d <= reach; d += 12f) probes.Add(d);
            else probes.Add((float)Math.Min(limit, Math.Max(6.0, speed * 1.6 + 4.0)));
            foreach (float ahead in probes)
            {
                Vector3 probe = (Vector3)v.CoMD + forward * ahead + upward * 3f;
                if (Physics.Raycast(probe, -upward, out RaycastHit ground, 60f, 1 << 15))
                {
                    double fall = ground.distance - 3.0 - wheelHeight;
                    if (fall > 2.5 + ahead * 0.25) return "the ground drops " + Math.Round(fall, 1) + " m about " + Math.Round(ahead) + " m ahead";
                }
                else if (v.mainBody.pqsController != null) return "there is no ground " + Math.Round(ahead) + " m ahead (an edge)";
            }
            return null;
        }

        Vector3 upAxisLocal = Vector3.zero;

        /// <summary>The body axis that pointed at the sky when the drive began; used to notice a roll-over.</summary>
        Vector3 UpAxisGuess()
        {
            if (upAxisLocal != Vector3.zero) return upAxisLocal;
            Vessel v = FlightGlobals.ActiveVessel;
            Vector3 local = Quaternion.Inverse(v.ReferenceTransform.rotation) * (Vector3)Guidance.Up(v);
            Vector3 best = Vector3.up; float bestDot = -1f;
            foreach (Vector3 axis in new[] { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
            {
                float d = Vector3.Dot(local, axis);
                if (d > bestDot) { bestDot = d; best = axis; }
            }
            upAxisLocal = best;
            return best;
        }

        static string Power(Vessel v)
        {
            double ec = 0, max = 0;
            foreach (Part p in v.parts)
                foreach (PartResource r in p.Resources)
                    if (r.resourceName == "ElectricCharge") { ec += r.amount; max += r.maxAmount; }
            return "electric charge " + Math.Round(ec) + " of " + Math.Round(max);
        }

        public override JObj Detail()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return null;
            return new JObj().Add("distance_m", Math.Round(TargetVector(v).magnitude)).Add("speed", Math.Round(v.srfSpeed, 1));
        }

        public override void OnEnd(Autopilot ap)
        {
            ap.WheelThrottle = null;
            ap.WheelSteer = null;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null && !ap.HandingOver) v.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
        }
    }
}
