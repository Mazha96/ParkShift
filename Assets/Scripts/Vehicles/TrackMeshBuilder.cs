using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Vehicles
{
    /// <summary>
    /// Generates the stylized low-poly coaster track along a TrackPath spline:
    /// twin octagonal tube rails, cross-ties, a continuous center spine box, support
    /// columns dropping to ground height with footers, brake-run fin strips on the brake
    /// block, and station running gear (deck + drive tires) on the station block(s).
    /// Frames come from the parallel-transported FrameTable below (loop-safe); banking
    /// (roll from horizontal curvature, clamped) is layered on for flat-ish turns only.
    /// All meshes are flat-shaded (per-face vertices) for the chunky look.
    /// Runtime-safe (no UnityEditor) so SceneBuilder and any future track editor can both use it.
    /// </summary>
    public static class TrackMeshBuilder
    {
        /// <summary>Styling/feature knobs for Build(). Defaults match the shipped scene:
        /// ground at y=0, station on block 0 (and 1 when there are 5+ blocks), brakes on the
        /// last block. Pass explicit block arrays for custom layouts.</summary>
        public class TrackStyle
        {
            public float GroundY = 0f;

            // Rails (twin octagonal tubes)
            public float RailOffset = 0.55f;      // lateral distance centerline -> rail center
            public float RailRadius = 0.09f;
            public int RailSides = 8;
            public float RailRingStep = 0.7f;     // meters between rail cross-sections

            // Center spine (continuous rectangular box tube)
            public float SpineWidth = 0.34f;
            public float SpineHeight = 0.26f;
            public float SpineDrop = 0.30f;       // spine centerline below rail centers
            public float SpineRingStep = 1.1f;

            // Cross-ties
            public float TieStep = 1.4f;
            public Vector3 TieSize = new Vector3(1.7f, 0.09f, 0.3f);
            public float TieDrop = 0.18f;         // tie centerline below rail centers

            // Support columns
            public bool BuildSupports = true;
            public float SupportStep = 3.6f;      // meters of arc between support samples
            public float SupportMinClearance = 0.12f; // below this the track hugs the ground
            public float ColumnWidth = 0.26f;

            // Brake-run fins
            public bool BuildBrakeFins = true;
            public float FinStep = 0.75f;

            // Station running gear (deck strip + friction drive tires)
            public bool BuildStationGear = true;
            public float TireStep = 1.1f;

            // Block roles (null => heuristic from BlockLabels, else defaults above)
            public int[] StationBlocks = null;
            public int[] BrakeBlocks = null;

            // Explicit brake-fin spans as (block, progress0, progress1) triples. When set, fins
            // are laid only on these in-block arc ranges (e.g. just the flat mid-course shelf and
            // the final brake straight) instead of across the whole brake block — layouts whose
            // brake block also contains curves/hills need this. Null => legacy whole-block fins.
            public Vector3[] FinSpans = null;

            // Optional material overrides (null => railMat / tieMat fallbacks)
            public Material SpineMaterial = null;
            public Material SupportMaterial = null;
            public Material AccentMaterial = null;
        }

        /// <summary>Legacy entry point (SceneBuilder calls this) — full styled track with defaults.</summary>
        public static GameObject Build(TrackPath path, Material railMat, Material tieMat)
        {
            return Build(path, railMat, tieMat, new TrackStyle());
        }

        public static GameObject Build(TrackPath path, Material railMat, Material tieMat, TrackStyle style)
        {
            path.Rebuild();
            float len = path.TotalLength;

            var root = new GameObject("TrackMesh");
            root.transform.SetParent(path.transform, false);
            root.isStatic = true;
            if (len <= 0.01f) return root;

            Material spineMat = style.SpineMaterial != null ? style.SpineMaterial : railMat;
            Material supportMat = style.SupportMaterial != null ? style.SupportMaterial : tieMat;
            Material accentMat = style.AccentMaterial != null ? style.AccentMaterial : railMat;

            var frames = new FrameTable(path);
            CreateChild(root, "Rails", BuildRails(frames, len, style), railMat);
            CreateChild(root, "Spine", BuildSpine(frames, len, style), spineMat);
            CreateChild(root, "Ties", BuildTies(frames, len, style), tieMat);
            if (style.BuildSupports)
                CreateChild(root, "Supports", BuildSupports(frames, len, style), supportMat);
            if (style.BuildBrakeFins)
                CreateChild(root, "BrakeFins", BuildBrakeFins(path, frames, len, style), accentMat);
            if (style.BuildStationGear)
            {
                CreateChild(root, "StationRails", BuildStationDeck(path, frames, len, style), tieMat);
                CreateChild(root, "DriveTires", BuildDriveTires(path, frames, len, style), accentMat);
            }
            return root;
        }

        // ---------------- spline framing ----------------

        /// <summary>
        /// Parallel-transported frame table along the closed spline. TrackPath.FrameAtDistance
        /// builds every frame from Quaternion.LookRotation(fwd, Vector3.up); that world-up
        /// projection degenerates when the tangent goes vertical and flips 180° through the top
        /// of a vertical loop. This table transports the up-vector along the tangent instead
        /// (minimal-torsion / zero-roll transport), closes the loop seam by feathering any
        /// residual roll around the lap, and re-applies TrackPath's curvature banking only where
        /// the track is upright and near-horizontal so loop roll is never fought by the bank
        /// clamp. Runtime-safe (no UnityEditor) and public on purpose: ride vehicles can build
        /// one of these and sample the exact same frames as the rails — TrainVisual should swap
        /// its Path.FrameAtDistance calls to FrameAt to ride the loop without the world-up flip.
        /// </summary>
        public class FrameTable
        {
            readonly TrackPath _path;
            readonly Quaternion[] _rots;
            readonly float _step;
            readonly float _len;
            readonly int _count;

            public FrameTable(TrackPath path, float step = 0.5f)
            {
                _path = path;
                path.Rebuild();
                _len = path.TotalLength;
                _count = Mathf.Max(8, Mathf.CeilToInt(_len / Mathf.Max(0.05f, step)));
                _step = _len / _count;
                _rots = new Quaternion[_count];

                // Seed at d=0 (the station straight — flat by construction) and transport around.
                Vector3 prevT = path.TangentAtDistance(0f);
                Quaternion q = Quaternion.LookRotation(prevT, Vector3.up);
                _rots[0] = q;
                for (int i = 1; i < _count; i++)
                {
                    Vector3 t = path.TangentAtDistance(i * _step);
                    q = Quaternion.FromToRotation(prevT, t) * q;
                    _rots[i] = q;
                    prevT = t;
                }

                // Seam closure: a full lap of transport can come home rolled (holonomy).
                // Feather the residual roll across the lap so frame(len) == frame(0).
                Vector3 t0 = path.TangentAtDistance(0f);
                Quaternion qEnd = Quaternion.FromToRotation(prevT, t0) * q;
                float residual = Vector3.SignedAngle(qEnd * Vector3.up, _rots[0] * Vector3.up, t0);
                for (int i = 1; i < _count; i++)
                    _rots[i] = _rots[i] * Quaternion.AngleAxis(residual * (i / (float)_count), Vector3.forward);

                // Curvature banking, faded out on steep or inverted track (lift spine, loop)
                // so only flat-ish turns bank and the transported roll is otherwise untouched.
                for (int i = 0; i < _count; i++)
                {
                    Vector3 fwd = _rots[i] * Vector3.forward;
                    Vector3 up = _rots[i] * Vector3.up;
                    float horiz = Mathf.Clamp01(1f - Mathf.Abs(fwd.y) * 1.6f);
                    if (up.y > 0.2f && horiz > 0.01f)
                        _rots[i] = _rots[i] * Quaternion.AngleAxis(-_path.BankAtDistance(i * _step) * horiz, Vector3.forward);
                }
            }

            public float TotalLength => _len;

            /// <summary>Position + parallel-transported rotation at arc distance d (wrapped).</summary>
            public void FrameAt(float d, out Vector3 pos, out Quaternion rot)
            {
                d = Mathf.Repeat(d, _len);
                pos = _path.PointAtDistance(d);
                float f = d / _step;
                int i = Mathf.FloorToInt(f);
                if (i >= _count) i = _count - 1;
                rot = Quaternion.Slerp(_rots[i], _rots[(i + 1) % _count], f - i);
            }
        }

        static void CreateChild(GameObject root, string name, Mesh mesh, Material mat)
        {
            if (mesh == null || mesh.vertexCount == 0) return;
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // ---------------- block-role helpers ----------------

        static List<int> ResolveBlocks(TrackPath path, int[] explicitBlocks, string[] labelKeys, bool defaultIsStation)
        {
            var result = new List<int>();
            if (explicitBlocks != null) { result.AddRange(explicitBlocks); return result; }

            int count = Mathf.Max(1, path.EffectiveBlockCount);
            if (path.BlockLabels != null)
            {
                for (int i = 0; i < path.BlockLabels.Count && i < count; i++)
                {
                    string label = path.BlockLabels[i];
                    if (string.IsNullOrEmpty(label)) continue;
                    string low = label.ToLowerInvariant();
                    for (int k = 0; k < labelKeys.Length; k++)
                        if (low.Contains(labelKeys[k])) { result.Add(i); break; }
                }
            }
            if (result.Count == 0)
            {
                if (defaultIsStation)
                {
                    // Shipped scene: block 0 = Unload and block 1 = Load share the station straight.
                    result.Add(0);
                    if (count >= 5) result.Add(1);
                }
                else
                {
                    result.Add(count - 1); // brake run is conventionally the last block
                }
            }
            return result;
        }

        /// <summary>Arc span [s0, s1] of (block, p0..p1); s1 is unwrapped (may exceed len).</summary>
        static void BlockSpan(TrackPath path, float len, int block, float p0, float p1, out float s0, out float s1)
        {
            s0 = path.DistanceOf(block, p0);
            s1 = path.DistanceOf(block, p1);
            if (s1 <= s0) s1 += len;
        }

        // ---------------- rails ----------------

        static Mesh BuildRails(FrameTable frames, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();
            int rings = Mathf.Max(12, Mathf.CeilToInt(len / st.RailRingStep));
            int sides = Mathf.Max(4, st.RailSides);
            Vector2[] prof = FlatMeshBuilder.NGon(st.RailRadius, sides);

            for (int side = -1; side <= 1; side += 2)
            {
                // Sample every ring's corner set once, then stitch quads with wraparound.
                var ringPts = new Vector3[rings][];
                for (int r = 0; r < rings; r++)
                {
                    float d = r / (float)rings * len;
                    frames.FrameAt(d, out var pos, out var rot);
                    Vector3 c = pos + rot * new Vector3(side * st.RailOffset, 0f, 0f);
                    var arr = new Vector3[sides];
                    for (int k = 0; k < sides; k++)
                        arr[k] = c + rot * new Vector3(prof[k].x, prof[k].y, 0f);
                    ringPts[r] = arr;
                }
                for (int r = 0; r < rings; r++)
                {
                    Vector3[] A = ringPts[r];
                    Vector3[] B = ringPts[(r + 1) % rings];
                    float u0 = r / (float)rings * len * 0.5f;
                    float u1 = (r + 1) / (float)rings * len * 0.5f;
                    for (int k = 0; k < sides; k++)
                    {
                        int k2 = (k + 1) % sides;
                        float v0 = k / (float)sides, v1 = (k + 1) / (float)sides;
                        b.AddQuad(A[k], A[k2], B[k2], B[k],
                            new Vector2(u0, v0), new Vector2(u0, v1),
                            new Vector2(u1, v1), new Vector2(u1, v0));
                    }
                }
            }
            return b.ToMesh("Rails");
        }

        // ---------------- spine ----------------

        static Mesh BuildSpine(FrameTable frames, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();
            int rings = Mathf.Max(8, Mathf.CeilToInt(len / st.SpineRingStep));
            float hw = st.SpineWidth * 0.5f, hh = st.SpineHeight * 0.5f;
            // CCW rectangle profile (right-bottom -> right-top -> left-top -> left-bottom)
            Vector2[] prof =
            {
                new Vector2(hw, -hh), new Vector2(hw, hh),
                new Vector2(-hw, hh), new Vector2(-hw, -hh),
            };

            var ringPts = new Vector3[rings][];
            for (int r = 0; r < rings; r++)
            {
                float d = r / (float)rings * len;
                frames.FrameAt(d, out var pos, out var rot);
                Vector3 c = pos + rot * new Vector3(0f, -st.SpineDrop, 0f);
                var arr = new Vector3[4];
                for (int k = 0; k < 4; k++)
                    arr[k] = c + rot * new Vector3(prof[k].x, prof[k].y, 0f);
                ringPts[r] = arr;
            }
            for (int r = 0; r < rings; r++)
            {
                Vector3[] A = ringPts[r];
                Vector3[] B = ringPts[(r + 1) % rings];
                float u0 = r / (float)rings * len * 0.5f;
                float u1 = (r + 1) / (float)rings * len * 0.5f;
                for (int k = 0; k < 4; k++)
                {
                    int k2 = (k + 1) % 4;
                    b.AddQuad(A[k], A[k2], B[k2], B[k],
                        new Vector2(u0, k / 4f), new Vector2(u0, (k + 1) / 4f),
                        new Vector2(u1, (k + 1) / 4f), new Vector2(u1, k / 4f));
                }
            }
            return b.ToMesh("Spine");
        }

        // ---------------- ties ----------------

        static Mesh BuildTies(FrameTable frames, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();
            int count = Mathf.Max(4, Mathf.CeilToInt(len / st.TieStep));
            for (int i = 0; i < count; i++)
            {
                float d = i / (float)count * len;
                frames.FrameAt(d, out var pos, out var rot);
                Vector3 center = pos + rot * new Vector3(0f, -st.TieDrop, 0f);
                b.AddBox(center, rot, st.TieSize);
            }
            return b.ToMesh("Ties");
        }

        // ---------------- supports ----------------

        static Mesh BuildSupports(FrameTable frames, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();
            int count = Mathf.Max(1, Mathf.CeilToInt(len / st.SupportStep));
            float spineUnderside = st.SpineDrop + st.SpineHeight * 0.5f;

            for (int i = 0; i < count; i++)
            {
                float d = i / (float)count * len;
                frames.FrameAt(d, out var pos, out var rot);
                // Inverted / steeply rolled track (loop) can't drop a plumb column from its
                // spine — those spans get external bracing (the scene's loop ring) instead.
                if ((rot * Vector3.up).y < 0.35f) continue;
                Vector3 attach = pos + rot * new Vector3(0f, -spineUnderside, 0f);
                float h = attach.y - st.GroundY;
                if (h < st.SupportMinClearance) continue; // track hugs the ground here

                // Vertical column: yawed with the track but never rolled (columns stay plumb).
                // Tall lift/loop columns thicken with height so they don't read as spaghetti.
                Vector3 fwd = rot * Vector3.forward;
                float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                Quaternion colRot = Quaternion.Euler(0f, yaw, 0f);
                float w = st.ColumnWidth * Mathf.Max(1f, Mathf.Sqrt(h * 0.25f));

                b.AddBox(new Vector3(attach.x, st.GroundY + h * 0.5f, attach.z), colRot,
                         new Vector3(w, h, w));

                if (h > 0.45f) // concrete footer pad
                    b.AddBox(new Vector3(attach.x, st.GroundY + 0.09f, attach.z), colRot,
                             new Vector3(Mathf.Max(0.7f, w * 2.4f), 0.18f, Mathf.Max(0.7f, w * 2.4f)));

                if (h > 0.8f)  // saddle beam hugging the spine (banked with the track)
                    b.AddBox(pos + rot * new Vector3(0f, -(spineUnderside + 0.07f), 0f), rot,
                             new Vector3(1.5f, 0.14f, 0.34f));

                if (h > 9f)    // mid-height collar so very tall columns get a visual joint
                    b.AddBox(new Vector3(attach.x, st.GroundY + h * 0.5f, attach.z), colRot,
                             new Vector3(w * 1.6f, 0.3f, w * 1.6f));
            }
            return b.ToMesh("Supports");
        }

        // ---------------- brake fins ----------------

        static Mesh BuildBrakeFins(TrackPath path, FrameTable frames, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();

            if (st.FinSpans != null)
            {
                // Explicit spans: fins only on the designated flat brake shelves.
                for (int i = 0; i < st.FinSpans.Length; i++)
                {
                    Vector3 span = st.FinSpans[i];
                    BlockSpan(path, len, Mathf.RoundToInt(span.x), Mathf.Clamp01(span.y), Mathf.Clamp01(span.z), out var f0, out var f1);
                    AddFinRun(b, frames, len, f0, f1, st);
                }
                return b.ToMesh("BrakeFins");
            }

            List<int> brakes = ResolveBlocks(path, st.BrakeBlocks, new[] { "brake" }, false);
            float endP = Mathf.Min(0.985f, path.BrakePoint01 + 0.05f);

            for (int i = 0; i < brakes.Count; i++)
            {
                BlockSpan(path, len, brakes[i], 0.06f, endP, out var s0, out var s1);
                AddFinRun(b, frames, len, s0, s1, st);
            }
            return b.ToMesh("BrakeFins");
        }

        /// <summary>Lays a strip of brake fins along the arc span [s0, s1] (s1 may be unwrapped).</summary>
        static void AddFinRun(FlatMeshBuilder b, FrameTable frames, float len, float s0, float s1, TrackStyle st)
        {
            int fins = Mathf.FloorToInt((s1 - s0) / st.FinStep);
            for (int f = 0; f < fins; f++)
            {
                float d = Mathf.Repeat(s0 + (f + 0.5f) * st.FinStep, len);
                frames.FrameAt(d, out var pos, out var rot);
                // Center fin strip: tops out just under the train chassis, pokes above the spine.
                b.AddBox(pos + rot * new Vector3(0f, -0.21f, 0f), rot,
                         new Vector3(0.055f, 0.22f, 0.5f));
            }
        }

        // ---------------- station running gear ----------------

        static Mesh BuildStationDeck(TrackPath path, FrameTable frames, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();
            List<int> stations = ResolveBlocks(path, st.StationBlocks, new[] { "load", "station" }, true);
            for (int i = 0; i < stations.Count; i++)
            {
                BlockSpan(path, len, stations[i], 0.02f, 0.98f, out var s0, out var s1);
                float span = s1 - s0;
                int pieces = Mathf.Max(1, Mathf.CeilToInt(span / 1.0f));
                float segLen = span / pieces;
                for (int p = 0; p < pieces; p++)
                {
                    float d = Mathf.Repeat(s0 + (p + 0.5f) * segLen, len);
                    frames.FrameAt(d, out var pos, out var rot);
                    // Low center deck the drive tires poke through — the station "running rail".
                    b.AddBox(pos + rot * new Vector3(0f, -0.19f, 0f), rot,
                             new Vector3(0.6f, 0.1f, segLen + 0.05f));
                }
            }
            return b.ToMesh("StationRails");
        }

        static Mesh BuildDriveTires(TrackPath path, FrameTable frames, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();
            List<int> stations = ResolveBlocks(path, st.StationBlocks, new[] { "load", "station" }, true);
            for (int i = 0; i < stations.Count; i++)
            {
                BlockSpan(path, len, stations[i], 0.05f, 0.95f, out var s0, out var s1);
                int tires = Mathf.Max(1, Mathf.FloorToInt((s1 - s0) / st.TireStep));
                for (int t = 0; t < tires; t++)
                {
                    float d = Mathf.Repeat(s0 + (t + 0.5f) * st.TireStep, len);
                    frames.FrameAt(d, out var pos, out var rot);
                    // Paired friction tires (octagonal discs on a lateral axle) under the RV chassis.
                    Quaternion axle = rot * Quaternion.Euler(0f, 90f, 0f); // prism Z -> track lateral
                    b.AddPrism(pos + rot * new Vector3(-0.13f, -0.28f, 0f), axle, 0.15f, 0.045f, 8);
                    b.AddPrism(pos + rot * new Vector3(0.13f, -0.28f, 0f), axle, 0.15f, 0.045f, 8);
                }
            }
            return b.ToMesh("DriveTires");
        }
    }
}
// (procedural coaster overhaul)
