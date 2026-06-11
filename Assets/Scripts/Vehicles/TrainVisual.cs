using UnityEngine;
using ParkShift.Core;
using ParkShift.Attraction;

namespace ParkShift.Vehicles
{
    /// <summary>Renders one sim train onto the track path. Pure presentation — no game logic.</summary>
    public class TrainVisual : MonoBehaviour
    {
        public int TrainId;
        public TrackPath Path;
        public Transform[] SeatAnchors; // filled by scene builder; guests parent here visually

        Vector3 _targetPos; Quaternion _targetRot;

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || Path == null) return;
            var t = m.Sim.Trains[TrainId];
            Path.Sample(t.CurrentBlock, t.Progress, out _targetPos, out _targetRot);
            transform.position = Vector3.Lerp(transform.position, _targetPos, Time.deltaTime * 10f);
            transform.rotation = Quaternion.Slerp(transform.rotation, _targetRot, Time.deltaTime * 10f);
        }

        public TrainState State => AttractionManager.Instance.Sim.Trains[TrainId].State;
    }
}
