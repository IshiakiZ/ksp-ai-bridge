using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.Localization;

namespace AIBridge.Api
{
    /// <summary>Science experiments: see what is aboard, run them without the results popup, transmit the data.</summary>
    public static class ScienceApi
    {
        static BridgeHost host;
        static MethodInfo gatherData;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("science", "Experiments and stored data on the active vessel.", a => Status(FlightApi.Active())).AlwaysAllowed = true;
            Rpc.Async("science_run", "Run experiments. params: {experiment} (omit for every experiment that can run here)", Run);
            Rpc.Sync("science_transmit", "Transmit stored science data with the best antenna. params: {experiment}", Transmit);
            Rpc.Sync("science_reset", "Reset experiments so they can run again, discarding their data. params: {experiment}", Reset);
        }

        static IEnumerable<ModuleScienceExperiment> Experiments(Vessel v, string filter)
        {
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleScienceExperiment exp) || exp.experiment == null) continue;
                    if (!string.IsNullOrEmpty(filter) &&
                        exp.experimentID.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        Localizer.Format(exp.experiment.experimentTitle).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.partInfo.title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    yield return exp;
                }
        }

        static JObj DataInfo(ScienceData d)
        {
            var o = new JObj().Add("subject", d.title);
            ScienceSubject subject = ResearchAndDevelopment.GetSubjectByID(d.subjectID);
            if (subject != null && ResearchAndDevelopment.Instance != null)
            {
                o.Add("value_if_recovered", Math.Round(ResearchAndDevelopment.GetScienceValue(d.dataAmount, subject, 1f), 1));
                o.Add("value_if_transmitted", Math.Round(ResearchAndDevelopment.GetScienceValue(d.dataAmount, subject, d.baseTransmitValue), 1));
            }
            return o;
        }

        static object Status(Vessel v)
        {
            ExperimentSituations situation = ScienceUtil.GetExperimentSituation(v);
            var o = new JObj();
            o.Add("situation", situation.ToString());
            o.Add("body", v.mainBody.bodyName);
            o.Add("biome", ScienceUtil.GetExperimentBiome(v.mainBody, v.latitude, v.longitude));
            if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX) o.Add("note", "sandbox save: science points are not tracked");
            var list = new List<object>();
            foreach (ModuleScienceExperiment exp in Experiments(v, null))
            {
                var e = new JObj();
                e.Add("experiment", exp.experimentID);
                e.Add("title", Localizer.Format(exp.experiment.experimentTitle));
                e.Add("part", exp.part.partInfo.title);
                bool available = exp.experiment.IsAvailableWhile(situation, v.mainBody);
                string state = exp.Inoperable ? "inoperable (needs a scientist to restore)" : exp.GetScienceCount() > 0 ? "holding data" : exp.Deployed ? "used (reset to run again)" : available ? "ready" : "cannot run in this situation";
                e.Add("state", state);
                foreach (ScienceData d in exp.GetData()) e.Add("data", DataInfo(d));
                list.Add(e);
            }
            o.Add("experiments", list);
            var stored = new List<object>();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleScienceContainer c)
                        foreach (ScienceData d in c.GetData()) stored.Add(DataInfo(d).Add("stored_in", p.partInfo.title));
            if (stored.Count > 0) o.Add("stored_data", stored);
            IScienceDataTransmitter tx = ScienceUtil.GetBestTransmitter(v);
            o.Add("can_transmit", tx != null && tx.CanTransmit());
            return o;
        }

        static IEnumerator Run(Call c)
        {
            Vessel v = FlightApi.Active();
            string filter = c.Args.Str("experiment");
            ExperimentSituations situation = ScienceUtil.GetExperimentSituation(v);
            if (gatherData == null) gatherData = typeof(ModuleScienceExperiment).GetMethod("gatherData", BindingFlags.NonPublic | BindingFlags.Instance);

            var ran = new List<object>();
            var skipped = new List<object>();
            foreach (ModuleScienceExperiment exp in new List<ModuleScienceExperiment>(Experiments(v, filter)))
            {
                string title = Localizer.Format(exp.experiment.experimentTitle);
                if (exp.Inoperable) { skipped.Add(title + ": inoperable"); continue; }
                if (exp.GetScienceCount() > 0) { skipped.Add(title + ": already holds data"); continue; }
                if (exp.Deployed && !exp.IsRerunnable()) { skipped.Add(title + ": already used"); continue; }
                if (!exp.experiment.IsAvailableWhile(situation, v.mainBody)) { skipped.Add(title + ": not possible while " + situation); continue; }
                // gatherData(false) is the stock routine without the results window.
                if (gatherData != null) exp.StartCoroutine((IEnumerator)gatherData.Invoke(exp, new object[] { false }));
                else exp.DeployExperiment();
                ran.Add(title);
            }
            if (ran.Count == 0 && skipped.Count == 0) throw new ApiException(string.IsNullOrEmpty(filter) ? "This vessel carries no science experiments." : "No experiment matches '" + filter + "'.");
            yield return new WaitSec(1.5f);
            host.Note("ran " + ran.Count + " experiment(s)");
            var o = new JObj().Add("ran", ran);
            if (skipped.Count > 0) o.Add("skipped", skipped);
            o.Add("now", Status(v));
            c.Result = o;
        }

        static object Transmit(Args a)
        {
            Vessel v = FlightApi.Active();
            IScienceDataTransmitter tx = ScienceUtil.GetBestTransmitter(v);
            if (tx == null) throw new ApiException("This vessel has no antenna that can transmit science.");
            if (!tx.CanTransmit()) throw new ApiException("The antenna cannot transmit right now (no connection to Kerbin, or not enough electric charge).");
            string filter = a.Str("experiment");
            var queue = new List<ScienceData>();
            foreach (ModuleScienceExperiment exp in Experiments(v, filter))
                foreach (ScienceData d in exp.GetData()) { queue.Add(d); exp.DumpData(d); }
            if (string.IsNullOrEmpty(filter))
                foreach (Part p in v.parts)
                    foreach (PartModule m in p.Modules)
                        if (m is ModuleScienceContainer c)
                            foreach (ScienceData d in c.GetData()) { queue.Add(d); c.DumpData(d); }
            if (queue.Count == 0) throw new ApiException("There is no science data aboard to transmit.");
            var titles = new List<object>();
            foreach (ScienceData d in queue) titles.Add(d.title);
            tx.TransmitData(queue);
            host.Note("transmitting " + queue.Count + " science result(s)");
            return new JObj().Add("transmitting", titles);
        }

        static object Reset(Args a)
        {
            Vessel v = FlightApi.Active();
            int count = 0;
            foreach (ModuleScienceExperiment exp in Experiments(v, a.Str("experiment")))
            {
                if (!exp.Deployed && exp.GetScienceCount() == 0) continue;
                if (!exp.resettable || exp.Inoperable) continue;
                exp.ResetExperiment();
                count++;
            }
            return new JObj().Add("reset", count);
        }
    }
}
