using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ParkShift.Vehicles
{
    /// <summary>
    /// Closed Catmull-Rom spline through the ordered waypoint loop, with an arc-length
    /// lookup table so trains move at uniform spatial speed within a block.
    /// Blocks remain equal contiguous slices of the waypoint list (boundaries sit on waypoints),
    /// so the sim's (blockIndex, progress01) contract is unchanged.
    /// Also computes banking (roll) from horizontal turn rate for the visuals/mesh builder.
    /// </summary>
    public class TrackPath : MonoBehaviour
    {
        public List<Transform> Waypoints = new List<Transform>();
        public int BlockCount = 4;

        [Header("Spline")]
        [Range(2, 32)] public int SamplesPerSegment = 12;

        [Header("Banking")]
        public float MaxBankDegrees = 32f;
        [Tooltip("Degrees of roll per (degree of heading change per meter).")]
        public float BankFactor = 9f;

        [Header("Block Zones (data-driven; tune in inspector)")]
        [Tooltip("Normalized arc position (0..1) of each block's START, ascending. Block 0 usually starts at 0 (station). " +
                 "Count here = number of blocks and overrides BlockCount. Leave empty to fall back to the legacy even split.")]
        public List<float> BlockStarts = new List<float>();
        [Tooltip("Optional display names per block, e.g. Station / Lift / Course A / Brake Run. Cosmetic — used by gizmo labels.")]
        public List<string> BlockLabels = new List<string>();
        [Tooltip("Where inside a block a gated RV brakes to a stop (0..1). Drawn as a gizmo so you can see the brake point. " +
                 "Keep this matched to SimConfig.BrakePoint01 for the visuals to line up.")]
        [Range(0.5f, 1f)] public float BrakePoint01 = 0.92f;

        [Header("Gizmos")]
        public bool DrawBlockGizmos = true;
        public float GizmoMarkerHeight = 4f;

        /// <summary>Number of blocks this path defines — BlockStarts when set, else BlockCount.</summary>
        public int EffectiveBlockCount => (BlockStarts != null && BlockStarts.Count >= 2) ? BlockStarts.Count : BlockCount;

        // Dense arc-length table
        Vector3[] _pts;      // dense sampled spline points (closed loop)
        float[] _cum;        // cumulative distance at each dense sample; _cum[0] = 0
        float _totalLen;
        bool _built;

        public float TotalLength { get { EnsureBuilt(); return _totalLen; } }

        void Awake() { Rebuild(); }

        void EnsureBuilt() { if (!_built) Rebuild(); }

        public void Rebuild()
        {
            int n = Waypoints.Count;
            if (n < 4) { _built = false; return; }

            int dense = n * SamplesPerSegment;
            _pts = new Vector3[dense];
            for (int seg = 0; seg < n; seg++)
            {
                Vector3 p0 = Waypoints[(seg - 1 + n) % n].position;
                Vector3 p1 = Waypoints[seg].position;
                Vector3 p2 = Waypoints[(seg + 1) % n].position;
                Vector3 p3 = Waypoints[(seg + 2) % n].position;
                for (int s = 0; s < SamplesPerSegment; s++)
                    _pts[seg * SamplesPerSegment + s] = CatmullRom(p0, p1, p2, p3, s / (float)SamplesPerSegment);
            }

            _cum = new float[dense + 1];
            for (int i = 0; i < dense; i++)
                _cum[i + 1] = _cum[i] + Vector3.Distance(_pts[i], _pts[(i + 1) % dense]);
            _totalLen = _cum[dense];
            _built = _totalLen > 0.01f;
        }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1)
                + (-p0 + p2) * t
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        /// <summary>Arc distance at a waypoint-index-space parameter u in [0, Waypoints.Count).</summary>
        float UToDistance(float u)
        {
            int dense = _pts.Length;
            float f = Mathf.Repeat(u, Waypoints.Count) * SamplesPerSegment;
            int i = Mathf.FloorToInt(f);
            if (i >= dense) i -= dense;
            float frac = f - Mathf.Floor(f);
            return Mathf.Lerp(_cum[i], _cum[i + 1], frac);
        }

        /// <summary>World position at arc distance d (wrapped).</summary>
        public Vector3 PointAtDistance(float d)
        {
            EnsureBuilt();
            d = Mathf.Repeat(d, _totalLen);
            int lo = 0, hi = _pts.Length; // _cum has length dense+1, monotonic
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_cum[mid] <= d) lo = mid; else hi = mid;
            }
            float span = _cum[lo + 1] - _cum[lo];
            float t = span > 0.0001f ? (d - _cum[lo]) / span : 0f;
            return Vector3.Lerp(_pts[lo], _pts[(lo + 1) % _pts.Length], t);
        }

        /// <summary>Tangent direction (normalized) at arc distance d.</summary>
        public Vector3 TangentAtDistance(float d, float h = 0.35f)
        {
            Vector3 a = PointAtDistance(d - h);
            Vector3 b = PointAtDistance(d + h);
            Vector3 dir = b - a;
            return dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;
        }

        /// <summary>Signed bank roll (degrees) at arc distance d, from horizontal turn rate.</summary>
        public float BankAtDistance(float d)
        {
            const float h = 1.0f;
            Vector3 d0 = TangentAtDistance(d - h);
            Vector3 d1 = TangentAtDistance(d + h);
            d0.y = 0; d1.y = 0;
            float turn = Vector3.SignedAngle(d0, d1, Vector3.up); // degrees over 2h meters
            float degPerMeter = turn / (2f * h);
            return Mathf.Clamp(degPerMeter * BankFactor, -MaxBankDegrees, MaxBankDegrees);
        }

        /// <summary>Full frame at arc distance: position + rotation including banking roll.</summary>
        public void FrameAtDistance(float d, out Vector3 pos, out Quaternion rot, out float bank)
        {
            pos = PointAtDistance(d);
            Vector3 fwd = TangentAtDistance(d);
            bank = BankAtDistance(d);
            rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.AngleAxis(-bank, Vector3.forward);
        }

        /// <summary>Arc distance of the (block, progress01) sim coordinate. Uniform speed inside the block.</summary>
        public float DistanceOf(int block, float progress)
        {
            EnsureBuilt();

            // Data-driven boundaries: each block spans [BlockStarts[i], BlockStarts[i+1]) in arc length.
            if (BlockStarts != null && BlockStarts.Count >= 2)
            {
                int count = BlockStarts.Count;
                block = ((block % count) + count) % count;
                float s0 = Mathf.Repeat(BlockStarts[block], 1f) * _totalLen;
                float s1 = Mathf.Repeat(BlockStarts[(block + 1) % count], 1f) * _totalLen;
                if (s1 <= s0) s1 += _totalLen; // wrap across the loop seam
                return Mathf.Repeat(Mathf.Lerp(s0, s1, Mathf.Clamp01(progress)), _totalLen);
            }

            // Legacy fallback: equal contiguous slices of the waypoint list (boundaries on waypoints).
            float perBlock = Waypoints.Count / (float)BlockCount;
            float d0 = UToDistance(block * perBlock);
            float d1 = UToDistance((block + 1) * perBlock);
            if (d1 <= d0) d1 += _totalLen; // wrap across the loop seam
            return Mathf.Repeat(Mathf.Lerp(d0, d1, Mathf.Clamp01(progress)), _totalLen);
        }

        /// <summary>Maps (blockIndex, progress01) -> world position/rotation (banked).</summary>
        public void Sample(int block, float progress, out Vector3 pos, out Quaternion rot)
        {
            Sample(block, progress, out pos, out rot, out _);
        }

        public void Sample(int block, float progress, out Vector3 pos, out Quaternion rot, out float bank)
        {
            EnsureBuilt();
            if (!_built) { pos = transform.position; rot = Quaternion.identity; bank = 0f; return; }
            FrameAtDistance(DistanceOf(block, progress), out pos, out rot, out bank);
        }

        /// <summary>World position of a block's START boundary (for gizmos / placement tools).</summary>
        public Vector3 BlockStartPosition(int block) => PointAtDistance(DistanceOf(block, 0f));

        /// <summary>Editor helper: fill BlockStarts with an even split for the current EffectiveBlockCount.
        /// Right-click the component header → "Distribute Blocks Evenly", then drag values to taste.</summary>
        [ContextMenu("Distribute Blocks Evenly")]
        public void DistributeBlocksEvenly()
        {
            int count = Mathf.Max(2, EffectiveBlockCount);
            BlockStarts = new List<float>(count);
            for (int i = 0; i < count; i++) BlockStarts.Add(i / (float)count);
        }

        // ---------------- Gizmos ----------------

        void OnDrawGizmos()
        {
            if (!DrawBlockGizmos || Waypoints == null || Waypoints.Count < 4) return;
            foreach (var w in Waypoints) if (w == null) return; // mid-edit safety
            if (Application.isPlaying) EnsureBuilt(); else Rebuild(); // editor: reflect live edits
            if (!_built) return;

            int count = Mathf.Max(2, EffectiveBlockCount);
            for (int b = 0; b < count; b++)
            {
                Color c = BlockColor(b, count);

                // Block body: sample the arc from start→end so the colored ribbon hugs the spline.
                Gizmos.color = c;
                const int seg = 24;
                Vector3 prev = PointAtDistance(DistanceOf(b, 0f));
                for (int s = 1; s <= seg; s++)
                {
                    Vector3 p = PointAtDistance(DistanceOf(b, s / (float)seg));
                    Gizmos.DrawLine(prev, p);
                    prev = p;
                }

                // Boundary marker (vertical post) at the block start.
                Vector3 startPos = PointAtDistance(DistanceOf(b, 0f));
                Vector3 up = Vector3.up * GizmoMarkerHeight;
                Gizmos.color = c;
                Gizmos.DrawLine(startPos, startPos + up);
                Gizmos.DrawSphere(startPos + up, GizmoMarkerHeight * 0.08f);

                // Brake point: where a gated RV coasts to a stop inside this block.
                Vector3 brakePos = PointAtDistance(DistanceOf(b, BrakePoint01));
                Gizmos.color = Color.red;
                Gizmos.DrawLine(brakePos, brakePos + Vector3.up * (GizmoMarkerHeight * 0.6f));

#if UNITY_EDITOR
                string label = (BlockLabels != null && b < BlockLabels.Count && !string.IsNullOrEmpty(BlockLabels[b]))
                    ? BlockLabels[b] : $"Block {b}";
                Handles.color = c;
                Handles.Label(startPos + up * 1.1f, $"{label}");
#endif
            }
        }

        static Color BlockColor(int index, int count)
        {
            float h = count > 0 ? (index / (float)count) : 0f;
            var c = Color.HSVToRGB(Mathf.Repeat(h, 1f), 0.65f, 1f);
            c.a = 1f;
            return c;
        }
    }
}
