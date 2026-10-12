using ModuleWheels;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Landing gear and legs. KSP's Gear action group is a toggle whose stored state often disagrees with
    /// the hardware (legs launch retracted while the group says "down"), so each leg is commanded directly.
    /// </summary>
    public static class Gear
    {
        public static int Set(Vessel v, bool down)
        {
            int count = 0;
            v.ActionGroups.SetGroup(KSPActionGroup.Gear, down);
            foreach (Part p in v.parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleWheelDeployment d) || d.fsm == null || !d.fsm.Started) continue;
                    d.ActionToggle(down ? KSPActionType.Activate : KSPActionType.Deactivate);
                    count++;
                }
            }
            return count;
        }

        /// <summary>True when every deployable leg or wheel is fully down (or the vessel has none).</summary>
        public static bool IsDown(Vessel v)
        {
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleWheelDeployment d && d.fsm != null && d.fsm.Started && d.fsm.CurrentState != d.st_deployed) return false;
            return true;
        }
    }
}
