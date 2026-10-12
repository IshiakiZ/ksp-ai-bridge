using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>Describes the parts available in this install so an AI can choose them by real numbers, not memory.</summary>
    public static class PartCatalog
    {
        const double G0 = 9.80665;

        public static bool IsUsable(AvailablePart ap)
        {
            if (ap == null || ap.partPrefab == null) return false;
            if (ap.category == PartCategories.none || ap.TechHidden) return false;
            return true;
        }

        /// <summary>Career and science saves only allow researched parts.</summary>
        public static bool IsUnlocked(AvailablePart ap)
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX) return true;
            return ResearchAndDevelopment.PartTechAvailable(ap) && ResearchAndDevelopment.PartModelPurchased(ap);
        }

        /// <summary>Find a part by internal name or by its display title, forgiving '_' vs '.' and case.</summary>
        public static AvailablePart Find(string query)
        {
            if (string.IsNullOrEmpty(query)) throw new ApiException("Part name is empty.");
            string q = query.Trim();
            AvailablePart ap = PartLoader.getPartInfoByName(q) ?? PartLoader.getPartInfoByName(q.Replace('_', '.'));
            if (ap != null && ap.partPrefab != null) return ap;

            string norm = Normalise(q);
            AvailablePart byTitle = null;
            foreach (AvailablePart p in PartLoader.LoadedPartsList)
            {
                if (p.partPrefab == null) continue;
                if (Normalise(p.name) == norm) return p;
                if (byTitle == null && IsUsable(p) && Normalise(p.title) == norm) byTitle = p;
            }
            if (byTitle != null) return byTitle;

            // Suggest the parts that share the most words with what was asked for.
            string[] tokens = q.ToLowerInvariant().Split(new[] { ' ', '.', '_', '-', '"' }, StringSplitOptions.RemoveEmptyEntries);
            var scored = new List<KeyValuePair<int, string>>();
            foreach (AvailablePart p in PartLoader.LoadedPartsList)
            {
                if (!IsUsable(p)) continue;
                string hay = (p.name + " " + p.title).ToLowerInvariant();
                int score = 0;
                foreach (string t in tokens) if (t.Length >= 3 && hay.Contains(t)) score++;
                if (score > 0) scored.Add(new KeyValuePair<int, string>(score, p.name + " (" + p.title + ")"));
            }
            scored.Sort((x, y) => y.Key.CompareTo(x.Key));
            var close = new List<string>();
            for (int n = 0; n < scored.Count && n < 6; n++) close.Add(scored[n].Value);
            throw new ApiException("Unknown part '" + query + "'." + (close.Count > 0 ? " Similar: " + string.Join("; ", close.ToArray()) : " Use list_parts to search the catalog."));
        }

        /// <summary>
        /// Where a part comes from: the game itself ("stock"), one of its two expansions, or the mod whose folder in
        /// GameData it is in. (Parts of mods are in the catalogue like any other and are built with the same way.)
        /// </summary>
        public static string Origin(AvailablePart ap)
        {
            string url = ap.partUrl ?? "";
            string[] bits = url.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length == 0) return "unknown";
            if (bits[0] == "Squad") return "stock";
            if (bits[0] == "SquadExpansion" && bits.Length > 1) return bits[1] == "MakingHistory" ? "Making History" : bits[1] == "Serenity" ? "Breaking Ground" : bits[1];
            return bits[0];
        }

        /// <summary>Whether a variant is more than a coat of paint: whether it puts any of the part's attachment points somewhere else.</summary>
        public static bool ChangesShape(Part p, PartVariant v)
        {
            if (v.AttachNodes == null) return false;
            foreach (AttachNode mine in v.AttachNodes)
                foreach (AttachNode usual in p.attachNodes)
                    if (usual.id == mine.id && ((usual.position - mine.position).sqrMagnitude > 1e-6f || usual.size != mine.size)) return true;
            return false;
        }

        /// <summary>The looks a part can be given (its variants), if it has more than one: their names, and what each is like where the name does not say.</summary>
        public static List<object> Looks(Part p)
        {
            ModulePartVariants variants = p.FindModuleImplementing<ModulePartVariants>();
            if (variants == null || variants.variantList == null || variants.variantList.Count < 2) return null;
            var list = new List<object>();
            foreach (PartVariant v in variants.variantList)
            {
                string shown = KSP.Localization.Localizer.Format(v.DisplayName ?? "");
                string theme = v.themeName ?? "";
                bool moves = ChangesShape(p, v);
                if ((shown.Length == 0 || shown == v.Name) && (theme.Length == 0 || theme == v.Name) && !moves) { list.Add(v.Name); continue; }
                var o = new JObj().Add("name", v.Name);
                if (shown.Length > 0 && shown != v.Name) o.Add("shown_as", shown);
                if (theme.Length > 0 && theme != v.Name) o.Add("theme", theme);
                if (moves) o.Add("changes_shape", true);
                list.Add(o);
            }
            return list;
        }

        static string Normalise(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static object List(Args a)
        {
            string category = a.Str("category");
            string from = a.Str("mod") ?? a.Str("from");
            string search = (a.Str("search") ?? "").ToLowerInvariant();
            string[] terms = search.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            int limit = Math.Max(1, Math.Min(500, a.Int("limit", 80)));
            bool includeLocked = a.Bool("include_locked", false);

            var counts = new SortedDictionary<string, int>();
            var origins = new SortedDictionary<string, int>();
            var themes = new SortedDictionary<string, int>();
            var hits = new List<KeyValuePair<int, AvailablePart>>();
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
            {
                if (!IsUsable(ap)) continue;
                if (!includeLocked && !IsUnlocked(ap)) continue;
                string cat = ap.category.ToString();
                counts[cat] = counts.TryGetValue(cat, out int c) ? c + 1 : 1;
                string origin = Origin(ap);
                origins[origin] = origins.TryGetValue(origin, out int oc) ? oc + 1 : 1;
                ModulePartVariants looks = ap.partPrefab.FindModuleImplementing<ModulePartVariants>();
                if (looks != null && looks.variantList != null)
                    foreach (PartVariant v in looks.variantList)
                    {
                        // (a theme is a coat of paint: what changes a part's shape, an engine with and without its shroud say, is that part's own business)
                        if (ChangesShape(ap.partPrefab, v)) continue;
                        string theme = string.IsNullOrEmpty(v.themeName) ? v.Name : v.themeName;
                        if (!string.IsNullOrEmpty(theme)) themes[theme] = themes.TryGetValue(theme, out int tc) ? tc + 1 : 1;
                    }
                if (!string.IsNullOrEmpty(category) && !cat.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(from) && origin.IndexOf(from, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int score = 0;
                if (terms.Length > 0)
                {
                    // Names and titles outrank the free-text tags, which match loosely.
                    string strong = (ap.name + " " + ap.title).ToLowerInvariant();
                    string weak = (ap.tags + " " + ap.bulkheadProfiles + " " + cat + " " + origin).ToLowerInvariant();
                    bool all = true;
                    foreach (string t in terms)
                    {
                        if (strong.Contains(t)) score += 10;
                        else if (weak.Contains(t)) score += 1;
                        else { all = false; break; }
                    }
                    if (!all) continue;
                }
                hits.Add(new KeyValuePair<int, AvailablePart>(score, ap));
            }
            int matched = hits.Count;
            var ordered = new List<KeyValuePair<int, AvailablePart>>(hits);
            // List.Sort is unstable; keep catalog order within equal scores.
            var index = new Dictionary<AvailablePart, int>();
            for (int n = 0; n < hits.Count; n++) index[hits[n].Value] = n;
            ordered.Sort((x, y) => x.Key != y.Key ? y.Key.CompareTo(x.Key) : index[x.Value].CompareTo(index[y.Value]));
            var result = new List<object>();
            foreach (KeyValuePair<int, AvailablePart> hit in ordered)
            {
                if (result.Count >= limit) break;
                result.Add(Describe(hit.Value, false));
            }
            var o = new JObj().Add("matched", matched).Add("returned", result.Count);
            if (string.IsNullOrEmpty(category) && terms.Length == 0 && string.IsNullOrEmpty(from))
            {
                o.Add("categories", counts);
                // (which mods have put parts in the catalogue, and the looks parts can be given: how many parts have each)
                o.Add("from", origins);
                var common = new SortedDictionary<string, int>();
                foreach (KeyValuePair<string, int> kv in themes) if (kv.Value >= 4) common[kv.Key] = kv.Value;
                if (common.Count > 0) o.Add("themes", common);
            }
            o.Add("parts", result);
            return o;
        }

        public static JObj Describe(AvailablePart ap, bool full)
        {
            Part p = ap.partPrefab;
            var o = new JObj();
            o.Add("name", ap.name);
            o.Add("title", ap.title);
            o.Add("category", ap.category.ToString());
            string origin = Origin(ap);
            if (full || origin != "stock") o.Add("from", origin);
            double resourceMass = 0;
            var resources = new JObj();
            foreach (PartResource r in p.Resources)
            {
                resources.Add(r.resourceName, Math.Round(r.maxAmount, 2));
                resourceMass += r.amount * r.info.density;
            }
            o.Add("dry_mass_t", Math.Round(p.mass, 4));
            if (resourceMass > 0) o.Add("wet_mass_t", Math.Round(p.mass + resourceMass, 4));
            o.Add("cost", Math.Round(ap.cost));
            if (!string.IsNullOrEmpty(ap.bulkheadProfiles)) o.Add("size", ap.bulkheadProfiles.Replace(" ", ""));
            if (resources.Count > 0) o.Add("resources", resources);
            if (p.CrewCapacity > 0) o.Add("crew", p.CrewCapacity);

            var nodes = new List<object>();
            foreach (AttachNode n in p.attachNodes) nodes.Add(n.id);
            o.Add("stack_nodes", nodes);
            List<object> looks = Looks(p);
            if (looks != null) { if (full) o.Add("variants", looks); else o.Add("variants", looks.Count); }
            if (p.attachRules.srfAttach) o.Add("attaches_radially", true);
            if (!p.attachRules.allowSrfAttach) o.Add("accepts_radial_children", false);

            var engines = new List<object>();
            ModuleGimbal gimbal = p.FindModuleImplementing<ModuleGimbal>();
            foreach (PartModule m in p.Modules)
            {
                if (m is ModuleEngines e)
                {
                    double ispVac = e.atmosphereCurve.Evaluate(0f), ispAsl = e.atmosphereCurve.Evaluate(1f);
                    var props = new List<string>();
                    foreach (Propellant pr in e.propellants) props.Add(pr.name);
                    var eo = new JObj();
                    if (!string.IsNullOrEmpty(e.engineID) && e.engineID != "Engine") eo.Add("mode", e.engineID);
                    eo.Add("thrust_vac_kn", Math.Round(e.maxThrust, 1));
                    eo.Add("thrust_asl_kn", Math.Round(ispVac > 0 ? e.maxThrust * ispAsl / ispVac : 0, 1));
                    eo.Add("isp_vac", Math.Round(ispVac));
                    eo.Add("isp_asl", Math.Round(ispAsl));
                    eo.Add("propellants", string.Join("+", props.ToArray()));
                    if (e.throttleLocked) eo.Add("throttleable", false);
                    if (gimbal != null) eo.Add("gimbal_deg", Math.Round(gimbal.gimbalRange, 1));
                    engines.Add(eo);
                }
            }
            if (engines.Count == 1) o.Add("engine", engines[0]);
            else if (engines.Count > 1) o.Add("engine_modes", engines);

            var traits = new List<object>();
            foreach (PartModule m in p.Modules)
            {
                switch (m)
                {
                    case ModuleAnchoredDecoupler ad:
                        if (ad.stagingEnabled) o.Add("decoupler", new JObj().Add("kind", "radial").Add("force", ad.ejectionForce));
                        break;
                    case ModuleDecouple d:
                        if (d.stagingEnabled) o.Add("decoupler", new JObj().Add("kind", d.isOmniDecoupler ? "separator (both sides)" : "stack").Add("releases_node", d.explosiveNodeID).Add("force", d.ejectionForce));
                        else traits.Add("jettisonable (not staged by default)");
                        break;
                    case ModuleCommand cmd:
                        o.Add("command", cmd.minimumCrew > 0 ? "crewed (needs " + cmd.minimumCrew + ")" : "probe core");
                        break;
                    case ModuleSAS sas:
                        o.Add("sas_level", sas.SASServiceLevel);
                        break;
                    case ModuleReactionWheel rw:
                        o.Add("reaction_wheel_torque", Math.Round((rw.PitchTorque + rw.YawTorque + rw.RollTorque) / 3f, 2));
                        break;
                    case ModuleRCS rcs:
                        o.Add("rcs_thrust_kn", Math.Round(rcs.thrusterPower, 2));
                        break;
                    case ModuleDeployableSolarPanel sp:
                        o.Add("solar_ec_per_s", Math.Round(sp.chargeRate, 2));
                        break;
                    case ModuleDataTransmitter tx:
                        o.Add("antenna", new JObj().Add("power", tx.antennaPower).Add("type", tx.antennaType.ToString().ToLowerInvariant()));
                        break;
                    case ModuleScienceExperiment exp:
                        traits.Add("experiment:" + exp.experimentID);
                        break;
                    case ModuleParachute _: traits.Add("parachute"); break;
                    case LaunchClamp _: traits.Add("launch_clamp"); break;
                    case ModuleProceduralFairing _: traits.Add("fairing"); break;
                    case ModuleDockingNode dn: traits.Add("docking_port:" + dn.nodeType); break;
                    case ModuleWheelBase wb: traits.Add(wb.wheelType == WheelType.LEG ? "landing_leg" : "wheel"); break;
                    case ModuleControlSurface _: traits.Add("control_surface"); break;
                    case ModuleAblator _: traits.Add("heat_shield"); break;
                    case ModuleResourceIntake _: traits.Add("air_intake"); break;
                    case ModuleGenerator _: traits.Add("generator"); break;
                    case ModuleCargoBay _: traits.Add("cargo_bay"); break;
                    case ModuleLight _: traits.Add("light"); break;
                    case ModuleResourceHarvester _: traits.Add("drill"); break;
                    case ModuleResourceConverter _: traits.Add("resource_converter"); break;
                    case ModuleActiveRadiator _: traits.Add("radiator"); break;
                    case ModuleScienceContainer _: traits.Add("science_storage"); break;
                }
            }
            if (p is CompoundPart cp) traits.Add("compound_part: give \"on\" (where it is mounted) and \"to\" (what it connects to), reach " + cp.maxLength + " m");
            ModuleLiftingSurface lift = p.FindModuleImplementing<ModuleLiftingSurface>();
            if (lift != null && ap.category == PartCategories.Aero)
            {
                var wing = new JObj().Add("lift_rating", Math.Round(lift.deflectionLiftCoeff, 2));
                if (lift is ModuleControlSurface cs) wing.Add("control_surface", true).Add("deflection_deg", Math.Round(cs.ctrlSurfaceRange, 1));
                o.Add("wing", wing);
            }
            ModuleResourceIntake intake = p.FindModuleImplementing<ModuleResourceIntake>();
            if (intake != null) o.Add("intake_area", Math.Round(intake.area, 4));
            ModuleWheelBase wheel = p.FindModuleImplementing<ModuleWheelBase>();
            if (wheel != null && wheel.wheelType != WheelType.LEG)
            {
                var motor = p.FindModuleImplementing<ModuleWheels.ModuleWheelMotor>();
                var steer = p.FindModuleImplementing<ModuleWheels.ModuleWheelSteering>();
                o.Add("wheel", new JObj().Add("powered", motor != null).Add("steers", steer != null).Add("retractable", p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null));
            }
            if (ap.category == PartCategories.Aero && p.FindModuleImplementing<ModuleLiftingSurface>() != null && p.FindModuleImplementing<ModuleControlSurface>() == null) traits.Add("wing");
            if (traits.Count > 0) o.Add("traits", traits);
            if (!IsUnlocked(ap)) o.Add("locked", "needs tech: " + ap.TechRequired);

            if (full)
            {
                var detail = new List<object>();
                foreach (AttachNode n in p.attachNodes)
                    detail.Add(new JObj().Add("id", n.id).Add("size", n.size).Add("position", n.position).Add("direction", n.orientation));
                o["stack_nodes"] = detail;
                if (p.srfAttachNode != null && p.attachRules.srfAttach)
                    o.Add("radial_node", new JObj().Add("position", p.srfAttachNode.position).Add("direction", p.srfAttachNode.orientation));
                if (p.CrewCapacity > 0 && p.airlock != null)
                {
                    // Where the hatch is, so ladders can be put under it.
                    Vector3 hatch = p.transform.InverseTransformPoint(p.airlock.position);
                    o.Add("hatch", new JObj().Add("angle", Math.Round(Mathf.Atan2(hatch.z, hatch.x) * Mathf.Rad2Deg)).Add("height", Round2(hatch.y))
                        .Add("note", "mount ladders with radial.angle equal to this angle so they lead to the hatch"));
                }
                Bounds b = PartGeometry.For(ap).Bounds;
                o.Add("extent_m", new JObj().Add("x", Round2(b.size.x)).Add("y", Round2(b.size.y)).Add("z", Round2(b.size.z)).Add("y_min", Round2(b.min.y)).Add("y_max", Round2(b.max.y))
                    .Add("x_min", Round2(b.min.x)).Add("x_max", Round2(b.max.x)).Add("z_min", Round2(b.min.z)).Add("z_max", Round2(b.max.z)));
                o.Add("manufacturer", ap.manufacturer);
                string desc = ap.description ?? "";
                o.Add("description", desc.Length > 320 ? desc.Substring(0, 320) + "…" : desc);
                var mods = new List<object>();
                foreach (PartModule m in p.Modules) mods.Add(m.moduleName);
                o.Add("modules", mods);
                o.Add("tech_required", ap.TechRequired);
            }
            return o;
        }

        static double Round2(float v) => Math.Round(v, 2);

        // ---- helpers shared with the builder ----

        public static bool IsEngine(Part p) => p.FindModuleImplementing<ModuleEngines>() != null;
        /// <summary>Only decouplers that take part in staging by default (a heat shield's jettison does not).</summary>
        public static bool IsDecoupler(Part p)
        {
            ModuleDecouplerBase d = p.FindModuleImplementing<ModuleDecouplerBase>();
            return d != null && d.stagingEnabled;
        }
        public static bool IsParachute(Part p) => p.FindModuleImplementing<ModuleParachute>() != null;
        public static bool IsClamp(Part p) => p.FindModuleImplementing<LaunchClamp>() != null;
        public static bool IsFairing(Part p) => p.FindModuleImplementing<ModuleProceduralFairing>() != null;

        /// <summary>Seconds a solid motor burns at full thrust; used to spot separation motors. Returns +inf for anything else.</summary>
        public static double SolidBurnSeconds(Part p)
        {
            ModuleEngines e = p.FindModuleImplementing<ModuleEngines>();
            if (e == null || !e.throttleLocked) return double.PositiveInfinity;
            double fuelMass = 0;
            foreach (PartResource r in p.Resources)
                foreach (Propellant pr in e.propellants)
                    if (pr.name == r.resourceName) fuelMass += r.maxAmount * r.info.density;
            double isp = e.atmosphereCurve.Evaluate(0f);
            if (fuelMass <= 0 || isp <= 0 || e.maxThrust <= 0) return double.PositiveInfinity;
            return fuelMass / (e.maxThrust / (isp * G0));
        }
    }
}
