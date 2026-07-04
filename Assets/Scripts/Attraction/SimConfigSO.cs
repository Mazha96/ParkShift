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

        [Header("Block layout & RVs (testbed: Station → Lift → Course A/B/C → Brake Run)")]
        [Tooltip("Number of block zones. Must match the TrackPath BlockStarts count on the spline.")]
        public int BlockCount = 6;
        [Tooltip("RVs in the system. 3 cycles cleanly in 6 blocks; set 4 to force deliberate brake-run stacking.")]
        public int TrainCount = 3;
        [Tooltip("Two-position station: block 0 = Unload, block 1 = Load, so two RVs park at once " +
                 "(one dropping riders, one boarding). Off = single-block station (one RV at a time).")]
        public bool SeparateLoadUnload = false;
        [Tooltip("Per-block speed multiplier (>1 = faster). One entry per block. " +
                 "Default: station crawl, fast lift, full course x3, slow brake run.")]
        public float[] BlockSpeedMultipliers = { 1f, 1.6f, 1.15f, 1.15f, 1.15f, 0.55f };

        [Header("Motion feel")]
        [Tooltip("Ramp up from a stop and coast smoothly onto the brake run instead of snapping to speed / hard-stopping.")]
        public bool SmoothMotion = true;
        [Tooltip("Where inside a block a gated RV parks (0..1). Keep matched to TrackPath.BrakePoint01.")]
        [Range(0.5f, 1f)] public float BrakePoint01 = 0.92f;
        [Tooltip("Brake governor deceleration (block-progress units / s^2). Lower = longer lazy coast.")]
        public float BrakeDecel = 0.6f;
        [Tooltip("Launch acceleration from a stop (block-progress units / s^2). Lower = softer roll-out.")]
        public float LaunchAccel = 0.9f;

        public SimConfig ToConfig()
        {
            return new SimConfig
            {
                ShiftLengthSeconds = ShiftLengthSeconds,
                SeatsPerTrain = SeatsPerTrain,
                BlockCount = BlockCount,
                TrainCount = TrainCount,
                SeparateLoadUnload = SeparateLoadUnload,
                BlockTraverseSeconds = BlockTraverseSeconds,
                GuestSpawnInterval = GuestSpawnInterval,
                DispatchTimeoutSeconds = DispatchTimeoutSeconds,
                RandomFaultChancePerSecond = RandomFaultChancePerSecond,
                AssistantFixSeconds = AssistantFixSeconds,
                BlockSpeedMultipliers = BlockSpeedMultipliers,
                SmoothMotion = SmoothMotion,
                BrakePoint01 = BrakePoint01,
                BrakeDecel = BrakeDecel,
                LaunchAccel = LaunchAccel
            };
        }
    }
}
