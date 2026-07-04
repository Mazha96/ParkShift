using UnityEngine;
using ParkShift.Core;
using ParkShift.Attraction;

namespace ParkShift.Vehicles
{
    /// <summary>Renders one sim train onto the track spline. Pure presentation — no game logic.
    /// Follows the sim by smoothing its arc DISTANCE along the spline (never cuts corners),
    /// and applies banking roll from track curvature.</summary>
    public class TrainVisual : MonoBehaviour
    {
        public int TrainId;
        public TrackPath Path;
        public Transform[] SeatAnchors; // filled by scene builder; guests parent here visually

        [Tooltip("How quickly the visual catches up to the sim position (1/s).")]
        public float FollowSharpness = 8f;

        float _dist;          // current smoothed arc distance
        bool _init;

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || Path == null) return;
            var t = m.Sim.Trains[TrainId];

            float target = Path.DistanceOf(t.CurrentBlock, t.Progress);
            float len = Path.TotalLength;
            if (len <= 0.01f) return;

            if (!_init) { _dist = target; _init = true; }

            // shortest signed gap around the loop, then exponential (framerate-independent) catch-up
            float gap = Mathf.Repeat(target - _dist + len * 0.5f, len) - len * 0.5f;
            _dist = Mathf.Repeat(_dist + gap * (1f - Mathf.Exp(-FollowSharpness * Time.deltaTime)), len);

            Path.FrameAtDistance(_dist, out var pos, out var rot, out _);
            transform.position = pos;
            transform.rotation = rot;
        }

        public TrainState State => AttractionManager.Instance.Sim.Trains[TrainId].State;
    }
}
