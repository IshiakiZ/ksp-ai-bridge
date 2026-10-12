using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using KSP.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIBridge.Api
{
    /// <summary>
    /// KSP interrupts with popups (tutorial intros, recovery summaries, confirmations). These commands let
    /// an AI see them and press their buttons, and press ordinary on-screen buttons by their label.
    /// </summary>
    public static class DialogApi
    {
        static BridgeHost host;
        static readonly Regex tags = new Regex("<[^>]+>", RegexOptions.Compiled);

        public static void Register(BridgeHost h)
        {
            host = h;
            Rpc.Sync("dialogs", "Popup dialogs currently open, with their text and buttons.", a => Describe()).AlwaysAllowed = true;
            Rpc.Sync("dialog_click", "Press a button on an open popup, optionally typing into its text boxes first. params: {button, dialog, text: [..]}", Click);
            Rpc.Sync("ui_buttons", "Visible on-screen buttons that have a text label. params: {search}", UiButtons).AlwaysAllowed = true;
            Rpc.Sync("ui_click", "Click a visible on-screen button by its label. params: {label, exact}", UiClick);
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = tags.Replace(Localizer.Format(s), "");
            return s.Replace("\r", "").Trim();
        }

        static void Walk(DialogGUIBase item, List<DialogGUIButton> buttons, StringBuilder text)
        {
            if (item == null) return;
            if (item is DialogGUIButton b) buttons.Add(b);
            else if (item is DialogGUILabel label)
            {
                string t = Clean(label.GetString != null ? label.GetString() : label.OptionText);
                if (t.Length > 0 && t != "No Function") text.Append(t).Append(' ');
            }
            if (item.children != null) foreach (DialogGUIBase child in item.children) Walk(child, buttons, text);
        }

        static string ButtonLabel(DialogGUIButton b)
        {
            string label = null;
            try { label = b.GetString != null ? b.GetString() : b.OptionText; } catch { }
            return Clean(label);
        }

        sealed class Popup
        {
            public PopupDialog Dialog;
            public string Title, Message;
            public List<DialogGUIButton> Buttons = new List<DialogGUIButton>();
        }

        static List<Popup> Popups()
        {
            var list = new List<Popup>();
            foreach (PopupDialog pd in UnityEngine.Object.FindObjectsOfType<PopupDialog>())
            {
                if (pd == null || !pd.gameObject.activeInHierarchy || pd.dialogToDisplay == null) continue;
                MultiOptionDialog d = pd.dialogToDisplay;
                var p = new Popup { Dialog = pd, Title = Clean(d.title) };
                var text = new StringBuilder(Clean(d.message));
                if (text.Length > 0) text.Append(' ');
                if (d.Options != null) foreach (DialogGUIBase item in d.Options) Walk(item, p.Buttons, text);
                p.Message = text.ToString().Trim();
                if (p.Message == "Choose an option:") p.Message = "";
                list.Add(p);
            }
            return list;
        }

        static List<object> Describe()
        {
            var result = new List<object>();
            foreach (Popup p in Popups())
            {
                var labels = new List<object>();
                foreach (DialogGUIButton b in p.Buttons) { string l = ButtonLabel(b); if (l.Length > 0) labels.Add(l); }
                string msg = p.Message.Length > 600 ? p.Message.Substring(0, 600) + "…" : p.Message;
                result.Add(new JObj().Add("title", p.Title).Add("text", msg).Add("buttons", labels));
            }
            if (KSP.UI.Dialogs.FlightResultsDialog.isDisplaying)
                result.Add(new JObj().Add("title", "Flight Results").Add("text", FlightResultsNote).Add("buttons", new List<object> { "Close" }).Add("note", "press its buttons with ui_click"));
            return result;
        }

        const string FlightResultsNote = "The game's end-of-flight summary is on screen. It appears by itself when the vessel being flown is destroyed (check vessel and events to see what is left).";

        /// <summary>One-line description of open popups for error messages; empty when there are none.</summary>
        public static string Summary()
        {
            var sb = new StringBuilder();
            if (KSP.UI.Dialogs.FlightResultsDialog.isDisplaying) sb.Append("Open dialog(s): \"Flight Results\" ").Append(FlightResultsNote).Append(' ');
            foreach (Popup p in Popups())
            {
                if (sb.Length == 0) sb.Append("Open dialog(s): ");
                sb.Append('"').Append(p.Title).Append("\" ");
                string msg = p.Message.Length > 240 ? p.Message.Substring(0, 240) + "…" : p.Message;
                sb.Append(msg).Append(' ');
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Finish the stock "Welcome to the Space Center / VAB / Tracking Station" tutorial of a new save so
        /// that it never blocks the screen. Returns true if there was one to finish.
        /// </summary>
        public static bool SkipIntro()
        {
            ScenarioNewGameIntro intro = UnityEngine.Object.FindObjectOfType<ScenarioNewGameIntro>();
            if (intro == null) return false;
            intro.kscComplete = intro.editorComplete = intro.tsComplete = true;
            var field = typeof(TutorialScenario).GetField("dialogDisplay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var dialog = field != null ? field.GetValue(intro) as PopupDialog : null;
            if (dialog != null)
            {
                foreach (Popup p in Popups())
                {
                    if (p.Dialog != dialog || p.Buttons.Count == 0) continue;
                    DialogGUIButton b = p.Buttons[0];
                    Button ui = b.uiItem != null ? b.uiItem.GetComponent<Button>() : null;
                    if (ui != null) ui.onClick.Invoke(); else b.OptionSelected();
                    break;
                }
            }
            if (HighLogic.CurrentGame != null) HighLogic.CurrentGame.RemoveProtoScenarioModule(typeof(ScenarioNewGameIntro));
            return true;
        }

        public static List<string> Titles()
        {
            var titles = new List<string>();
            foreach (Popup p in Popups()) titles.Add(p.Title.Length > 0 ? p.Title : "(untitled dialog)");
            return titles;
        }

        /// <summary>Type into the text boxes of an open popup, in the order they appear. Returns how many were filled.</summary>
        public static int FillText(string dialogTitle, List<string> values)
        {
            int filled = 0;
            foreach (PopupDialog pd in UnityEngine.Object.FindObjectsOfType<PopupDialog>())
            {
                if (pd == null || !pd.gameObject.activeInHierarchy || pd.dialogToDisplay == null) continue;
                if (!string.IsNullOrEmpty(dialogTitle) && Clean(pd.dialogToDisplay.title).IndexOf(dialogTitle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (TMPro.TMP_InputField box in pd.GetComponentsInChildren<TMPro.TMP_InputField>(false))
                {
                    if (filled >= values.Count) break;
                    if (values[filled] != null)
                    {
                        box.text = values[filled];
                        try { box.onEndEdit?.Invoke(values[filled]); } catch { }
                    }
                    filled++;
                }
            }
            return filled;
        }

        static object Click(Args a)
        {
            string want = a.Require("button").Trim();
            string dialog = a.Str("dialog");
            if (a.Has("text"))
            {
                var values = new List<string>();
                foreach (string v in a.Strings("text")) values.Add(v);
                FillText(dialog, values);
            }
            return Press(dialog, want);
        }

        /// <summary>Press the button with this label on an open popup (optionally only on the popup with this title).</summary>
        public static object Press(string dialog, string want)
        {
            foreach (Popup p in Popups())
            {
                if (!string.IsNullOrEmpty(dialog) && p.Title.IndexOf(dialog, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (bool exact in new[] { true, false })
                    foreach (DialogGUIButton b in p.Buttons)
                    {
                        string label = ButtonLabel(b);
                        bool match = exact ? label.Equals(want, StringComparison.OrdinalIgnoreCase) : label.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!match) continue;
                        Button ui = b.uiItem != null ? b.uiItem.GetComponent<Button>() : null;
                        if (ui != null && ui.interactable) ui.onClick.Invoke();
                        else
                        {
                            b.OptionSelected();
                            if (b.DismissOnSelect && p.Dialog != null) p.Dialog.Dismiss();
                        }
                        host.Note("pressed '" + label + "' on dialog '" + p.Title + "'");
                        return new JObj().Add("pressed", label).Add("dialog", p.Title);
                    }
            }
            throw new ApiException("No open dialog has a button matching '" + want + "'. " + (Summary().Length > 0 ? Summary() : "No dialogs are open."));
        }

        // ---------------------------------------------------------------- generic buttons

        static string LabelOf(Button b)
        {
            TMP_Text tmp = b.GetComponentInChildren<TMP_Text>(false);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return Clean(tmp.text);
            Text legacy = b.GetComponentInChildren<Text>(false);
            if (legacy != null && !string.IsNullOrEmpty(legacy.text)) return Clean(legacy.text);
            return "";
        }

        static List<KeyValuePair<string, Button>> VisibleButtons()
        {
            var list = new List<KeyValuePair<string, Button>>();
            foreach (Button b in UnityEngine.Object.FindObjectsOfType<Button>())
            {
                if (b == null || !b.isActiveAndEnabled || !b.interactable) continue;
                CanvasGroup[] groups = b.GetComponentsInParent<CanvasGroup>();
                bool hidden = false;
                foreach (CanvasGroup g in groups) if (g.alpha < 0.05f || !g.interactable) { hidden = true; break; }
                if (hidden) continue;
                string label = LabelOf(b);
                if (label.Length == 0 || label.Length > 60) continue;
                list.Add(new KeyValuePair<string, Button>(label, b));
            }
            return list;
        }

        static object UiButtons(Args a)
        {
            string search = a.Str("search");
            var seen = new HashSet<string>();
            var labels = new List<object>();
            foreach (KeyValuePair<string, Button> kv in VisibleButtons())
            {
                if (!string.IsNullOrEmpty(search) && kv.Key.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (seen.Add(kv.Key)) labels.Add(kv.Key);
                if (labels.Count >= 150) break;
            }
            return labels;
        }

        static object UiClick(Args a)
        {
            string want = a.Require("label").Trim();
            bool exactOnly = a.Bool("exact", false);
            List<KeyValuePair<string, Button>> buttons = VisibleButtons();
            foreach (bool exact in new[] { true, false })
            {
                if (!exact && exactOnly) break;
                foreach (KeyValuePair<string, Button> kv in buttons)
                {
                    bool match = exact ? kv.Key.Equals(want, StringComparison.OrdinalIgnoreCase) : kv.Key.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match) continue;
                    kv.Value.onClick.Invoke();
                    host.Note("clicked '" + kv.Key + "'");
                    return new JObj().Add("clicked", kv.Key);
                }
            }
            throw new ApiException("No visible button is labelled '" + want + "'. Use ui_buttons to list them.");
        }
    }
}
