using System;
using System.Collections.Generic;
using AIBridge.Api;

namespace AIBridge.Pilot
{
    /// <summary>
    /// A running record of what happened in flight (staging, explosions, sphere-of-influence changes...).
    /// An AI polls every few seconds at best, so the game has to remember what it would otherwise miss.
    /// </summary>
    public sealed class FlightLog
    {
        struct Entry { public int Id; public double UT, MET; public string Text; }

        readonly List<Entry> entries = new List<Entry>();
        int nextId = 1;
        bool hooked;
        double lastJointBreakUT = -100, lastStageUT = -100;

        public int LastId => nextId - 1;

        public void Add(string text)
        {
            double ut = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0;
            Vessel v = FlightGlobals.ActiveVessel;
            entries.Add(new Entry { Id = nextId++, UT = ut, MET = v != null ? v.missionTime : 0, Text = text });
            if (entries.Count > 500) entries.RemoveRange(0, entries.Count - 500);
            Autopilot.I?.Narrator.OnLog(text);
        }

        public List<object> Since(int id, int max)
        {
            var list = new List<object>();
            foreach (Entry e in entries)
            {
                if (e.Id <= id) continue;
                list.Add(new JObj().Add("id", e.Id).Add("met", Math.Round(e.MET, 1)).Add("event", e.Text));
            }
            if (list.Count > max) list.RemoveRange(0, list.Count - max);
            return list;
        }

        public void Hook()
        {
            if (hooked) return;
            hooked = true;
            GameEvents.onStageActivate.Add(OnStage);
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onCrash.Add(OnCrash);
            GameEvents.onCrashSplashdown.Add(OnCrash);
            GameEvents.onCrewKilled.Add(OnCrewKilled);
            GameEvents.onVesselSituationChange.Add(OnSituation);
            GameEvents.onVesselSOIChanged.Add(OnSoi);
            GameEvents.onPartCouple.Add(OnCouple);
            GameEvents.onPartJointBreak.Add(OnJointBreak);
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onLaunch.Add(OnLaunch);
        }

        public void Unhook()
        {
            if (!hooked) return;
            hooked = false;
            GameEvents.onStageActivate.Remove(OnStage);
            GameEvents.onPartDie.Remove(OnPartDie);
            GameEvents.onCrash.Remove(OnCrash);
            GameEvents.onCrashSplashdown.Remove(OnCrash);
            GameEvents.onCrewKilled.Remove(OnCrewKilled);
            GameEvents.onVesselSituationChange.Remove(OnSituation);
            GameEvents.onVesselSOIChanged.Remove(OnSoi);
            GameEvents.onPartCouple.Remove(OnCouple);
            GameEvents.onPartJointBreak.Remove(OnJointBreak);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onLaunch.Remove(OnLaunch);
        }

        static bool IsActive(Vessel v) => v != null && v == FlightGlobals.ActiveVessel;

        void OnStage(int stage)
        {
            lastStageUT = Planetarium.GetUniversalTime();
            Add("stage " + stage + " activated");
        }

        void OnPartDie(Part p)
        {
            if (p != null && IsActive(p.vessel)) Add("part destroyed: " + p.partInfo.title);
        }

        void OnCrash(EventReport r)
        {
            if (r?.origin == null || !IsActive(r.origin.vessel)) return;
            // Names of planets arrive with a grammar tag on the end ("Kerbin^N"); drop it.
            string other = string.IsNullOrEmpty(r.other) ? "something" : r.other.Split('^')[0];
            Add("crash: " + r.origin.partInfo.title + " hit " + other);
        }

        void OnCrewKilled(EventReport r) => Add("crew member killed: " + (r?.sender ?? "unknown"));

        void OnSituation(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> a)
        {
            if (IsActive(a.host)) Add("situation changed: " + a.from + " -> " + a.to);
        }

        void OnSoi(GameEvents.HostedFromToAction<Vessel, CelestialBody> a)
        {
            if (IsActive(a.host)) Add("entered the sphere of influence of " + Places.Name(a.to) + " (left " + Places.Name(a.from) + ")");
        }

        void OnCouple(GameEvents.FromToAction<Part, Part> a)
        {
            if (a.to != null && a.from != null && (IsActive(a.to.vessel) || IsActive(a.from.vessel)))
            {
                // Docking and undocking rebuild the joints around the ports, which looks like breakage.
                lastStageUT = Planetarium.GetUniversalTime();
                Add("docked: " + a.from.partInfo.title + " to " + a.to.partInfo.title);
            }
        }

        void OnJointBreak(PartJoint joint, float force)
        {
            if (joint == null || joint.Host == null || !IsActive(joint.Host.vessel)) return;
            // (how far it had been pulled apart when it went, for the account of the craft's strain)
            Api.StructureApi.Broke(joint, force);
            // Decouplers break their own joint when they fire; that is not a failure.
            if (IsSeparator(joint.Host) || IsSeparator(joint.Target)) return;
            double ut = Planetarium.GetUniversalTime();
            // Staging rebuilds the joints of legs and wheels; those are not failures either.
            if (Math.Abs(ut - lastStageUT) < 1.5 || joint.Host.FindModuleImplementing<ModuleWheelBase>() != null) return;
            if (ut - lastJointBreakUT < 1.0) return;
            lastJointBreakUT = ut;
            Add("structural failure at " + joint.Host.partInfo.title);
        }

        static bool IsSeparator(Part p) =>
            p != null && (p.FindModuleImplementing<ModuleDecouplerBase>() != null || p.FindModuleImplementing<LaunchClamp>() != null || p.FindModuleImplementing<ModuleDockingNode>() != null);

        void OnVesselChange(Vessel v)
        {
            if (v != null) Add("now controlling '" + v.GetDisplayName() + "'");
        }

        void OnLaunch(EventReport r)
        {
            // The game also reports a "launch" for the new vessel that staging creates in mid-flight.
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null && v.radarAltitude > 500 && v.missionTime > 30) return;
            Add("lift-off");
        }
    }
}
