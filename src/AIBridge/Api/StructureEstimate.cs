using System;
using System.Collections.Generic;
using CompoundParts;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// The editor's estimate of how a craft will hold together under its own thrust, stage by stage. (In flight the same
    /// command measures it instead: see StructureApi.)
    ///
    /// The game joins each part to its parent with a joint that is, as it sets it up, a ball joint as good as rigid along
    /// its length with a spring against turning; one 2.5 m or wider is three of those round the rim, which cannot turn;
    /// rigid attachment locks a joint outright. A strut, an autostrut and the joint a closed fairing makes between its base
    /// and what it holds are each one more joint of the same make (a size-one surface joint: 24,000 kN m a radian against
    /// turning), between two parts that are already joined through others. So each pins two parts together at one point
    /// and adds a spring between them. Where that point is off the line of the joints it crosses, those joints can no
    /// longer turn freely: two parts pinned at two places, a joint and a strut, cannot turn against each other at all,
    /// while a long run of joints crossed by one strut can still zigzag between its ends, which only their own springs
    /// resist. All of that is worked out together here: the joints a strut, autostrut or fairing crosses are solved as
    /// one small system (rotations at each joint, a spring at each joint and each link, each link's pin held by a very
    /// stiff spring), and the others each on its own, as before.
    ///
    /// Folding: a craft tilted a little at its joints is tipped further by the push that goes through them, while their
    /// springs hold it back. How far the push tips it is a quadratic in the joints' turns (each pushed part's sideways
    /// shift times the load on it), how hard the springs hold it back another; the ratio given is the sum, over the ways
    /// the craft can bend, of the first over the second, in the softest direction (Dunkerley's sum). With no struts it
    /// is exactly the ratio this estimate has always given: each joint's pull times the distance to the middle of what it
    /// pushes, over its spring, added up. 1 or more folds: a thin-necked test craft scored 1.4 and folded, one with a
    /// single thin joint 0.33 and flew.
    ///
    /// Stages: the craft is taken as it is after each stage that lights or drops something has fired, with the parts
    /// still on it and the engines burning then, at the start of that stage's burn and at its end (tanks as the game's
    /// own delta-v reckoning has them), since a stage pushes hardest on what it carries when its own tanks are nearly
    /// empty. Thrust is the engines' rated vacuum thrust as their limiters leave it.
    /// </summary>
    static class StructureEstimate
    {
        const double G = 9.81;
        // The stiffness of a joint's spring against turning, for each unit of its size factor: the game's 1500, times the 10 its
        // flight scene multiplies by (read off joints in flight: 60,000 for a 1.25 m stack joint, 24,000 for a part on a surface).
        const double Stiff = 15000.0;
        // What struts and autostruts are made as: a size-one joint on a surface.
        const double LinkSpring = Stiff * 2.0 * 0.8;
        // Joints crossed by struts that are worked out together, at most (each is three unknowns): beyond it they are taken as rigid.
        const int MostWorkedOut = 200;

        static double R(double v, int digits = 1) => double.IsNaN(v) || double.IsInfinity(v) ? double.NaN : Math.Round(v, digits);

        // ---------------------------------------------------------------- what is worked out

        sealed class Joint
        {
            public Part Part;                         // the part on its far side from the root
            public Vector3d At;                       // where it is
            public double Spring;                     // against turning, kN m a radian
            public bool Locked, Three;
            public double Far, Pull, Strength, Moment, Turning;     // tonnes beyond it; kN through it; kN it holds; pull x reach to the pushed mass (kN m); kN m from a sideways g
            public Vector3d ThrustMoment;             // what thrust alone bends it with, where something pushes off to one side of it (kN m)
            public bool FarPushed;                    // which side the push goes into: the part's own side, or the root's
            public int Index = -1;                    // among the joints worked out together, where a link crosses it
            public double Share, BendPerG, ThrustBend;    // of folding; degrees for a sideways g; degrees from thrust
            public List<string> HeldBy;
        }

        sealed class Link
        {
            public string Kind, Says;
            public Part A, B;
            public Vector3d At;
            public double Spring;
            public readonly List<Joint> Path = new List<Joint>();
            public readonly List<int> Sign = new List<int>();
        }

        sealed class Phase
        {
            public int Stage, Order;
            public HashSet<Part> Present, Firing;
        }

        sealed class Case
        {
            public Phase Phase;
            public bool End;
            public double Mass, Push, Folding;
            public double WorstPull, WorstBend, SumBend, WorstThrustBend;
            public readonly List<Joint> Joints = new List<Joint>();
            public readonly List<Link> Links = new List<Link>();
            public int Struts, Autostruts, Fairings, Crossed;
            public bool Rough;
        }

        public sealed class Result
        {
            public JObj Full, Brief;
        }

        // ---------------------------------------------------------------- the reply

        public static JObj Estimate(ShipConstruct ship, int limit, string only) => Work(ship, limit, only).Full;

        public static JObj Brief(ShipConstruct ship)
        {
            try
            {
                if (ship == null || ship.parts.Count < 2) return null;
                return Work(ship, 1, null).Brief;
            }
            catch (Exception ex) { BridgeHost.LogWarn("structure estimate failed: " + ex); return null; }
        }

        static Result Work(ShipConstruct ship, int limit, string only)
        {
            Dictionary<Part, DeltaVPartInfo> info = null;
            VesselDeltaV dv = ship.vesselDeltaV;
            try
            {
                if (dv != null && dv.IsReady && dv.PartInfo != null && dv.PartInfo.Count > 0)
                {
                    info = new Dictionary<Part, DeltaVPartInfo>();
                    foreach (DeltaVPartInfo i in dv.PartInfo) if (i != null && i.part != null) info[i.part] = i;
                }
            }
            catch (Exception) { info = null; }

            List<Phase> phases = Phases(ship, info, dv);
            var cases = new List<Case>();
            foreach (Phase ph in phases)
            {
                cases.Add(Work(ph, false, info));
                if (info != null) cases.Add(Work(ph, true, info));
            }
            Case lift = cases[0];
            Case worst = lift;
            foreach (Case c in cases) if (c.Folding > worst.Folding) worst = c;
            double hardest = 0, bendiest = 0, bentByThrust = 0;
            Case hardestAt = lift, bentAt = lift;
            foreach (Case c in cases)
            {
                if (c.WorstPull > hardest) { hardest = c.WorstPull; hardestAt = c; }
                bendiest = Math.Max(bendiest, c.WorstBend);
                if (c.WorstThrustBend > bentByThrust) { bentByThrust = c.WorstThrustBend; bentAt = c; }
            }

            Vector3 box = ShipConstruction.CalculateCraftSize(ship);
            float longest = Mathf.Max(box.x, Mathf.Max(box.y, box.z));
            float across = Mathf.Max(0.3f, box.x + box.y + box.z - longest - Mathf.Min(box.x, Mathf.Min(box.y, box.z)));

            // ---- the folding, and where it is worst
            JObj folding = null;
            if (worst.Push > 0)
            {
                folding = new JObj().Add("ratio", R(worst.Folding, 2)).Add("means", Means(worst.Folding));
                if (cases.Count > 1)
                {
                    folding.Add("worst_at", Where(worst));
                    if (worst != lift) folding.Add("at_lift_off", R(lift.Folding, 2));
                }
            }

            // ---- notes: what to do about it
            int struts = 0, autostruts = 0, rigid = 0, clamps = 0;
            foreach (Part p in ship.parts)
            {
                if (p is CompoundPart && p.FindModuleImplementing<CModuleStrut>() != null) struts++;
                if (p.autoStrutMode != Part.AutoStrutMode.Off) autostruts++;
                if (p.rigidAttachment) rigid++;
                if (p.FindModuleImplementing<LaunchClamp>() != null) clamps++;
            }
            var notes = new List<object>();
            string where = cases.Count > 1 && worst != lift ? " (worst " + Where(worst) + ")" : "";
            Joint top = MostShare(worst);
            string at = top != null ? " The joint with the largest share is " + Between(top.Part) + "." : "";
            if (worst.Folding >= 1) notes.Add("This craft folds up under its own thrust" + where + ": its joints together are too soft for the push that goes through them (ratio " + R(worst.Folding, 2) + ", and 1 is the point of folding)." + at + " Make the thin part shorter or wider (a 2.5 m joint hardly bends), put the heavy thing lower, run struts past the thin joints (a strut pinned beside a joint it crosses stops it turning; one strut over a long run of joints still lets them zigzag), set autostrut on the parts beyond them, or use less thrust.");
            else if (worst.Folding >= 0.5) notes.Add("This craft is close to folding under its own thrust" + where + " (ratio " + R(worst.Folding, 2) + ", and 1 is the point of folding): expect it to sway, and to go if it is turned hard." + at + " Stiffen the joints with the largest share_of_folding.");
            if (bentByThrust >= 2) notes.Add("One joint is bent " + R(bentByThrust, 1) + " degrees by thrust alone (something pushing off to one side of where it is fixed, as a booster does" + (cases.Count > 1 && bentAt != lift ? ", " + Where(bentAt) : "") + "): run a strut from its far end to the core.");
            if (hardest >= 1.0) notes.Add("One joint has to pass on more than it can hold" + (hardestAt != lift ? " " + Where(hardestAt) : " when the first engines light") + ": it will stretch and then tear off. Join those two parts with something wider (a joint's strength goes up with the size of the attachment), or spread the thrust over more attachments.");
            else if (hardest >= 0.5) notes.Add("The hardest-pulled joint is at " + R(hardest * 100, 0) + "% of what it holds" + (hardestAt != lift ? " " + Where(hardestAt) : "") + ": it should stand a straight climb, with little to spare for a hard turn.");
            if (bendiest >= 3) notes.Add("One joint would bend " + R(bendiest, 1) + " degrees for every g of sideways push: the craft will wobble there and steer badly. Run struts across that joint, set autostrut on the heavy parts beyond it, or join them with something wider (2.5 m joints hardly bend).");
            else if (bendiest >= 1) notes.Add("The most bendable joint gives " + R(bendiest, 1) + " degrees for every g of sideways push: noticeable in a hard turn, harmless in a gentle one.");
            if (longest / across > 12 && struts + autostruts == 0) notes.Add("The craft is " + R(longest / across, 0) + " times as long as it is wide, with no struts: make it shorter and wider, or stiffen it.");
            if (worst.Rough || lift.Rough) notes.Add("So many joints are crossed by struts and autostruts that the ones crossed were taken as rigid rather than worked out one by one: the folding figure is too low if those joints are still soft.");
            if (info == null && phases.Count > 1) notes.Add("The game's delta-v reckoning was not ready, so every stage is taken with full tanks (the end of each burn, where a stage pushes hardest on what it carries, is not looked at). Ask again in a moment.");

            // ---- the full reply
            var o = new JObj().Add("editor_craft", ship.shipName).Add("measured", false).Add("parts", ship.parts.Count).Add("mass_t", R(lift.Mass, 2));
            o.Add("first_engines_kn", R(lift.Push, 0));
            if (lift.Push > 0) o.Add("push_per_tonne_g", R(lift.Push / (lift.Mass * G), 2));
            o.Add("length_m", R(longest, 1)).Add("width_m", R(across, 1));
            var list = new List<KeyValuePair<double, JObj>>();
            foreach (Joint j in lift.Joints)
            {
                if (!string.IsNullOrEmpty(only) && j.Part.partInfo.title.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0 && j.Part.partInfo.name.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(new KeyValuePair<double, JObj>(Rank(j), Describe(j, lift.Push > 0)));
            }
            list.Sort((x, y) => y.Key.CompareTo(x.Key));
            o.Add("joints", new JObj().Add("count", lift.Joints.Count).Add("hardest_pulled_percent", R(lift.WorstPull * 100, 0)).Add("most_bending_deg_per_g", R(lift.WorstBend, 2))
                .Add("all_bends_added_deg_per_g", R(lift.SumBend, 1)).Add("most_bent_by_thrust_deg", R(lift.WorstThrustBend, 2)).Add("weakest", Top(list, limit)));
            if (folding != null) o.Add("folding_under_thrust", folding);
            if (phases.Count > 1 || info != null) o.Add("stages", StageTable(cases, info != null, true));
            var stiff = new JObj().Add("struts", struts).Add("autostruts", autostruts).Add("rigid_attachments", rigid).Add("launch_clamps", clamps);
            if (lift.Links.Count > 0) stiff.Add("in_the_estimate", Counted(lift));
            o.Add("stiffening", stiff);
            if (notes.Count > 0) o.Add("notes", notes);
            o.Add("how_to_read", "An ESTIMATE made in the editor, where nothing is under load. joints: the craft at lift-off. pull_percent: what the joint must pass on when those engines light, against what it can hold (under 100 it cannot be torn off by thrust alone). bends_deg_per_g_sideways: how far the craft would bend at that joint under a sideways push of one g, from the stiffness the game gives the joint and whatever struts cross it; a gravity turn is a few tenths of a g, a hard turn in thick air one or two. folding_under_thrust: whether the craft's own thrust folds it up at its soft joints (1 is the point of folding; each joint's share_of_folding says where), worked out for every stage at the start and the end of its burn: stages lists each. Struts, autostruts and closed fairings are counted as the game makes them: each pins two parts together at one point, with a spring between them. Good for comparing designs and finding the weak place; the real figures come from this same command in flight.");

            // ---- the short one, for the reply to a build
            var b = new JObj();
            if (folding != null) b.Add("folding_under_thrust", folding);
            b.Add("hardest_pulled_joint_percent", R(hardest * 100, 0)).Add("most_bendable_joint_deg_per_g", R(bendiest, 2)).Add("most_bent_by_thrust_deg", R(bentByThrust, 2));
            if (phases.Count > 1) b.Add("by_stage", StageTable(cases, info != null, false));
            if (lift.Links.Count > 0) b.Add("stiffening_counted", Counted(lift));
            if (notes.Count > 0) b.Add("notes", notes);
            b.Add("more", "an estimate; the structure command lists every joint");
            return new Result { Full = o, Brief = b };
        }

        static string Means(double folding) => folding >= 1 ? "it folds up under its own thrust" : folding >= 0.5 ? "close to folding: it will sway and may go" : folding >= 0.25 ? "holds, and sways a little" : "stands up straight";

        /// <summary>Where in the flight a case is: lift-off, or which stage and when in its burn.</summary>
        static string Where(Case c)
        {
            if (c.Phase.Order == 1) return c.End ? "at the end of the first stage's burn" : "at lift-off";
            return "in stage " + c.Phase.Stage + " (the " + Ordinal(c.Phase.Order) + " to fire), " + (c.End ? "at the end of its burn" : "as it lights");
        }

        static string Ordinal(int n) => n == 2 ? "second" : n == 3 ? "third" : n == 4 ? "fourth" : n == 5 ? "fifth" : n == 6 ? "sixth" : n + "th";

        static string Between(Part p) => p.parent == null ? "'" + p.partInfo.title + "'" : "'" + p.partInfo.title + "' on '" + p.parent.partInfo.title + "'";

        static Joint MostShare(Case c)
        {
            Joint best = null;
            foreach (Joint j in c.Joints) if (j.Share > 0 && (best == null || j.Share > best.Share)) best = j;
            return best;
        }

        static double Rank(Joint j) => Math.Max(Math.Max(j.Strength > 0 ? j.Pull / j.Strength : 0, j.Share * 2), Math.Max(j.BendPerG, j.ThrustBend) / 6.0);

        static JObj Describe(Joint j, bool pushed)
        {
            Part p = j.Part;
            var o = new JObj().Add("part", p.partInfo.title).Add("id", p.craftID).Add("joined_to", p.parent.partInfo.title).Add("joined", p.attachMode == AttachModes.SRF_ATTACH ? "on its surface" : "end to end");
            o.Add("hangs_on_it_t", R(j.Far, 2));
            o.Add("pull_at_ignition_kn", R(j.Pull, 0)).Add("holds_up_to_kn", R(j.Strength, 0)).Add("pull_percent", R(j.Strength > 0 ? j.Pull / j.Strength * 100 : 0, 0));
            if (j.Three) o.Add("bends", "hardly: a wide joint is three joints round the rim");
            else if (j.Locked) o.Add("bends", "not at all: rigid attachment is on");
            else
            {
                o.Add("bends_deg_per_g_sideways", R(j.BendPerG, 2));
                if (pushed) o.Add("bends_deg_under_thrust", R(j.ThrustBend, 2)).Add("share_of_folding", R(Math.Max(0, j.Share), 2));
            }
            if (j.HeldBy != null) o.Add("crossed_by", j.HeldBy.Count <= 3 ? (object)j.HeldBy : new List<string>(j.HeldBy.GetRange(0, 3)) { "and " + (j.HeldBy.Count - 3) + " more" });
            if (p.autoStrutMode != Part.AutoStrutMode.Off) o.Add("autostrut", p.autoStrutMode.ToString());
            return o;
        }

        static List<object> Top(List<KeyValuePair<double, JObj>> sorted, int limit)
        {
            var list = new List<object>();
            for (int n = 0; n < sorted.Count && n < limit; n++) list.Add(sorted[n].Value);
            return list;
        }

        static string Counted(Case c)
        {
            var bits = new List<string>();
            if (c.Struts > 0) bits.Add(c.Struts + (c.Struts == 1 ? " strut" : " struts"));
            if (c.Autostruts > 0) bits.Add(c.Autostruts + (c.Autostruts == 1 ? " autostrut" : " autostruts"));
            if (c.Fairings > 0) bits.Add(c.Fairings + (c.Fairings == 1 ? " closed fairing holding what is in it" : " closed fairings holding what is in them"));
            return string.Join(", ", bits.ToArray()) + ", across " + c.Crossed + (c.Crossed == 1 ? " joint" : " joints") + (c.Rough ? " (taken as rigid: too many to work out)" : "");
        }

        /// <summary>A line for each stage: what burns, what it carries, and how near it comes to folding at the start and the end of its burn.</summary>
        static List<object> StageTable(List<Case> cases, bool ends, bool full)
        {
            var rows = new List<object>();
            for (int n = 0; n < cases.Count; n++)
            {
                Case start = cases[n];
                Case end = ends && n + 1 < cases.Count && cases[n + 1].Phase == start.Phase ? cases[n + 1] : null;
                if (end != null) n++;
                var row = new JObj().Add("ksp_stage", start.Phase.Stage).Add("fires", start.Phase.Order);
                if (full) row.Add("engines", Engines(start.Phase));
                row.Add("thrust_kn", R(start.Push, 0)).Add("mass_t", R(start.Mass, 2));
                if (end != null) row.Add("mass_at_burnout_t", R(end.Mass, 2));
                if (full && start.Mass > 0) row.Add("push_per_tonne_g", R(start.Push / (start.Mass * G), 2));
                if (full && end != null && end.Mass > 0) row.Add("push_per_tonne_g_at_burnout", R(end.Push / (end.Mass * G), 2));
                row.Add(end != null ? "folding_as_it_lights" : "folding", R(start.Folding, 2));
                if (end != null) row.Add("folding_at_burnout", R(end.Folding, 2));
                Case worse = end != null && end.Folding > start.Folding ? end : start;
                row.Add("hardest_pull_percent", R(Math.Max(start.WorstPull, end != null ? end.WorstPull : 0) * 100, 0));
                if (full) row.Add("most_bent_by_thrust_deg", R(Math.Max(start.WorstThrustBend, end != null ? end.WorstThrustBend : 0), 2));
                Joint most = MostShare(worse);
                if (most != null && worse.Folding >= 0.05) row.Add("largest_share_of_folding", Between(most.Part) + " " + R(most.Share, 2));
                if (full && worse.Links.Count > 0) row.Add("stiffening_counted", Counted(worse));
                rows.Add(row);
            }
            return rows;
        }

        static string Engines(Phase ph)
        {
            var count = new Dictionary<string, int>();
            var order = new List<string>();
            foreach (Part p in ph.Firing)
            {
                string t = p.partInfo.title;
                if (!count.ContainsKey(t)) { count[t] = 0; order.Add(t); }
                count[t]++;
            }
            var bits = new List<string>();
            foreach (string t in order) bits.Add(t + (count[t] > 1 ? " x" + count[t] : ""));
            return string.Join(", ", bits.ToArray());
        }

        // ---------------------------------------------------------------- the stages

        static double ThrustOf(Part p)
        {
            double t = 0;
            foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>()) t += e.maxThrust * e.thrustPercentage / 100.0;
            return t;
        }

        static bool Decoupler(Part p)
        {
            foreach (PartModule m in p.Modules)
                if ((m is ModuleDecouple d && d.stagingEnabled) || (m is ModuleAnchoredDecoupler a && a.stagingEnabled) || m is LaunchClamp) return true;
            return false;
        }

        /// <summary>The stage at whose firing a part leaves the craft (-1: never): the game's own reckoning, or else the first decoupler on its way to the root to fire.</summary>
        static int Leaves(Part p, Dictionary<Part, DeltaVPartInfo> info)
        {
            if (info != null && info.TryGetValue(p, out DeltaVPartInfo i)) return i.decoupleStage;
            int stage = -1;
            for (Part q = p; q != null; q = q.parent) if (Decoupler(q)) stage = Math.Max(stage, q.inverseStage);
            return stage;
        }

        /// <summary>
        /// The craft as it is after each stage that lights an engine or drops something has fired: the parts still on it and the
        /// engines burning. (KSP's stages count down: the highest number fires first.)
        /// </summary>
        static List<Phase> Phases(ShipConstruct ship, Dictionary<Part, DeltaVPartInfo> info, VesselDeltaV dv)
        {
            int highest = -1;
            foreach (Part p in ship.parts) highest = Math.Max(highest, p.inverseStage);
            var leaves = new Dictionary<Part, int>();
            foreach (Part p in ship.parts) leaves[p] = Leaves(p, info);
            var phases = new List<Phase>();
            string last = null;
            for (int s = highest; s >= 0; s--)
            {
                var present = new HashSet<Part>();
                foreach (Part p in ship.parts) if (leaves[p] < s) present.Add(p);
                var firing = new HashSet<Part>();
                DeltaVStageInfo stage = null;
                try { stage = info != null && dv != null ? dv.GetStage(s) : null; } catch (Exception) { }
                if (stage != null && stage.enginesActiveInStage != null && stage.enginesActiveInStage.Count > 0)
                {
                    foreach (DeltaVEngineInfo e in stage.enginesActiveInStage)
                        if (e != null && e.engine != null && present.Contains(e.engine.part) && ThrustOf(e.engine.part) > 0) firing.Add(e.engine.part);
                }
                else
                    foreach (Part p in present) if (ThrustOf(p) > 0 && p.inverseStage >= s) firing.Add(p);
                if (firing.Count == 0) continue;
                // (a stage that changes nothing, a parachute's say, is not a new case)
                var key = new System.Text.StringBuilder();
                foreach (Part p in ship.parts) key.Append(present.Contains(p) ? (firing.Contains(p) ? 'F' : 'P') : '.');
                if (key.ToString() == last) continue;
                last = key.ToString();
                phases.Add(new Phase { Stage = s, Order = phases.Count + 1, Present = present, Firing = firing });
            }
            if (phases.Count == 0)
            {
                // (nothing staged to burn: the whole craft, with whatever engines it has)
                var all = new HashSet<Part>(ship.parts);
                var firing = new HashSet<Part>();
                int first = -1;
                foreach (Part p in ship.parts) if (ThrustOf(p) > 0 && p.inverseStage > first) first = p.inverseStage;
                foreach (Part p in ship.parts) if (ThrustOf(p) > 0 && p.inverseStage == first) firing.Add(p);
                phases.Add(new Phase { Stage = Math.Max(0, first), Order = 1, Present = all, Firing = firing });
            }
            return phases;
        }

        // ---------------------------------------------------------------- one stage, at the start or the end of its burn

        static Case Work(Phase ph, bool end, Dictionary<Part, DeltaVPartInfo> info)
        {
            var c = new Case { Phase = ph, End = end };
            double Fuel(Part p)
            {
                if (info != null && info.TryGetValue(p, out DeltaVPartInfo i))
                {
                    try { return end ? i.GetStageEndMass(ph.Stage) : i.GetStageStartMass(ph.Stage); }
                    catch (Exception) { }
                }
                return p.GetResourceMass();
            }

            // ---- the craft as it stands: the parts still on it, from the one whose branch has the thrust
            Part root = null;
            double most = -1;
            foreach (Part p in ph.Present)
            {
                if (p.parent != null && ph.Present.Contains(p.parent)) continue;
                double t = 0;
                foreach (Part q in Below(p, ph.Present)) if (ph.Firing.Contains(q)) t += ThrustOf(q);
                if (t > most) { most = t; root = p; }
            }
            if (root == null) return c;
            List<Part> order = Below(root, ph.Present);
            var inTree = new HashSet<Part>(order);
            var mass = new Dictionary<Part, double>();
            var moment = new Dictionary<Part, Vector3d>();
            var thrust = new Dictionary<Part, double>();
            var depth = new Dictionary<Part, int>();
            var body = new Dictionary<Part, Part>();
            var enter = new Dictionary<Part, int>();
            var leave = new Dictionary<Part, int>();
            int clock = 0;
            Sum(root, 0);
            void Sum(Part p, int d)
            {
                depth[p] = d;
                enter[p] = clock++;
                // (a part without physics of its own rides on its parent's body: it has no joint, and what is on it is joined to that body)
                body[p] = p == root || p.PhysicsSignificance != 1 ? p : body[p.parent];
                double m = p.mass + Fuel(p);
                Vector3d at = (Vector3d)p.transform.position * m;
                double t = ph.Firing.Contains(p) ? ThrustOf(p) : 0;
                foreach (Part child in p.children)
                {
                    if (!inTree.Contains(child)) continue;
                    Sum(child, d + 1);
                    m += mass[child]; at += moment[child]; t += thrust[child];
                }
                mass[p] = m; moment[p] = at; thrust[p] = t;
                leave[p] = clock++;
            }
            double total = mass[root], push = thrust[root];
            Vector3d whole = moment[root];
            c.Mass = total; c.Push = push;
            bool Holds(Part a, Part b) => enter[a] <= enter[b] && leave[b] <= leave[a];           // b is a on or below a

            // ---- every joint as the game will make it
            // Which way the craft is pushed: nose first, which is up in the rocket hangar and forward in the aircraft one.
            Vector3d ahead = EditorDriver.editorFacility == EditorFacility.SPH ? Vector3d.forward : Vector3d.up;
            var jointOf = new Dictionary<Part, Joint>();
            foreach (Part p in order)
            {
                if (p == root || p is CompoundPart || body[p] != p) continue;
                // Its strength is the weaker part's, times a factor for the size and kind of the joint; its spring against turning is as
                // stiff as that same factor says; and a wide end-to-end joint is three joints set apart round the rim, which cannot turn.
                bool surface = p.attachMode == AttachModes.SRF_ATTACH;
                AttachNode node = surface ? p.srfAttachNode : p.FindAttachNodeByPart(p.parent);
                // (of the two parts' attachment points, the game goes by the smaller)
                AttachNode other = surface ? null : p.parent.FindAttachNodeByPart(p);
                if (node != null && other != null && other.size < node.size) node = other;
                int size = node != null ? node.size : 1;
                double factor = (size + 1.0) * (surface ? 0.8 : 2.0);
                var j = new Joint { Part = p, Spring = Stiff * factor };
                j.Three = !surface && size >= 2 && p.FindModuleImplementing<ModuleDockingNode>() == null && p.parent.FindModuleImplementing<ModuleDockingNode>() == null;
                j.Locked = j.Three || p.rigidAttachment;
                j.Strength = Math.Min(p.breakingForce, p.parent.breakingForce) * factor * PhysicsGlobals.JointBreakForceFactor;
                // What it must pass on: everything on its far side is pushed along by the whole craft's thrust in proportion to its mass,
                // less whatever thrust it makes itself.
                double far = mass[p], near = total - far;
                j.Far = far;
                j.Pull = push > 0 ? Math.Abs(far / total * push - thrust[p]) : far * G;
                Vector3d here = node != null ? (Vector3d)node.owner.transform.TransformPoint(node.position) : (Vector3d)p.transform.position;
                j.At = here;
                // How hard a sideways shove of one g turns it: the lighter-armed side's mass times how far off its middle is.
                double farArm = far > 1e-6 ? (moment[p] / far - here).magnitude : 0;
                double nearArm = near > 1e-6 ? ((whole - moment[p]) / near - here).magnitude : 0;
                j.Turning = Math.Min(far * farArm, near * nearArm) * G;
                // What its own thrust does to it. The side that is pushed along (the one making less thrust for its mass) leans on the
                // joint with that pull, at the middle of its mass. Where that middle is off to one side of the joint, the joint is bent
                // outright (a booster on the side of a core); where it is straight ahead, the pull times the distance ahead is what
                // tips the craft further once it is tilted.
                j.FarPushed = far > 1e-6 && near > 1e-6 ? thrust[p] / far <= (push - thrust[p]) / near : far > 1e-6;
                Vector3d middle = j.FarPushed ? moment[p] / far : (whole - moment[p]) / Math.Max(near, 1e-6);
                Vector3d reach = middle - here;
                double along = Vector3d.Dot(reach, ahead);
                Vector3d aside = reach - along * ahead;
                j.Moment = push > 0 ? j.Pull * Math.Abs(along) : 0;
                j.ThrustMoment = push > 0 ? Vector3d.Cross(aside, ahead) * j.Pull : Vector3d.zero;
                jointOf[p] = j;
                c.Joints.Add(j);
            }

            // ---- struts, autostruts and closed fairings: each one more joint, pinning two parts together at one point
            foreach (Part p in order)
            {
                if (!(p is CompoundPart cp) || cp.target == null || !inTree.Contains(cp.target) || p.FindModuleImplementing<CModuleStrut>() == null) continue;
                Part a = body[p], b = body[cp.target];
                if (a == b) continue;
                // (where the game pins them: the far end's place on the target, as the target sees it, taken in the frame of the part the
                // strut is on. That is the game's own doing, and it can put the pin a long way from either end; it is a pin all the same.)
                Vector3 far = cp.transform.TransformPoint(cp.targetPosition);
                Vector3d pin = a.transform.TransformPoint(b.transform.InverseTransformPoint(far));
                if (AddLink(c, "strut", "a strut from '" + p.parent.partInfo.title + "' to '" + cp.target.partInfo.title + "'", a, b, pin, LinkSpring, jointOf, depth))
                    c.Struts++;
            }
            foreach (Part p in order)
            {
                if (p.autoStrutMode == Part.AutoStrutMode.Off || p is CompoundPart) continue;
                bool allowed = true;
                try { allowed = p.AllowAutoStruts(); } catch (Exception) { }
                if (!allowed) continue;
                Part anchor = AutoStrutTo(p, root, inTree, q => q.mass + Fuel(q));
                if (anchor == null || anchor == p) continue;
                var anchors = new List<Part> { anchor };
                // (the game struts to every one of the anchor's symmetry copies too)
                if (anchor.symmetryCounterparts != null && !anchor.symmetryCounterparts.Contains(p))
                    foreach (Part s in anchor.symmetryCounterparts) if (s != null && inTree.Contains(s) && s != p) anchors.Add(s);
                bool any = false;
                foreach (Part s in anchors)
                    if (body[p] != body[s] && AddLink(c, "autostrut", "an autostrut (" + p.autoStrutMode + ") from '" + p.partInfo.title + "' to '" + s.partInfo.title + "'", body[p], body[s], p.transform.TransformPoint(p.strutOffset), LinkSpring, jointOf, depth))
                        any = true;
                if (any) c.Autostruts++;
            }
            foreach (Part f in order)
            {
                var shell = f.FindModuleImplementing<ModuleProceduralFairing>();
                if (shell == null || shell.xSections == null || shell.xSections.Count < 2) continue;
                // (thrown off once its stage has fired)
                if (shell.stagingEnabled && f.inverseStage >= 0 && f.inverseStage >= ph.Stage) continue;
                float h = shell.xSections[shell.xSections.Count - 1].h;
                Vector3d axis = (Vector3d)(f.transform.rotation * shell.axis).normalized;
                Vector3d top = (Vector3d)f.transform.position + axis * h;
                Part held = null;
                int size;
                if (shell.interstageCraftID != 0)
                {
                    foreach (Part q in order) if (q.craftID == shell.interstageCraftID) { held = q; break; }
                    size = (int)shell.xSections[shell.xSections.Count - 1].r;
                }
                else
                {
                    held = FirstDown(top, -axis, h, f, order);
                    size = (int)shell.baseRadius;
                }
                if (held == null || body[held] == body[f]) continue;
                if (AddLink(c, "fairing", "the fairing on '" + f.partInfo.title + "', holding '" + held.partInfo.title + "'", body[f], body[held], top, Stiff * (size + 1.0) * 0.8, jointOf, depth))
                    c.Fairings++;
            }

            // ---- the joints the links cross, worked out together
            var crossed = new List<Joint>();
            foreach (Link l in c.Links)
                foreach (Joint j in l.Path)
                    if (j.Index < 0) { j.Index = crossed.Count; crossed.Add(j); }
            c.Crossed = crossed.Count;
            Vector3d u = Vector3d.Cross(ahead, Math.Abs(ahead.x) < 0.9 ? Vector3d.right : Vector3d.forward).normalized;
            Vector3d v = Vector3d.Cross(ahead, u).normalized;
            double coupled = 0;
            double[] flex = null;
            int n = crossed.Count * 3;
            if (crossed.Count > MostWorkedOut) c.Rough = true;
            else if (crossed.Count > 0)
            {
                flex = new double[n * n];
                double springiest = 0;
                foreach (Joint j in crossed)
                {
                    for (int a = 0; a < 3; a++) flex[(3 * j.Index + a) * n + 3 * j.Index + a] += j.Spring;
                    springiest = Math.Max(springiest, j.Spring);
                }
                foreach (Link l in c.Links)
                {
                    // its own spring against the two parts turning against each other
                    for (int x = 0; x < l.Path.Count; x++)
                        for (int y = 0; y < l.Path.Count; y++)
                        {
                            double k = l.Spring * l.Sign[x] * l.Sign[y];
                            for (int a = 0; a < 3; a++) flex[(3 * l.Path[x].Index + a) * n + 3 * l.Path[y].Index + a] += k;
                        }
                    // its pin, as a spring a million times stiffer than the stiffest joint: the turns at the joints it crosses may not move
                    // the pinned point of one part against the other's
                    double reach = 0.05;
                    foreach (Joint j in l.Path) reach = Math.Max(reach, (l.At - j.At).magnitude);
                    double w = 1e6 * springiest / (reach * reach);
                    for (int x = 0; x < l.Path.Count; x++)
                    {
                        Vector3d rx = l.At - l.Path[x].At;
                        for (int y = 0; y < l.Path.Count; y++)
                        {
                            Vector3d ry = l.At - l.Path[y].At;
                            double s = w * l.Sign[x] * l.Sign[y], dot = Vector3d.Dot(rx, ry);
                            int bx = 3 * l.Path[x].Index, by = 3 * l.Path[y].Index;
                            for (int a = 0; a < 3; a++)
                                for (int bb = 0; bb < 3; bb++)
                                    flex[(bx + a) * n + by + bb] += s * ((a == bb ? dot : 0) - ry[a] * rx[bb]);
                        }
                    }
                }
                if (!Invert(flex, n)) { c.Rough = true; flex = null; }
            }
            if (flex != null)
            {
                // How hard the push tips the craft, between each two joints: the load beyond both, times its reach, where what one pushes
                // holds what the other pushes (the inner one carries the outer one's load); and nothing between joints on different
                // branches. Signs: each joint's turn is taken as its part's side against its parent's, and the push goes into one side or
                // the other.
                var tip = new double[crossed.Count * crossed.Count];
                for (int x = 0; x < crossed.Count; x++)
                    for (int y = 0; y < crossed.Count; y++)
                        tip[x * crossed.Count + y] = Tipping(crossed[x], crossed[y], Holds);
                // the softest way across the craft to bend in
                double[,] sum = new double[3, 3];
                for (int x = 0; x < crossed.Count; x++)
                    for (int y = 0; y < crossed.Count; y++)
                    {
                        double t = tip[x * crossed.Count + y];
                        if (t == 0) continue;
                        for (int a = 0; a < 3; a++)
                            for (int bb = 0; bb < 3; bb++) sum[a, bb] += t * flex[(3 * x + a) * n + 3 * y + bb];
                    }
                Vector3d e = Softest(sum, u, v, out coupled);
                for (int x = 0; x < crossed.Count; x++)
                {
                    double share = 0;
                    for (int y = 0; y < crossed.Count; y++)
                    {
                        double t = tip[x * crossed.Count + y];
                        if (t != 0) share += t * Along(flex, n, x, y, e, e);
                    }
                    Joint j = crossed[x];
                    j.Share = share;
                    // a joint's own bends, with whatever crosses it holding it
                    double[,] own = new double[3, 3];
                    for (int a = 0; a < 3; a++) for (int bb = 0; bb < 3; bb++) own[a, bb] = flex[(3 * x + a) * n + 3 * x + bb];
                    Softest(own, u, v, out double softest);
                    j.BendPerG = j.Turning * Math.Max(0, softest) * Mathf.Rad2Deg;
                    Vector3d turn = Times(flex, n, x, j.ThrustMoment);
                    j.ThrustBend = turn.magnitude * Mathf.Rad2Deg;
                }
            }
            double alone = 0;
            foreach (Joint j in c.Joints)
            {
                if (j.Index >= 0 && flex != null) continue;
                if (j.Locked || j.Index >= 0)
                {
                    // (cannot turn: wide, rigid, or crossed by so many struts that it was taken as rigid)
                    j.Share = 0; j.BendPerG = 0; j.ThrustBend = 0;
                    continue;
                }
                j.Share = j.Moment / j.Spring;
                j.BendPerG = j.Turning / j.Spring * Mathf.Rad2Deg;
                j.ThrustBend = j.ThrustMoment.magnitude / j.Spring * Mathf.Rad2Deg;
                alone += j.Share;
            }
            c.Folding = push > 0 ? coupled + alone : 0;
            foreach (Joint j in c.Joints)
            {
                if (push <= 0) { j.Share = 0; j.ThrustBend = 0; }
                c.WorstPull = Math.Max(c.WorstPull, j.Strength > 0 ? j.Pull / j.Strength : 0);
                c.WorstBend = Math.Max(c.WorstBend, j.BendPerG);
                c.SumBend += j.BendPerG;
                c.WorstThrustBend = Math.Max(c.WorstThrustBend, j.ThrustBend);
            }
            return c;
        }

        /// <summary>A part and everything hung on it that is still on the craft, parents before children.</summary>
        static List<Part> Below(Part top, HashSet<Part> present)
        {
            var list = new List<Part>();
            var open = new Stack<Part>();
            open.Push(top);
            while (open.Count > 0)
            {
                Part p = open.Pop();
                list.Add(p);
                for (int i = p.children.Count - 1; i >= 0; i--) if (present.Contains(p.children[i])) open.Push(p.children[i]);
            }
            return list;
        }

        /// <summary>
        /// One more link between two bodies of the craft: the joints between them (up from each to where their branches meet), each
        /// with the sign of its turn as seen from the first body. False where it crosses no joint that can turn (it then changes nothing).
        /// </summary>
        static bool AddLink(Case c, string kind, string says, Part a, Part b, Vector3d at, double spring, Dictionary<Part, Joint> jointOf, Dictionary<Part, int> depth)
        {
            var l = new Link { Kind = kind, Says = says, A = a, B = b, At = at, Spring = spring };
            Part x = a, y = b;
            void Step(ref Part q, int sign)
            {
                if (jointOf.TryGetValue(q, out Joint j) && !j.Locked) { l.Path.Add(j); l.Sign.Add(sign); }
                q = q.parent;
            }
            while (x != null && y != null && depth[x] > depth[y]) Step(ref x, 1);
            while (x != null && y != null && depth[y] > depth[x]) Step(ref y, -1);
            while (x != null && y != null && x != y) { Step(ref x, 1); Step(ref y, -1); }
            if (l.Path.Count == 0) return false;
            foreach (Joint j in l.Path)
            {
                if (j.HeldBy == null) j.HeldBy = new List<string>();
                j.HeldBy.Add(says);
            }
            c.Links.Add(l);
            return true;
        }

        /// <summary>The part an autostrut runs to, as the game chooses it (null: none).</summary>
        static Part AutoStrutTo(Part p, Part root, HashSet<Part> inTree, Func<Part, double> massOf)
        {
            switch (p.autoStrutMode)
            {
                case Part.AutoStrutMode.Root:
                case Part.AutoStrutMode.ForceRoot:
                    // (never to its own parent)
                    return p.autoStrutExcludeParent && p.parent == root ? null : root;
                case Part.AutoStrutMode.Grandparent:
                case Part.AutoStrutMode.ForceGrandparent:
                    if (p.parent == null) return null;
                    return p.parent.parent != null && inTree.Contains(p.parent.parent) ? p.parent.parent : p.parent;
                case Part.AutoStrutMode.Heaviest:
                case Part.AutoStrutMode.ForceHeaviest:
                {
                    // the heaviest part on the craft but itself (and its parent), the nearer of two that weigh the same
                    Part best = null;
                    double heaviest = -1, nearest = double.MaxValue;
                    foreach (Part q in inTree)
                    {
                        if (q == p || (p.autoStrutExcludeParent && q == p.parent) || q is CompoundPart) continue;
                        double m = massOf(q);
                        double d = (q.transform.position - p.transform.position).magnitude;
                        if (m > heaviest + 1e-4 || (Math.Abs(m - heaviest) <= 1e-4 && d < nearest)) { best = q; heaviest = m; nearest = d; }
                    }
                    return best;
                }
                default:
                    return null;
            }
        }

        /// <summary>The first part a ray from the top of a fairing straight down its middle meets (the game holds that one to the base).</summary>
        static Part FirstDown(Vector3d from, Vector3d down, double length, Part fairing, List<Part> parts)
        {
            Part best = null;
            double nearest = length;
            foreach (Part q in parts)
            {
                // (the base's own model is left out: its truss and the caps up its inside are hidden unless used, and the prefab may have them)
                if (q is CompoundPart || q == fairing) continue;
                Vector3 o = q.transform.InverseTransformPoint((Vector3)from), d = q.transform.InverseTransformDirection((Vector3)down);
                PartGeometry shape = PartGeometry.For(q.partInfo);
                double t;
                if (shape.HasMesh)
                {
                    if (!shape.Raycast(o, d, out Vector3 hit, out Vector3 _)) continue;
                    t = (hit - o).magnitude;
                }
                else if (!Slab(shape.Bounds, o, d.normalized, out t)) continue;
                if (t < nearest) { nearest = t; best = q; }
            }
            return best;
        }

        static bool Slab(Bounds b, Vector3 o, Vector3 d, out double t)
        {
            double near = 0, far = double.MaxValue;
            for (int a = 0; a < 3; a++)
            {
                if (Math.Abs(d[a]) < 1e-9) { if (o[a] < b.min[a] || o[a] > b.max[a]) { t = 0; return false; } continue; }
                double t1 = (b.min[a] - o[a]) / d[a], t2 = (b.max[a] - o[a]) / d[a];
                if (t1 > t2) { double s = t1; t1 = t2; t2 = s; }
                near = Math.Max(near, t1); far = Math.Min(far, t2);
                if (near > far) { t = 0; return false; }
            }
            t = near;
            return true;
        }

        // ---------------------------------------------------------------- the arithmetic

        /// <summary>
        /// How hard the push tips the craft through two joints together (kN m): the load the inner one's pushed side carries beyond the
        /// outer one, times its reach (the outer one's own figure), where one pushed side holds the other; otherwise nothing.
        /// </summary>
        static double Tipping(Joint x, Joint y, Func<Part, Part, bool> holds)
        {
            if (x == y) return x.Moment;
            // each pushed side is the part's own branch (FarPushed) or everything else
            bool xs = x.FarPushed, ys = y.FarPushed;
            Part px = x.Part, py = y.Part;
            double sign = (xs ? 1 : -1) * (ys ? 1 : -1);
            if (xs && ys)
            {
                if (holds(py, px)) return sign * x.Moment;           // x's branch inside y's
                if (holds(px, py)) return sign * y.Moment;
                return 0;
            }
            if (!xs && !ys)
            {
                // everything but x's branch inside everything but y's, when y's branch is inside x's
                if (holds(px, py)) return sign * x.Moment;
                if (holds(py, px)) return sign * y.Moment;
                return 0;
            }
            // one a branch, the other everything else: the branch is inside the rest when the two branches are apart
            Joint branch = xs ? x : y, rest = xs ? y : x;
            if (!holds(branch.Part, rest.Part) && !holds(rest.Part, branch.Part)) return sign * branch.Moment;
            return 0;
        }

        /// <summary>The softest direction across the craft (unit vector in the plane of u and v) for a 3x3 form, and its value.</summary>
        static Vector3d Softest(double[,] m, Vector3d u, Vector3d v, out double most)
        {
            double Q(Vector3d a, Vector3d b)
            {
                double s = 0;
                for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) s += a[i] * m[i, k] * b[k];
                return s;
            }
            double aa = Q(u, u), dd = Q(v, v), bb = 0.5 * (Q(u, v) + Q(v, u));
            double mid = 0.5 * (aa + dd), half = Math.Sqrt(0.25 * (aa - dd) * (aa - dd) + bb * bb);
            most = mid + half;
            double angle = 0.5 * Math.Atan2(2 * bb, aa - dd);
            return (u * Math.Cos(angle) + v * Math.Sin(angle)).normalized;
        }

        /// <summary>a . F(x,y) . b, for the 3x3 block of a flexibility matrix that links joint x to joint y.</summary>
        static double Along(double[] f, int n, int x, int y, Vector3d a, Vector3d b)
        {
            double s = 0;
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++) s += a[i] * f[(3 * x + i) * n + 3 * y + k] * b[k];
            return s;
        }

        static Vector3d Times(double[] f, int n, int x, Vector3d m)
        {
            var r = new Vector3d();
            for (int i = 0; i < 3; i++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += f[(3 * x + i) * n + 3 * x + k] * m[k];
                r[i] = s;
            }
            return r;
        }

        /// <summary>A symmetric positive-definite matrix (n by n, row after row) replaced by its inverse. False if it is not positive definite.</summary>
        static bool Invert(double[] a, int n)
        {
            // a = L L': L in the lower triangle
            for (int j = 0; j < n; j++)
            {
                double d = a[j * n + j];
                for (int k = 0; k < j; k++) d -= a[j * n + k] * a[j * n + k];
                if (!(d > 0)) return false;
                d = Math.Sqrt(d);
                a[j * n + j] = d;
                for (int i = j + 1; i < n; i++)
                {
                    double s = a[i * n + j];
                    for (int k = 0; k < j; k++) s -= a[i * n + k] * a[j * n + k];
                    a[i * n + j] = s / d;
                }
            }
            // M = the inverse of L (lower triangular)
            var m = new double[n * n];
            for (int j = 0; j < n; j++)
            {
                m[j * n + j] = 1.0 / a[j * n + j];
                for (int i = j + 1; i < n; i++)
                {
                    double s = 0;
                    for (int k = j; k < i; k++) s -= a[i * n + k] * m[k * n + j];
                    m[i * n + j] = s / a[i * n + i];
                }
            }
            // the inverse of a is M' M
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double s = 0;
                    for (int k = i; k < n; k++) s += m[k * n + i] * m[k * n + j];
                    a[i * n + j] = s; a[j * n + i] = s;
                }
            return true;
        }
    }
}
