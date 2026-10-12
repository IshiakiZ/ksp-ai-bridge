using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// Turns a declarative part list ("this engine under that tank, four boosters around it") into a
    /// stock .craft file. Positions are computed with the same maths the VAB uses when you click parts
    /// together, so the game's own loader accepts the result exactly like a hand-built ship.
    /// </summary>
    public sealed class CraftBuilder
    {
        public sealed class Placed
        {
            public string Id;
            public int Order;
            public AvailablePart Info;
            public Part Prefab => Info.partPrefab;
            public uint CraftId, PersistentId;
            public Vector3 Pos;
            public Quaternion Rot = Quaternion.identity;
            public Placed Parent;
            public readonly List<Placed> Children = new List<Placed>();
            public bool Surface;
            public Vector3 Outward = Vector3.right;
            public readonly Dictionary<string, Placed> Nodes = new Dictionary<string, Placed>();
            public List<Placed> Group;
            public bool Staged;
            public int Stage = -1, StageSlot = -1, SepIndex = -1;
            public Args Spec;
            /// <summary>For a mirrored copy, the part it is the mirror image of.</summary>
            public Placed MirrorOf;
            public bool MirrorSym;
            /// <summary>Model flip (the craft file's "mir") for the few parts that mirror by scaling.</summary>
            public Vector3 Mir = Vector3.one;
            /// <summary>World point where a surface-attached part touches its parent.</summary>
            public Vector3 AttachPoint;
            /// <summary>This part's own node that joins it to its parent, and the parent's node it sits on.</summary>
            public AttachNode UsedNode;
            public string ParentNodeId;
            /// <summary>Struts and fuel lines: the part at the far end and where the line meets it, in this part's frame.</summary>
            public Placed LinkTarget;
            public Vector3 LinkPos, LinkDir;
            public Quaternion LinkRot = Quaternion.identity;
            public string AutoStrut = "Off";
            public bool Rigid;
            public string Ref => Info.name + "_" + CraftId;
            public int Depth { get { int d = 0; for (Placed p = Parent; p != null; p = p.Parent) d++; return d; } }
        }

        public string Name, Description = "";
        public EditorFacility Facility = EditorFacility.VAB;
        public readonly List<Placed> Parts = new List<Placed>();
        public readonly List<string> Warnings = new List<string>();
        /// <summary>Things worth telling the AI about what was built that are not faults.</summary>
        public readonly List<string> Notes = new List<string>();
        /// <summary>Stages in firing order (first entry fires first), each a list of the parts it activates.</summary>
        public readonly List<List<Placed>> Firing = new List<List<Placed>>();

        readonly Dictionary<string, List<Placed>> groups = new Dictionary<string, List<Placed>>(StringComparer.OrdinalIgnoreCase);
        readonly System.Random rng = new System.Random();
        uint nextCraftId = 4290000000u;

        public IEnumerable<KeyValuePair<string, List<Placed>>> Groups => groups;

        public ConfigNode Build(Args spec)
        {
            Name = spec.Require("name").Trim();
            Description = spec.Str("description", "") ?? "";
            string facility = (spec.Str("facility", "VAB") ?? "VAB").ToUpperInvariant();
            if (facility != "VAB" && facility != "SPH") throw new ApiException("facility must be VAB or SPH");
            Facility = facility == "SPH" ? EditorFacility.SPH : EditorFacility.VAB;

            List<object> entries = spec.List("parts");
            if (entries == null || entries.Count == 0) throw new ApiException("'parts' must be a non-empty list; the first entry is the root part.");
            if (entries.Count > 400) throw new ApiException("Too many part entries (limit 400).");

            for (int n = 0; n < entries.Count; n++)
            {
                var e = new Args(entries[n]);
                string where = "parts[" + n + "]";
                string partName = e.Str("part");
                if (string.IsNullOrEmpty(partName)) throw new ApiException(where + " needs a 'part' name.");
                AvailablePart info = PartCatalog.Find(partName);
                if (!PartCatalog.IsUnlocked(info)) throw new ApiException(where + ": '" + info.title + "' is not researched in this save (needs " + info.TechRequired + ").");
                bool compound = info.partPrefab is CompoundPart;
                List<Placed> linkTargets = null;
                if (compound)
                {
                    string to = e.Str("to");
                    if (n == 0 || string.IsNullOrEmpty(to)) throw new ApiException(where + ": a " + info.title + " needs \"on\" (the part it is mounted on) and \"to\" (the part it connects to).");
                    if (!groups.TryGetValue(to, out linkTargets)) throw new ApiException(where + ": 'to' refers to unknown id '" + to + "'.");
                }
                string id = e.Str("id") ?? ("p" + n);
                if (groups.ContainsKey(id)) throw new ApiException(where + ": duplicate id '" + id + "'.");

                var group = new List<Placed>();
                if (n == 0)
                {
                    if (e.Has("on")) throw new ApiException("The first part is the root and cannot have 'on'.");
                    Placed root = NewPlaced(id, n, info, e);
                    root.Rot = Facility == EditorFacility.SPH ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
                    group.Add(root);
                }
                else
                {
                    string on = e.Str("on");
                    if (string.IsNullOrEmpty(on)) throw new ApiException(where + " ('" + id + "') needs 'on': the id of the part it attaches to.");
                    if (!groups.TryGetValue(on, out List<Placed> parents))
                        throw new ApiException(where + ": 'on' refers to unknown id '" + on + "'. Parts must be listed after the part they attach to. Known ids: " + string.Join(", ", new List<string>(groups.Keys).ToArray()));
                    Args radial = e.Obj("radial");
                    if (radial == null && (e.Bool("radial", false) || compound)) radial = new Args(null);
                    // Children of a mirrored part are mirror images of the children on its twin, not fresh placements.
                    var made = new Dictionary<Placed, List<Placed>>();
                    foreach (Placed parent in parents)
                    {
                        if (parent.MirrorOf != null && made.TryGetValue(parent.MirrorOf, out List<Placed> originals))
                        {
                            foreach (Placed o in originals) group.Add(MirrorCopy(o, parent, id, n, e));
                            continue;
                        }
                        int before = group.Count;
                        if (radial != null) PlaceRadial(parent, id, n, info, e, radial, group, where, compound ? linkTargets : null);
                        else group.Add(PlaceStack(parent, id, n, info, e, where));
                        made[parent] = group.GetRange(before, group.Count - before);
                    }
                }
                string autostrut = (e.Str("autostrut") ?? spec.Str("autostrut") ?? "off").ToLowerInvariant();
                foreach (Placed p in group)
                {
                    p.Group = group;
                    p.Rigid = e.Bool("rigid", false);
                    switch (autostrut)
                    {
                        case "off": case "none": case "false": break;
                        case "root": p.AutoStrut = "Root"; break;
                        case "heaviest": case "true": p.AutoStrut = "Heaviest"; break;
                        case "grandparent": p.AutoStrut = "Grandparent"; break;
                        default: throw new ApiException(where + ": autostrut must be off, root, heaviest or grandparent.");
                    }
                    if (n == 0) p.AutoStrut = "Off";
                    Parts.Add(p);
                }
                groups[id] = group;
                if (compound) foreach (Placed p in group) LinkCompound(p, linkTargets, e, where);
            }

            LiftOffFloor();
            PlanFairings();
            PlanStaging(spec);
            return Emit();
        }

        Placed NewPlaced(string id, int order, AvailablePart info, Args spec)
        {
            nextCraftId -= (uint)(1000 + rng.Next(0, 8000));
            return new Placed { Id = id, Order = order, Info = info, Spec = spec, CraftId = nextCraftId, PersistentId = (uint)rng.Next(1000000, int.MaxValue) };
        }

        // ---------------------------------------------------------------- stack attachment

        static AttachNode FindNode(Part prefab, string id)
        {
            foreach (AttachNode n in prefab.attachNodes)
                if (string.Equals(n.id, id, StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        /// <summary>
        /// A node that is never taken unasked: the ones up the inside of a fairing base, which are for hanging a second
        /// payload within the shell. (Taken by their place in the list, a fairing base used to be hung from the first of
        /// them, half a metre out of place.) Named outright with "node" or "my_node", they can still be used.
        /// </summary>
        static bool Inner(AttachNode n) => n.id != null && n.id.StartsWith("interstage", StringComparison.OrdinalIgnoreCase);

        static string NodeList(Placed p)
        {
            var names = new List<string>();
            foreach (AttachNode n in p.Prefab.attachNodes) names.Add(n.id + (p.Nodes.ContainsKey(n.id) ? " (in use)" : ""));
            return names.Count == 0 ? "none" : string.Join(", ", names.ToArray());
        }

        Placed PlaceStack(Placed parent, string id, int order, AvailablePart info, Args e, string where)
        {
            Part pp = parent.Prefab, cp = info.partPrefab;
            if (!cp.attachRules.stack) throw new ApiException(where + ": '" + info.title + "' cannot be stack-attached; attach it to the side with \"radial\": {}.");
            if (!pp.attachRules.allowStack) throw new ApiException(where + ": '" + parent.Info.title + "' does not accept stacked parts.");

            AttachNode pn;
            string wanted = e.Str("node");
            if (!string.IsNullOrEmpty(wanted))
            {
                pn = FindNode(pp, wanted);
                if (pn == null) throw new ApiException(where + ": '" + parent.Info.title + "' has no node '" + wanted + "'. Its stack nodes: " + NodeList(parent));
                if (parent.Nodes.ContainsKey(pn.id)) throw new ApiException(where + ": node '" + pn.id + "' of '" + parent.Id + "' is already used by '" + parent.Nodes[pn.id].Id + "'. Its stack nodes: " + NodeList(parent));
            }
            else
            {
                pn = null;
                AttachNode bottom = FindNode(pp, "bottom");
                if (bottom != null && !parent.Nodes.ContainsKey(bottom.id)) pn = bottom;
                if (pn == null)
                    foreach (AttachNode n in pp.attachNodes)
                        if (!parent.Nodes.ContainsKey(n.id) && !Inner(n) && (pn == null || n.orientation.y < pn.orientation.y)) pn = n;
                if (pn == null) throw new ApiException(where + ": '" + parent.Id + "' (" + parent.Info.title + ") has no free stack node. Its stack nodes: " + NodeList(parent) + ". Attach to the side with \"radial\": {} instead?");
            }

            Vector3 pnPosition = Vector3.Scale(pn.position + pn.offset, parent.Mir);
            Vector3 parentDir = (parent.Rot * Vector3.Scale(pn.orientation, parent.Mir)).normalized;
            AttachNode cn;
            string mine = e.Str("my_node");
            if (!string.IsNullOrEmpty(mine))
            {
                cn = FindNode(cp, mine);
                if (cn == null) throw new ApiException(where + ": '" + info.title + "' has no node '" + mine + "'.");
            }
            else
            {
                cn = null;
                float best = float.MaxValue;
                foreach (AttachNode n in cp.attachNodes)
                {
                    if (Inner(n)) continue;
                    float d = Vector3.Dot((parent.Rot * n.orientation).normalized, parentDir);
                    if (d < best) { best = d; cn = n; }
                }
                if (cn == null) throw new ApiException(where + ": '" + info.title + "' has no stack nodes; attach it to the side with \"radial\": {}.");
            }

            Placed c = NewPlaced(id, order, info, e);
            Quaternion baseRot = parent.Rot;
            Vector3 childDir = (baseRot * cn.orientation).normalized;
            Quaternion align;
            if (Vector3.Dot(childDir, -parentDir) < -0.999f)
            {
                Vector3 axis = baseRot * Vector3.right;
                if (Mathf.Abs(Vector3.Dot(axis, childDir)) > 0.9f) axis = baseRot * Vector3.forward;
                align = Quaternion.AngleAxis(180f, axis);
            }
            else align = Quaternion.FromToRotation(childDir, -parentDir);
            c.Rot = align * baseRot;
            float spin = (float)e.Num("rotate", 0);
            if (spin != 0f) c.Rot = Quaternion.AngleAxis(spin, parentDir) * c.Rot;
            c.Pos = parent.Pos + parent.Rot * pnPosition - c.Rot * cn.position;
            c.UsedNode = cn;
            c.ParentNodeId = pn.id;
            c.Mir = MirrorScale(c);

            // (what stands on a fairing base, or hangs on the interstage nodes up its inside, is inside the shell, whatever its width)
            bool shelled = PartCatalog.IsFairing(cp) && (cn.id == "top" || Inner(cn)) || PartCatalog.IsFairing(pp) && (pn.id == "top" || Inner(pn));
            if (pn.size != cn.size && !shelled)
                Warnings.Add("'" + id + "' (" + info.title + ", node size " + cn.size + ") is stacked on '" + parent.Id + "' node '" + pn.id + "' (size " + pn.size + "): diameters differ.");
            Link(parent, c);
            parent.Nodes[pn.id] = c;
            c.Nodes[cn.id] = parent;
            return c;
        }

        static void Link(Placed parent, Placed child)
        {
            child.Parent = parent;
            parent.Children.Add(child);
        }

        // ---------------------------------------------------------------- radial (surface) attachment

        static readonly Dictionary<string, Vector3> worldSides = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase)
        {
            { "right", Vector3.right }, { "left", Vector3.left }, { "top", Vector3.up }, { "up", Vector3.up }, { "bottom", Vector3.down }, { "down", Vector3.down },
            { "front", Vector3.forward }, { "forward", Vector3.forward }, { "back", Vector3.back }, { "aft", Vector3.back }
        };

        void PlaceRadial(Placed parent, string id, int order, AvailablePart info, Args e, Args radial, List<Placed> group, string where, List<Placed> linkTargets = null)
        {
            Part pp = parent.Prefab, cp = info.partPrefab;
            if (!cp.attachRules.srfAttach || cp.srfAttachNode == null)
                throw new ApiException(where + ": '" + info.title + "' cannot be attached to the side of another part. Mount it on a radial decoupler or radial attachment point, or stack it.");
            if (!pp.attachRules.allowSrfAttach)
                throw new ApiException(where + ": '" + parent.Info.title + "' does not accept parts on its surface.");

            int count = Math.Max(1, radial.Int("count", 1));
            if (count > 16) throw new ApiException(where + ": radial count is limited to 16.");
            bool mirror = radial.Bool("mirror", false) || e.Bool("mirror", false);
            if (mirror && count > 1) throw new ApiException(where + ": use either \"mirror\": true (a left/right pair) or \"count\" (copies around the parent), not both.");
            if (mirror && parent.Group != null && parent.Group.Count > 1)
                throw new ApiException(where + ": '" + parent.Id + "' already exists as symmetric copies, so whatever is attached to it is copied automatically; remove \"mirror\".");
            float height = (float)radial.Num("height", 0);

            // Which side of the parent. A direction that runs along the parent's own length is an end or
            // edge mount: the trailing edge of a wing, the tip of a beam.
            Vector3 dirLocal;
            bool endMount = false;
            string side = radial.Str("side");
            if (radial.Has("angle"))
            {
                float a = (float)radial.Num("angle") * Mathf.Deg2Rad;
                dirLocal = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            }
            else if (!string.IsNullOrEmpty(side))
            {
                if (!worldSides.TryGetValue(side, out Vector3 world)) throw new ApiException(where + ": side must be right, left, top, bottom, front or back.");
                dirLocal = Quaternion.Inverse(parent.Rot) * world;
                if (Mathf.Abs(dirLocal.y) > 0.75f)
                {
                    endMount = true;
                    dirLocal = new Vector3(0f, Mathf.Sign(dirLocal.y), 0f);
                }
                else
                {
                    dirLocal.y = 0f;
                    dirLocal.Normalize();
                }
            }
            else if (linkTargets != null && linkTargets.Count > 0)
            {
                // A strut or fuel line starts on the side of its parent that faces what it connects to.
                Placed nearest = Nearest(linkTargets, parent.Pos);
                dirLocal = Quaternion.Inverse(parent.Rot) * (nearest.Pos - parent.Pos);
                dirLocal.y = 0f;
                dirLocal = dirLocal.sqrMagnitude > 1e-4f ? dirLocal.normalized : Vector3.right;
            }
            else if (parent.Surface)
            {
                // Keep going "outward" from whatever the parent is itself mounted on (booster on a radial decoupler).
                dirLocal = Quaternion.Inverse(parent.Rot) * parent.Outward;
                dirLocal.y = 0f;
                dirLocal = dirLocal.sqrMagnitude > 1e-4f ? dirLocal.normalized : Vector3.right;
            }
            else dirLocal = Vector3.right;

            // Where on that side: "height" runs along the parent's own length, while right/up/forward are
            // metres in the craft's axes measured from the parent's origin.
            PartGeometry geo = PartGeometry.For(parent.Info);
            Bounds pb = geo.Bounds;
            Vector3 slide = Quaternion.Inverse(parent.Rot) * new Vector3((float)radial.Num("right", 0), (float)radial.Num("up", 0), (float)radial.Num("forward", 0));
            slide.y += height;
            slide -= dirLocal * Vector3.Dot(slide, dirLocal);
            bool thinX = pb.size.x < 0.4f && pb.size.x < pb.size.z, thinZ = pb.size.z < 0.4f && pb.size.z <= pb.size.x;
            Vector3 thinLocal = thinX ? Vector3.right : Vector3.forward;
            if (endMount)
            {
                // Seen edge-on, a wing or plate is easy to miss: stay inside its thickness.
                if (thinX) slide.x = pb.center.x;
                if (thinZ) slide.z = pb.center.z;
            }
            Vector3 hitLocal = Vector3.zero, normalLocal = Vector3.zero;
            bool found = false;
            foreach (float wobble in new[] { 0f, 0.02f, -0.02f, 0.05f, -0.05f })
            {
                Vector3 from = slide + (endMount ? thinLocal * wobble : Vector3.zero) + dirLocal * 60f;
                if (geo.Raycast(from, -dirLocal, out hitLocal, out normalLocal)) { found = true; break; }
                if (!endMount) break;
            }
            if (!found)
            {
                Vector3 lo = parent.Rot * pb.min, hi = parent.Rot * pb.max;
                throw new ApiException(where + ": that spot misses the " + (string.IsNullOrEmpty(side) ? "side" : side + " side") + " of '" + parent.Id + "' (" + parent.Info.title + "). Measured from its origin the part spans right " +
                                       Math.Round(Mathf.Min(lo.x, hi.x), 2) + " to " + Math.Round(Mathf.Max(lo.x, hi.x), 2) + " m, up " + Math.Round(Mathf.Min(lo.y, hi.y), 2) + " to " + Math.Round(Mathf.Max(lo.y, hi.y), 2) +
                                       " m, forward " + Math.Round(Mathf.Min(lo.z, hi.z), 2) + " to " + Math.Round(Mathf.Max(lo.z, hi.z), 2) + " m; along its own length (height) " + Math.Round(pb.min.y, 2) + " to " + Math.Round(pb.max.y, 2) + " m.");
            }

            Vector3 axis = (parent.Rot * Vector3.up).normalized;
            Vector3 radialWorld = (parent.Rot * dirLocal).normalized;
            Vector3 normal;
            if (endMount) normal = radialWorld;
            else
            {
                normal = (parent.Rot * normalLocal).normalized;
                // Same correction the VAB's angle snap makes: keep the surface's tilt, but point exactly outward.
                float axial = Vector3.Dot(normal, axis);
                float lateral = Mathf.Sqrt(Mathf.Max(0f, 1f - axial * axial));
                normal = (radialWorld * lateral + axis * axial).normalized;
                if (lateral < 0.05f) normal = radialWorld;
            }

            Vector3 hitWorld = parent.Pos + parent.Rot * hitLocal;
            // The stock formula turns the child about the mount normal until its "up" follows the parent's
            // length; on an end mount that is degenerate, so the parent's flat face is used instead.
            Vector3 upRef = endMount ? (parent.Rot * thinLocal).normalized : axis;
            bool wheel = IsWheel(cp);
            string orient = radial.Str("orient");
            bool swung = !string.IsNullOrEmpty(orient) && !orient.Equals("out", StringComparison.OrdinalIgnoreCase);
            Quaternion rot0;
            if (wheel && !swung)
            {
                // Wheels and landing gear always stand upright and roll fore-and-aft, with their mount facing the parent.
                Vector3 nodeDir = cp.srfAttachNode.orientation.normalized;
                Quaternion forward = Quaternion.identity, reversed = Quaternion.AngleAxis(180f, Vector3.up);
                rot0 = Vector3.Dot(reversed * nodeDir, -normal) > Vector3.Dot(forward * nodeDir, -normal) + 1e-3f ? reversed : forward;
            }
            else if (endMount && cp.FindModuleImplementing<ModuleControlSurface>() != null)
            {
                // An elevon on a wing's edge lies in the plane of the wing with its hinge along the edge.
                rot0 = Quaternion.LookRotation(upRef, -normal);
            }
            else rot0 = Quaternion.LookRotation(normal, upRef) * Quaternion.LookRotation(cp.srfAttachNode.orientation, Vector3.up);

            float spin = (float)e.Num("rotate", 0);
            if (spin != 0f) rot0 = Quaternion.AngleAxis(spin, wheel && !swung ? Vector3.up : normal) * rot0;
            Vector3 pos0 = hitWorld - rot0 * cp.srfAttachNode.position;
            if (swung)
            {
                // Swing the part about its attachment point so that it points the requested way.
                if (!worldSides.TryGetValue(orient, out Vector3 wanted)) throw new ApiException(where + ": orient must be out, down, up, left, right, front or back.");
                Quaternion swing = Quaternion.FromToRotation(normal, wanted);
                rot0 = swing * rot0;
                pos0 = hitWorld + swing * (pos0 - hitWorld);
            }
            float incidence = (float)radial.Num("incidence", 0), dihedral = (float)radial.Num("dihedral", 0);
            if ((incidence != 0f || dihedral != 0f) && !wheel && !endMount)
            {
                // Wing angles, positive meaning leading edge up and tip up whichever side the wing is on.
                Quaternion tilt = Quaternion.identity;
                if (incidence != 0f) tilt = Quaternion.AngleAxis(incidence * (Vector3.Dot(Vector3.Cross(normal, axis), Vector3.up) >= 0f ? 1f : -1f), normal);
                if (dihedral != 0f) tilt = Quaternion.AngleAxis(dihedral * (Vector3.Dot(Vector3.Cross(axis, normal), Vector3.up) >= 0f ? 1f : -1f), axis) * tilt;
                rot0 = tilt * rot0;
                pos0 = hitWorld + tilt * (pos0 - hitWorld);
            }
            Vector3 nudge = axis * (float)radial.Num("shift", 0) + radialWorld * (float)radial.Num("outset", 0);
            pos0 += nudge;
            hitWorld += nudge;

            for (int n = 0; n < count; n++)
            {
                Quaternion q = Quaternion.AngleAxis(n * 360f / count, axis);
                Placed c = NewPlaced(id, order, info, e);
                c.Surface = true;
                c.Rot = q * rot0;
                c.Pos = parent.Pos + q * (pos0 - parent.Pos);
                c.Outward = q * radialWorld;
                c.AttachPoint = parent.Pos + q * (hitWorld - parent.Pos);
                c.UsedNode = cp.srfAttachNode;
                c.Mir = MirrorScale(c);
                Link(parent, c);
                group.Add(c);
                if (mirror) group.Add(MirrorCopy(c, parent, id, order, e));
            }
        }

        static bool IsWheel(Part prefab)
        {
            ModuleWheelBase wheel = prefab.FindModuleImplementing<ModuleWheelBase>();
            return wheel != null && wheel.wheelType != WheelType.LEG;
        }

        static Placed Nearest(List<Placed> candidates, Vector3 point)
        {
            Placed best = candidates[0];
            foreach (Placed p in candidates) if ((p.Pos - point).sqrMagnitude < (best.Pos - point).sqrMagnitude) best = p;
            return best;
        }

        // ---------------------------------------------------------------- mirror symmetry

        /// <summary>
        /// The left-hand twin of a part, placed the way the SPH's mirror symmetry does it: reflected across the
        /// craft's centre plane, with the half-turn the editor gives parts that attach by their side.
        /// </summary>
        Placed MirrorCopy(Placed original, Placed newParent, string id, int order, Args e)
        {
            Placed root = Parts.Count > 0 ? Parts[0] : original;
            Placed anchor = newParent;
            while (anchor.Parent != null && anchor.Group != null && anchor.Group.Count > 1) anchor = anchor.Parent;
            Vector3 planeNormal = (root.Rot * Vector3.right).normalized;
            Vector3 Reflect(Vector3 v) => v - 2f * Vector3.Dot(v, planeNormal) * planeNormal;

            Placed c = NewPlaced(id, order, original.Info, e);
            c.Pos = anchor.Pos + Reflect(original.Pos - anchor.Pos);
            Vector3 up = Reflect(original.Rot * Vector3.up), forward = Reflect(original.Rot * Vector3.forward);
            Quaternion rot = Quaternion.LookRotation(forward, up);
            AttachNode node = original.UsedNode;
            bool handled = false;
            ModuleMirrorPlacement special = original.Prefab.FindModuleImplementing<ModuleMirrorPlacement>();
            if (special != null && (special.ignoreMirrorIfSurfaceXOffset || node == null || !original.Surface || node.orientation.x == 0f))
            {
                if (special.applyMirrorRotationXAxis) rot *= Quaternion.AngleAxis(special.mirrorRotationXAxis, Vector3.right);
                if (special.applyMirrorRotationYAxis) rot *= Quaternion.AngleAxis(special.mirrorRotationYAxis, Vector3.up);
                if (special.applyMirrorRotationZAxis) rot *= Quaternion.AngleAxis(special.mirrorRotationZAxis, Vector3.forward);
                handled = true;
            }
            if (node != null && !handled && node.orientation.x != 0f) rot = Quaternion.AngleAxis(180f, up) * rot;
            c.Rot = rot;
            c.Surface = original.Surface;
            c.Outward = Reflect(original.Outward);
            c.AttachPoint = anchor.Pos + Reflect(original.AttachPoint - anchor.Pos);
            c.UsedNode = original.UsedNode;
            c.ParentNodeId = original.ParentNodeId;
            c.MirrorOf = original;
            c.MirrorSym = original.MirrorSym = true;
            c.Mir = MirrorScale(c);
            Link(newParent, c);
            if (!original.Surface && original.UsedNode != null && original.ParentNodeId != null)
            {
                newParent.Nodes[original.ParentNodeId] = c;
                c.Nodes[original.UsedNode.id] = newParent;
            }
            return c;
        }

        /// <summary>A handful of parts (Mk2 fuselages, fixed gear) mirror by flipping their model rather than turning.</summary>
        Vector3 MirrorScale(Placed p)
        {
            Vector3 axis = p.Prefab.mirrorRefAxis;
            if (axis == Vector3.zero || Parts.Count == 0) return Vector3.one;
            Quaternion vessel = Parts[0].Rot;
            if (Vector3.Dot(p.Rot * axis, vessel * axis) >= 0f) return Vector3.one;
            return new Vector3(axis.x != 0f ? -1f : 1f, axis.y != 0f ? -1f : 1f, axis.z != 0f ? -1f : 1f);
        }

        // ---------------------------------------------------------------- struts and fuel lines

        void LinkCompound(Placed c, List<Placed> targets, Args e, string where)
        {
            Placed target = Nearest(targets, c.Pos);
            if (target == c.Parent) throw new ApiException(where + ": a " + c.Info.title + " cannot connect '" + c.Parent.Id + "' to itself; 'on' and 'to' must be different parts.");
            Vector3 targetAxis = (target.Rot * Vector3.up).normalized;
            PartGeometry geo = PartGeometry.For(target.Info);
            float along = e.Has("to_height") ? (float)e.Num("to_height") : Vector3.Dot(c.Pos - target.Pos, targetAxis);
            along = Mathf.Clamp(along, geo.Bounds.min.y + 0.05f, geo.Bounds.max.y - 0.05f);
            Vector3 aim = target.Pos + targetAxis * along;
            Vector3 direction = aim - c.Pos;
            if (direction.sqrMagnitude < 1e-4f) throw new ApiException(where + ": the two ends of the " + c.Info.title + " coincide.");
            direction.Normalize();

            Quaternion toLocal = Quaternion.Inverse(target.Rot);
            Vector3 hit = aim, hitNormal = -direction;
            if (geo.Raycast(toLocal * (c.Pos - target.Pos), toLocal * direction, out Vector3 hitLocal, out Vector3 normalLocal))
            {
                hit = target.Pos + target.Rot * hitLocal;
                hitNormal = (target.Rot * normalLocal).normalized;
            }
            float length = (hit - c.Pos).magnitude;
            float reach = c.Prefab is CompoundPart cpart ? cpart.maxLength : 10f;
            if (length > reach) throw new ApiException(where + ": '" + c.Parent.Id + "' and '" + target.Id + "' are " + Math.Round(length, 1) + " m apart where this " + c.Info.title + " would run; the limit is " + reach + " m. Choose a closer pair or a different height.");

            Quaternion mine = Quaternion.Inverse(c.Rot);
            c.LinkTarget = target;
            c.LinkPos = mine * (hit - c.Pos);
            c.LinkDir = c.LinkPos.normalized;
            c.LinkRot = Quaternion.FromToRotation(Vector3.right, mine * hitNormal);
        }

        // ---------------------------------------------------------------- layout

        void LiftOffFloor()
        {
            float min = float.MaxValue;
            foreach (Placed p in Parts)
            {
                Bounds b = PartGeometry.For(p.Info).Bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    min = Mathf.Min(min, (p.Pos + p.Rot * c).y);
                }
            }
            float clearance = Facility == EditorFacility.SPH ? 4f : 2f;
            var shift = new Vector3(0f, clearance - min, 0f);
            foreach (Placed p in Parts) p.Pos += shift;
        }

        // ---------------------------------------------------------------- staging

        static bool Stageable(Placed p)
        {
            Part pr = p.Prefab;
            return pr.hasStagingIcon || PartCatalog.IsEngine(pr) || PartCatalog.IsDecoupler(pr) || PartCatalog.IsParachute(pr) || PartCatalog.IsClamp(pr) || PartCatalog.IsFairing(pr);
        }

        static bool IsRadialDecoupler(Placed p) => PartCatalog.IsDecoupler(p.Prefab) && (p.Surface || p.Prefab.FindModuleImplementing<ModuleAnchoredDecoupler>() != null);
        static bool IsStackDecoupler(Placed p) => PartCatalog.IsDecoupler(p.Prefab) && !IsRadialDecoupler(p);

        static int StackDecouplersAbove(Placed p, bool inclusive)
        {
            int count = 0;
            for (Placed q = inclusive ? p : p.Parent; q != null; q = q.Parent) if (IsStackDecoupler(q)) count++;
            return count;
        }

        static Placed GoverningDecoupler(Placed p)
        {
            for (Placed q = p; q != null; q = q.Parent) if (PartCatalog.IsDecoupler(q.Prefab)) return q;
            return null;
        }

        /// <summary>The decoupler sitting under an engine's nozzle, i.e. the one that must fire before it can burn.</summary>
        static Placed BlockingDecoupler(Placed engine)
        {
            Placed below = null;
            AttachNode lowest = null;
            foreach (AttachNode n in engine.Prefab.attachNodes)
                if (lowest == null || n.orientation.y < lowest.orientation.y) lowest = n;
            if (lowest == null || lowest.orientation.y > -0.5f || !engine.Nodes.TryGetValue(lowest.id, out below)) return null;
            if (below == engine.Parent) return GoverningDecoupler(below);
            var queue = new Queue<Placed>();
            queue.Enqueue(below);
            while (queue.Count > 0)
            {
                Placed q = queue.Dequeue();
                if (PartCatalog.IsDecoupler(q.Prefab)) return q;
                foreach (Placed child in q.Children) queue.Enqueue(child);
            }
            return null;
        }

        void PlanStaging(Args spec)
        {
            List<object> explicitStages = spec.List("stages");
            var assigned = new HashSet<Placed>();
            if (explicitStages != null && explicitStages.Count > 0)
            {
                foreach (object stageObj in explicitStages)
                {
                    var stage = new List<Placed>();
                    var ids = stageObj as List<object> ?? new List<object> { stageObj };
                    foreach (object idObj in ids)
                    {
                        string id = Convert.ToString(idObj);
                        if (!groups.TryGetValue(id, out List<Placed> g)) throw new ApiException("stages: unknown part id '" + id + "'.");
                        foreach (Placed p in g)
                        {
                            if (!Stageable(p)) throw new ApiException("stages: '" + id + "' (" + p.Info.title + ") is not a stageable part (engines, decouplers, parachutes, launch clamps and fairings are).");
                            if (!assigned.Add(p)) throw new ApiException("stages: '" + id + "' is listed twice.");
                            stage.Add(p);
                        }
                    }
                    if (stage.Count > 0) Firing.Add(stage);
                }
                var leftovers = new List<Placed>();
                foreach (Placed p in Parts) if (Stageable(p) && !assigned.Contains(p)) leftovers.Add(p);
                if (leftovers.Count > 0)
                {
                    var names = new HashSet<string>();
                    foreach (Placed p in leftovers) names.Add(p.Id);
                    Warnings.Add("Stageable parts missing from 'stages' were put in an extra final stage: " + string.Join(", ", new List<string>(names).ToArray()));
                    Firing.Add(leftovers);
                }
            }
            else AutoStage();

            int total = Firing.Count;
            for (int n = 0; n < total; n++)
            {
                int slot = 0;
                foreach (Placed p in Firing[n]) { p.Staged = true; p.Stage = total - 1 - n; p.StageSlot = slot++; }
            }
            SpreadStageInfo(Parts[0], -1, -1);
        }

        void AutoStage()
        {
            var done = new HashSet<Placed>();
            var blockers = new Dictionary<Placed, List<Placed>>();
            var liftoff = new List<Placed>();
            var parachutes = new List<Placed>();
            var fairings = new List<Placed>();
            var decouplerGroups = new List<List<Placed>>();
            var seenGroups = new HashSet<List<Placed>>();
            // (an engine inside an interstage lights when the stage under it is thrown off, whatever is or is not under its own bell)
            var interstage = new Dictionary<Placed, Placed>();
            foreach (KeyValuePair<Placed, FairingPlan> kv in fairingPlans)
                if (kv.Value.Closing != null && kv.Value.Separator != null)
                    foreach (Placed q in kv.Value.Inside) if (PartCatalog.IsEngine(q.Prefab)) interstage[q] = kv.Value.Separator;

            foreach (Placed p in Parts)
            {
                Part pr = p.Prefab;
                if (PartCatalog.IsParachute(pr)) { parachutes.Add(p); continue; }
                if (PartCatalog.IsFairing(pr)) { fairings.Add(p); continue; }
                if (PartCatalog.IsClamp(pr)) { liftoff.Add(p); continue; }
                if (PartCatalog.IsDecoupler(pr))
                {
                    if (seenGroups.Add(p.Group)) decouplerGroups.Add(p.Group);
                    continue;
                }
                if (PartCatalog.IsEngine(pr))
                {
                    Placed trigger = interstage.TryGetValue(p, out Placed separator) ? separator : PartCatalog.SolidBurnSeconds(pr) < 8.0 ? GoverningDecoupler(p) : BlockingDecoupler(p);
                    if (trigger == null) liftoff.Add(p);
                    else
                    {
                        if (!blockers.TryGetValue(trigger, out List<Placed> list)) blockers[trigger] = list = new List<Placed>();
                        list.Add(p);
                    }
                }
            }

            decouplerGroups.Sort((a, b) =>
            {
                Placed x = a[0], y = b[0];
                bool xr = IsRadialDecoupler(x), yr = IsRadialDecoupler(y);
                int dx = StackDecouplersAbove(x, !xr), dy = StackDecouplersAbove(y, !yr);
                if (dx != dy) return dy.CompareTo(dx);
                if (xr != yr) return xr ? -1 : 1;
                if (x.Depth != y.Depth) return y.Depth.CompareTo(x.Depth);
                return y.Order.CompareTo(x.Order);
            });

            if (liftoff.Count > 0) Firing.Add(liftoff);
            // (an interstage is thrown off with the separation it covers; any other fairing with the first stack separation)
            var withSeparation = new Dictionary<Placed, List<Placed>>();
            var others = new List<Placed>();
            foreach (Placed f in fairings)
            {
                if (fairingPlans.TryGetValue(f, out FairingPlan plan) && plan.Closing != null && plan.Separator != null)
                {
                    if (!withSeparation.TryGetValue(plan.Separator, out List<Placed> shells)) withSeparation[plan.Separator] = shells = new List<Placed>();
                    shells.Add(f);
                }
                else others.Add(f);
            }
            bool fairingsPlaced = others.Count == 0;
            foreach (List<Placed> g in decouplerGroups)
            {
                var stage = new List<Placed>(g);
                foreach (Placed d in g)
                {
                    if (blockers.TryGetValue(d, out List<Placed> waiting)) stage.AddRange(waiting);
                    if (withSeparation.TryGetValue(d, out List<Placed> shells)) { stage.AddRange(shells); withSeparation.Remove(d); }
                }
                if (!fairingsPlaced && IsStackDecoupler(g[0])) { stage.AddRange(others); fairingsPlaced = true; }
                Firing.Add(stage);
            }
            if (!fairingsPlaced) Firing.Add(others);
            foreach (List<Placed> shells in withSeparation.Values) Firing.Add(shells);
            if (parachutes.Count > 0) Firing.Add(parachutes);

            foreach (List<Placed> stage in Firing) foreach (Placed p in stage) done.Add(p);
            var rest = new List<Placed>();
            foreach (Placed p in Parts) if (!done.Contains(p) && p.Prefab.hasStagingIcon && !PartCatalog.IsEngine(p.Prefab)) rest.Add(p);
            if (rest.Count > 0) Firing.Insert(Math.Max(0, Firing.Count - (parachutes.Count > 0 ? 1 : 0)), rest);
        }

        /// <summary>Mirror what stock does for parts without their own staging icon: inherit the stage they leave with.</summary>
        static void SpreadStageInfo(Placed p, int carried, int sep)
        {
            if (p.Staged)
            {
                if (PartCatalog.IsDecoupler(p.Prefab)) sep = p.Stage;
                if (p.Prefab.inverseStageCarryover) carried = p.Stage;
            }
            else p.Stage = carried;
            p.SepIndex = sep;
            foreach (Placed c in p.Children) SpreadStageInfo(c, carried, sep);
        }

        // ---------------------------------------------------------------- output

        ConfigNode Emit()
        {
            var root = new ConfigNode();
            root.AddValue("ship", Name);
            root.AddValue("version", Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision);
            root.AddValue("description", Description.Replace('\n', '¨'));
            root.AddValue("type", Facility.ToString());
            root.AddValue("size", "0,0,0");
            root.AddValue("steamPublishedFileId", "0");
            root.AddValue("persistentId", ((uint)rng.Next(1000000, int.MaxValue)).ToString());
            root.AddValue("rot", "0,0,0,1");
            root.AddValue("missionFlag", HighLogic.CurrentGame != null && !string.IsNullOrEmpty(HighLogic.CurrentGame.flagURL) ? HighLogic.CurrentGame.flagURL : "Squad/Flags/default");
            root.AddValue("vesselType", "Debris");

            foreach (Placed p in Parts)
            {
                ConfigNode n = root.AddNode("PART");
                n.AddValue("part", p.Ref);
                n.AddValue("partName", "Part");
                n.AddValue("persistentId", p.PersistentId.ToString());
                n.AddValue("pos", KSPUtil.WriteVector(p.Pos));
                n.AddValue("attPos", "0,0,0");
                Vector3 localPos = p.Parent == null ? p.Pos : Quaternion.Inverse(p.Parent.Rot) * (p.Pos - p.Parent.Pos);
                Quaternion localRot = p.Parent == null ? p.Rot : Quaternion.Inverse(p.Parent.Rot) * p.Rot;
                n.AddValue("attPos0", KSPUtil.WriteVector(localPos));
                n.AddValue("rot", KSPUtil.WriteQuaternion(p.Rot));
                n.AddValue("attRot", "0,0,0,1");
                n.AddValue("attRot0", KSPUtil.WriteQuaternion(localRot));
                n.AddValue("mir", KSPUtil.WriteVector(p.Mir));
                bool mirrorGroup = p.MirrorSym || (p.Group.Count == 1 && Facility == EditorFacility.SPH);
                n.AddValue("symMethod", mirrorGroup ? "Mirror" : "Radial");
                n.AddValue("autostrutMode", p.AutoStrut);
                n.AddValue("rigidAttachment", p.Rigid ? "True" : "False");
                n.AddValue("istg", p.Stage.ToString());
                n.AddValue("resPri", "0");
                n.AddValue("dstg", Math.Max(0, p.Stage).ToString());
                n.AddValue("sidx", (p.Staged ? p.StageSlot : -1).ToString());
                n.AddValue("sqor", (p.Staged ? p.Stage : -1).ToString());
                n.AddValue("sepI", p.SepIndex.ToString());
                n.AddValue("attm", p.Surface ? "1" : "0");
                n.AddValue("sameVesselCollision", "False");
                n.AddValue("modCost", "0");
                n.AddValue("modMass", "0");
                n.AddValue("modSize", "0,0,0");
                foreach (Placed c in p.Children) n.AddValue("link", c.Ref);
                foreach (Placed s in p.Group) if (s != p) n.AddValue("sym", s.Ref);
                if (p.Surface && p.Parent != null) n.AddValue("srfN", p.Prefab.srfAttachNode.id + "," + p.Parent.Ref);
                foreach (AttachNode an in p.Prefab.attachNodes)
                    if (p.Nodes.TryGetValue(an.id, out Placed other)) n.AddValue("attN", an.id + "," + other.Ref);
                n.AddNode("EVENTS");
                n.AddNode("ACTIONS");
                Shell(p, n);
                ConfigNode data = n.AddNode("PARTDATA");
                if (p.LinkTarget != null)
                {
                    data.AddValue("tgt", p.LinkTarget.CraftId.ToString());
                    data.AddValue("tpersID", p.LinkTarget.PersistentId.ToString());
                    data.AddValue("pos", KSPUtil.WriteVector(p.LinkPos));
                    data.AddValue("rot", KSPUtil.WriteQuaternion(p.LinkRot));
                    data.AddValue("dir", KSPUtil.WriteVector(p.LinkDir));
                }
            }
            return root;
        }

        // ---------------------------------------------------------------- fairings

        /// <summary>What was made of each fairing, for the reply: its part's id, how tall and how wide it came out, and how many parts are inside.</summary>
        public readonly List<object> Fairings = new List<object>();

        /// <summary>What a fairing base's shell is to be: what it goes round, and, for an interstage, the part of the stage above that it ends on.</summary>
        sealed class FairingPlan
        {
            public ModuleProceduralFairing Module;
            public int Index;
            public Args Spec;
            public bool Bare, Manual;
            public string Down;                                  // the base's node towards the stage below it
            public List<Placed> Inside = new List<Placed>();
            public Placed Closing;                               // an interstage ends on this part
            public Placed Separator;                             // and is thrown off with this decoupler
        }

        readonly Dictionary<Placed, FairingPlan> fairingPlans = new Dictionary<Placed, FairingPlan>();

        // (between a shell and what it goes round: the game's own fairing editor wants 2 cm of the parts' colliders, and a
        // little more reads as a shell rather than a skin)
        const float Clearance = 0.07f;
        const float Bin = 0.05f;

        /// <summary>The parts on the far side of one of this part's nodes: everything that hangs together through whatever is on that node, without going back through this part.</summary>
        static List<Placed> Beyond(Placed from, string node)
        {
            var found = new List<Placed>();
            if (!from.Nodes.TryGetValue(node, out Placed first) || first == null) return found;
            var seen = new HashSet<Placed> { from, first };
            var open = new Queue<Placed>();
            open.Enqueue(first);
            while (open.Count > 0)
            {
                Placed q = open.Dequeue();
                found.Add(q);
                if (q.Parent != null && seen.Add(q.Parent)) open.Enqueue(q.Parent);
                foreach (Placed c in q.Children) if (seen.Add(c)) open.Enqueue(c);
            }
            return found;
        }

        /// <summary>A part's outline as a fairing base sees it: for every point of it, the height above the shell's pivot along its axis and the distance from that axis.</summary>
        static List<Vector2> Seen(Placed q, Placed basePart, ModuleProceduralFairing m, bool shrouds)
        {
            Quaternion back = Quaternion.Inverse(basePart.Rot);
            Vector3 axis = m.axis.sqrMagnitude > 0f ? m.axis.normalized : Vector3.up;
            var list = new List<Vector2>();
            foreach (Vector3 local in PartGeometry.Outline(q.Info, shrouds, q.Spec != null ? q.Spec.Str("variant") : null))
            {
                Vector3 fromBase = back * (q.Pos + q.Rot * Vector3.Scale(local, q.Mir) - basePart.Pos) - m.pivot;
                float along = Vector3.Dot(fromBase, axis);
                list.Add(new Vector2(along, (fromBase - axis * along).magnitude));
            }
            return list;
        }

        /// <summary>An engine with something under it shows the shroud it drops when it fires.</summary>
        static bool ShowsShroud(Placed q)
        {
            if (!PartCatalog.IsEngine(q.Prefab) || q.Prefab.FindModuleImplementing<ModuleJettison>() == null) return false;
            foreach (AttachNode n in q.Prefab.attachNodes) if (n.orientation.y < -0.5f && q.Nodes.ContainsKey(n.id)) return true;
            return false;
        }

        /// <summary>
        /// Works out every fairing base's shell before staging: what it goes round, and whether it is an interstage, a shell that ends on
        /// the stage above it (round that stage's engine) rather than closing over a payload. "fairing": {"interstage": "id"} names the part
        /// it ends on; {"interstage": false} closes it over everything above whatever that is; left out, a base with an engine standing on
        /// its upper side, bell down, under a part nearly as wide as the base, is taken to be an interstage ending on that part.
        /// </summary>
        void PlanFairings()
        {
            foreach (Placed p in Parts)
            {
                int index = -1;
                ModuleProceduralFairing module = null;
                for (int i = 0; i < p.Prefab.Modules.Count; i++)
                    if (p.Prefab.Modules[i] is ModuleProceduralFairing f) { index = i; module = f; break; }
                if (module == null) continue;
                var plan = new FairingPlan { Module = module, Index = index, Spec = p.Spec != null ? p.Spec.Obj("fairing") : null };
                plan.Bare = plan.Spec == null && p.Spec != null && p.Spec.Has("fairing") && !p.Spec.Bool("fairing", true);
                plan.Manual = plan.Spec != null && (plan.Spec.Has("height") || plan.Spec.Has("radius"));
                fairingPlans[p] = plan;
                if (plan.Bare) continue;
                // (the base's bottom node leads to the stage below; what is on its other nodes, the top and the interstage nodes up its
                // inside, is above it)
                AttachNode bottom = FindNode(p.Prefab, "bottom");
                plan.Down = bottom != null ? bottom.id : null;
                var above = new List<Placed>();
                var had = new HashSet<Placed>();
                foreach (AttachNode n in p.Prefab.attachNodes)
                    if (n.id != plan.Down && p.Nodes.ContainsKey(n.id))
                        foreach (Placed q in Beyond(p, n.id)) if (had.Add(q)) above.Add(q);
                object asked = plan.Spec != null ? plan.Spec.Raw("interstage") : null;
                if (asked is string name)
                {
                    if (!groups.TryGetValue(name, out List<Placed> g)) throw new ApiException("'" + p.Id + "': fairing.interstage names '" + name + "', and no part has that id.");
                    Placed c = Nearest(g, p.Pos);
                    if (!had.Contains(c)) throw new ApiException("'" + p.Id + "': fairing.interstage names '" + name + "', which is not above the fairing base (it must be reached through the base's top node or its interstage nodes).");
                    plan.Closing = c;
                }
                else if (!(asked is bool wanted && !wanted) && !plan.Manual) plan.Closing = UpperStage(p, plan, above);
                plan.Inside = plan.Closing == null ? above : Enclosed(p, plan.Down, plan.Closing);
                if (plan.Closing != null && plan.Down != null && p.Nodes.TryGetValue(plan.Down, out Placed below) && below != null)
                    plan.Separator = FirstDecoupler(plan.Closing, below);
            }
        }

        /// <summary>
        /// The part of an upper stage an interstage shell ends on, if the base has one above it: over the lowest engine on the base's upper
        /// side that stands bell down, the lowest part whose foot is at that engine's top or higher and which is three quarters as wide as
        /// the base or more. Null: what is above is a payload, to be closed over.
        /// </summary>
        static Placed UpperStage(Placed p, FairingPlan plan, List<Placed> above)
        {
            ModuleProceduralFairing m = plan.Module;
            Vector3 axis = m.axis.sqrMagnitude > 0f ? m.axis.normalized : Vector3.up;
            Quaternion back = Quaternion.Inverse(p.Rot);
            Placed engine = null;
            float engineFoot = float.MaxValue, engineTop = 0f;
            foreach (Placed q in above)
            {
                // (its bell pointing down the shell's axis, at the base)
                if (!PartCatalog.IsEngine(q.Prefab) || Vector3.Dot(back * (q.Rot * Vector3.down), axis) > -0.7f) continue;
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (Vector2 s in Seen(q, p, m, ShowsShroud(q))) { lo = Mathf.Min(lo, s.x); hi = Mathf.Max(hi, s.x); }
                if (lo < engineFoot) { engine = q; engineFoot = lo; engineTop = hi; }
            }
            if (engine == null) return null;
            // (what hangs under that engine is not the stage above)
            var under = new HashSet<Placed>();
            foreach (AttachNode n in engine.Prefab.attachNodes)
                if (n.orientation.y < -0.5f && engine.Nodes.ContainsKey(n.id)) foreach (Placed q in Beyond(engine, n.id)) under.Add(q);
            Placed best = null;
            float bestFoot = float.MaxValue;
            foreach (Placed q in above)
            {
                if (q == engine || under.Contains(q) || q.Prefab is CompoundPart) continue;
                List<Vector2> seen = Seen(q, p, m, false);
                float foot = float.MaxValue;
                foreach (Vector2 s in seen) foot = Mathf.Min(foot, s.x);
                if (foot < engineTop - 0.3f || foot >= bestFoot) continue;
                float wide = 0f;
                foreach (Vector2 s in seen) if (s.x <= foot + 0.5f) wide = Mathf.Max(wide, s.y);
                if (wide < 0.75f * m.baseRadius) continue;
                best = q;
                bestFoot = foot;
            }
            return best;
        }

        /// <summary>What an interstage shell goes round: everything on the base's upper side short of the part it ends on and what is beyond that.</summary>
        static List<Placed> Enclosed(Placed p, string down, Placed closing)
        {
            var seen = new HashSet<Placed> { p, closing };
            var found = new List<Placed>();
            var open = new Queue<Placed>();
            foreach (KeyValuePair<string, Placed> kv in p.Nodes)
                if (kv.Key != down && kv.Value != null && seen.Add(kv.Value)) open.Enqueue(kv.Value);
            while (open.Count > 0)
            {
                Placed q = open.Dequeue();
                found.Add(q);
                if (q.Parent != null && seen.Add(q.Parent)) open.Enqueue(q.Parent);
                foreach (Placed c in q.Children) if (seen.Add(c)) open.Enqueue(c);
            }
            return found;
        }

        /// <summary>The first decoupler on the way through the craft from one part to another (not counting the first), or null.</summary>
        static Placed FirstDecoupler(Placed from, Placed to)
        {
            var up = new List<Placed>();
            for (Placed q = from; q != null; q = q.Parent) up.Add(q);
            var down = new List<Placed>();
            bool met = false;
            for (Placed q = to; q != null; q = q.Parent)
            {
                int at = up.IndexOf(q);
                if (at >= 0) { up.RemoveRange(at + 1, up.Count - at - 1); met = true; break; }
                down.Add(q);
            }
            if (!met) return null;
            down.Reverse();
            var path = new List<Placed>(up);
            path.AddRange(down);
            for (int i = 1; i < path.Count; i++) if (PartCatalog.IsDecoupler(path[i].Prefab)) return path[i];
            return null;
        }

        /// <summary>
        /// A fairing's shell. The game keeps one as a list of rings, each a height above the base and a radius, and the player draws it ring
        /// by ring with the mouse. Here it is worked out, from what PlanFairings found it is to go round:
        ///
        /// * over a payload, it follows the payload's outline: at each height it is as wide as the widest point of the payload there or
        ///   anywhere above (by the corners of each part's shape and of its colliders, the clearance added), drawn as the tightest round,
        ///   convex shell that keeps out of all of that, flared out from the base's own ring and closed in a rounded nose over the top;
        /// * as an interstage, it goes round what is between the base and the stage above in the same way and ends on that stage's part,
        ///   just above its foot, as wide as it is there, the way the game's editor snaps a shell onto a part above it;
        /// * set by hand ("fairing": {"height": metres above the base, "radius": metres}), it is one width from a short flare to the nose.
        ///
        /// It is written into the craft file as the game writes its own, so that the game makes the panels, weighs them and prices them when
        /// it opens the file.
        /// </summary>
        void Shell(Placed p, ConfigNode part)
        {
            if (!fairingPlans.TryGetValue(p, out FairingPlan plan) || plan.Bare) return;
            ModuleProceduralFairing module = plan.Module;
            Args spec = plan.Spec;
            float least = module.baseRadius, most = module.maxRadius, cap = module.capRadius;
            float stepLeast = Mathf.Max(0.05f, module.xSectionHeightMin), stepMost = Mathf.Max(stepLeast, module.xSectionHeightMax);

            // ---- what it has to go round
            var points = new List<Vector2>();
            float top = 0f, wide = 0f;
            foreach (Placed q in plan.Inside)
                foreach (Vector2 s in Seen(q, p, module, ShowsShroud(q)))
                {
                    if (s.x < -0.05f) continue;                          // (below the base: not the fairing's to cover)
                    points.Add(s);
                    top = Mathf.Max(top, s.x);
                    wide = Mathf.Max(wide, s.y);
                }

            List<Vector2> rings;
            string shape;
            if (plan.Manual)
            {
                float radius = Mathf.Clamp((float)spec.Num("radius", Mathf.Max(least, wide + 0.12f)), least, most);
                float nose0 = Mathf.Max(1.2f * stepLeast, 1.65f * radius);
                float height = Mathf.Max((float)spec.Num("height", top + 0.15f + nose0), 2f * stepLeast + 0.5f * radius);
                rings = ByHand(least, radius, height, cap, stepLeast, stepMost);
                shape = "set by hand";
            }
            else if (plan.Closing != null)
            {
                // ---- an interstage: round what is below the stage above, ending on that stage's part just above its foot
                List<Vector2> onto = Seen(plan.Closing, p, module, false);
                float foot = float.MaxValue;
                foreach (Vector2 s in onto) foot = Mathf.Min(foot, s.x);
                float h = foot + 0.15f, r = 0f;
                if (h < stepLeast)
                {
                    Warnings.Add("The fairing base '" + p.Id + "' was to end its shell on '" + plan.Closing.Id + "', but that part starts at the base itself or below it, so no shell was made. Name a part higher up (\"fairing\": {\"interstage\": \"id\"}), or leave the base bare.");
                    return;
                }
                foreach (Vector2 s in onto) if (s.x <= h + 0.05f) r = Mathf.Max(r, s.y);
                r += 0.01f;
                if (r > most)
                {
                    Warnings.Add("The fairing base '" + p.Id + "' cannot reach out to '" + plan.Closing.Id + "' (" + Math.Round(2f * r, 2) + " m across; this base makes a shell no wider than " + Math.Round(2f * most, 2) + " m). Use a larger fairing base.");
                    r = most;
                }
                points.RemoveAll(s => s.x > h - 0.03f);
                rings = Fit(points, least, new List<Vector2> { new Vector2(h, r) }, stepLeast, stepMost);
                shape = "an interstage: round what is between the base and '" + plan.Closing.Id + "', ending on '" + plan.Closing.Id + "'";
            }
            else
            {
                if (plan.Inside.Count == 0)
                {
                    Warnings.Add("The fairing base '" + p.Id + "' has nothing on its top node to cover, so no shell was made. Put the payload on it (\"on\": \"" + p.Id + "\", \"node\": \"top\", or build the payload first and the base under it), or give \"fairing\": {\"height\": .., \"radius\": ..}.");
                    return;
                }
                if (wide + Clearance > most)
                    Warnings.Add("What stands on the fairing base '" + p.Id + "' is " + Math.Round(2f * wide, 2) + " m across, and this base can make a shell no wider than " + Math.Round(2f * most, 2) + " m: it will stick out through it. Use a larger fairing base.");
                // ---- over a payload: its outline, and a rounded nose on its top as wide as its top is
                float shoulder = cap + 0.05f, head = 0f;
                foreach (Vector2 s in points)
                {
                    head = Mathf.Max(head, (Mathf.Floor(s.x / Bin) + 1f) * Bin);
                    if (s.x >= top - 0.25f) shoulder = Mathf.Max(shoulder, s.y + Clearance);
                }
                shoulder = Mathf.Min(shoulder, most);
                float nose = Mathf.Max(1.2f * stepLeast, 1.65f * (shoulder - cap));
                int steps = Mathf.Clamp(Mathf.CeilToInt(nose / (0.3f * Mathf.Max(shoulder, 0.4f))), 4, 9);
                var dome = new List<Vector2> { new Vector2(head, shoulder) };
                for (int k = 1; k <= steps; k++)
                {
                    float t = k / (float)steps;
                    // (rounded like the nose of a real one: nearly straight at first, turning in faster towards the tip)
                    dome.Add(new Vector2(head + nose * t, k == steps ? cap : Mathf.Max(cap, cap + (shoulder - cap) * Mathf.Pow(1f - t * t, 0.62f))));
                }
                rings = Fit(points, least, dome, stepLeast, stepMost);
                shape = "follows what it covers";
            }
            float widest = 0f;
            foreach (Vector2 ring in rings) widest = Mathf.Max(widest, ring.y);
            if (widest > most + 0.001f)
                for (int i = 0; i < rings.Count; i++) rings[i] = new Vector2(rings[i].x, Mathf.Min(rings[i].y, most));

            // ---- written as the game writes it. A part's modules are looked up in the file in their order, so those before
            // this one are named too (with nothing in them: they stay as the part comes).
            for (int i = 0; i < plan.Index; i++) part.AddNode("MODULE").AddValue("name", p.Prefab.Modules[i].moduleName);
            ConfigNode made = part.AddNode("MODULE");
            made.AddValue("name", module.moduleName);
            made.AddValue("isEnabled", "True");
            if (plan.Closing != null)
            {
                // (as the editor leaves one it has snapped onto a part above: ended there, not capped with a nose)
                made.AddValue("interstageCraftID", plan.Closing.CraftId.ToString());
                made.AddValue("isCapped", "True");
            }
            foreach (Vector2 ring in rings)
            {
                ConfigNode one = made.AddNode("XSECTION");
                one.AddValue("h", ring.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                one.AddValue("r", ring.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            var report = new JObj().Add("id", p.Id).Add("height_m", Math.Round(rings[rings.Count - 1].x, 2)).Add("width_m", Math.Round(2f * Mathf.Min(widest, most), 2))
                .Add("parts_inside", plan.Inside.Count).Add("rings", rings.Count).Add("shape", shape);
            if (plan.Closing != null)
                report.Add("ends_on", plan.Closing.Id).Add("thrown_off_with", plan.Separator != null ? (object)plan.Separator.Id : "no decoupler was found between the two stages: stage the fairing yourself");
            Fairings.Add(report);
        }

        /// <summary>A shell set by hand: flared out from the base's ring to one width in a short cone, straight up, and a rounded nose.</summary>
        static List<Vector2> ByHand(float least, float radius, float height, float cap, float stepLeast, float stepMost)
        {
            var rings = new List<Vector2> { new Vector2(0f, least) };          // (height, radius)
            float at = 0f;
            if (radius > least + 0.02f)
            {
                at = Mathf.Clamp((radius - least) * 1.2f, stepLeast, stepMost);
                rings.Add(new Vector2(at, radius));
            }
            float nose = Mathf.Clamp(1.65f * radius, 1.2f * stepLeast, Mathf.Max(1.2f * stepLeast, height - at - 0.01f));
            float straight = height - nose;
            while (straight - at > 0.02f)
            {
                at = Mathf.Min(straight, at + stepMost);
                rings.Add(new Vector2(at, radius));
            }
            // (enough rings for the nose to look turned and not folded, and none closer together than the game would let a player put them)
            int steps = Mathf.Clamp(Mathf.CeilToInt(nose / (0.3f * Mathf.Max(radius, 0.4f))), 4, 9);
            steps = Mathf.Max(2, Mathf.Min(steps, Mathf.FloorToInt(nose / stepLeast)));
            for (int k = 1; k <= steps; k++)
            {
                float t = k / (float)steps;
                float r = k == steps ? cap : Mathf.Max(cap, cap + (radius - cap) * Mathf.Pow(1f - t * t, 0.62f));
                rings.Add(new Vector2(straight + nose * t, r));
            }
            return rings;
        }

        /// <summary>
        /// The tightest round, convex shell from the base's ring (height 0, radius 'least') to the given end (a nose, or the ring an
        /// interstage ends on) that keeps out of every point given, the clearance added: the upper hull of all of them, with rings only where
        /// the outline turns, one at least every few metres up a straight stretch, and none crowded together.
        /// </summary>
        static List<Vector2> Fit(List<Vector2> points, float least, List<Vector2> end, float stepLeast, float stepMost)
        {
            // the widest point in each few centimetres of height (the width of a whole band holds from its foot to its head)
            float last = 0f;
            foreach (Vector2 e in end) last = Mathf.Max(last, e.x);
            var bands = new Dictionary<int, float>();
            foreach (Vector2 s in points)
            {
                if (s.x > last) continue;
                int b = Mathf.Max(0, Mathf.FloorToInt(s.x / Bin));
                bands[b] = bands.TryGetValue(b, out float r) ? Mathf.Max(r, s.y) : s.y;
            }
            var all = new List<Vector2> { new Vector2(0f, least) };
            foreach (KeyValuePair<int, float> kv in bands)
            {
                all.Add(new Vector2(Mathf.Max(0.001f, kv.Key * Bin), kv.Value + Clearance));
                all.Add(new Vector2(Mathf.Min(last - 0.001f, (kv.Key + 1) * Bin), kv.Value + Clearance));
            }
            all.AddRange(end);
            all.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : b.y.CompareTo(a.y));
            var hull = new List<Vector2>();
            foreach (Vector2 q in all)
            {
                if (hull.Count > 0 && Mathf.Abs(hull[hull.Count - 1].x - q.x) < 1e-4f) continue;          // (the widest at one height came first)
                while (hull.Count >= 2 && Turn(hull[hull.Count - 2], hull[hull.Count - 1], q) >= 0f) hull.RemoveAt(hull.Count - 1);
                hull.Add(q);
            }
            // (no ring sooner after the base's than a player could put one: the flare is drawn over at least that height)
            if (hull.Count > 2 && hull[1].x < stepLeast) hull[1] = new Vector2(Mathf.Max(hull[1].x, Mathf.Min(stepLeast, 0.5f * hull[2].x)), hull[1].y);
            // (rings closer together than a sensible panel are merged: the edges either side are carried on until they meet, which
            // keeps the shell outside everything it was outside of)
            for (int i = 1; i + 2 < hull.Count; i++)
            {
                if (hull[i + 1].x - hull[i].x >= 0.12f) continue;
                Vector2 a = hull[i - 1], b = hull[i], c = hull[i + 1], d = hull[i + 2];
                if (Meet(a, b, c, d, out Vector2 m) && m.x > a.x + 0.01f && m.x < d.x - 0.01f) hull[i] = m;
                else hull[i] = new Vector2(0.5f * (b.x + c.x), Mathf.Max(b.y, c.y));
                hull.RemoveAt(i + 1);
                i--;
            }
            // (one ring at least every few metres up a long straight stretch, as a player's own shell has)
            var rings = new List<Vector2> { hull[0] };
            for (int i = 1; i < hull.Count; i++)
            {
                Vector2 a = hull[i - 1], b = hull[i];
                int pieces = Mathf.CeilToInt((b.x - a.x) / stepMost - 0.001f);
                for (int k = 1; k < pieces; k++) rings.Add(Vector2.Lerp(a, b, k / (float)pieces));
                rings.Add(b);
            }
            return rings;
        }

        static float Turn(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        /// <summary>Where the line through a and b meets the line through c and d.</summary>
        static bool Meet(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out Vector2 m)
        {
            m = Vector2.zero;
            Vector2 r = b - a, s = d - c;
            float den = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(den) < 1e-6f) return false;
            float t = ((c.x - a.x) * s.y - (c.y - a.y) * s.x) / den;
            m = a + r * t;
            return true;
        }

        public List<object> StagingReport()
        {
            var report = new List<object>();
            for (int n = 0; n < Firing.Count; n++)
            {
                var names = new List<string>();
                var seen = new HashSet<string>();
                foreach (Placed p in Firing[n])
                    if (seen.Add(p.Id)) names.Add(p.Id + (p.Group.Count > 1 ? " x" + p.Group.Count : "") + " [" + p.Info.title + "]");
                report.Add(new JObj().Add("fires", n + 1).Add("ksp_stage", Firing.Count - 1 - n).Add("activates", names));
            }
            return report;
        }
    }
}
