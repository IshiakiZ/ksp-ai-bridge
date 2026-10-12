using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// The visible shape of a part, in the part's own coordinates, read straight from its prefab.
    /// Used to find where the side of a part is so other parts can be attached radially without
    /// needing the editor's physics raycasts.
    /// </summary>
    public sealed class PartGeometry
    {
        static readonly Dictionary<string, PartGeometry> cache = new Dictionary<string, PartGeometry>();

        Vector3[] verts = new Vector3[0];
        int[] tris = new int[0];
        public Bounds Bounds;
        public bool HasMesh => tris.Length >= 3;
        /// <summary>Every corner of the part's shape, in the part's own space (none where the part has no shape to read).</summary>
        public Vector3[] Corners => verts;

        public static PartGeometry For(AvailablePart ap)
        {
            if (cache.TryGetValue(ap.name, out PartGeometry g)) return g;
            g = Build(ap.partPrefab);
            cache[ap.name] = g;
            return g;
        }

        static PartGeometry Build(Part prefab)
        {
            var g = new PartGeometry();
            var allVerts = new List<Vector3>();
            var allTris = new List<int>();
            Transform root = prefab.transform;
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PartModule m in prefab.Modules)
                if (m is ModuleJettison mj && !string.IsNullOrEmpty(mj.jettisonName))
                    foreach (string n in mj.jettisonName.Split(',')) excluded.Add(n.Trim());

            bool boundsStarted = false;
            foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh == null || !ShownInPrefab(mf.transform, root, excluded)) continue;
                var renderer = mf.GetComponent<Renderer>();
                if (renderer != null && !renderer.enabled) continue;

                // Bounds are available even for meshes whose vertex data was released to the GPU.
                Bounds mb = mesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    Vector3 local = root.InverseTransformPoint(mf.transform.TransformPoint(c));
                    if (!boundsStarted) { g.Bounds = new Bounds(local, Vector3.zero); boundsStarted = true; }
                    else g.Bounds.Encapsulate(local);
                }

                if (!mesh.isReadable) continue;
                int offset = allVerts.Count;
                foreach (Vector3 v in mesh.vertices) allVerts.Add(root.InverseTransformPoint(mf.transform.TransformPoint(v)));
                foreach (int t in mesh.triangles) allTris.Add(offset + t);
            }
            if (!boundsStarted) g.Bounds = new Bounds(Vector3.zero, new Vector3(1.25f, 1.25f, 1.25f));
            g.verts = allVerts.ToArray();
            g.tris = allTris.ToArray();
            return g;
        }

        static readonly Dictionary<string, Vector3[]> outlines = new Dictionary<string, Vector3[]>();

        /// <summary>
        /// Every point of a part's outside, in the part's own space: the corners of its drawn shape and of its colliders, which are what
        /// the game's own fairing editor keeps a shell clear of (by 2 cm). With shrouds, also the shrouds an engine shows while something
        /// is attached under it, and drops when it fires.
        /// </summary>
        public static Vector3[] Outline(AvailablePart ap, bool shrouds, string variant = null)
        {
            Part prefab = ap.partPrefab;
            // (a look that switches parts of the model on and off: those switches, by the names of the objects)
            Dictionary<string, bool> switched = null;
            string look = null;
            ModulePartVariants looks = string.IsNullOrEmpty(variant) ? null : prefab.FindModuleImplementing<ModulePartVariants>();
            if (looks != null && looks.variantList != null)
            {
                string want = variant.Replace(" ", "").Replace("_", "").Replace("-", "");
                foreach (PartVariant v in looks.variantList)
                {
                    bool match = false;
                    foreach (string name in new[] { v.Name, v.themeName, v.DisplayName })
                        if (!string.IsNullOrEmpty(name) && name.Replace(" ", "").Replace("_", "").Replace("-", "").Equals(want, StringComparison.OrdinalIgnoreCase)) match = true;
                    if (!match || v.InfoGameObjects == null || v.InfoGameObjects.Count == 0) continue;
                    switched = new Dictionary<string, bool>();
                    foreach (PartGameObjectInfo info in v.InfoGameObjects) switched[info.Name] = info.Status;
                    look = v.Name;
                    break;
                }
            }
            string key = ap.name + (shrouds ? "|shrouds" : "") + (look != null ? "|" + look : "");
            if (outlines.TryGetValue(key, out Vector3[] found)) return found;
            Transform root = prefab.transform;
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!shrouds)
                foreach (PartModule m in prefab.Modules)
                    if (m is ModuleJettison mj && !string.IsNullOrEmpty(mj.jettisonName))
                        foreach (string n in mj.jettisonName.Split(',')) excluded.Add(n.Trim());
            bool Shown(Transform t)
            {
                for (; t != null && t != root; t = t.parent)
                {
                    if (excluded.Contains(t.name)) return false;
                    bool on = t.gameObject.activeSelf;
                    if (switched != null && switched.TryGetValue(t.name, out bool status)) on = status;
                    if (!on) return false;
                }
                return true;
            }
            var points = new List<Vector3>();
            void Add(Transform t, Vector3 local) => points.Add(root.InverseTransformPoint(t.TransformPoint(local)));
            void Corners(Transform t, Bounds b)
            {
                for (int corner = 0; corner < 8; corner++)
                    Add(t, b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)));
            }
            // (a ball, by 26 points on it: a box round it would be half as wide again at its corners)
            void Ball(Transform t, Vector3 centre, float radius)
            {
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                            if (x != 0 || y != 0 || z != 0) Add(t, centre + new Vector3(x, y, z).normalized * radius);
            }
            // (a part that folds out, an aerial or a solar panel, stands folded in the editor, its animation at its start; the model is read in
            // that pose and every transform put back as it was afterwards. The prefab's own pose is whatever the part was made in, which for
            // some is unfolded, and would make a shell far taller than the folded part needs.)
            var saved = new List<Pose>();
            try
            {
                foreach (PartModule m in prefab.Modules)
                {
                    if (!(m is ModuleDeployablePart folding) || string.IsNullOrEmpty(folding.animationName)) continue;
                    Animation[] animations = prefab.FindModelAnimators(folding.animationName);
                    if (animations == null) continue;
                    foreach (Animation animation in animations)
                    {
                        AnimationState state = animation != null ? animation[folding.animationName] : null;
                        if (state == null) continue;
                        if (saved.Count == 0)
                            foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true)) saved.Add(new Pose { T = t, Position = t.localPosition, Rotation = t.localRotation, Scale = t.localScale });
                        bool wasOn = state.enabled;
                        float weight = state.weight, time = state.normalizedTime;
                        try
                        {
                            state.enabled = true; state.weight = 1f; state.normalizedTime = 0f;
                            animation.Sample();
                        }
                        catch (Exception ex) { BridgeHost.LogWarn("could not fold " + ap.name + " for its outline: " + ex.Message); }
                        finally { state.enabled = wasOn; state.weight = weight; state.normalizedTime = time; }
                    }
                }
                Read();
            }
            finally
            {
                foreach (Pose s in saved) if (s.T != null) { s.T.localPosition = s.Position; s.T.localRotation = s.Rotation; s.T.localScale = s.Scale; }
            }
            if (points.Count == 0) Corners(root, For(ap).Bounds);
            found = points.ToArray();
            outlines[key] = found;
            return found;

            void Read()
            {
                foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = mf.sharedMesh;
                    if (mesh == null || !Shown(mf.transform)) continue;
                    var renderer = mf.GetComponent<Renderer>();
                    if (renderer != null && !renderer.enabled) continue;
                    if (mesh.isReadable) foreach (Vector3 v in mesh.vertices) Add(mf.transform, v);
                    else Corners(mf.transform, mesh.bounds);
                }
                foreach (Collider c in prefab.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null || c.isTrigger || !c.enabled || c.gameObject.layer == 21 || !Shown(c.transform)) continue;
                    switch (c)
                    {
                        case BoxCollider box: Corners(c.transform, new Bounds(box.center, box.size)); break;
                        case SphereCollider ball: Ball(c.transform, ball.center, ball.radius); break;
                        case CapsuleCollider cap:
                        {
                            Vector3 along = cap.direction == 0 ? Vector3.right : cap.direction == 2 ? Vector3.forward : Vector3.up;
                            float half = Mathf.Max(0f, cap.height * 0.5f - cap.radius);
                            Ball(c.transform, cap.center + along * half, cap.radius);
                            Ball(c.transform, cap.center - along * half, cap.radius);
                            break;
                        }
                        case MeshCollider mc when mc.sharedMesh != null:
                            if (mc.sharedMesh.isReadable) foreach (Vector3 v in mc.sharedMesh.vertices) Add(c.transform, v);
                            else Corners(c.transform, mc.sharedMesh.bounds);
                            break;
                    }
                }
            }
        }

        struct Pose
        {
            public Transform T;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
        }

#if DEV
        /// <summary>Development build only (dev_call): how tall and wide a part's outline is, and what its fold-out animations are.</summary>
        public static string DevOutline(string name)
        {
            AvailablePart ap = PartLoader.getPartInfoByName(name);
            if (ap == null) return "no part " + name;
            outlines.Clear();
            Vector3[] pts = Outline(ap, false);
            float lo = float.MaxValue, hi = float.MinValue, r = 0f;
            foreach (Vector3 p in pts) { lo = Mathf.Min(lo, p.y); hi = Mathf.Max(hi, p.y); r = Mathf.Max(r, new Vector2(p.x, p.z).magnitude); }
            var said = new System.Text.StringBuilder();
            said.Append(name + ": " + pts.Length + " points, up " + lo.ToString("F2") + " to " + hi.ToString("F2") + ", out to " + r.ToString("F2") + "; prefab active " + ap.partPrefab.gameObject.activeSelf);
            foreach (PartModule m in ap.partPrefab.Modules)
            {
                if (!(m is ModuleDeployablePart folding)) continue;
                Animation[] animations = ap.partPrefab.FindModelAnimators(folding.animationName);
                said.Append("; " + m.moduleName + " anim '" + folding.animationName + "': " + (animations == null ? "none" : animations.Length + " animators"));
                if (animations != null)
                    foreach (Animation a in animations)
                        said.Append(" [" + (a == null ? "null" : a.name + " active " + a.gameObject.activeInHierarchy + " clip " + (a[folding.animationName] != null ? a[folding.animationName].length.ToString("F2") + " s" : "missing")) + "]");
            }
            foreach (MeshFilter mf in ap.partPrefab.GetComponentsInChildren<MeshFilter>(true))
                said.Append("; mesh " + mf.name + (mf.sharedMesh != null && mf.sharedMesh.isReadable ? " readable" : " unreadable") + " y " + ap.partPrefab.transform.InverseTransformPoint(mf.transform.position).y.ToString("F2"));
            return said.ToString();
        }
#endif

        /// <summary>Prefabs are inactive at the root, so walk the chain and test each object's own flag instead.</summary>
        static bool ShownInPrefab(Transform t, Transform root, HashSet<string> excludedNames)
        {
            while (t != null && t != root)
            {
                if (!t.gameObject.activeSelf || excludedNames.Contains(t.name)) return false;
                t = t.parent;
            }
            return true;
        }

        /// <summary>
        /// Cast a ray (part-local) and return the first surface it meets. Falls back to a cylinder
        /// around the long axis when the mesh could not be read.
        /// </summary>
        public bool Raycast(Vector3 origin, Vector3 dir, out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.zero;
            dir.Normalize();
            float best = float.MaxValue;
            for (int n = 0; n + 2 < tris.Length; n += 3)
            {
                Vector3 a = verts[tris[n]], b = verts[tris[n + 1]], c = verts[tris[n + 2]];
                Vector3 e1 = b - a, e2 = c - a;
                Vector3 p = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, p);
                if (det > -1e-7f && det < 1e-7f) continue;
                float inv = 1f / det;
                Vector3 s = origin - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0f || u > 1f) continue;
                Vector3 q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(dir, q) * inv;
                if (v < 0f || u + v > 1f) continue;
                float t = Vector3.Dot(e2, q) * inv;
                if (t <= 1e-4f || t >= best) continue;
                best = t;
                normal = Vector3.Cross(e1, e2).normalized;
            }
            if (best < float.MaxValue)
            {
                point = origin + dir * best;
                if (Vector3.Dot(normal, dir) > 0f) normal = -normal;
                return true;
            }
            if (HasMesh) return false;

            // Cylinder fallback: only meaningful for rays aimed at the part's long (Y) axis.
            if (origin.y < Bounds.min.y || origin.y > Bounds.max.y) return false;
            float radius = Mathf.Max(Bounds.extents.x, Bounds.extents.z);
            var flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 1e-6f) return false;
            normal = -flat.normalized;
            point = new Vector3(Bounds.center.x, origin.y, Bounds.center.z) + normal * radius;
            return true;
        }
    }
}
