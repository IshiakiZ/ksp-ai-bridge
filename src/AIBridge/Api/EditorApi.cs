using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KSP.Localization;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>Vehicle design: parts catalog, building craft from a description, inspecting and launching them.</summary>
    public static class EditorApi
    {
        static BridgeHost host;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("list_parts", "Search the parts catalog (the game's own parts and those of every mod installed). params: {category, search, mod: only parts from this mod (or 'stock', 'Making History', 'Breaking Ground'), limit, include_locked}. With nothing asked for it also says how many parts each mod has put in and which looks (themes) parts come in.", PartCatalog.List).AlwaysAllowed = true;
            Rpc.Sync("part_info", "Full details for parts. params: {names: [..]}", PartInfo).AlwaysAllowed = true;
            Rpc.Async("build_craft", "Assemble a craft from a part list, save it and open it in the editor, where the player sees it put together part by part. params: {name, parts:[...] (each may give variant: one of the part's looks), theme: a look for every part that has it (Dark, White, Orange...), stages, facility, show: false opens it whole at once, show_s: how long the putting together is shown for (default about 0.4 s a step, 14 s at most)}", BuildCraft);
            Rpc.Async("craft_info", "Stats for the craft currently in the editor: mass, cost, per-stage delta-v and TWR, parts. params: {parts: bool (the part list, default true), breakdown: bool (where the mass and the money are: the parts grouped by kind, heaviest first)}", CraftInfo).AlwaysAllowed = true;
            Rpc.Sync("list_crafts", "Saved craft files in this save plus the stock ones.", ListCrafts).AlwaysAllowed = true;
            Rpc.Async("load_craft", "Open a saved craft in the editor. params: {name}", LoadCraft);
            Rpc.Async("launch", "Put a craft on the launch pad or runway and start the flight. params: {craft, site, crew, clear_site}", Launch);
        }

        static object PartInfo(Args a)
        {
            List<string> names = a.Strings("names");
            if (names.Count == 0 && a.Has("name")) names.Add(a.Str("name"));
            if (names.Count == 0) throw new ApiException("Give 'names': a list of part names.");
            var list = new List<object>();
            foreach (string n in names) list.Add(PartCatalog.Describe(PartCatalog.Find(n), true));
            return list;
        }

        static void RequireGame()
        {
            if (SceneWatch.Loading) throw new ApiException("The game is still loading; try again in a few seconds.");
            if (!HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null)
                throw new ApiException("No game is loaded. Use load_save or new_save first.");
        }

        // ---------------------------------------------------------------- craft files

        static string ShipsDir(EditorFacility facility) =>
            KSPUtil.ApplicationRootPath + "saves/" + HighLogic.SaveFolder + "/Ships/" + (facility == EditorFacility.SPH ? "SPH" : "VAB") + "/";

        static string StockDir(EditorFacility facility) =>
            KSPUtil.ApplicationRootPath + "Ships/" + (facility == EditorFacility.SPH ? "SPH" : "VAB") + "/";

        static string FileNameFor(string shipName) => KSPUtil.SanitizeString(shipName, '_', true);

        static object ListCrafts(Args a)
        {
            RequireGame();
            var list = new List<object>();
            foreach (EditorFacility f in new[] { EditorFacility.VAB, EditorFacility.SPH })
            {
                AddCrafts(list, ShipsDir(f), f, false);
                if (a.Bool("include_stock", true)) AddCrafts(list, StockDir(f), f, true);
            }
            return list;
        }

        static void AddCrafts(List<object> list, string dir, EditorFacility facility, bool stock)
        {
            if (!Directory.Exists(dir)) return;
            string[] files = Directory.GetFiles(dir, "*.craft");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                var o = new JObj().Add("name", Path.GetFileNameWithoutExtension(f)).Add("facility", facility.ToString());
                if (stock) o.Add("stock", true);
                list.Add(o);
            }
        }

        static string FindCraft(string name, out EditorFacility facility)
        {
            string want = name.Trim();
            if (want.EndsWith(".craft", StringComparison.OrdinalIgnoreCase)) want = want.Substring(0, want.Length - 6);
            string sanitized = FileNameFor(want);
            foreach (bool stock in new[] { false, true })
                foreach (EditorFacility f in new[] { EditorFacility.VAB, EditorFacility.SPH })
                {
                    string dir = stock ? StockDir(f) : ShipsDir(f);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string file in Directory.GetFiles(dir, "*.craft"))
                    {
                        string stem = Path.GetFileNameWithoutExtension(file);
                        if (stem.Equals(want, StringComparison.OrdinalIgnoreCase) || stem.Equals(sanitized, StringComparison.OrdinalIgnoreCase))
                        {
                            facility = f;
                            return file;
                        }
                    }
                }
            throw new ApiException("No craft named '" + name + "'. Use list_crafts to see what exists.");
        }

        // ---------------------------------------------------------------- build

        static IEnumerator BuildCraft(Call c)
        {
            RequireGame();
            var builder = new CraftBuilder();
            ConfigNode node = builder.Build(c.Args);

            string fileName = FileNameFor(builder.Name);
            string dir = ShipsDir(builder.Facility);
            Directory.CreateDirectory(dir);
            string path = dir + fileName + ".craft";
            node.Save(path);
            host.Note("built craft '" + builder.Name + "' (" + builder.Parts.Count + " parts)");

            // (the player is to see it put together: it is hidden the moment it is loaded, and brought out part by part below)
            bool show = c.Args.Bool("show", true);
            BuildShow.Restore();
#if DEV
            float t0 = Time.realtimeSinceStartup;
#endif
            yield return OpenInEditor(path, builder.Facility, builder.Parts.Count, show ? (Action<ShipConstruct>)BuildShow.Hide : null);
            ShipConstruct ship = EditorLogic.fetch.ship;
#if DEV
            float t1 = Time.realtimeSinceStartup;
#endif

            ApplyTweaks(builder, ship, c.Args.Str("theme"));
            yield return StowLegs(builder, ship);
            GameEvents.onEditorShipModified.Fire(ship);
            yield return null;
            yield return null;
            // Re-save the live craft through the stock writer so the file carries full module and resource
            // state, including the tweaks above. (SaveShip(name) alone would write the editor's stale backup.)
            ship.shipName = builder.Name;
            ship.shipDescription = builder.Description;
            EditorLogic.fetch.SetBackup();
            ShipConstruction.SaveShip(ship, fileName);
#if DEV
            float t2 = Time.realtimeSinceStartup;
#endif
            if (show)
            {
                int steps = 0;
                foreach (KeyValuePair<string, List<CraftBuilder.Placed>> one in builder.Groups) steps++;
                BuildShow.Hide(ship);                            // (whatever has switched itself on since)
                yield return BuildShow.Play(builder, ship, Mathf.Clamp((float)c.Args.Num("show_s", BuildShow.UsualSeconds(steps)), 0.5f, 40f));
            }
#if DEV
            float t3 = Time.realtimeSinceStartup;
#endif
            yield return WaitDeltaV(ship);

            JObj result = CraftStats(ship);
#if DEV
            result.Add("dev_seconds", new JObj().Add("opening", Math.Round(t1 - t0, 2)).Add("tweaks_and_saving", Math.Round(t2 - t1, 2)).Add("showing", Math.Round(t3 - t2, 2)).Add("delta_v", Math.Round(Time.realtimeSinceStartup - t3, 2)));
#endif
            result.Add("staging", builder.StagingReport());
            if (builder.Fairings.Count > 0) result.Add("fairings", builder.Fairings);
            result.Add("ids", IdMap(builder));
            var warnings = new List<string>(builder.Warnings);
            warnings.AddRange(DesignWarnings(ship));
            if (warnings.Count > 0) result.Add("warnings", warnings);
            if (builder.Notes.Count > 0) result.Add("notes", builder.Notes);
            result.Add("saved_as", fileName);
            c.Result = result;
        }

        static IEnumerator OpenInEditor(string path, EditorFacility facility, int expectedParts, Action<ShipConstruct> onLoaded = null)
        {
            if (HighLogic.LoadedScene != GameScenes.EDITOR || EditorDriver.editorFacility != facility)
            {
                yield return GameApi.WaitClearToSave();
                GameApi.SaveBeforeLeaving();
                EditorDriver.StartAndLoadVessel(path, facility);
                yield return SceneWatch.WaitFor(GameScenes.EDITOR, 150f);
            }
            else
            {
                EditorLogic.LoadShipFromFile(path);
                yield return null;
                yield return null;
            }
            float deadline = Time.realtimeSinceStartup + 30f;
            while (true)
            {
                EditorLogic editor = EditorLogic.fetch;
                if (editor != null && editor.ship != null && editor.ship.parts.Count > 0 && !SceneWatch.Loading) break;
                if (Time.realtimeSinceStartup > deadline)
                    throw new ApiException("KSP did not load the craft file. " + DialogApi.Summary());
                yield return null;
            }
            onLoaded?.Invoke(EditorLogic.fetch.ship);
            // Parts finish initialising over the next frames; their modules are not usable before that.
            float started = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < started)
            {
                bool all = true;
                // (in the editor the game never sets 'started', only 'editorStarted': waiting on the first alone always ran the full ten seconds)
                foreach (Part p in EditorLogic.fetch.ship.parts) if (p == null || !(p.started || p.editorStarted)) { all = false; break; }
                if (all) break;
                yield return null;
            }
#if DEV
            {
                var slow = new List<string>();
                foreach (Part p in EditorLogic.fetch.ship.parts) if (p != null && !(p.started || p.editorStarted)) slow.Add(p.partInfo.name + (p.gameObject.activeInHierarchy ? "" : " (not active)") + (p.enabled ? "" : " (switched off)"));
                Debug.Log("[AIBridge] parts not started after " + Math.Round(Time.realtimeSinceStartup - (started - 10f), 2) + " s: " + (slow.Count == 0 ? "none" : string.Join(", ", slow.ToArray())));
            }
#endif
            for (int n = 0; n < 10; n++) yield return null;
            int loaded = EditorLogic.fetch.ship.parts.Count;
            if (expectedParts > 0 && loaded != expectedParts)
                throw new ApiException("KSP loaded " + loaded + " parts but the craft file has " + expectedParts + ". " + DialogApi.Summary());
        }

        /// <summary>
        /// Parts come out of the catalog with landing legs extended. Fold them for launch unless the design
        /// says "deployed": true; the landing autopilot and the gear control extend them again.
        /// </summary>
        static IEnumerator StowLegs(CraftBuilder builder, ShipConstruct ship)
        {
            var keep = new HashSet<uint>();
            foreach (CraftBuilder.Placed placed in builder.Parts) if (placed.Spec.Bool("deployed", false)) keep.Add(placed.CraftId);
            var folding = new List<ModuleWheels.ModuleWheelDeployment>();
            foreach (Part p in ship.parts)
            {
                if (keep.Contains(p.craftID)) continue;
                ModuleWheelBase wheel = p.FindModuleImplementing<ModuleWheelBase>();
                var deploy = p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>();
                if (wheel == null || deploy == null || wheel.wheelType != WheelType.LEG) continue;
                if (deploy.fsm == null || !deploy.fsm.Started || deploy.fsm.CurrentState != deploy.st_deployed) continue;
                deploy.ActionToggle(KSPActionType.Deactivate);
                folding.Add(deploy);
            }
            float deadline = Time.realtimeSinceStartup + 8f;
            while (folding.Count > 0 && Time.realtimeSinceStartup < deadline)
            {
                folding.RemoveAll(d => d == null || d.fsm.CurrentState == d.st_retracted);
                yield return null;
            }
        }

        /// <summary>
        /// Give a part one of its looks, named as the game names it, as the editor shows it, or by its theme (so that one
        /// word, "Dark" say, finds each part's own dark variant). False if the part has no such look.
        /// </summary>
        static bool SetLook(Part part, string wanted, out string had)
        {
            had = null;
            ModulePartVariants variants = part.FindModuleImplementing<ModulePartVariants>();
            if (variants == null || variants.variantList == null || variants.variantList.Count == 0) return false;
            string want = wanted.Replace(" ", "").Replace("_", "").Replace("-", "");
            PartVariant found = null;
            for (int pass = 0; pass < 2 && found == null; pass++)
                foreach (PartVariant v in variants.variantList)
                {
                    foreach (string name in new[] { v.Name, v.themeName, Localizer.Format(v.DisplayName ?? "") })
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        string n = name.Replace(" ", "").Replace("_", "").Replace("-", "");
                        // (the name itself first; then anything that has the word in it)
                        if (pass == 0 ? n.Equals(want, StringComparison.OrdinalIgnoreCase) : n.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) { found = v; break; }
                    }
                    if (found != null) break;
                }
            if (found == null)
            {
                var names = new List<string>();
                foreach (PartVariant v in variants.variantList) names.Add(v.Name);
                had = string.Join(", ", names.ToArray());
                return false;
            }
            if (variants.SelectedVariant != found) variants.SetVariant(found.Name);
            return true;
        }

        static void ApplyTweaks(CraftBuilder builder, ShipConstruct ship, string theme = null)
        {
            var byId = new Dictionary<uint, Part>();
            foreach (Part p in ship.parts) byId[p.craftID] = p;
            int themed = 0, themeable = 0;
            var noSuch = new HashSet<string>();
            foreach (CraftBuilder.Placed placed in builder.Parts)
            {
                if (!byId.TryGetValue(placed.CraftId, out Part part)) continue;
                // ---- how it looks: the part's own variant if one is asked for, otherwise the craft's theme where the part has it
                string look = placed.Spec.Str("variant");
                if (!string.IsNullOrEmpty(look))
                {
                    if (!SetLook(part, look, out string had) && noSuch.Add(placed.Id))
                        builder.Warnings.Add("'" + placed.Id + "' has no variant '" + look + "'" + (had != null ? " (it has: " + had + ")." : ": this part comes in one look only."));
                }
                else if (!string.IsNullOrEmpty(theme) && part.FindModuleImplementing<ModulePartVariants>() != null)
                {
                    themeable++;
                    if (SetLook(part, theme, out string _)) themed++;
                }
                Args resources = placed.Spec.Obj("resources");
                if (resources != null)
                    foreach (KeyValuePair<string, object> kv in resources.Map)
                    {
                        PartResource r = part.Resources.Get(kv.Key);
                        if (r == null) { builder.Warnings.Add("'" + placed.Id + "' has no resource '" + kv.Key + "'."); continue; }
                        r.amount = Math.Max(0, Math.Min(r.maxAmount, resources.Num(kv.Key, r.amount)));
                    }
                Args fields = placed.Spec.Obj("fields");
                if (fields != null)
                    foreach (KeyValuePair<string, object> kv in fields.Map)
                        if (!SetField(part, kv.Key, kv.Value)) builder.Warnings.Add("'" + placed.Id + "' has no tweakable field '" + kv.Key + "'.");
            }
            if (!string.IsNullOrEmpty(theme))
            {
                if (themeable == 0) builder.Warnings.Add("No part of this craft comes in more than one look, so the theme '" + theme + "' changed nothing.");
                else if (themed == 0) builder.Warnings.Add("None of this craft's parts has a '" + theme + "' look. list_parts (with nothing asked for) lists the themes there are.");
                else builder.Notes.Add("theme '" + theme + "': " + themed + " of the " + themeable + " parts that come in more than one look have it");
            }
        }

        /// <summary>Set a module field by "Module.field" or just "field" (first module that has it).</summary>
        public static bool SetField(Part part, string key, object value)
        {
            string moduleName = null, fieldName = key;
            int dot = key.IndexOf('.');
            if (dot > 0) { moduleName = key.Substring(0, dot); fieldName = key.Substring(dot + 1); }
            string text = value is bool b ? (b ? "True" : "False") : Convert.ToString(value, CultureInfo.InvariantCulture);
            bool any = false;
            foreach (PartModule m in part.Modules)
            {
                if (moduleName != null && !m.moduleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)) continue;
                if (m.Fields[fieldName] == null) continue;
                if (m.Fields.ReadValue(fieldName, text)) { any = true; if (moduleName != null) break; }
            }
            return any;
        }

        static JObj IdMap(CraftBuilder builder)
        {
            var o = new JObj();
            foreach (KeyValuePair<string, List<CraftBuilder.Placed>> kv in builder.Groups)
                o.Add(kv.Key, kv.Value[0].Info.title + (kv.Value.Count > 1 ? " x" + kv.Value.Count : ""));
            return o;
        }

        // ---------------------------------------------------------------- stats

        static IEnumerator WaitDeltaV(ShipConstruct ship)
        {
            VesselDeltaV dv = ship.vesselDeltaV;
            if (dv == null) yield break;
            dv.SetCalcsDirty(true);
            float deadline = Time.realtimeSinceStartup + 12f;
            for (int n = 0; n < 8; n++) yield return null;
            // The stock simulation publishes intermediate totals while it runs; wait for the figure to settle.
            double last = double.NaN;
            int steady = 0;
            while (Time.realtimeSinceStartup < deadline && steady < 25)
            {
                double total = dv.IsReady && !dv.SimulationRunning ? dv.TotalDeltaVVac : double.NaN;
                steady = !double.IsNaN(total) && total == last ? steady + 1 : 0;
                last = total;
                yield return null;
            }
        }

        static IEnumerator CraftInfo(Call c)
        {
            if (HighLogic.LoadedScene != GameScenes.EDITOR || EditorLogic.fetch == null)
                throw new ApiException("craft_info needs the VAB or SPH to be open (use goto vab, load_craft or build_craft).");
            ShipConstruct ship = EditorLogic.fetch.ship;
            if (ship == null || ship.parts.Count == 0) throw new ApiException("The editor is empty.");
            yield return WaitDeltaV(ship);
            JObj result = CraftStats(ship);
            if (c.Args.Bool("parts", true))
            {
                var parts = new List<object>();
                foreach (Part p in ship.parts)
                {
                    var o = new JObj().Add("part", p.partInfo.name).Add("title", p.partInfo.title);
                    if (p.parent != null) o.Add("on", p.parent.partInfo.name + "#" + ship.parts.IndexOf(p.parent));
                    o.Add("index", ship.parts.IndexOf(p));
                    if (p.hasStagingIcon || p.stackIcon?.StageIcon != null) o.Add("stage", p.inverseStage);
                    parts.Add(o);
                }
                result.Add("parts", parts);
            }
            if (c.Args.Bool("breakdown", false)) result.Add("breakdown", Breakdown(ship));
            List<string> warnings = DesignWarnings(ship);
            if (warnings.Count > 0) result.Add("warnings", warnings);
            c.Result = result;
        }

        /// <summary>
        /// Where the mass and the money are: the craft's parts grouped by kind, heaviest first, each with what it weighs
        /// empty and full, what it costs, and its share of the whole. (What to look at first when a craft is too heavy
        /// or too dear.)
        /// </summary>
        static List<object> Breakdown(ShipConstruct ship)
        {
            var kinds = new Dictionary<string, float[]>();          // count, dry, wet, cost
            var titles = new Dictionary<string, string>();
            float all = 0f, allCost = 0f;
            foreach (Part p in ship.parts)
            {
                float dry = p.mass, wet = p.mass + p.GetResourceMass();
                // (what a part costs as it stands: its listed price is with its tanks full, and what is missing is taken off)
                float cost = p.partInfo.cost + p.GetModuleCosts(p.partInfo.cost);
                foreach (PartResource r in p.Resources) cost -= (float)((r.maxAmount - r.amount) * r.info.unitCost);
                if (!kinds.TryGetValue(p.partInfo.name, out float[] k)) { kinds[p.partInfo.name] = k = new float[4]; titles[p.partInfo.name] = p.partInfo.title; }
                k[0] += 1f; k[1] += dry; k[2] += wet; k[3] += cost;
                all += wet; allCost += cost;
            }
            var names = new List<string>(kinds.Keys);
            names.Sort((x, y) => kinds[y][2].CompareTo(kinds[x][2]));
            var rows = new List<object>();
            foreach (string name in names)
            {
                float[] k = kinds[name];
                if (rows.Count >= 30) break;
                rows.Add(new JObj().Add("part", name).Add("title", titles[name]).Add("count", (int)k[0])
                    .Add("mass_t", Math.Round(k[2], 3)).Add("dry_mass_t", Math.Round(k[1], 3)).Add("cost", Math.Round(k[3]))
                    .Add("percent_of_mass", all > 0f ? Math.Round(100.0 * k[2] / all, 1) : 0).Add("percent_of_cost", allCost > 0f ? Math.Round(100.0 * k[3] / allCost, 1) : 0));
            }
            return rows;
        }

        public static JObj CraftStats(ShipConstruct ship)
        {
            var o = new JObj();
            o.Add("name", ship.shipName);
            o.Add("part_count", ship.parts.Count);
            float mass = ship.GetShipMass(out float dryMass, out float fuelMass);
            float cost = ship.GetShipCosts(out float dryCost, out float fuelCost);
            o.Add("mass_t", Math.Round(mass, 3));
            o.Add("dry_mass_t", Math.Round(dryMass, 3));
            o.Add("cost", Math.Round(cost));
            Vector3 size = ShipConstruction.CalculateCraftSize(ship);
            o.Add("height_m", Math.Round(size.y, 1));
            o.Add("width_m", Math.Round(Mathf.Max(size.x, size.z), 1));
            int crew = 0;
            foreach (Part p in ship.parts) crew += p.CrewCapacity;
            o.Add("crew_capacity", crew);
            JObj aero = Aerodynamics(ship);
            if (aero != null) o.Add("aircraft", aero);

            VesselDeltaV dv = ship.vesselDeltaV;
            if (dv != null && dv.IsReady)
            {
                o.Add("total_dv_vac", Math.Round(dv.TotalDeltaVVac));
                o.Add("total_dv_asl", Math.Round(dv.TotalDeltaVASL));
                o.Add("stages", StageTable(dv));
            }
            else o.Add("delta_v", "not available (stock delta-v calculation is still running or disabled)");
            // (how well it will hold together, as far as that can be told before it flies)
            JObj structure = StructureApi.Brief(ship);
            if (structure != null) o.Add("structure", structure);
            return o;
        }

        /// <summary>Per-stage numbers from the stock delta-v calculator, listed in the order the stages fire.</summary>
        public static List<object> StageTable(VesselDeltaV dv)
        {
            var infos = new List<DeltaVStageInfo>(dv.OperatingStageInfo);
            infos.Sort((x, y) => y.stage.CompareTo(x.stage));
            var stages = new List<object>();
            foreach (DeltaVStageInfo s in infos)
            {
                var so = new JObj().Add("ksp_stage", s.stage);
                so.Add("dv_vac", Math.Round(s.deltaVinVac));
                so.Add("dv_asl", Math.Round(s.deltaVatASL));
                so.Add("twr_vac", Math.Round(s.TWRVac, 2));
                so.Add("twr_asl", Math.Round(s.TWRASL, 2));
                so.Add("thrust_vac_kn", Math.Round(s.thrustVac, 1));
                so.Add("isp_vac", Math.Round(s.ispVac));
                so.Add("burn_s", Math.Round(s.stageBurnTime, 1));
                so.Add("start_mass_t", Math.Round(s.startMass, 3));
                so.Add("end_mass_t", Math.Round(s.endMass, 3));
                stages.Add(so);
            }
            return stages;
        }

        static List<string> DesignWarnings(ShipConstruct ship)
        {
            var w = new List<string>();
            bool command = false, chute = false, crewed = false, power = false, engine = false;
            foreach (Part p in ship.parts)
            {
                if (p.FindModuleImplementing<ModuleCommand>() != null) command = true;
                if (PartCatalog.IsParachute(p)) chute = true;
                if (p.CrewCapacity > 0) crewed = true;
                if (PartCatalog.IsEngine(p)) engine = true;
                if (p.FindModuleImplementing<ModuleDeployableSolarPanel>() != null || p.FindModuleImplementing<ModuleGenerator>() != null) power = true;
            }
            JObj aero = Aerodynamics(ship);
            bool aircraft = aero != null && HasWheels(ship);
            if (!command) w.Add("No command pod or probe core: the craft cannot be controlled.");
            if (crewed && !chute && !aircraft) w.Add("Crewed craft without a parachute: it cannot land safely on Kerbin.");
            if (!power && command && !crewed) w.Add("Probe with no solar panels or generator: it will run out of electricity.");
            if (aircraft)
            {
                double behind = Convert.ToDouble(aero["lift_behind_mass_centre_m"]);
                if (behind < 0) w.Add("The centre of lift is " + Math.Round(-behind, 2) + " m AHEAD of the centre of mass: the aircraft will flip. Move the wings back or the weight forward.");
                else if (behind < 0.05) w.Add("The centre of lift is almost on the centre of mass: barely stable. Move the wings back a little.");
                else if (behind > 1.5) w.Add("The centre of lift is " + Math.Round(behind, 2) + " m behind the centre of mass: very nose-heavy, it may not rotate for take-off. Move the wings forward.");
                double gearBehind = MainGearBehindMass(ship);
                if (!double.IsNaN(gearBehind) && gearBehind > 1.8) w.Add("The main wheels are " + Math.Round(gearBehind, 1) + " m behind the centre of mass: it will be hard to lift the nose. Put them just behind it.");
                else if (!double.IsNaN(gearBehind) && gearBehind < 0) w.Add("The main wheels are ahead of the centre of mass: the aircraft will sit on its tail.");
            }

            // (a rocket with fins: the same check, by the game's own marker; an aeroplane has had its own, above)
            if (!aircraft)
            {
                try
                {
                    bool fins = false;
                    foreach (Part p in ship.parts) if (p.FindModuleImplementing<ModuleLiftingSurface>() != null) { fins = true; break; }
                    if (fins && AirApi.EditorLift(ship, out double behind, out double behindEmpty))
                    {
                        string said = AirApi.Unsteady(behind, behindEmpty);
                        if (said != null) w.Add(said);
                    }
                }
                catch (Exception) { }
            }

            w.AddRange(LooksWarnings(ship, aircraft));

            VesselDeltaV dv = ship.vesselDeltaV;
            if (engine && dv != null && dv.IsReady)
            {
                DeltaVStageInfo first = null;
                foreach (DeltaVStageInfo s in dv.OperatingStageInfo)
                    if (s.deltaVinVac > 1f && (first == null || s.stage > first.stage)) first = s;
                bool anyThrust = false;
                foreach (DeltaVStageInfo s in dv.OperatingStageInfo) if (s.deltaVatASL > 1f || s.deltaVinVac > 1f) anyThrust = true;
                if (first == null && !anyThrust) w.Add("No stage produces delta-v: check that engines have fuel and are in the staging list.");
                else if (first == null) { }   // air-breathing engines only: fine for an aircraft
                else if (EditorDriver.editorFacility == EditorFacility.VAB)
                {
                    if (first.TWRASL < 1.0f) w.Add("Lift-off TWR at sea level is " + first.TWRASL.ToString("0.00") + " (< 1): it will not leave the pad. Add thrust or remove mass.");
                    else if (first.TWRASL < 1.2f) w.Add("Lift-off TWR at sea level is only " + first.TWRASL.ToString("0.00") + "; 1.3 to 1.8 is typical.");
                    else if (first.TWRASL > 2.6f) w.Add("Lift-off TWR at sea level is " + first.TWRASL.ToString("0.00") + ", which is very high; expect drag losses unless the ascent is throttled.");
                }
            }
            return w;
        }

        static string Across(int size) => size <= 0 ? "0.625 m" : size == 1 ? "1.25 m" : size == 2 ? "2.5 m" : size == 3 ? "3.75 m" : size == 4 ? "5 m" : (1.25f * size).ToString("0.##") + " m";

        /// <summary>
        /// What will make a craft look wrong, said the way a weak or unflyable one is: a step in width where one part
        /// sits straight on a wider or narrower one, a flat end left open to the air on top, and tanks painted in more
        /// than one way. (These are also drag: a flat end and a step are the two worst things a rocket can show the air.)
        /// </summary>
        /// <summary>
        /// The parts a fairing's shell is round (everything on the upper side of a fairing base that has a shell: its top node and the
        /// interstage nodes up its inside; for an interstage, only as far as the part it ends on), and those bases themselves: what
        /// cannot be seen does not look wrong.
        /// </summary>
        static HashSet<Part> UnderFairings(ShipConstruct ship, HashSet<Part> bases)
        {
            var hidden = new HashSet<Part>();
            foreach (Part p in ship.parts)
            {
                ModuleProceduralFairing shell = p.FindModuleImplementing<ModuleProceduralFairing>();
                if (shell == null || shell.xSections == null || shell.xSections.Count < 2) continue;
                bases.Add(p);
                Part end = null;
                if (shell.interstageCraftID != 0) foreach (Part q in ship.parts) if (q.craftID == shell.interstageCraftID) { end = q; break; }
                var seen = new HashSet<Part> { p };
                if (end != null) seen.Add(end);
                var open = new Queue<Part>();
                foreach (AttachNode n in p.attachNodes)
                    if (n.id != "bottom" && n.attachedPart != null && seen.Add(n.attachedPart)) open.Enqueue(n.attachedPart);
                while (open.Count > 0)
                {
                    Part q = open.Dequeue();
                    hidden.Add(q);
                    if (q.parent != null && seen.Add(q.parent)) open.Enqueue(q.parent);
                    foreach (Part c in q.children) if (seen.Add(c)) open.Enqueue(c);
                }
            }
            return hidden;
        }

        static List<string> LooksWarnings(ShipConstruct ship, bool aircraft)
        {
            var w = new List<string>();
            try
            {
                var shells = new HashSet<Part>();
                HashSet<Part> hidden = UnderFairings(ship, shells);
                // ---- steps in width: two parts joined end to end whose joining faces are of different sizes
                var steps = new List<string>();
                foreach (Part p in ship.parts)
                {
                    Part q = p.parent;
                    if (q == null) continue;
                    // (inside a fairing, or where a payload stands on its base: not to be seen)
                    if (hidden.Contains(p) && (hidden.Contains(q) || shells.Contains(q)) || hidden.Contains(q) && shells.Contains(p)) continue;
                    AttachNode mine = p.FindAttachNodeByPart(q), its = q.FindAttachNodeByPart(p);
                    if (mine == null || its == null || mine.nodeType != AttachNode.NodeType.Stack || its.nodeType != AttachNode.NodeType.Stack) continue;
                    if (Math.Abs(mine.size - its.size) < 1) continue;
                    // (an engine under a wider tank is how rockets are; it is the other way round, and tank on tank, that looks wrong)
                    bool engineUnder = PartCatalog.IsEngine(p) && mine.size < its.size;
                    if (engineUnder) continue;
                    steps.Add(p.partInfo.title + " (" + Across(mine.size) + " where it joins) on " + q.partInfo.title + " (" + Across(its.size) + ")");
                }
                if (steps.Count > 0)
                    w.Add("Looks: a step in width where " + string.Join("; ", steps.GetRange(0, Math.Min(3, steps.Count)).ToArray()) + (steps.Count > 3 ? "; and " + (steps.Count - 3) + " more" : "")
                        + ". Put an adapter between them (search the parts for 'adapter'), or use parts of one width.");

                // ---- flat ends open to the air on top
                if (!aircraft)
                {
                    var blunt = new Dictionary<string, int>();
                    foreach (Part p in ship.parts)
                    {
                        if (p.partInfo == null || hidden.Contains(p)) continue;
                        bool tank = p.partInfo.category == PartCategories.FuelTank || p.partInfo.category == PartCategories.Propulsion || p.partInfo.category == PartCategories.Structural
                            || (PartCatalog.IsEngine(p) && p.Resources.Contains("SolidFuel"));
                        if (!tank) continue;
                        foreach (AttachNode node in p.attachNodes)
                        {
                            if (node.nodeType != AttachNode.NodeType.Stack || node.attachedPart != null || node.size < 1) continue;
                            if (Vector3.Dot(p.transform.TransformDirection(node.orientation), Vector3.up) < 0.7f) continue;
                            blunt[p.partInfo.title] = blunt.TryGetValue(p.partInfo.title, out int n) ? n + 1 : 1;
                        }
                    }
                    if (blunt.Count > 0)
                    {
                        var said = new List<string>();
                        foreach (KeyValuePair<string, int> kv in blunt) said.Add(kv.Key + (kv.Value > 1 ? " x " + kv.Value : ""));
                        w.Add("Looks: a flat end is left open to the air on top of " + string.Join(", ", said.GetRange(0, Math.Min(4, said.Count)).ToArray())
                            + ". Put a nose cone on it (search the parts for 'nose cone'); boosters want one each. It is drag as well as looks.");
                    }
                }

                // ---- tanks painted more than one way
                var paints = new Dictionary<string, int>();
                foreach (Part p in ship.parts)
                {
                    if (p.partInfo == null || p.partInfo.category != PartCategories.FuelTank || hidden.Contains(p)) continue;
                    ModulePartVariants looks = p.FindModuleImplementing<ModulePartVariants>();
                    if (looks == null || looks.SelectedVariant == null) continue;
                    string paint = !string.IsNullOrEmpty(looks.SelectedVariant.themeName) ? looks.SelectedVariant.themeName : looks.SelectedVariant.Name;
                    paints[paint] = paints.TryGetValue(paint, out int n) ? n + 1 : 1;
                }
                if (paints.Count > 1)
                    w.Add("Looks: the tanks are painted " + paints.Count + " different ways (" + string.Join(", ", new List<string>(paints.Keys).ToArray()) + "). Give the craft one look with theme, or the same variant on each.");

                // ---- proportions
                if (!aircraft && EditorDriver.editorFacility == EditorFacility.VAB)
                {
                    Vector3 size = ShipConstruction.CalculateCraftSize(ship);
                    float wide = Mathf.Max(size.x, size.z);
                    if (wide > 0.5f && size.y / wide < 1.6f && ship.parts.Count > 3)
                        w.Add("Looks: the craft is squat, " + size.y.ToString("0.#") + " m tall and " + wide.ToString("0.#") + " m across. A rocket is usually four to twelve times as tall as it is wide.");
                }
            }
            catch (Exception ex) { BridgeHost.LogWarn("looks check failed: " + ex.Message); }
            return w;
        }

        static bool HasWheels(ShipConstruct ship)
        {
            foreach (Part p in ship.parts)
            {
                ModuleWheelBase w = p.FindModuleImplementing<ModuleWheelBase>();
                if (w != null && w.wheelType != WheelType.LEG) return true;
            }
            return false;
        }

        static Vector3 MassCentre(ShipConstruct ship, out float mass)
        {
            Vector3 sum = Vector3.zero;
            mass = 0f;
            foreach (Part p in ship.parts)
            {
                float m = p.mass + p.GetResourceMass();
                sum += (p.transform.position + p.transform.rotation * p.CoMOffset) * m;
                mass += m;
            }
            return mass > 0 ? sum / mass : Vector3.zero;
        }

        /// <summary>Wing data for craft that have wings: where the lift acts relative to the mass, and the speed needed to fly.</summary>
        static JObj Aerodynamics(ShipConstruct ship)
        {
            if (ship.parts.Count == 0) return null;
            Transform root = ship.parts[0].transform;
            Vector3 nose = root.up, top = -root.forward;
            Vector3 centre = Vector3.zero;
            double rating = 0;
            FloatCurve curve = null;
            foreach (Part p in ship.parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleLiftingSurface ls) || ls.deflectionLiftCoeff <= 0f) continue;
                    Transform t = p.transform;
                    Vector3 lift = ls.transformDir == ModuleLiftingSurface.TransformDir.Z ? t.forward : ls.transformDir == ModuleLiftingSurface.TransformDir.Y ? t.up : t.right;
                    float share = ls.deflectionLiftCoeff * Mathf.Abs(Vector3.Dot(lift, top));
                    centre += (t.position + t.rotation * p.CoLOffset) * share;
                    rating += share;
                    if (curve == null) curve = ls.liftCurve;
                }
            }
            if (rating < 0.3 || EditorDriver.editorFacility != EditorFacility.SPH) return null;
            centre /= (float)rating;
            Vector3 com = MassCentre(ship, out float mass);
            var o = new JObj();
            o.Add("wing_lift_rating", Math.Round(rating, 2));
            o.Add("lift_behind_mass_centre_m", Math.Round(Vector3.Dot(com - centre, nose), 2));
            double cl = curve != null ? curve.Evaluate(Mathf.Sin(10f * Mathf.Deg2Rad)) : 0.34;
            if (cl > 0.01) o.Add("takeoff_speed_estimate", Math.Round(Math.Sqrt(2.0 * (mass * 9.81) / (PhysicsGlobals.LiftMultiplier * cl * rating) / 1.2)));
            return o;
        }

        /// <summary>How far the rearmost pair of wheels sits behind the centre of mass (negative: ahead of it).</summary>
        static double MainGearBehindMass(ShipConstruct ship)
        {
            Transform root = ship.parts[0].transform;
            Vector3 com = MassCentre(ship, out float _);
            double best = double.NaN, bestDistance = double.MaxValue;
            foreach (Part p in ship.parts)
            {
                ModuleWheelBase w = p.FindModuleImplementing<ModuleWheelBase>();
                if (w == null || w.wheelType == WheelType.LEG) continue;
                double behind = Vector3.Dot(com - p.transform.position, root.up);
                // The main gear is the set closest to the centre of mass; the nose or tail wheel is the far one.
                if (Math.Abs(behind) < bestDistance) { bestDistance = Math.Abs(behind); best = behind; }
            }
            return best;
        }

        // ---------------------------------------------------------------- load / launch

        static IEnumerator LoadCraft(Call c)
        {
            RequireGame();
            string path = FindCraft(c.Args.Require("name"), out EditorFacility facility);
            host.Note("opening craft '" + Path.GetFileNameWithoutExtension(path) + "'");
            yield return OpenInEditor(path, facility, 0);
            ShipConstruct ship = EditorLogic.fetch.ship;
            yield return WaitDeltaV(ship);
            JObj result = CraftStats(ship);
            List<string> warnings = DesignWarnings(ship);
            if (warnings.Count > 0) result.Add("warnings", warnings);
            c.Result = result;
        }

        /// <summary>Do something once the next frame's updates are over, where a coroutine that waits for nothing goes on.</summary>
        internal static IEnumerator AfterUpdates(Action then)
        {
            yield return null;
            then();
        }

        static IEnumerator Launch(Call c)
        {
            RequireGame();
            string path;
            EditorFacility facility;
            string craft = c.Args.Str("craft");
            if (!string.IsNullOrEmpty(craft)) path = FindCraft(craft, out facility);
            else
            {
                if (HighLogic.LoadedScene != GameScenes.EDITOR || EditorLogic.fetch == null || EditorLogic.fetch.ship == null || EditorLogic.fetch.ship.parts.Count == 0)
                    throw new ApiException("Give 'craft' (a saved craft name), or open one in the editor first.");
                ShipConstruct ship = EditorLogic.fetch.ship;
                facility = EditorDriver.editorFacility;
                EditorLogic.fetch.SetBackup();
                path = ShipConstruction.SaveShip(ship, FileNameFor(string.IsNullOrEmpty(ship.shipName) ? "Untitled Space Craft" : ship.shipName));
            }
            if (!File.Exists(path)) throw new ApiException("Craft file not found: " + path);

            string site = c.Args.Str("site") ?? (facility == EditorFacility.SPH ? "Runway" : "LaunchPad");
            if (site.Equals("launchpad", StringComparison.OrdinalIgnoreCase) || site.Equals("pad", StringComparison.OrdinalIgnoreCase)) site = "LaunchPad";
            if (site.Equals("runway", StringComparison.OrdinalIgnoreCase)) site = "Runway";

            ConfigNode craftNode = ConfigNode.Load(path);
            if (craftNode == null) throw new ApiException("Could not read craft file " + path);
            string problem = "";
            if (!ShipConstruction.AllPartsFound(craftNode, ref problem)) throw new ApiException("Craft uses parts that are not installed: " + problem);

            if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER && Funding.Instance != null)
            {
                float total = 0f;
                foreach (ConfigNode pn in craftNode.GetNodes("PART"))
                {
                    AvailablePart ap = PartLoader.getPartInfoByName(KSPUtil.GetPartName(pn.GetValue("part")));
                    if (ap != null) total += ShipConstruction.GetPartCostsAndMass(pn, ap, out float _, out float _, out float _, out float _);
                }
                if (total > Funding.Instance.Funds) throw new ApiException("Not enough funds: craft costs " + Math.Round(total) + ", you have " + Math.Round(Funding.Instance.Funds) + ".");
            }

            if (HighLogic.LoadedScene == GameScenes.FLIGHT)
            {
                yield return GameApi.WaitClearToSave();
                GameApi.SaveBeforeLeaving();
            }
            else GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);

            List<ProtoVessel> blocking = ShipConstruction.FindVesselsLandedAt(HighLogic.CurrentGame.flightState, site);
            if (blocking != null && blocking.Count > 0)
            {
                if (!c.Args.Bool("clear_site", true))
                    throw new ApiException(site + " is occupied by '" + blocking[0].vesselName + "'. Pass clear_site=true to recover it first.");
                foreach (ProtoVessel pv in blocking)
                {
                    host.Note("recovered '" + pv.vesselName + "' to clear the " + site);
                    ShipConstruction.RecoverVesselFromFlight(pv, HighLogic.CurrentGame.flightState, true);
                }
            }

            VesselCrewManifest manifest;
            string hired = null, crewProblem = null;
            object crewArg = c.Args.Raw("crew");
            if (crewArg is string crewMode && crewMode.Equals("none", StringComparison.OrdinalIgnoreCase))
                manifest = VesselCrewManifest.FromConfigNode(craftNode);
            else if (crewArg is List<object> names && names.Count > 0)
                manifest = ManifestFor(craftNode, names);
            else if (crewArg is double crewCount && crewCount >= 1)
                manifest = ManifestOf(craftNode, (int)Math.Round(crewCount), ref hired);
            else
            {
                KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
                manifest = roster.DefaultCrewForVessel(craftNode, null, true, false);
                // With every kerbal away on a mission the default crew is nobody, and a pod without a
                // pilot or probe core cannot be flown. Hiring is free outside career mode, so do it.
                if (Aboard(manifest) == 0 && manifest.GetCrewableParts().Count > 0 && NeedsCrew(craftNode))
                {
                    if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
                    {
                        ProtoCrewMember recruit = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                        KerbalRoster.SetExperienceTrait(recruit, KerbalRoster.pilotTrait);
                        recruit.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        hired = recruit.name;
                        host.Note("hired " + recruit.name + " because no kerbal was free");
                        manifest = roster.DefaultCrewForVessel(craftNode, null, true, false);
                    }
                    else crewProblem = "No kerbal is available, so the craft launched without crew and has no probe core: it cannot be controlled. Hire crew in the Astronaut Complex or add a probe core.";
                }
            }

            string shipName = craftNode.GetValue("ship") ?? Path.GetFileNameWithoutExtension(path);
            host.Note("launching '" + Localizer.Format(shipName) + "' from " + site);
            string flag = HighLogic.CurrentGame.flagURL;
            // (Started from a coroutine of the host, once the frame's updates are over, as the editor's own Launch button starts it.
            // Started from here, inside the bridge's Update, the scene counted as flight before the editor's parts had had their
            // update in that frame, with no flight camera yet: each of them threw once, in the game's Part.UpdateMouseOver.)
            string failed = null;
            bool started = false;
            host.StartCoroutine(AfterUpdates(() =>
            {
                try { FlightDriver.StartWithNewLaunch(path, string.IsNullOrEmpty(flag) ? "Squad/Flags/default" : flag, site, manifest); }
                catch (Exception ex) { failed = ex.Message; }
                started = true;
            }));
            while (!started) yield return null;
            if (failed != null) throw new ApiException("The launch did not start: " + failed);
            yield return SceneWatch.WaitFor(GameScenes.FLIGHT, 180f);
            // Physics takes a moment to settle the vessel onto the pad.
            float settle = Time.realtimeSinceStartup + 20f;
            while (FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.packed && Time.realtimeSinceStartup < settle) yield return null;
            yield return FlightApi.Settle();
            JObj summary = FlightApi.VesselSummary(FlightGlobals.ActiveVessel);
            if (hired != null) summary.Add("hired", hired + " (too few kerbals were free)");
            if (crewProblem != null) summary.Add("warning", crewProblem);
            else if (FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.IsControllable)
                summary.Add("warning", "This vessel cannot be controlled as launched: it has no crew in a command pod and no working probe core.");
            c.Result = summary;
        }

        static int Aboard(VesselCrewManifest manifest)
        {
            int count = 0;
            foreach (PartCrewManifest pcm in manifest.GetCrewableParts())
                foreach (string name in pcm.partCrew) if (!string.IsNullOrEmpty(name)) count++;
            return count;
        }

        /// <summary>True when the craft has a crewed command pod and no probe core to fly it instead.</summary>
        static bool NeedsCrew(ConfigNode craftNode)
        {
            bool pod = false;
            foreach (ConfigNode pn in craftNode.GetNodes("PART"))
            {
                AvailablePart ap = PartLoader.getPartInfoByName(KSPUtil.GetPartName(pn.GetValue("part")));
                if (ap == null || ap.partPrefab == null) continue;
                ModuleCommand cmd = ap.partPrefab.FindModuleImplementing<ModuleCommand>();
                if (cmd == null) continue;
                if (cmd.minimumCrew == 0) return false;
                pod = true;
            }
            return pod;
        }

        static VesselCrewManifest ManifestFor(ConfigNode craftNode, List<object> names)
        {
            VesselCrewManifest manifest = VesselCrewManifest.FromConfigNode(craftNode);
            KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
            var wanted = new Queue<ProtoCrewMember>();
            foreach (object n in names)
            {
                string name = Convert.ToString(n);
                ProtoCrewMember found = null;
                foreach (ProtoCrewMember k in roster.Crew)
                    if (k.name.Equals(name, StringComparison.OrdinalIgnoreCase) || k.name.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)) { found = k; break; }
                if (found == null) throw new ApiException("No kerbal named '" + name + "' in the roster.");
                if (found.rosterStatus != ProtoCrewMember.RosterStatus.Available) throw new ApiException(found.name + " is not available (" + found.rosterStatus + ").");
                wanted.Enqueue(found);
            }
            foreach (PartCrewManifest pcm in manifest.GetCrewableParts())
            {
                for (int seat = 0; seat < pcm.partCrew.Length && wanted.Count > 0; seat++)
                    if (string.IsNullOrEmpty(pcm.partCrew[seat])) pcm.AddCrewToSeat(wanted.Dequeue(), seat);
                if (wanted.Count == 0) break;
            }
            if (wanted.Count > 0) throw new ApiException("The craft does not have enough seats for that crew.");
            return manifest;
        }

        /// <summary>So many kerbals aboard, those who are free first. Where too few are free, more are hired: outside career mode
        /// hiring costs nothing (in a career it is the player's money, and theirs to spend).</summary>
        static VesselCrewManifest ManifestOf(ConfigNode craftNode, int count, ref string hired)
        {
            VesselCrewManifest manifest = VesselCrewManifest.FromConfigNode(craftNode);
            int seats = 0;
            foreach (PartCrewManifest pcm in manifest.GetCrewableParts()) seats += pcm.partCrew.Length;
            if (count > seats) throw new ApiException("The craft has " + seats + " seats, not " + count + ".");
            KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
            var wanted = new Queue<ProtoCrewMember>();
            foreach (ProtoCrewMember k in roster.Crew)
                if (k.rosterStatus == ProtoCrewMember.RosterStatus.Available && wanted.Count < count) wanted.Enqueue(k);
            var newcomers = new List<string>();
            while (wanted.Count < count)
            {
                if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER)
                    throw new ApiException("Only " + wanted.Count + " kerbals are free. Hire more in the Astronaut Complex, or ask for fewer.");
                ProtoCrewMember recruit = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                recruit.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                newcomers.Add(recruit.name);
                wanted.Enqueue(recruit);
            }
            foreach (PartCrewManifest pcm in manifest.GetCrewableParts())
                for (int seat = 0; seat < pcm.partCrew.Length && wanted.Count > 0; seat++)
                    if (string.IsNullOrEmpty(pcm.partCrew[seat])) pcm.AddCrewToSeat(wanted.Dequeue(), seat);
            if (newcomers.Count > 0) hired = string.Join(", ", newcomers.ToArray());
            return manifest;
        }
    }
}
