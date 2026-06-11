using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Designer-tunable wrapper over SimConfig. Assign to AttractionManager to override defaults.</summary>
    [CreateAssetMenu(menuName = "ParkShift/Sim Config", fileName = "SimConfig")]
    public class SimConfigSO : ScriptableObject
    {
        public float ShiftLengthSeconds = 180f;
        public int SeatsPerTrain = 8;
        public float BlockTraverseSeconds = 6f;
        public float GuestSpawnInterval = 1.6f;
        public float DispatchTimeoutSeconds = 35f;
        [Range(0f, 0.2f)] public float RandomFaultChancePerSecond = 0.022f;
        public float AssistantFixSeconds = 8f;

        public SimConfig ToConfig()
        {
            return new SimConfig
            {
                ShiftLengthSeconds = ShiftLengthSeconds,
                SeatsPerTrain = SeatsPerTrain,
                BlockTraverseSeconds = BlockTraverseSeconds,
                GuestSpawnInterval = GuestSpawnInterval,
                DispatchTimeoutSeconds = DispatchTimeoutSeconds,
                RandomFaultChancePerSecond = RandomFaultChancePerSecond,
                AssistantFixSeconds = AssistantFixSeconds
            };
        }
    }
}
