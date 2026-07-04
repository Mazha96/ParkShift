using System.Collections.Generic;
using UnityEngine;
using ParkShift.Core;
using ParkShift.Attraction;

namespace ParkShift.Vehicles
{
    /// <summary>Renders one sim train onto the track spline. Pure presentation — no game logic.
    /// Follows the sim by smoothing its arc DISTANCE along the spline (never cuts corners),
    /// and applies banking roll from track curvature.
    ///
    /// Extras (all auto-discovered, all optional — a bare FBX body works exactly as before):
    /// - Articulation: children named "Car0", "Car1", ... are each posed at their own arc
    ///   distance (front car on the sim position, followers CarSpacing behind), so multi-car
    ///   trains bend through curves instead of moving as one rigid slab.
    /// - Wheel spin: children named "WheelAxle*" roll about their local X from arc speed.
    /// - Restraint fallback: if no TrainRestraintDriver is present, children named
    ///   "RestraintBar*" are eased between rest (open) and rest * RestraintClosedEuler
    ///   (closed), mirroring the sim exactly like the driver does.</summary>
    public class TrainVisual : MonoBehaviour
    {
        public int TrainId;
        public TrackPath Path;
        public Transform[] SeatAnchors; // filled by scene builder; guests parent here visually

        [Tooltip("How quickly the visual catches up to the sim position (1/s).")]
        public float FollowSharpness = 8f;

        [Header("Articulated cars (auto-discovered children named Car0..CarN if empty)")]
        public Transform[] Cars;
        [Tooltip("Arc-length spacing between car pivots along the spline.")]
        public float CarSpacing = TrainCarFactory.CarSpacing;

        [Header("Wheel spin (auto-discovered children named WheelAxle*)")]
        public float WheelRadius = 0.16f;

        [Header("Restraint fallback (used only when no TrainRestraintDriver exists)")]
        public Vector3 RestraintClosedEuler = new Vector3(95f, 0f, 0f);
        public float RestraintSpeed = 2.5f;

        float _dist;          // current smoothed arc distance
        bool _init;

        Transform[] _axles;
        Quaternion[] _axleRest;
        float _wheelAngle;

        Transform[] _bars;    // null when a TrainRestraintDriver owns the bars
        Quaternion[] _barRest;
        float _barT;          // 0 = open (rest), 1 = closed

        void Start()
        {
            if (Cars == null || Cars.Length == 0)
            {
                var cars = new List<Transform>();
                for (int i = 0; i < 16; i++)
                {
                    var c = transform.Find("Car" + i);
                    if (c == null) break;
                    cars.Add(c);
                }
                Cars = cars.ToArray();
            }

            var axles = new List<Transform>();
            CollectByPrefix(transform, "WheelAxle", axles);
            _axles = axles.ToArray();
            _axleRest = new Quaternion[_axles.Length];
            for (int i = 0; i < _axles.Length; i++) _axleRest[i] = _axles[i].localRotation;

            // Restraint fallback only when nothing else drives the bars (FBX trains get a
            // TrainRestraintDriver from the scene builder — that keeps ownership).
            if (GetComponentInChildren<TrainRestraintDriver>(true) == null)
            {
                var bars = new List<Transform>();
                CollectByPrefix(transform, "RestraintBar", bars);
                if (bars.Count > 0)
                {
                    _bars = bars.ToArray();
                    _barRest = new Quaternion[_bars.Length];
                    for (int i = 0; i < _bars.Length; i++) _barRest[i] = _bars[i].localRotation;
                }
            }
        }

        static void CollectByPrefix(Transform t, string prefix, List<Transform> into)
        {
            foreach (Transform c in t)
            {
                if (c.name.StartsWith(prefix)) into.Add(c);
                CollectByPrefix(c, prefix, into);
            }
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || Path == null || m.Sim == null) return;
            if (TrainId < 0 || TrainId >= m.Sim.Trains.Length) return;
            var t = m.Sim.Trains[TrainId];

            float target = Path.DistanceOf(t.CurrentBlock, t.Progress);
            float len = Path.TotalLength;
            if (len <= 0.01f) return;

            if (!_init) { _dist = target; _init = true; }

            // shortest signed gap around the loop, then exponential (framerate-independent) catch-up
            float gap = Mathf.Repeat(target - _dist + len * 0.5f, len) - len * 0.5f;
            float step = gap * (1f - Mathf.Exp(-FollowSharpness * Time.deltaTime));
            _dist = Mathf.Repeat(_dist + step, len);

            Path.FrameAtDistance(_dist, out var pos, out var rot, out _);
            transform.position = pos;
            transform.rotation = rot;

            // Articulate follower cars at their own arc distances so the train bends and
            // banks through curves per-car (children, so world pose assignment is safe).
            if (Cars != null)
            {
                for (int i = 0; i < Cars.Length; i++)
                {
                    if (Cars[i] == null) continue;
                    if (i == 0) { Cars[i].position = pos; Cars[i].rotation = rot; continue; }
                    Path.FrameAtDistance(_dist - i * CarSpacing, out var cp, out var cr, out _);
                    Cars[i].position = cp;
                    Cars[i].rotation = cr;
                }
            }

            // Roll the wheels from actual arc movement (direction-aware).
            if (_axles != null && _axles.Length > 0 && WheelRadius > 0.01f)
            {
                _wheelAngle = Mathf.Repeat(_wheelAngle + (step / WheelRadius) * Mathf.Rad2Deg, 360f);
                var spin = Quaternion.Euler(_wheelAngle, 0f, 0f);
                for (int i = 0; i < _axles.Length; i++)
                    if (_axles[i] != null) _axles[i].localRotation = _axleRest[i] * spin;
            }

            // Restraint fallback: same open/closed rule as TrainRestraintDriver.
            if (_bars != null)
            {
                bool barsUp = (t.State == TrainState.InStationLoading || t.State == TrainState.InStationUnloading)
                              && !t.RestraintsVerified;
                _barT = Mathf.MoveTowards(_barT, barsUp ? 0f : 1f, RestraintSpeed * Time.deltaTime);
                var swing = Quaternion.Euler(RestraintClosedEuler * Mathf.SmoothStep(0f, 1f, _barT));
                for (int i = 0; i < _bars.Length; i++)
                    if (_bars[i] != null) _bars[i].localRotation = _barRest[i] * swing;
            }
        }

        public TrainState State => AttractionManager.Instance.Sim.Trains[TrainId].State;
    }
}
// (procedural coaster overhaul)
