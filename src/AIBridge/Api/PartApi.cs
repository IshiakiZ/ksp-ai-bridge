using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Localization;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// The parts of the vessel in flight and the right-click menu each one offers. Every button a player
    /// could press on a part ("Extend Solar Panel", "Deploy Chute", "Toggle Engine"...) is reachable here.
    /// </summary>
    public static class PartApi
    {
        static BridgeHost host;

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("vessel_parts", "Parts of the active vessel (or of the craft in the editor) with their state. params: {search, kind, limit}", VesselParts).AlwaysAllowed = true;
            Rpc.Sync("part_menu", "Buttons, actions and readouts a part offers. params: {part}", PartMenu).AlwaysAllowed = true;
            Rpc.Sync("part_do", "Press a part's button or run its action. params: {part, do (or 'action', for flight plans where 'do' names the command), module, type}", PartDo);
            Rpc.Sync("part_set", "Set a tweakable value on a part. params: {part, field, value}", PartSet);
        }

        static string Loc(string s) => string.IsNullOrEmpty(s) ? "" : Localizer.Format(s);

        // ---------------------------------------------------------------- selection

        static bool InEditor => HighLogic.LoadedScene == GameScenes.EDITOR;

        /// <summary>The parts these commands act on: the vessel being flown, or the craft open in the editor.</summary>
        static List<Part> Scope(out string name)
        {
            if (InEditor)
            {
                ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
                if (ship == null || ship.parts.Count == 0) throw new ApiException("The editor is empty.");
                name = ship.shipName;
                return ship.parts;
            }
            Vessel v = FlightApi.Active();
            name = v.GetDisplayName();
            return v.parts;
        }

        static uint IdOf(Part p) => InEditor ? p.craftID : p.flightID;

        /// <summary>Resolve "all", an id, or a name/title fragment to parts.</summary>
        public static List<Part> Select(List<Part> parts, string selector)
        {
            var result = new List<Part>();
            if (string.IsNullOrEmpty(selector)) throw new ApiException("Say which part: its id from vessel_parts, part of its name, or \"all\".");
            string s = selector.Trim();
            if (s.Equals("all", StringComparison.OrdinalIgnoreCase) || s == "*") { result.AddRange(parts); return result; }
            if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id))
            {
                foreach (Part p in parts) if (IdOf(p) == id) result.Add(p);
                if (result.Count > 0) return result;
            }
            foreach (Part p in parts)
                if (p.partInfo.name.Equals(s, StringComparison.OrdinalIgnoreCase) || p.partInfo.title.Equals(s, StringComparison.OrdinalIgnoreCase)) result.Add(p);
            if (result.Count > 0) return result;
            foreach (Part p in parts)
                if (p.partInfo.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 || p.partInfo.title.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) result.Add(p);
            if (result.Count == 0) throw new ApiException("No part matches '" + selector + "'. Use vessel_parts to list them.");
            return result;
        }

        static List<string> Kinds(Part p)
        {
            var k = new List<string>();
            foreach (PartModule m in p.Modules)
            {
                string kind = null;
                switch (m)
                {
                    case ModuleEngines _: kind = "engine"; break;
                    case ModuleDecouplerBase d: kind = d.stagingEnabled ? "decoupler" : null; break;
                    case ModuleParachute _: kind = "parachute"; break;
                    case ModuleCommand _: kind = "command"; break;
                    case ModuleDeployableSolarPanel _: kind = "solar_panel"; break;
                    case ModuleDeployableAntenna _: kind = "antenna"; break;
                    case ModuleDeployableRadiator _: kind = "radiator"; break;
                    case ModuleDataTransmitter _: kind = "antenna"; break;
                    case ModuleScienceExperiment _: kind = "experiment"; break;
                    case ModuleDockingNode _: kind = "docking_port"; break;
                    case ModuleWheelBase wb: kind = wb.wheelType == WheelType.LEG ? "landing_leg" : "wheel"; break;
                    case ModuleRCS _: kind = "rcs"; break;
                    case ModuleReactionWheel _: kind = "reaction_wheel"; break;
                    case ModuleProceduralFairing _: kind = "fairing"; break;
                    case ModuleLight _: kind = "light"; break;
                    case ModuleControlSurface _: kind = "control_surface"; break;
                    case ModuleResourceHarvester _: kind = "drill"; break;
                    case ModuleResourceConverter _: kind = "converter"; break;
                    case LaunchClamp _: kind = "launch_clamp"; break;
                    case ModuleCargoBay _: kind = "cargo_bay"; break;
                    case ModuleAblator _: kind = "heat_shield"; break;
                }
                if (kind != null && !k.Contains(kind)) k.Add(kind);
            }
            if (p is CompoundPart) k.Add(p.partInfo.name == "fuelLine" ? "fuel_line" : "strut");
            if (p.Resources.Count > 0 && k.Count == 0) k.Add("tank");
            return k;
        }

        static JObj Describe(Part p)
        {
            var o = new JObj();
            o.Add("id", IdOf(p));
            o.Add("title", p.partInfo.title);
            List<string> kinds = Kinds(p);
            if (kinds.Count > 0) o.Add("kind", string.Join(",", kinds.ToArray()));
            if (p.inverseStage >= 0 && (p.hasStagingIcon || (p.stackIcon != null && p.stackIcon.StageIcon != null))) o.Add("stage", p.inverseStage);

            var state = new List<string>();
            foreach (PartModule m in p.Modules)
            {
                switch (m)
                {
                    case ModuleEngines e:
                        state.Add(!e.EngineIgnited ? "engine off" : e.flameout ? "FLAMED OUT" : "running " + Math.Round(e.finalThrust, 1) + " kN");
                        if (e.thrustPercentage < 99.5f) state.Add("thrust limit " + Math.Round(e.thrustPercentage) + "%");
                        break;
                    case ModuleParachute chute:
                        state.Add("chute " + chute.deploymentState.ToString().ToLowerInvariant() + (chute.deploymentState == ModuleParachute.deploymentStates.STOWED ? " (deploy now would be " + chute.deploymentSafeState.ToString().ToLowerInvariant() + ")" : ""));
                        break;
                    case ModuleDeployablePart dp:
                        state.Add(dp.deployState.ToString().ToLowerInvariant());
                        break;
                    case ModuleDecouplerBase d:
                        if (d.isDecoupled) state.Add("decoupled");
                        break;
                    case ModuleScienceExperiment exp:
                        state.Add(exp.GetScienceCount() > 0 ? "has data" : exp.Deployed ? "used" : "ready");
                        break;
                    case ModuleDockingNode dn:
                        if (dn.fsm != null && dn.fsm.Started) state.Add(dn.fsm.currentStateName);
                        break;
                    case ModuleWheels.ModuleWheelDeployment wd:
                        state.Add(wd.stateDisplayString);
                        break;
                }
            }
            if (p is CompoundPart link)
            {
                // Struts and fuel lines: say what the far end is fixed to, or that it is hanging loose.
                state.Add(link.target != null ? "connects '" + (p.parent != null ? p.parent.partInfo.title : "?") + "' to '" + link.target.partInfo.title + "'" : "NOT CONNECTED (" + link.attachState + ")");
            }
            if (state.Count > 0) o.Add("state", string.Join("; ", state.ToArray()));
            if (p.Resources.Count > 0)
            {
                var res = new JObj();
                foreach (PartResource r in p.Resources) res.Add(r.resourceName, Math.Round(r.amount, 1) + "/" + Math.Round(r.maxAmount, 1));
                o.Add("resources", res);
            }
            if (p.protoModuleCrew != null && p.protoModuleCrew.Count > 0)
            {
                var crew = new List<object>();
                foreach (ProtoCrewMember k in p.protoModuleCrew) crew.Add(k.name);
                o.Add("crew", crew);
            }
            if (p.temperature > p.maxTemp * 0.75) o.Add("hot", Math.Round(p.temperature) + " K of " + Math.Round(p.maxTemp) + " K max");
            return o;
        }

        static object VesselParts(Args a)
        {
            List<Part> all = Scope(out string scopeName);
            string search = a.Str("search");
            string kind = a.Str("kind");
            int limit = Math.Max(1, Math.Min(400, a.Int("limit", 120)));
            var list = new List<object>();
            int matched = 0;
            foreach (Part p in all)
            {
                if (!string.IsNullOrEmpty(search) && p.partInfo.title.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && p.partInfo.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(kind) && !Kinds(p).Contains(kind.ToLowerInvariant())) continue;
                matched++;
                if (list.Count < limit) list.Add(Describe(p));
            }
            return new JObj().Add(InEditor ? "editor_craft" : "vessel", scopeName).Add("total_parts", all.Count).Add("matched", matched).Add("parts", list);
        }

        // ---------------------------------------------------------------- menus

        static bool Usable(BaseEvent e) => e.active && (InEditor ? e.guiActiveEditor : e.guiActive || e.guiActiveUnfocused) && !string.IsNullOrEmpty(e.guiName);

        static bool Shown(BaseField f) => (InEditor ? f.guiActiveEditor : f.guiActive) && !string.IsNullOrEmpty(f.guiName);

        static void EditorChanged()
        {
            if (!InEditor || EditorLogic.fetch == null) return;
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            EditorLogic.fetch.SetBackup();
        }

        static object PartMenu(Args a)
        {
            List<Part> parts = Select(Scope(out string _), a.Require("part"));
            var result = new List<object>();
            var seen = new HashSet<string>();
            foreach (Part p in parts)
            {
                // Symmetric copies offer the same menu; show one of each kind.
                if (!seen.Add(p.partInfo.name) && parts.Count > 1) continue;
                if (result.Count >= 8) break;
                JObj o = Describe(p);
                var modules = new List<object>();
                foreach (PartModule m in p.Modules)
                {
                    var buttons = new List<object>();
                    foreach (BaseEvent e in m.Events) if (Usable(e)) buttons.Add(Loc(e.guiName));
                    var actions = new List<object>();
                    foreach (BaseAction act in m.Actions) if (act.active && !string.IsNullOrEmpty(act.guiName)) actions.Add(Loc(act.guiName));
                    var fields = new JObj();
                    foreach (BaseField f in m.Fields)
                    {
                        if (!Shown(f)) continue;
                        object value = null;
                        try { value = f.GetValue(m); } catch { }
                        if (value is float fl) value = Math.Round(fl, 3);
                        else if (value is double db) value = Math.Round(db, 3);
                        else if (value != null && !(value is bool) && !(value is int)) value = Loc(value.ToString());
                        fields[Loc(f.guiName) + " [" + f.name + "]"] = value;
                    }
                    if (buttons.Count == 0 && actions.Count == 0 && fields.Count == 0) continue;
                    var mo = new JObj().Add("module", m.moduleName);
                    if (buttons.Count > 0) mo.Add("buttons", buttons);
                    if (actions.Count > 0) mo.Add("actions", actions);
                    if (fields.Count > 0) mo.Add("readouts", fields);
                    modules.Add(mo);
                }
                o.Add("menu", modules);
                result.Add(o);
            }
            return result;
        }

        static bool Matches(string candidateGui, string candidateName, string want, bool exact)
        {
            string gui = Loc(candidateGui);
            if (exact) return gui.Equals(want, StringComparison.OrdinalIgnoreCase) || candidateName.Equals(want, StringComparison.OrdinalIgnoreCase);
            return gui.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0 || candidateName.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static object PartDo(Args a)
        {
            List<Part> parts = Select(Scope(out string _), a.Require("part"));
            string want = (a.Str("do") ?? a.Str("action") ?? a.Require("do")).Trim();
            string moduleFilter = a.Str("module");
            KSPActionType type = KSPActionType.Activate;
            string typeName = (a.Str("type") ?? "").ToLowerInvariant();
            if (typeName == "deactivate" || typeName == "off") type = KSPActionType.Deactivate;
            else if (typeName == "toggle") type = KSPActionType.Toggle;

            var done = new List<string>();
            var handled = new HashSet<Part>();
            foreach (bool exact in new[] { true, false })
            {
                foreach (Part p in parts)
                {
                    // In the editor a button press already reaches the part's symmetric twins.
                    if (handled.Contains(p)) { done.Add(p.partInfo.title + ": (with its symmetric twin)"); continue; }
                    foreach (PartModule m in p.Modules)
                    {
                        if (!string.IsNullOrEmpty(moduleFilter) && !m.moduleName.Equals(moduleFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        bool pressed = false;
                        foreach (BaseEvent e in m.Events)
                        {
                            if (!Usable(e) || !Matches(e.guiName, e.name, want, exact)) continue;
                            string label = Loc(e.guiName);
                            e.Invoke();
                            done.Add(p.partInfo.title + ": " + label);
                            if (InEditor) foreach (Part twin in p.symmetryCounterparts) handled.Add(twin);
                            pressed = true;
                            break;
                        }
                        if (pressed) break;
                        foreach (BaseAction act in m.Actions)
                        {
                            if (!act.active || !Matches(act.guiName, act.name, want, exact)) continue;
                            act.Invoke(new KSPActionParam(KSPActionGroup.None, type));
                            done.Add(p.partInfo.title + ": " + Loc(act.guiName));
                            pressed = true;
                            break;
                        }
                        if (pressed) break;
                    }
                }
                if (done.Count > 0) break;
            }
            if (done.Count == 0)
                throw new ApiException("None of the " + parts.Count + " selected part(s) has an available button or action matching '" + want + "'. Use part_menu to see what each part offers right now.");
            EditorChanged();
            host.Note("part action '" + want + "' on " + done.Count + " part(s)");
            var summary = new List<object>();
            for (int n = 0; n < done.Count && n < 12; n++) summary.Add(done[n]);
            return new JObj().Add("parts_affected", done.Count).Add("did", summary);
        }

        static object PartSet(Args a)
        {
            List<Part> parts = Select(Scope(out string _), a.Require("part"));
            string field = a.Require("field");
            object value = a.Raw("value");
            if (value == null) throw new ApiException("Give 'value'.");
            int changed = 0;
            foreach (Part p in parts) if (EditorApi.SetField(p, field, value)) changed++;
            if (changed == 0) throw new ApiException("No selected part has a field named '" + field + "'. Field names appear in [brackets] in part_menu readouts.");
            EditorChanged();
            host.Note("set " + field + " = " + value + " on " + changed + " part(s)");
            return new JObj().Add("parts_changed", changed);
        }
    }
}
