using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// A craft put together in front of the player. The builder writes a craft as a file and the editor loads
    /// it whole, so there was nothing to watch: the finished craft was simply there. This hides the craft the
    /// moment it is loaded and brings its parts out one after another in the order the AI gave them (each with
    /// its mirror or ring copies), every one growing into place with the editor's own click, the camera on
    /// what is being added and a line across the screen saying what it is.
    ///
    /// Nothing about the craft is changed by it: only whether each part is drawn, and for a moment how large.
    /// Whatever happens (the player takes hold of a part, the scene changes, something throws) every part ends
    /// up drawn at its own size.
    /// </summary>
    public static class BuildShow
    {
        sealed class Step
        {
            public string What;
            public int Order;
            public readonly List<Part> Parts = new List<Part>();
        }

        static readonly List<Renderer> hidden = new List<Renderer>();
        static readonly Dictionary<Transform, Vector3> sized = new Dictionary<Transform, Vector3>();
        static float touched;                                    // when this last did anything, in real seconds
        static bool playing;

        /// <summary>
        /// Called every frame: if parts have been left hidden with nothing going on (the build failed after loading
        /// the craft, or whatever was showing it stopped), they are drawn again. Nothing here may leave a craft invisible.
        /// </summary>
        public static void Watch()
        {
            if (hidden.Count == 0 && sized.Count == 0) return;
            if (Time.realtimeSinceStartup - touched > (playing ? 3f : 25f)) { playing = false; Restore(); }
        }

        /// <summary>How long putting a craft of so many steps together is shown for, in seconds, when the caller does not say.</summary>
        public static float UsualSeconds(int steps) => Mathf.Clamp(0.4f * steps, 1.5f, 14f);

        /// <summary>Stop drawing the craft, as soon as it has been loaded (before the player has seen it whole).</summary>
        public static void Hide(ShipConstruct ship)
        {
            if (ship == null) return;
            touched = Time.realtimeSinceStartup;
            foreach (Part p in ship.parts)
            {
                if (p == null) continue;
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled) { r.enabled = false; hidden.Add(r); }
            }
        }

        /// <summary>Draw everything again, at its own size.</summary>
        public static void Restore()
        {
            foreach (Renderer r in hidden) if (r != null) r.enabled = true;
            hidden.Clear();
            foreach (KeyValuePair<Transform, Vector3> kv in sized) if (kv.Key != null) kv.Key.localScale = kv.Value;
            sized.Clear();
        }

        static void Draw(Part p)
        {
            for (int n = hidden.Count - 1; n >= 0; n--)
            {
                Renderer r = hidden[n];
                if (r == null) { hidden.RemoveAt(n); continue; }
                if (r.transform.IsChildOf(p.transform) && OwnerOf(r.transform) == p) { r.enabled = true; hidden.RemoveAt(n); }
            }
        }

        /// <summary>(A part's children in the craft are children of its transform too: a renderer belongs to the nearest part above it.)</summary>
        static Part OwnerOf(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                Part p = t.GetComponent<Part>();
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>The part of a part that is its looks (the game keeps a part's meshes under a child called "model").</summary>
        static Transform Looks(Part p)
        {
            Transform model = p.transform.Find("model");
            return model != null ? model : null;
        }

        /// <summary>
        /// Bring the hidden craft out part by part, over about so many seconds. The steps are the builder's own, in the
        /// order the parts were given.
        /// </summary>
        public static IEnumerator Play(CraftBuilder builder, ShipConstruct ship, float seconds)
        {
            var steps = new List<Step>();
            try
            {
                var byId = new Dictionary<uint, Part>();
                foreach (Part p in ship.parts) if (p != null) byId[p.craftID] = p;
                foreach (KeyValuePair<string, List<CraftBuilder.Placed>> kv in builder.Groups)
                {
                    if (kv.Value.Count == 0) continue;
                    var step = new Step { What = kv.Value[0].Info.title + (kv.Value.Count > 1 ? "  x " + kv.Value.Count : ""), Order = kv.Value[0].Order };
                    foreach (CraftBuilder.Placed placed in kv.Value) if (byId.TryGetValue(placed.CraftId, out Part part)) { step.Parts.Add(part); byId.Remove(placed.CraftId); }
                    if (step.Parts.Count > 0) steps.Add(step);
                }
                steps.Sort((x, y) => x.Order.CompareTo(y.Order));
                // (anything the builder did not name, a strut's far end say, comes with the last step)
                if (byId.Count > 0 && steps.Count > 0) foreach (Part left in byId.Values) steps[steps.Count - 1].Parts.Add(left);
            }
            catch (Exception) { steps.Clear(); }
            if (steps.Count < 2) { Restore(); yield break; }
            playing = true; touched = Time.realtimeSinceStartup;

            float each = Mathf.Clamp(seconds / steps.Count, 0.07f, 0.7f), grow = Mathf.Min(0.22f, each * 0.8f);
            var line = new ScreenMessage("", each + 1.5f, ScreenMessageStyle.UPPER_CENTER);
            Bounds shown = default;
            bool any = false;
            float heading0 = float.NaN;
            EditorLogic editor = EditorLogic.fetch;
            for (int n = 0; n < steps.Count; n++)
            {
                // (the player has taken the craft away, or the editor has gone: stop, and leave everything drawn)
                if (HighLogic.LoadedScene != GameScenes.EDITOR || EditorLogic.fetch != editor || EditorLogic.fetch.ship != ship) break;
                Step step = steps[n];
                var looks = new List<Transform>();
                foreach (Part p in step.Parts)
                {
                    if (p == null) continue;
                    Draw(p);
                    Transform model = Looks(p);
                    if (model != null && !sized.ContainsKey(model)) { sized[model] = model.localScale; looks.Add(model); }
                    Bounds part = new Bounds(p.transform.position, Vector3.one * 0.6f);
                    foreach (Renderer r in p.GetComponentsInChildren<Renderer>(false)) if (r != null && r.enabled && OwnerOf(r.transform) == p && !(r is ParticleSystemRenderer)) part.Encapsulate(r.bounds);
                    if (!any) { shown = part; any = true; } else shown.Encapsulate(part);
                }
                try
                {
                    line.message = "<color=#8fd8ff>Building " + builder.Name + "</color>  <color=#a0a0a0>" + (n + 1) + "/" + steps.Count + "</color>  " + step.What;
                    ScreenMessages.PostScreenMessage(line);
                    if (EditorLogic.fetch.attachClip != null && Camera.main != null && each >= 0.12f)
                        AudioSource.PlayClipAtPoint(EditorLogic.fetch.attachClip, Camera.main.transform.position, GameSettings.UI_VOLUME * 0.7f);
                }
                catch (Exception) { }

                float began = Time.realtimeSinceStartup;
                while (true)
                {
                    float t = Time.realtimeSinceStartup - began;
                    touched = Time.realtimeSinceStartup;
                    // Each part grows into place, a little past its size and back.
                    float k = Mathf.Clamp01(t / grow), size = 1f + 0.18f * Mathf.Sin(k * Mathf.PI) - (1f - k) * (1f - k) * 0.65f;
                    foreach (Transform model in looks) if (model != null) model.localScale = sized[model] * size;
                    // The camera goes to where the craft has got to: on its middle, far enough off to see all of it, turning slowly.
                    try { Aim(shown, ref heading0, (n + Mathf.Clamp01(t / each)) / steps.Count); } catch (Exception) { }
                    if (t >= each) break;
                    yield return null;
                }
                foreach (Transform model in looks) if (model != null) { model.localScale = sized[model]; sized.Remove(model); }
            }
            playing = false;
            Restore();
            try { ScreenMessages.RemoveMessage(line); } catch (Exception) { }
        }

        static float atHeight = float.NaN, atDistance = float.NaN;

        static void Aim(Bounds shown, ref float heading0, float done)
        {
            float wantHeight = shown.center.y;
            float wantDistance = Mathf.Clamp(Mathf.Max(shown.size.y, Mathf.Max(shown.size.x, shown.size.z)) * 1.35f + 4f, 5f, 70f);
            if (float.IsNaN(atHeight) || done <= 0f) { atHeight = wantHeight; atDistance = wantDistance; }
            float ease = 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime);
            atHeight = Mathf.Lerp(atHeight, wantHeight, ease); atDistance = Mathf.Lerp(atDistance, wantDistance, ease);
            var focus = new Vector3(shown.center.x, atHeight, shown.center.z);
            VABCamera vab = UnityEngine.Object.FindObjectOfType<VABCamera>();
            if (vab != null && vab.isActiveAndEnabled)
            {
                if (float.IsNaN(heading0)) heading0 = vab.camHdg;
                vab.maxDistance = Mathf.Max(vab.maxDistance, atDistance + 1f);
                vab.camHdg = heading0 + done * 1.2f;                    // (about seventy degrees round, over the whole of it)
                vab.scrollHeight = atHeight;
                vab.PlaceCamera(focus, atDistance);
                return;
            }
            SPHCamera sph = UnityEngine.Object.FindObjectOfType<SPHCamera>();
            if (sph != null && sph.isActiveAndEnabled)
            {
                if (float.IsNaN(heading0)) heading0 = sph.camHdg;
                sph.maxDistance = Mathf.Max(sph.maxDistance, atDistance + 1f);
                sph.camHdg = heading0 + done * 1.2f;
                sph.scrollHeight = atHeight;
                sph.PlaceCamera(focus, atDistance);
            }
        }
    }
}
