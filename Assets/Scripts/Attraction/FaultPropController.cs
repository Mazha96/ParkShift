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

        void Update()
        {
            var s = AttractionManager.Instance != null ? AttractionManager.Instance.Sim : null;
            if (s == null) return;
            if (DroppedItemProp) DroppedItemProp.SetActive(s.Faults.HasFault(FaultType.GuestDroppedItem));
            if (FlaggedSeatProp) FlaggedSeatProp.SetActive(s.Faults.HasFault(FaultType.RestraintNotVerified));
            if (LotoTagProp) LotoTagProp.SetActive(s.LotoEngaged);
        }
    }
}
