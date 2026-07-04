using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Shows/hides physical fault props (dropped item, flagged seat marker, LOTO tag).</summary>
    public class FaultPropController : MonoBehaviour
    {
        public GameObject DroppedItemProp;
        public GameObject FlaggedSeatProp;
        public GameObject LotoTagProp;
        public GameObject IntruderProp;     // guest standing on the track
        public GameObject CollisionProp;    // contact-point marker on the brake run approach
        public GameObject ValleyedProp;     // stall marker out on the course

        void Update()
        {
            var s = AttractionManager.Instance != null ? AttractionManager.Instance.Sim : null;
            if (s == null) return;
            if (DroppedItemProp) DroppedItemProp.SetActive(s.Faults.HasFault(FaultType.GuestDroppedItem));
            if (FlaggedSeatProp) FlaggedSeatProp.SetActive(s.Faults.HasFault(FaultType.RestraintNotVerified));
            if (LotoTagProp) LotoTagProp.SetActive(s.LotoEngaged);
            if (IntruderProp) IntruderProp.SetActive(s.Faults.HasFault(FaultType.TrackIntrusion));
            if (CollisionProp) CollisionProp.SetActive(s.Faults.HasFault(FaultType.TrainCollision));
            if (ValleyedProp) ValleyedProp.SetActive(s.Faults.HasFault(FaultType.TrainValleyed));
        }
    }
}
