using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Lowers/raises the lap bars to mirror the sim train's restraint state.</summary>
    public class TrainRestraintDriver : MonoBehaviour
    {
        public int TrainId;
        public Transform[] Bars;
        public Vector3 ClosedEuler = new Vector3(95, 0, 0);
        Hinge[] _h;
        bool _synced;

        public static bool ShouldOpen(TrainSim train, AttractionSim sim) =>
            train.State == TrainState.InStationUnloading ? sim.RestraintsReleased
                : train.State == TrainState.InStationLoading && !train.RestraintsVerified
                    && (train.Riders == 0 || sim.Guests.GatesOpen);

        void Start()
        {
            _h = new Hinge[Bars != null ? Bars.Length : 0];
            for (int i = 0; i < _h.Length; i++)
            {
                _h[i] = new Hinge(Bars[i], ClosedEuler);
                _h[i].Overshoot = 0.25f; // restrained mechanical settle, no broad swing through riders
            }
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null || TrainId < 0 || TrainId >= m.Sim.Trains.Length) return;
            var t = m.Sim.Trains[TrainId];
            bool barsUp = ShouldOpen(t, m.Sim);
            for (int i = 0; i < _h.Length; i++)
            {
                if (!_synced) _h[i].Snap(!barsUp);
                else _h[i].Tick(!barsUp, Time.deltaTime, 2.5f + i % 4 * 0.12f);
            }
            _synced = true;
        }
    }
}
