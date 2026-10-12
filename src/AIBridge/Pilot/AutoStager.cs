using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;

namespace AIBridge.Pilot
{
    /// <summary>
    /// Fires the next stage when the current one is spent: either every running engine has flamed out,
    /// or the boosters that the next stage would drop have burned dry.
    /// </summary>
    public sealed class AutoStager
    {
        public bool Enabled;
        /// <summary>Never fire a stage numbered below this (0 = no limit).</summary>
        public int StopAtStage;
        public float DelayBefore = 0.4f, DelayAfter = 1.0f;

        float lastStagedAt = -100f;
        float wantSince = -1f;

        public double SecondsSinceStaging => Time.realtimeSinceStartup - lastStagedAt;

        public void Tick(Autopilot ap, Vessel v)
        {
            if (!Enabled || v == null || v.packed) return;
            if (StageManager.Instance == null || !StageManager.CanSeparate) return;
            int current = v.currentStage;
            if (current <= 0 || current - 1 < StopAtStage) return;
            if (Time.realtimeSinceStartup - lastStagedAt < DelayAfter) return;

            if (!ShouldStage(v, current)) { wantSince = -1f; return; }
            if (wantSince < 0f) { wantSince = Time.realtimeSinceStartup; return; }
            // In thick air give the pilot a moment longer to line up with the airflow before dropping anything.
            float delay = v.dynamicPressurekPa > 3.0 ? Mathf.Max(DelayBefore, 0.8f) : DelayBefore;
            if (Time.realtimeSinceStartup - wantSince < delay) return;
            Fire(ap, "auto-stage");
        }

        /// <summary>True from the moment a staging is decided until it has happened.</summary>
        public bool Pending => Enabled && wantSince >= 0f;

        /// <summary>Seconds until the first running engine runs dry at its present burn rate.</summary>
        public static double TimeToBurnout(Vessel v)
        {
            double best = double.PositiveInfinity;
            double dt = Mathf.Max(TimeWarp.fixedDeltaTime, 0.001f);
            foreach (Part p in v.parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleEngines e) || !e.EngineIgnited || e.flameout) continue;
                    foreach (Propellant pr in e.propellants)
                    {
                        if (pr.ignoreForIsp || pr.currentRequirement <= 1e-9) continue;
                        best = System.Math.Min(best, pr.totalResourceAvailable / (pr.currentRequirement / dt));
                    }
                }
            }
            return best;
        }

        public void Fire(Autopilot ap, string why)
        {
            wantSince = -1f;
            lastStagedAt = Time.realtimeSinceStartup;
            StageManager.ActivateNextStage();
        }

        static bool ShouldStage(Vessel v, int current)
        {
            var burning = new List<Part>();
            var spent = new List<Part>();
            foreach (Part p in v.parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleEngines e) || !e.EngineIgnited) continue;
                    if (e.flameout) spent.Add(p); else burning.Add(p);
                    break;
                }
            }

            var next = new List<Part>();
            bool nextHasEngine = false, nextHasSeparator = false;
            foreach (Part p in v.parts)
            {
                if (p.inverseStage != current - 1) continue;
                bool engine = p.FindModuleImplementing<ModuleEngines>() != null;
                bool separator = p.FindModuleImplementing<ModuleDecouplerBase>() != null || p.FindModuleImplementing<LaunchClamp>() != null;
                if (!engine && !separator) continue;
                next.Add(p);
                nextHasEngine |= engine;
                nextHasSeparator |= separator;
            }
            // A stage holding only parachutes or fairings is left for the pilot (or the AI) to trigger deliberately.
            if (!nextHasEngine && !nextHasSeparator) return false;

            // Staging only helps while some engine is still waiting to be lit; otherwise it just throws hardware away.
            bool engineWaiting = false;
            foreach (Part p in v.parts)
            {
                if (p.inverseStage >= current) continue;
                ModuleEngines e = p.FindModuleImplementing<ModuleEngines>();
                if (e != null && !e.EngineIgnited) { engineWaiting = true; break; }
            }
            if (!engineWaiting) return false;

            if (burning.Count == 0) return true;
            if (spent.Count == 0 || !nextHasSeparator) return false;

            // Some engines are dry while others still burn: stage only if the next stage drops dry ones and no live ones.
            var dropped = new HashSet<Part>();
            foreach (Part p in next)
            {
                ModuleDecouplerBase d = p.FindModuleImplementing<ModuleDecouplerBase>();
                if (d == null) continue;
                Part detachRoot = p;
                AttachNode node = d.ExplosiveNode;
                if (node != null && node.attachedPart != null && node.attachedPart != p.parent) detachRoot = node.attachedPart;
                Collect(detachRoot, dropped);
            }
            bool dropsSpent = false;
            foreach (Part p in spent) if (dropped.Contains(p)) { dropsSpent = true; break; }
            if (!dropsSpent) return false;
            foreach (Part p in burning) if (dropped.Contains(p)) return false;
            return true;
        }

        static void Collect(Part root, HashSet<Part> into)
        {
            if (!into.Add(root)) return;
            foreach (Part c in root.children) Collect(c, into);
        }
    }
}
