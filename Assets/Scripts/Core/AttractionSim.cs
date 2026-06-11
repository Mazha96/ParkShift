using System;

namespace ParkShift.Core
{
    /// <summary>
    /// Engine-agnostic heart of Cosmic Conveyor: state machine, two trains, blocks,
    /// guests, faults, scoring, shift clock. The Unity layer renders this and feeds
    /// player verbs in; in multiplayer this runs server-side only.
    /// </summary>
    public sealed class AttractionSim
    {
        public AttractionState State { get; private set; } = AttractionState.Closed;
        public readonly SimConfig Config;
        public readonly BlockSystem Blocks;
        public readonly TrainSim[] Trains;
        public readonly GuestSim Guests;
        public readonly FaultSystem Faults;
        public readonly ScoreSystem Score = new ScoreSystem();
        public readonly Telemetry Telemetry = new Telemetry();

        public float SimTime { get; private set; }
        public float ShiftRemaining => Math.Max(0f, Config.ShiftLengthSeconds - SimTime);
        public bool LotoEngaged { get; private set; }
        public bool RidePowered => !LotoEngaged && !Faults.AnyStopsRide && State != AttractionState.Closed;

        public event Action<AttractionState> OnStateChanged;
        public event Action OnShiftEnded;

        bool _stackingFaultLatched;
        bool _timeoutFaultLatched;
        float _holdingSeconds;
        const float StackingGraceSeconds = 4f;

        public AttractionSim(SimConfig cfg, int seed = 0)
        {
            Config = cfg;
            Blocks = new BlockSystem(cfg.BlockCount);
            Guests = new GuestSim(cfg);
            Faults = new FaultSystem(cfg, seed);
            Trains = new TrainSim[2];
            Trains[0] = new TrainSim(0, Blocks, 0);                    // platform train
            Trains[1] = new TrainSim(1, Blocks, cfg.BlockCount - 1);   // staged on brake run
            Faults.OnFaultTriggered += f =>
            {
                Guests.Penalize(f.Def.SatisfactionHit);
                Score.AddPenalty(f.Def.ScorePenalty);
                Telemetry.Log(SimTime, "FAULT", $"TRIGGERED {f.Def.Type}: {f.Def.HmiMessage}");
                SetState(AttractionState.Faulted);
            };
            Faults.OnFaultResolved += f =>
            {
                Score.AddFaultResolved();
                Telemetry.Log(SimTime, "FAULT", $"RESOLVED {f.Def.Type}");
                if (Faults.Active.Count == 0 && State == AttractionState.Faulted)
                    SetState(LotoEngaged ? AttractionState.LotoActive : AttractionState.Ready);
            };
        }

        void SetState(AttractionState s)
        {
            if (State == s) return;
            State = s;
            Telemetry.Log(SimTime, "STATE", s.ToString());
            OnStateChanged?.Invoke(s);
        }

        // ---- Player verbs (server-authoritative in MP) ----

        public void OpenShift()
        {
            if (State != AttractionState.Closed) return;
            SetState(AttractionState.Ready);
            Telemetry.Log(SimTime, "SHIFT", "Shift opened");
        }

        public void SetGates(bool open)
        {
            if (Faults.HasFault(FaultType.GateSensorFault) && open) return; // sensor fault holds gates
            Guests.SetGates(open);
            Telemetry.Log(SimTime, "OPS", open ? "Gates opened" : "Gates closed");
        }

        /// <summary>Cycling the gates closed re-seats the gate sensor (its fix interaction).</summary>
        public void CycleGates()
        {
            Guests.SetGates(false);
            if (Faults.Resolve(FaultType.GateSensorFault))
                Telemetry.Log(SimTime, "OPS", "Gate sensor re-seated via gate cycle");
        }

        public int LoadPlatformTrain()
        {
            var t = PlatformTrain();
            if (t == null || t.State != TrainState.InStationLoading) return 0;
            int n = Guests.Board(Config.SeatsPerTrain - t.Riders);
            if (n > 0) { t.Board(n); Telemetry.Log(SimTime, "OPS", $"Boarded {n} guests (train {t.Id})"); }
            return n;
        }

        public bool VerifyRestraints()
        {
            var t = PlatformTrain();
            if (t == null || t.Riders == 0) return false;
            if (Faults.HasFault(FaultType.RestraintNotVerified)) return false; // must inspect seat first
            t.VerifyRestraints();
            Telemetry.Log(SimTime, "OPS", $"Restraints verified (train {t.Id})");
            return true;
        }

        public void InspectFaultySeat()
        {
            if (Faults.Resolve(FaultType.RestraintNotVerified))
            {
                PlatformTrain()?.InvalidateRestraints(); // must re-verify after inspection
                Telemetry.Log(SimTime, "OPS", "Faulty seat inspected — re-verify restraints");
            }
        }

        public void ClearDroppedItem()
        {
            if (Faults.Resolve(FaultType.GuestDroppedItem))
                Telemetry.Log(SimTime, "OPS", "Track swept, guest item recovered");
        }

        public bool CanDispatch(out string reason)
        {
            var t = PlatformTrain();
            reason = null;
            if (State == AttractionState.Closed) { reason = "Shift not opened"; return false; }
            if (t == null) { reason = "No train in station"; return false; }
            if (t.State != TrainState.ReadyToDispatch)
            {
                reason = t.Riders == 0 ? "Train empty — open gates and load"
                       : !t.RestraintsVerified ? "Restraints not verified" : "Train not ready";
                return false;
            }
            if (Guests.GatesOpen) { reason = "Close gates before dispatch"; return false; }
            if (Faults.AnyBlocksDispatch) { reason = "Active fault holds dispatch"; return false; }
            if (!RidePowered) { reason = "Ride power is locked out"; return false; }
            if (!Blocks.IsClear(Blocks.NextBlock(0))) { reason = "Block 1 occupied"; return false; }
            return true;
        }

        public bool Dispatch()
        {
            if (!CanDispatch(out _)) return false;
            var t = PlatformTrain();
            t.Dispatch();
            Score.AddDispatch();
            _timeoutFaultLatched = false;
            Telemetry.Log(SimTime, "OPS", $"DISPATCH train {t.Id} with {t.Riders} riders");
            return true;
        }

        public void EngageLoto(bool engaged)
        {
            LotoEngaged = engaged;
            Telemetry.Log(SimTime, "MAINT", engaged ? "LOTO ENGAGED — energy isolated, tag hung" : "LOTO RELEASED");
            if (engaged)
            {
                foreach (var f in Faults.Active) if (f.Def.RequiresLoto) Faults.MarkLoto(f.Def.Type);
                if (State == AttractionState.Ready) SetState(AttractionState.LotoActive);
            }
            else if (State == AttractionState.LotoActive && Faults.Active.Count == 0)
                SetState(AttractionState.Ready);
        }

        public bool MaintenanceReset()
        {
            if (!Faults.HasFault(FaultType.MaintenanceResetRequired)) return false;
            bool ok = Faults.Resolve(FaultType.MaintenanceResetRequired);
            if (!ok) Telemetry.Log(SimTime, "MAINT", "Reset refused — LOTO not engaged");
            return ok;
        }

        // ---- Simulation tick ----

        public void Tick(float dt)
        {
            if (State == AttractionState.Closed || State == AttractionState.EndOfShift) return;
            SimTime += dt;

            bool rideDown = Faults.Active.Count > 0 || LotoEngaged;
            if (rideDown) Score.AddDowntime(dt);

            Guests.Tick(dt, rideDown);
            Faults.Tick(dt, SimTime);

            foreach (var t in Trains)
            {
                bool completed = t.Tick(dt, Config.BlockTraverseSeconds, RidePowered);
                if (completed)
                {
                    int riders = t.Unload();
                    if (riders > 0)
                    {
                        Score.AddRiders(riders);
                        Guests.RewardRiders(riders);
                        Telemetry.Log(SimTime, "OPS", $"Train {t.Id} returned, {riders} riders served");
                    }
                }
            }

            // Condition fault: stacking (a train holding outside while station occupied)
            bool anyHolding = false;
            foreach (var t in Trains) if (t.State == TrainState.HoldingAtBlock) anyHolding = true;
            _holdingSeconds = anyHolding ? _holdingSeconds + dt : 0f;
            if (_holdingSeconds > StackingGraceSeconds && !_stackingFaultLatched)
            { Faults.Trigger(FaultType.BlockOccupiedStacking, SimTime); _stackingFaultLatched = true; }
            if (!anyHolding && _stackingFaultLatched)
            { Faults.Resolve(FaultType.BlockOccupiedStacking); _stackingFaultLatched = false; }

            // Condition fault: dispatch timeout
            var p = PlatformTrain();
            if (p != null && p.StationIdleSeconds > Config.DispatchTimeoutSeconds && !_timeoutFaultLatched)
            { Faults.Trigger(FaultType.DispatchTimeout, SimTime); _timeoutFaultLatched = true; }
            if (_timeoutFaultLatched && (p == null || p.StationIdleSeconds < 1f))
            { Faults.Resolve(FaultType.DispatchTimeout); _timeoutFaultLatched = false; }

            if (SimTime >= Config.ShiftLengthSeconds)
            {
                SetState(AttractionState.EndOfShift);
                Telemetry.Log(SimTime, "SHIFT", $"Shift ended. Score {Score.Compute(Guests.Satisfaction)}");
                OnShiftEnded?.Invoke();
            }
        }

        public TrainSim PlatformTrain()
        {
            foreach (var t in Trains)
                if (t.CurrentBlock == 0 && (t.State == TrainState.InStationLoading || t.State == TrainState.ReadyToDispatch || t.State == TrainState.InStationUnloading))
                    return t;
            return null;
        }
    }
}
