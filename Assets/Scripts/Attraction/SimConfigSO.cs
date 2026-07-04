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

        [Header("Extended faults & escalation")]
        [Tooltip("Adds the three extended faults (block sensor ghost, power brownout, restraint jam) to the random pool.")]
        public bool ExtendedFaultsEnabled = false;
        [Tooltip("Cars per train — the restraint-jam fault flags one of these as its target.")]
        public int CarsPerTrain = 4;
        [Tooltip("Seconds each brownout restart stage must stabilize before the next is accepted.")]
        public float BrownoutStageSeconds = 2f;
        [Tooltip("Staged-restart steps required to clear a power brownout.")]
        public int BrownoutStageCount = 3;
        [Tooltip("Random fault chance ramps linearly to base*this by shift end. 1 = flat (legacy).")]
        public float FaultRampMultiplier = 1f;
        [Tooltip("Guest arrival rate ramps linearly to base*this by shift end. 1 = flat (legacy).")]
        public float GuestArrivalRampMultiplier = 1f;
        [Tooltip("Periodic guest surge waves (deterministic time windows).")]
        public bool SurgeWavesEnabled = false;
        [Tooltip("A surge window opens at the end of every period of this length (seconds).")]
        public float SurgeWavePeriodSeconds = 45f;
        [Tooltip("How long each surge window lasts (seconds).")]
        public float SurgeWaveDurationSeconds = 8f;
        [Tooltip("Guest arrival rate multiplier while a surge window is open.")]
        public float SurgeWaveSpawnMultiplier = 3f;

        [Header("Scoring depth")]
        [Tooltip("Bonus points per streak step on consecutive clean dispatches. 0 disables the combo.")]
        public int ComboBonusPerStreak = 5;
        [Tooltip("Streak steps that keep earning bonus (caps the combo multiplier).")]
        public int ComboStreakCap = 5;
        [Tooltip("Satisfaction recovered on each dispatch (smooth-operation reward).")]
        public float SatisfactionPerSmoothDispatch = 1.5f;
        [Tooltip("Platform idle seconds before the stalled-queue satisfaction drain kicks in.")]
        public float StallGraceSeconds = 12f;
        [Tooltip("Extra satisfaction drain per second while the platform train sits stalled.")]
        public float SatisfactionStallDrainPerSecond = 0.15f;

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
                LaunchAccel = LaunchAccel,
                ExtendedFaultsEnabled = ExtendedFaultsEnabled,
                CarsPerTrain = CarsPerTrain,
                BrownoutStageSeconds = BrownoutStageSeconds,
                BrownoutStageCount = BrownoutStageCount,
                FaultRampMultiplier = FaultRampMultiplier,
                GuestArrivalRampMultiplier = GuestArrivalRampMultiplier,
                SurgeWavesEnabled = SurgeWavesEnabled,
                SurgeWavePeriodSeconds = SurgeWavePeriodSeconds,
                SurgeWaveDurationSeconds = SurgeWaveDurationSeconds,
                SurgeWaveSpawnMultiplier = SurgeWaveSpawnMultiplier,
                ComboBonusPerStreak = ComboBonusPerStreak,
                ComboStreakCap = ComboStreakCap,
                SatisfactionPerSmoothDispatch = SatisfactionPerSmoothDispatch,
                StallGraceSeconds = StallGraceSeconds,
                SatisfactionStallDrainPerSecond = SatisfactionStallDrainPerSecond
            };
        }
    }
}
