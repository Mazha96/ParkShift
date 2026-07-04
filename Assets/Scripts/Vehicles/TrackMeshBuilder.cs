using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Vehicles
{
    /// <summary>
    /// Generates the stylized low-poly coaster track along a TrackPath spline:
    /// twin octagonal tube rails, cross-ties, a continuous center spine box, support
    /// columns dropping to ground height with footers, brake-run fin strips on the brake
    /// block, and station running gear (deck + drive tires) on the station block(s).
    /// Banking (roll from horizontal curvature, clamped) comes from TrackPath.FrameAtDistance.
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

            CreateChild(root, "Rails", BuildRails(path, len, style), railMat);
            CreateChild(root, "Spine", BuildSpine(path, len, style), spineMat);
            CreateChild(root, "Ties", BuildTies(path, len, style), tieMat);
            if (style.BuildSupports)
                CreateChild(root, "Supports", BuildSupports(path, len, style), supportMat);
            if (style.BuildBrakeFins)
                CreateChild(root, "BrakeFins", BuildBrakeFins(path, len, style), accentMat);
            if (style.BuildStationGear)
            {
                CreateChild(root, "StationRails", BuildStationDeck(path, len, style), tieMat);
                CreateChild(root, "DriveTires", BuildDriveTires(path, len, style), accentMat);
            }
            return root;
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

        static Mesh BuildRails(TrackPath path, float len, TrackStyle st)
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
                    path.FrameAtDistance(d, out var pos, out var rot, out _);
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

        static Mesh BuildSpine(TrackPath path, float len, TrackStyle st)
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
                path.FrameAtDistance(d, out var pos, out var rot, out _);
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

        static Mesh BuildTies(TrackPath path, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();
            int count = Mathf.Max(4, Mathf.CeilToInt(len / st.TieStep));
            for (int i = 0; i < count; i++)
            {
                float d = i / (float)count * len;
                path.FrameAtDistance(d, out var pos, out var rot, out _);
                Vector3 center = pos + rot * new Vector3(0f, -st.TieDrop, 0f);
                b.AddBox(center, rot, st.TieSize);
            }
            return b.ToMesh("Ties");
        }

        // ---------------- supports ----------------

        static Mesh BuildSupports(TrackPath path, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();
            int count = Mathf.Max(1, Mathf.CeilToInt(len / st.SupportStep));
            float spineUnderside = st.SpineDrop + st.SpineHeight * 0.5f;

            for (int i = 0; i < count; i++)
            {
                float d = i / (float)count * len;
                path.FrameAtDistance(d, out var pos, out var rot, out _);
                Vector3 attach = pos + rot * new Vector3(0f, -spineUnderside, 0f);
                float h = attach.y - st.GroundY;
                if (h < st.SupportMinClearance) continue; // track hugs the ground here

                // Vertical column: yawed with the track but never rolled (columns stay plumb).
                Vector3 fwd = rot * Vector3.forward;
                float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                Quaternion colRot = Quaternion.Euler(0f, yaw, 0f);

                b.AddBox(new Vector3(attach.x, st.GroundY + h * 0.5f, attach.z), colRot,
                         new Vector3(st.ColumnWidth, h, st.ColumnWidth));

                if (h > 0.45f) // concrete footer pad
                    b.AddBox(new Vector3(attach.x, st.GroundY + 0.09f, attach.z), colRot,
                             new Vector3(0.7f, 0.18f, 0.7f));

                if (h > 0.8f)  // saddle beam hugging the spine (banked with the track)
                    b.AddBox(pos + rot * new Vector3(0f, -(spineUnderside + 0.07f), 0f), rot,
                             new Vector3(1.5f, 0.14f, 0.34f));
            }
            return b.ToMesh("Supports");
        }

        // ---------------- brake fins ----------------

        static Mesh BuildBrakeFins(TrackPath path, float len, TrackStyle st)
        {
            var b = new FlatMeshBuilder();
            List<int> brakes = ResolveBlocks(path, st.BrakeBlocks, new[] { "brake" }, false);
            float endP = Mathf.Min(0.985f, path.BrakePoint01 + 0.05f);

            for (int i = 0; i < brakes.Count; i++)
            {
                BlockSpan(path, len, brakes[i], 0.06f, endP, out var s0, out var s1);
                int fins = Mathf.FloorToInt((s1 - s0) / st.FinStep);
                for (int f = 0; f < fins; f++)
                {
                    float d = Mathf.Repeat(s0 + (f + 0.5f) * st.FinStep, len);
                    path.FrameAtDistance(d, out var pos, out var rot, out _);
                    // Center fin strip: tops out just under the train chassis, pokes above the spine.
                    b.AddBox(pos + rot * new Vector3(0f, -0.21f, 0f), rot,
                             new Vector3(0.055f, 0.22f, 0.5f));
                }
            }
            return b.ToMesh("BrakeFins");
        }

        // ---------------- station running gear ----------------

        static Mesh BuildStationDeck(TrackPath path, float len, TrackStyle st)
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
                    path.FrameAtDistance(d, out var pos, out var rot, out _);
                    // Low center deck the drive tires poke through — the station "running rail".
                    b.AddBox(pos + rot * new Vector3(0f, -0.19f, 0f), rot,
                             new Vector3(0.6f, 0.1f, segLen + 0.05f));
                }
            }
            return b.ToMesh("StationRails");
        }

        static Mesh BuildDriveTires(TrackPath path, float len, TrackStyle st)
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
                    path.FrameAtDistance(d, out var pos, out var rot, out _);
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
