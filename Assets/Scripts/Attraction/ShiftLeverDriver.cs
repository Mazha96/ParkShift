using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Green open-shift lever: throws to the "on" position when the shift opens
    /// (sim leaves the Closed state) and stays there for the rest of the shift.</summary>
    public class ShiftLeverDriver : MonoBehaviour
    {
        public Transform Pivot;
        public Vector3 OnEuler = new Vector3(-55, 0, 0);
        public float Speed = 5f;
        Hinge _h;

        void Start() { _h = new Hinge(Pivot, OnEuler); _h.Overshoot = 1.3f; }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            bool shiftOpen = m.Sim.State != Core.AttractionState.Closed;
            _h.Tick(shiftOpen, Time.deltaTime, Speed);
        }
    }
}
