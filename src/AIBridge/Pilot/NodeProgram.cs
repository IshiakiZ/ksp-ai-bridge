using System;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Fly the next maneuver node: point along the burn, warp to the start, burn with the start centred
    /// on the node, ease off the throttle for the last metres per second, then delete the node.
    /// </summary>
    public sealed class NodeProgram : Program
    {
        public override string Name => "execute maneuver node";

        public bool UseWarp = true;
        public bool AutoStage = true;
        public double Tolerance = 0.02;

        enum State { Align, Wait, Burn }
        State state = State.Align;
        double totalDv = -1, alignSince = -1, minRemaining = double.MaxValue, noThrustSince = -1;
        Vector3d burnDirection;
        bool warpRequested, directionFrozen, stagerWasOn;

        ManeuverNode Node(Vessel v)
        {
            PatchedConicSolver solver = v.patchedConicSolver;
            return solver != null && solver.maneuverNodes.Count > 0 ? solver.maneuverNodes[0] : null;
        }

        public override void Tick(Autopilot ap, Vessel v)
        {
            double ut = Planetarium.GetUniversalTime();
            ManeuverNode node = Node(v);
            if (node == null) { Fail("the maneuver node disappeared"); return; }
            if (!v.IsControllable) { Fail("lost control of the vessel"); return; }

            Vector3d burn = node.GetBurnVector(v.orbit);
            double remaining = burn.magnitude;
            if (totalDv < 0)
            {
                totalDv = remaining;
                alignSince = ut;
                stagerWasOn = ap.Stager.Enabled;
                if (AutoStage) ap.Stager.Enabled = true;
                if (totalDv < Tolerance) { RemoveNode(node); Finish("node needed no burn"); return; }
            }

            double burnTime = Guidance.BurnTime(v, remaining);
            if (double.IsNaN(burnTime))
            {
                // Nothing lit yet. The stager ignites the next stage if it can; otherwise give up after a while.
                if (noThrustSince < 0) noThrustSince = ut;
                Phase = "no active engine, waiting for staging";
                if (AutoStage && ut - noThrustSince > 1.0 && KSP.UI.Screens.StageManager.CanSeparate && v.currentStage > 0 && state != State.Burn)
                {
                    ap.Stager.Fire(ap, "ignite for maneuver");
                    noThrustSince = ut;
                }
                if (ut - noThrustSince > 8.0 && (v.currentStage <= 0 || !AutoStage))
                {
                    Fail("no working engine with fuel; " + Math.Round(remaining, 1) + " m/s of the burn remain");
                    return;
                }
                if (state != State.Burn) { ap.SetAttitude(AttitudeMode.Maneuver); ap.SetThrottle(0); return; }
                burnTime = 0;
            }
            else noThrustSince = -1;

            double startUT = node.UT - burnTime / 2.0;

            switch (state)
            {
                case State.Align:
                    Phase = "turning to the burn direction (" + Round(ap.AngleError) + "° off)";
                    ap.SetAttitude(AttitudeMode.Maneuver);
                    ap.SetThrottle(0);
                    if (ap.AngleError < 1.5 || ut - alignSince > 90 || ut >= startUT - 1) state = State.Wait;
                    break;

                case State.Wait:
                {
                    double wait = startUT - ut;
                    Phase = "waiting for burn start, " + Math.Round(wait) + " s";
                    ap.SetAttitude(AttitudeMode.Maneuver);
                    ap.SetThrottle(0);
                    if (wait <= 0)
                    {
                        CancelWarp();
                        state = State.Burn;
                        burnDirection = burn.normalized;
                        break;
                    }
                    if (UseWarp && !warpRequested && wait > 30)
                    {
                        TimeWarp.fetch.WarpTo(startUT - 12);
                        warpRequested = true;
                    }
                    else if (wait < 10 && TimeWarp.CurrentRateIndex != 0) CancelWarp();
                    break;
                }

                case State.Burn:
                {
                    Phase = "burning, " + Math.Round(remaining, 1) + " m/s to go";
                    // Near the end the remaining vector swings wildly; stop chasing it.
                    if (!directionFrozen && remaining < Math.Max(1.5, totalDv * 0.01)) { directionFrozen = true; burnDirection = burn.normalized; }
                    if (!directionFrozen) ap.SetAttitude(AttitudeMode.Maneuver);
                    else { ap.FixedDirection = burnDirection; ap.SetAttitude(AttitudeMode.Fixed); }

                    Guidance.EngineStats(v, out double maxThrust, out double _, out double _, out int burning, out int _);
                    double accel = maxThrust / Math.Max(1e-6, v.totalMass);
                    bool aligned = double.IsNaN(ap.AngleError) || ap.AngleError < (remaining > 20 ? 20 : 45);
                    // Ease off towards the end; the floor keeps a trickle of thrust that is fine enough to stop within centimetres per second.
                    double floor = OrbitMath.Clamp(0.25 / Math.Max(0.1, accel), 0.002, 0.02);
                    ap.SetThrottle(aligned ? OrbitMath.Clamp(remaining / Math.Max(0.05, accel * 0.9), floor, 1.0) : 0.0);

                    minRemaining = Math.Min(minRemaining, remaining);
                    bool passed = Vector3d.Dot(burn, burnDirection) < 0 || remaining > minRemaining + Math.Max(0.5, totalDv * 0.02);
                    if (remaining <= Tolerance || (directionFrozen && passed))
                    {
                        ap.SetThrottle(0);
                        Orbit o = v.orbit;
                        RemoveNode(node);
                        Finish("burn complete (" + Math.Round(totalDv, 1) + " m/s, " + Math.Round(remaining, 2) + " m/s residual). " + Describe(o));
                        return;
                    }
                    if (burning == 0)
                    {
                        if (noThrustSince < 0) noThrustSince = ut;
                        if (ut - noThrustSince > 6.0)
                        {
                            ap.SetThrottle(0);
                            Fail("ran out of propellant with " + Math.Round(remaining, 1) + " m/s of the burn remaining. " + Describe(v.orbit));
                        }
                    }
                    break;
                }
            }
        }

        static string Round(double v) => double.IsNaN(v) ? "?" : Math.Round(v, 1).ToString();

        static string Describe(Orbit o)
        {
            string s = "Orbit around " + o.referenceBody.bodyName + ": ";
            if (o.eccentricity < 1.0) s += "apoapsis " + Math.Round(o.ApA / 1000, 1) + " km, ";
            return s + "periapsis " + Math.Round(o.PeA / 1000, 1) + " km, inclination " + Math.Round(o.inclination, 2) + "°";
        }

        static void RemoveNode(ManeuverNode node)
        {
            try { node.RemoveSelf(); } catch { }
        }

        void CancelWarp()
        {
            if (TimeWarp.CurrentRateIndex != 0)
            {
                TimeWarp.fetch.CancelAutoWarp();
                TimeWarp.SetRate(0, true, false);
            }
        }

        public override JObj Detail()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            ManeuverNode node = v != null ? Node(v) : null;
            if (node == null) return null;
            return new JObj()
                .Add("remaining_dv", Math.Round(node.GetBurnVector(v.orbit).magnitude, 1))
                .Add("total_dv", Math.Round(totalDv, 1))
                .Add("node_in_s", Math.Round(node.UT - Planetarium.GetUniversalTime(), 1));
        }

        public override void OnEnd(Autopilot ap)
        {
            CancelWarp();
            if (!ap.HandingOver) ap.SetThrottle(0);
            ap.Throttle = null;
            ap.Stager.Enabled = stagerWasOn;
            ap.SetAttitude(AttitudeMode.Off);
        }
    }
}
