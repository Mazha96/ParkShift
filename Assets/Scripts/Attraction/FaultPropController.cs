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
        public GameObject GhostSensorProp;      // phantom-occupancy beacon on the zone sensor post
        public GameObject BrownoutCabinetProp;  // undervolt warning light on the drive cabinet
        public GameObject JammedCarProp;        // lap-bar jam flag over the flagged car
        [Tooltip("Optional per-car anchors (Load berth check points). When set, the jam flag " +
                 "snaps to the anchor matching the fault's TargetIndex.")]
        public Transform[] JammedCarAnchors;

        void Start()
        {
            // Alarm strobes on the marker-style props (skips the intruder guest and the LOTO
            // tag, which should read as physical objects, not warning lights). FaultStrobe
            // only runs while the prop is active, and its phase is synced to the klaxon.
            AddStrobe(DroppedItemProp);
            AddStrobe(FlaggedSeatProp);
            AddStrobe(CollisionProp);
            AddStrobe(ValleyedProp);
            AddStrobe(GhostSensorProp);
            AddStrobe(BrownoutCabinetProp);
            AddStrobe(JammedCarProp);
        }

        static void AddStrobe(GameObject go)
        {
            if (go != null && go.GetComponent<Juice.FaultStrobe>() == null)
                go.AddComponent<Juice.FaultStrobe>();
        }

        int _flaggedTrain = -1;

        void Update()
        {
            var s = AttractionManager.Instance != null ? AttractionManager.Instance.Sim : null;
            if (s == null) return;
            if (DroppedItemProp) DroppedItemProp.SetActive(s.Faults.HasFault(FaultType.GuestDroppedItem));
            if (FlaggedSeatProp)
            {
                bool flagged = s.Faults.HasFault(FaultType.RestraintNotVerified);
                var platform = s.PlatformTrain();
                // The incident belongs to the loaded RV, which is not always Train0.
                if (flagged && platform != null && _flaggedTrain != platform.Id)
                {
                    foreach (var visual in Object.FindObjectsOfType<Vehicles.TrainVisual>())
                        if (visual.TrainId == platform.Id)
                        {
                            FlaggedSeatProp.transform.SetParent(visual.transform, false);
                            FlaggedSeatProp.transform.localPosition = new Vector3(0.2f, 0.9f, 0f);
                            FlaggedSeatProp.transform.localRotation = Quaternion.identity;
                            _flaggedTrain = platform.Id;
                            break;
                        }
                }
                FlaggedSeatProp.SetActive(flagged && platform != null);
            }
            if (LotoTagProp) LotoTagProp.SetActive(s.LotoEngaged);
            if (IntruderProp) IntruderProp.SetActive(s.Faults.HasFault(FaultType.TrackIntrusion));
            if (CollisionProp) CollisionProp.SetActive(s.Faults.HasFault(FaultType.TrainCollision));
            if (ValleyedProp) ValleyedProp.SetActive(s.Faults.HasFault(FaultType.TrainValleyed));
            if (GhostSensorProp) GhostSensorProp.SetActive(s.Faults.HasFault(FaultType.BlockSensorGhost));
            if (BrownoutCabinetProp) BrownoutCabinetProp.SetActive(s.Faults.HasFault(FaultType.PowerBrownout));
            if (JammedCarProp)
            {
                var jam = s.Faults.Find(FaultType.RestraintJammedCar);
                JammedCarProp.SetActive(jam != null);
                if (jam != null && JammedCarAnchors != null
                    && jam.TargetIndex >= 0 && jam.TargetIndex < JammedCarAnchors.Length
                    && JammedCarAnchors[jam.TargetIndex] != null)
                    JammedCarProp.transform.position = JammedCarAnchors[jam.TargetIndex].position;
            }
        }
    }
}
