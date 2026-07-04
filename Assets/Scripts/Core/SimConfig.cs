namespace ParkShift.Core
{
    /// <summary>All tunables in one place. The Unity layer can override via ScriptableObject.</summary>
    public class SimConfig
    {
        public float ShiftLengthSeconds = 180f;     // 3-minute vertical slice
        public int SeatsPerTrain = 8;
        public int BlockCount = 4;                  // 0=Station, 1=Launch, 2=Course, 3=BrakeRun
        public int TrainCount = 2;                  // RVs in the system. Need >= BlockCount-1 to keep cycling without a deadlock.
        public float BlockTraverseSeconds = 6f;     // time to cross one block at full speed

        // ---- Two-position station (Unload then Load) ----
        // Off by default: block 0 is a single station where one RV unloads AND loads in place
        // (original behaviour; keeps the headless tests unchanged). When on, the station spans two
        // blocks — block 0 = Unload, block 1 = Load — so two RVs can park at once: one dropping
        // riders at Unload while another boards at Load. An emptied RV auto-crawls Unload->Load.
        public bool SeparateLoadUnload = false;
        /// <summary>Block where arriving RVs drop their riders. Always 0 (the circuit completes here).</summary>
        public int UnloadBlock => 0;
        /// <summary>Block where RVs board guests and dispatch from. Same as Unload unless the station is split.</summary>
        public int LoadBlock => SeparateLoadUnload ? 1 : 0;
        /// <summary>Where in a station block an RV parks (0..1). 0.5 = block centre, so the Unload and
        /// Load RVs sit a full block apart and never overlap at the shared block boundary. Split only.</summary>
        public float StationParkProgress = 0.5f;
        /// <summary>How long the guest unload takes once restraints are released (stand up + walk out).
        /// The Unload area is considered clear after this, freeing the RV and allowing the door to close.</summary>
        public float UnloadSeconds = 4f;

        // ---- Motion feel (the "stacking up on the brake run" easing) ----
        // Core defaults keep instant motion + a hard stop at the block exit so the headless
        // tests stay deterministic. The Unity layer opts into smoothing for the actual feel,
        // mirroring how BlockSpeedMultipliers is left null by default.
        /// <summary>When true, RVs ramp up from a stop and coast smoothly to a halt at the
        /// block brake point instead of snapping to full speed / hard-stopping at the exit.</summary>
        public bool SmoothMotion = false;
        /// <summary>Where inside a block a gated RV brakes to a stop (0..1 progress).
        /// 1 = stop right at the exit (legacy). ~0.9 leaves it parked short of the next block.</summary>
        public float BrakePoint01 = 1f;
        /// <summary>Deceleration used by the brake governor, in block-progress units / s^2.
        /// Higher = later, harder braking; lower = long lazy coast onto the brake run.</summary>
        public float BrakeDecel = 0.6f;
        /// <summary>Acceleration when launching from a stop, in block-progress units / s^2.
        /// Smaller = softer roll-out of the station; large = effectively instant.</summary>
        public float LaunchAccel = 0.9f;
        public float GuestSpawnInterval = 1.6f;     // seconds per guest into queue
        public int QueueCapacity = 40;
        public float GuestBoardSeconds = 0.35f;     // per guest boarding time
        public float DispatchTimeoutSeconds = 35f;  // loitering in station triggers fault
        public float RandomFaultChancePerSecond = 0.022f;
        public float SatisfactionDrainPerQueued = 0.05f;  // per guest per second while waiting
        public float SatisfactionDrainFaulted = 1.2f;     // per second while ride is down
        public float SatisfactionPerRider = 0.8f;         // gain per completed rider
        public float AssistantFixSeconds = 8f;            // "call remote tech" auto-fix delay
        public float TechFaultChancePerSecond = 0.03f;    // major incident rate while in tech scenario mode

        // ---- Extended fault catalog (off by default — behavior-preserving) ----
        /// <summary>When true, the three extended faults (block sensor ghost, power brownout,
        /// restraint jam) join the random pool. Off by default so legacy runs are byte-identical.</summary>
        public bool ExtendedFaultsEnabled = false;
        /// <summary>Cars per train — the restraint-jam fault picks one of these as its target.</summary>
        public int CarsPerTrain = 4;
        /// <summary>Seconds each brownout restart stage must stabilize before the next is accepted.</summary>
        public float BrownoutStageSeconds = 2f;
        /// <summary>Staged-restart steps required to clear a power brownout.</summary>
        public int BrownoutStageCount = 3;

        // ---- Escalating difficulty (flat by default — behavior-preserving) ----
        /// <summary>Random fault chance ramps linearly to base*this by shift end. 1 = flat (legacy).</summary>
        public float FaultRampMultiplier = 1f;
        /// <summary>Guest arrival rate ramps linearly to base*this by shift end. 1 = flat (legacy).</summary>
        public float GuestArrivalRampMultiplier = 1f;
        /// <summary>Periodic guest surge waves (deterministic, time-based windows).</summary>
        public bool SurgeWavesEnabled = false;
        /// <summary>A surge window opens at the end of every period of this length.</summary>
        public float SurgeWavePeriodSeconds = 45f;
        /// <summary>How long each surge window lasts.</summary>
        public float SurgeWaveDurationSeconds = 8f;
        /// <summary>Guest arrival rate multiplier while a surge window is open.</summary>
        public float SurgeWaveSpawnMultiplier = 3f;

        // ---- Scoring depth ----
        /// <summary>Bonus points per streak step on consecutive clean dispatches. 0 disables the combo.</summary>
        public int ComboBonusPerStreak = 5;
        /// <summary>Streak steps that keep earning bonus (caps the combo multiplier).</summary>
        public int ComboStreakCap = 5;
        /// <summary>Satisfaction recovered each time a train dispatches (smooth-operation reward).</summary>
        public float SatisfactionPerSmoothDispatch = 1.5f;
        /// <summary>Platform idle seconds before the stalled-queue satisfaction drain kicks in.</summary>
        public float StallGraceSeconds = 12f;
        /// <summary>Extra satisfaction drain per second while the platform train sits stalled.</summary>
        public float SatisfactionStallDrainPerSecond = 0.15f;

        /// <summary>Optional per-block speed multiplier (>1 = faster). Index = block.
        /// Null or short array falls back to 1.0 — keeps default timing and tests unchanged.</summary>
        public float[] BlockSpeedMultipliers;

        /// <summary>Effective seconds to traverse a given block.</summary>
        public float TraverseSecondsFor(int block)
        {
            if (BlockSpeedMultipliers == null || block < 0 || block >= BlockSpeedMultipliers.Length)
                return BlockTraverseSeconds;
            float m = BlockSpeedMultipliers[block];
            return m > 0.01f ? BlockTraverseSeconds / m : BlockTraverseSeconds;
        }

        public static SimConfig Solo()
        {
            return new SimConfig
            {
                RandomFaultChancePerSecond = 0.012f,
                GuestSpawnInterval = 2.2f,
                DispatchTimeoutSeconds = 50f,
                SatisfactionDrainFaulted = 0.8f
            };
        }

        public static SimConfig Coop(int playerCount)
        {
            var c = new SimConfig();
            c.RandomFaultChancePerSecond *= 1f + 0.35f * (playerCount - 1);
            c.GuestSpawnInterval /= 1f + 0.2f * (playerCount - 1);
            return c;
        }
    }
}
