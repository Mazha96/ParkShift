using System;

namespace ParkShift.Core
{
    /// <summary>One ride vehicle. Movement is "progress through current block";
    /// the Unity layer maps (CurrentBlock, Progress) onto waypoints for visuals.</summary>
    public sealed class TrainSim
    {
        public int Id { get; }
        public TrainState State { get; private set; }
        public int CurrentBlock { get; private set; }
        public float Progress { get; private set; }   // 0..1 within current block
        public int Riders { get; private set; }
        public bool RestraintsVerified { get; private set; }
        public float StationIdleSeconds { get; private set; }

        /// <summary>Current speed in block-progress units / second (0 when parked).
        /// Presentation can read this for brake-light / lean FX; logic ignores it.</summary>
        public float Speed { get; private set; }

        /// <summary>Split station only: false while this RV is dropping guests at Unload. The RV will not
        /// crawl to Load until the operator has released restraints and the unload area is clear (set by
        /// AttractionSim). Always true for the single-block station so behaviour there is unchanged.</summary>
        public bool UnloadComplete = true;

        readonly BlockSystem _blocks;
        public event Action<TrainSim> OnArrivedStation;
        public event Action<TrainSim> OnHoldingChanged;

        public TrainSim(int id, BlockSystem blocks, int startBlock, int loadBlock = 0, float stationPark = 0f)
        {
            Id = id; _blocks = blocks;
            CurrentBlock = startBlock;
            _blocks.ForcePlace(startBlock, id);
            bool atStation = startBlock == loadBlock;
            State = atStation ? TrainState.InStationLoading : TrainState.HoldingAtBlock;
            Progress = atStation ? stationPark : 1f; // station RVs park (centre when split); staged RVs wait at end-of-block
        }

        public void Board(int guests) { Riders += guests; RestraintsVerified = false; }

        public int Unload(SimConfig cfg = null)
        {
            int r = Riders; Riders = 0; RestraintsVerified = false;
            if (State == TrainState.InStationUnloading)
            {
                // Split station: stay parked at the Unload centre (Tick crawls to Load once it frees).
                // Single station: board in place at block 0 (original behaviour).
                if (cfg == null || !cfg.SeparateLoadUnload) State = TrainState.InStationLoading;
            }
            return r;
        }

        public void VerifyRestraints() { RestraintsVerified = true; if (State == TrainState.InStationLoading) State = TrainState.ReadyToDispatch; }
        public void InvalidateRestraints() { RestraintsVerified = false; if (State == TrainState.ReadyToDispatch) State = TrainState.InStationLoading; }

        public bool Dispatch()
        {
            if (State != TrainState.ReadyToDispatch) return false;
            State = TrainState.Running;
            Speed = 0f;            // roll out from the parked spot (block start, or block centre when split)
            StationIdleSeconds = 0f;
            return true;
        }

        /// <summary>Per-RV gating question: may this RV advance past its current block's exit?
        /// Yes only when the NEXT block downstream is clear — the N+1 buffer rule.</summary>
        public bool MayAdvance() => _blocks.IsClear(_blocks.NextBlock(CurrentBlock));

        /// <summary>Advance simulation. Returns true if the train just completed a circuit (arrived station).</summary>
        public bool Tick(float dt, SimConfig cfg, bool ridePowered)
        {
            if (State == TrainState.InStationLoading || State == TrainState.ReadyToDispatch)
            {
                StationIdleSeconds += dt;
                return false;
            }
            if (State == TrainState.InStationUnloading)
            {
                // Split station: park at the Unload centre and wait until guests have cleared
                // (UnloadComplete, set by the operator workflow) AND the Load block frees, then crawl
                // across. (In single-station mode this state is cleared the same tick by Unload().)
                if (cfg.SeparateLoadUnload && ridePowered && UnloadComplete && MayAdvance()) State = TrainState.Running;
                else { StationIdleSeconds += dt; return false; }
            }
            if (!ridePowered) return false; // ride stop / LOTO freezes motion
            if (State != TrainState.Running && State != TrainState.HoldingAtBlock) return false;

            float traverse = cfg.TraverseSecondsFor(CurrentBlock);
            bool mayAdvance = MayAdvance();

            if (cfg.SmoothMotion)
                StepEased(dt, traverse, mayAdvance, cfg);
            else
                StepInstant(dt, traverse, mayAdvance);

            // Reaching the end of the block: hand off to the next zone if we're allowed in.
            if (Progress >= 1f)
            {
                int next = _blocks.NextBlock(CurrentBlock);
                if (_blocks.TryEnter(next, Id))
                {
                    bool wasHolding = State == TrainState.HoldingAtBlock;
                    _blocks.Leave(CurrentBlock, Id);
                    CurrentBlock = next;
                    // Station RVs park at the block centre when split (so Unload + Load sit a full block
                    // apart and never overlap); everything else starts at the block entry.
                    bool stationPark = cfg.SeparateLoadUnload
                                    && (CurrentBlock == cfg.UnloadBlock || CurrentBlock == cfg.LoadBlock);
                    Progress = stationPark ? cfg.StationParkProgress : 0f;
                    if (CurrentBlock == 0)
                    {
                        // Unload block (also the single-station spot): a circuit completed — drop riders.
                        State = TrainState.InStationUnloading;
                        StationIdleSeconds = 0f;
                        Speed = 0f;
                        OnArrivedStation?.Invoke(this);
                        return true;
                    }
                    if (cfg.SeparateLoadUnload && CurrentBlock == cfg.LoadBlock)
                    {
                        // Reached the Load block: park at centre and board; only Dispatch moves it onward.
                        State = TrainState.InStationLoading;
                        StationIdleSeconds = 0f;
                        Speed = 0f;
                        if (wasHolding) OnHoldingChanged?.Invoke(this);
                        return false;
                    }
                    // Normal course block.
                    if (wasHolding) { State = TrainState.Running; OnHoldingChanged?.Invoke(this); }
                }
                else
                {
                    // Next block taken — park at the exit and hold (only reached when BrakePoint01 == 1).
                    Progress = 1f;
                    Speed = 0f;
                    if (State != TrainState.HoldingAtBlock) { State = TrainState.HoldingAtBlock; OnHoldingChanged?.Invoke(this); }
                }
            }
            return false;
        }

        /// <summary>Client mirror setter (multiplayer presentation only): stamp a replicated pose
        /// onto this never-ticked RV. When the block changes, occupancy is re-registered with the
        /// Vacate + ForcePlace pair (the same hooks used for spawn/despawn) so MayAdvance() and
        /// the PlatformTrain()/UnloadingTrain() lookups stay truthful on clients. Never called
        /// when authoritative, so solo/host/headless behavior is untouched.</summary>
        public void MirrorPose(int block, float progress, TrainState state, float speed)
        {
            if (block < 0 || block >= _blocks.BlockCount) return; // config mismatch — ignore
            if (block != CurrentBlock)
            {
                _blocks.Vacate(Id);
                _blocks.ForcePlace(block, Id);
                CurrentBlock = block;
            }
            Progress = progress;
            State = state;
            Speed = speed;
        }

        /// <summary>Snapshot occupancy without running boarding/unload gameplay side effects.</summary>
        public void MirrorOccupants(int riders, bool verified)
        {
            Riders = System.Math.Max(0, riders);
            RestraintsVerified = verified;
        }

        /// <summary>Legacy motion: instant full speed, hard stop at the block exit when gated.
        /// Kept byte-for-byte so the headless core tests stay deterministic.</summary>
        void StepInstant(float dt, float traverse, bool mayAdvance)
        {
            Speed = traverse > 0.01f ? 1f / traverse : 0f;
            if (State == TrainState.Running)
                Progress += dt / traverse;
            // Holding at exit but the way cleared: resume so the end-of-block handoff can fire.
            if (State == TrainState.HoldingAtBlock && mayAdvance) { State = TrainState.Running; OnHoldingChanged?.Invoke(this); }
        }

        /// <summary>Eased motion: ramp up from a stop, and when the next block is occupied,
        /// run a brake governor so the RV coasts to a halt exactly on the block's brake point —
        /// this is what makes RVs stack up nose-to-tail on the brake run when over-dispatched.</summary>
        void StepEased(float dt, float traverse, bool mayAdvance, SimConfig cfg)
        {
            float vFull = traverse > 0.01f ? 1f / traverse : 0f;   // progress units / s at full line speed
            float brakePoint = cfg.BrakePoint01 <= 0f ? 1f : Math.Min(1f, cfg.BrakePoint01);

            float vTarget;
            if (mayAdvance)
            {
                vTarget = vFull;
                if (State == TrainState.HoldingAtBlock) { State = TrainState.Running; OnHoldingChanged?.Invoke(this); }
            }
            else
            {
                // Speed ceiling that still lets us stop in the remaining distance: v = sqrt(2*a*d).
                float remain = Math.Max(0f, brakePoint - Progress);
                float vGov = (float)Math.Sqrt(2f * Math.Max(0.0001f, cfg.BrakeDecel) * remain);
                vTarget = Math.Min(vFull, vGov);
            }

            // Accelerate up gently; follow the governor straight down (it is already smooth).
            if (vTarget > Speed) Speed = Math.Min(vTarget, Speed + Math.Max(0.0001f, cfg.LaunchAccel) * dt);
            else                 Speed = vTarget;

            Progress += Speed * dt;

            // Coast-to-stop arrival: park on the brake point and hold (don't overshoot into the next block).
            if (!mayAdvance && Progress >= brakePoint && brakePoint < 1f)
            {
                Progress = brakePoint;
                Speed = 0f;
                if (State != TrainState.HoldingAtBlock) { State = TrainState.HoldingAtBlock; OnHoldingChanged?.Invoke(this); }
            }
        }
    }
}
