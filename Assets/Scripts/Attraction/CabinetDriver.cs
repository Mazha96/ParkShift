using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Drive cabinet: door opens while there is technician business (LOTO engaged or a
    /// drive/collision fault); breaker lever drops when LOTO is engaged.</summary>
    public class CabinetDriver : MonoBehaviour
    {
        public Transform Door, Breaker, ResetButton;
        public Vector3 DoorOpenEuler = new Vector3(0, 0, 115);
        public Vector3 BreakerOffEuler = new Vector3(-70, 0, 0);
        public PressPulse ResetPress = new PressPulse();
        Hinge _door, _breaker;
        bool _hadDriveFault;

        void Start()
        {
            _door = new Hinge(Door, DoorOpenEuler);
            _breaker = new Hinge(Breaker, BreakerOffEuler);
            ResetPress.T = ResetButton;
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            var s = m.Sim;
            bool busy = s.LotoEngaged
                     || s.Faults.HasFault(FaultType.MaintenanceResetRequired)
                     || s.Faults.HasFault(FaultType.TrainCollision);
            _door.Tick(busy, Time.deltaTime, 2.5f);
            _breaker.Tick(s.LotoEngaged, Time.deltaTime, 5f);

            // Reset button depresses the moment a drive/collision fault clears (the reset itself).
            bool driveFault = s.Faults.HasFault(FaultType.MaintenanceResetRequired)
                           || s.Faults.HasFault(FaultType.TrainCollision);
            if (_hadDriveFault && !driveFault) ResetPress.Trigger();
            _hadDriveFault = driveFault;
            ResetPress.Tick(Time.deltaTime);
        }
    }
}
