using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Red evac lever: thrown (and gently cranked) while the technician is recovering a
    /// valleyed train — i.e. the stall fault is active with LOTO engaged — then springs back up.</summary>
    public class EvacLeverDriver : MonoBehaviour
    {
        public Transform Pivot;
        public Vector3 CrankEuler = new Vector3(-68, 0, 0);
        public float Speed = 4f;
        public float CrankWobbleDegrees = 6f;
        Hinge _h;
        float _phase;

        void Start() { _h = new Hinge(Pivot, CrankEuler); _h.Overshoot = 0.8f; }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || Pivot == null) return;
            var s = m.Sim;
            bool engaged = s.Faults.HasFault(FaultType.TrainValleyed) && s.LotoEngaged;
            _h.Tick(engaged, Time.deltaTime, Speed);
            if (engaged)
            {
                _phase += Time.deltaTime * 6f;
                Pivot.localRotation *= Quaternion.Euler(Mathf.Sin(_phase) * CrankWobbleDegrees, 0f, 0f);
            }
        }
    }
}
