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

        readonly BlockSystem _blocks;
        public event Action<TrainSim> OnArrivedStation;
        public event Action<TrainSim> OnHoldingChanged;

        public TrainSim(int id, BlockSystem blocks, int startBlock)
        {
            Id = id; _blocks = blocks;
            CurrentBlock = startBlock;
            _blocks.ForcePlace(startBlock, id);
            State = startBlock == 0 ? TrainState.InStationLoading : TrainState.HoldingAtBlock;
            Progress = startBlock == 0 ? 0f : 1f; // staged trains wait at end-of-block, ready to advance
        }

        public void Board(int guests) { Riders += guests; RestraintsVerified = false; }

        public int Unload()
        {
            int r = Riders; Riders = 0; RestraintsVerified = false;
            if (State == TrainState.InStationUnloading) State = TrainState.InStationLoading;
            return r;
        }

        public void VerifyRestraints() { RestraintsVerified = true; if (State == TrainState.InStationLoading) State = TrainState.ReadyToDispatch; }
        public void InvalidateRestraints() { RestraintsVerified = false; if (State == TrainState.ReadyToDispatch) State = TrainState.InStationLoading; }

        public bool Dispatch()
        {
            if (State != TrainState.ReadyToDispatch) return false;
            State = TrainState.Running;
            Progress = 0f;
            StationIdleSeconds = 0f;
            return true;
        }

        /// <summary>Advance simulation. Returns true if the train just completed a circuit (arrived station).</summary>
        public bool Tick(float dt, float blockTraverseSeconds, bool ridePowered)
        {
            if (State == TrainState.InStationLoading || State == TrainState.ReadyToDispatch)
            {
                StationIdleSeconds += dt;
                return false;
            }
            if (!ridePowered) return false; // ride stop / LOTO freezes motion

            if (State == TrainState.Running || State == TrainState.HoldingAtBlock)
            {
                if (State == TrainState.Running)
                    Progress += dt / blockTraverseSeconds;

                if (Progress >= 1f)
                {
                    int next = _blocks.NextBlock(CurrentBlock);
                    if (_blocks.TryEnter(next, Id))
                    {
                        _blocks.Leave(CurrentBlock, Id);
                        CurrentBlock = next;
                        Progress = 0f;
                        if (State == TrainState.HoldingAtBlock) { State = TrainState.Running; OnHoldingChanged?.Invoke(this); }
                        if (CurrentBlock == 0)
                        {
                            State = TrainState.InStationUnloading;
                            StationIdleSeconds = 0f;
                            OnArrivedStation?.Invoke(this);
                            return true;
                        }
                    }
                    else if (State == TrainState.Running)
                    {
                        Progress = 1f;
                        State = TrainState.HoldingAtBlock;   // stacking pressure
                        OnHoldingChanged?.Invoke(this);
                    }
                }
            }
            return false;
        }
    }
}
