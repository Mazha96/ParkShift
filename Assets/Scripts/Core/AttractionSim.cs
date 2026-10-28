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
        TrainSim[] _trains;
        /// <summary>Active RVs. Grows when the operator adds one from the HMI (see AddTrain).</summary>
        public TrainSim[] Trains => _trains;
        /// <summary>Fired when a new RV is added at runtime so the visual layer can spawn its body.</summary>
        public event Action<TrainSim> OnTrainAdded;
        public readonly GuestSim Guests;
        public readonly ManagementSim Management;
        public readonly FaultSystem Faults;
        public readonly ScoreSystem Score = new ScoreSystem();
        public readonly Telemetry Telemetry = new Telemetry();

        public float SimTime { get; private set; }
        public float ShiftRemaining => Math.Max(0f, Config.ShiftLengthSeconds - SimTime);
        public bool LotoEngaged { get; private set; }
        public bool RidePowered => !LotoEngaged && !Faults.AnyStopsRide && State != AttractionState.Closed;

        public event Action<AttractionState> OnStateChanged;
        public event Action OnShiftEnded;

        // ---- Unload workflow (split station only) ----
        /// <summary>The Unload-side door. Guests cannot leave the RV until it is open.</summary>
        public bool UnloadDoorOpen { get; private set; }
        /// <summary>Restraints on the Unload RV have been released — guests may now stand up and exit.</summary>
        public bool RestraintsReleased { get; private set; }
        /// <summary>True when no guests are mid-unload (start state and after each unload completes).</summary>
        public bool UnloadAreaClear { get; private set; } = true;
        /// <summary>True while guests are standing up / walking out after a restraints release.</summary>
        public bool GuestsUnloading { get; private set; }
        public Func<bool> UnloadDoorClearToClose;
        public Func<bool> UnloadDoorReadyToRelease;
        public Func<bool> UnloadDoorReadyToDepart;
        /// <summary>Fired when the operator releases restraints — the visual layer plays the
        /// stand-up + walk-to-exit flow for that RV's riders.</summary>
        public event Action<TrainSim> OnUnloadReleased;
        float _unloadTimer;

        bool _stackingFaultLatched;
        bool _timeoutFaultLatched;
        float _holdingSeconds;
        const float StackingGraceSeconds = 4f;

        public AttractionSim(SimConfig cfg, int seed = 0)
        {
            Config = cfg;
            Blocks = new BlockSystem(cfg.BlockCount);
            Guests = new GuestSim(cfg);
            Management = new ManagementSim(this);
            Faults = new FaultSystem(cfg, seed);
            // Spawn & register every RV with the block system. Train 0 sits in the station;
            // the rest stage downstream from the brake run backward (block N-1, N-2, ...),
            // which reproduces the original two-train {0, N-1} layout and stages extras
            // nose-to-tail so over-dispatching shows as brake-run stacking. ForcePlace is the
            // registration hook; BlockSystem.Vacate is the matching unregister on despawn.
            int trainCount = Math.Max(1, Math.Min(cfg.TrainCount, cfg.BlockCount)); // at most one RV per block
            int loadBlock = cfg.LoadBlock;                             // 0 normally; 1 when the station is split
            float park = cfg.SeparateLoadUnload ? cfg.StationParkProgress : 0f;
            _trains = new TrainSim[trainCount];
            _trains[0] = new TrainSim(0, Blocks, loadBlock, loadBlock, park); // platform RV parks at the Load block centre
            for (int i = 1; i < trainCount; i++)
            {
                int startBlock = cfg.BlockCount - i;                   // N-1, N-2, ... staged toward the brake run
                while (startBlock > loadBlock && !Blocks.IsClear(startBlock)) startBlock--; // never share a block
                if (startBlock <= loadBlock) startBlock = FirstClearStagingBlock(loadBlock);
                _trains[i] = new TrainSim(i, Blocks, startBlock, loadBlock, park);
            }
            Faults.OnFaultTriggered += f =>
            {
                Guests.Penalize(f.Def.SatisfactionHit);
                Score.AddPenalty(f.Def.ScorePenalty);
                Score.BreakStreak();   // a fault interrupts smooth operation — the dispatch combo resets
                Telemetry.Log(SimTime, "FAULT", $"TRIGGERED {f.Def.Type}: {f.Def.HmiMessage}");
                SetState(AttractionState.Faulted);
            };
            Faults.OnDeadlineMissed += f =>
            {
                Score.AddPenalty(20);
                Guests.Penalize(6f);
                Telemetry.Log(SimTime, "DEADLINE", $"Response window missed: {f.Def.Type}; -20 score, -6 mood. Repair still available.");
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
            if (open) PlatformTrain()?.InvalidateRestraints();
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
            if (t == null || t.Riders == 0 || t.RestraintsVerified || Guests.GatesOpen) return false;
            if (Faults.HasFault(FaultType.RestraintNotVerified) || Faults.HasFault(FaultType.RestraintJammedCar)) return false;
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

        // ---- Unload-side operator verbs (split station) ----

        /// <summary>Open the Unload door. Allowed only with an RV parked at Unload.</summary>
        public bool OpenUnloadDoor()
        {
            if (UnloadingTrain() == null || UnloadDoorOpen) return false;
            UnloadDoorOpen = true;
            Telemetry.Log(SimTime, "OPS", "Unload door opened");
            return true;
        }

        /// <summary>Close the Unload door. Allowed only once guests have cleared the area.</summary>
        public bool CloseUnloadDoor()
        {
            if (!UnloadDoorOpen || !UnloadAreaClear || (UnloadDoorClearToClose != null && !UnloadDoorClearToClose())) return false;
            UnloadDoorOpen = false;
            RestraintsReleased = false;
            Telemetry.Log(SimTime, "OPS", "Unload door closed");
            return true;
        }

        /// <summary>Release restraints on the Unload RV — drops the riders and starts the guest walk-out.
        /// Requires an RV at Unload with the door already open.</summary>
        public bool ReleaseRestraints()
        {
            var rv = UnloadingTrain();
            if (rv == null || !UnloadDoorOpen || RestraintsReleased
                || (UnloadDoorReadyToRelease != null && !UnloadDoorReadyToRelease())) return false;
            RestraintsReleased = true;
            GuestsUnloading = true;
            UnloadAreaClear = false;
            _unloadTimer = Config.UnloadSeconds;
            int riders = rv.Unload(Config);   // RV empties now; the visual layer plays the walk-out
            if (riders > 0)
            {
                Score.AddRiders(riders);
                Management.EarnRevenue(riders);
                Guests.RewardRiders(riders);
                Telemetry.Log(SimTime, "OPS", $"Restraints released — {riders} guests unloading");
            }
            OnUnloadReleased?.Invoke(rv);
            return true;
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
            if (!Blocks.IsClear(Blocks.NextBlock(Config.LoadBlock))) { reason = "Next block occupied"; return false; }
            return true;
        }

        public bool Dispatch()
        {
            if (!CanDispatch(out _)) return false;
            var t = PlatformTrain();
            t.Dispatch();
            Score.AddDispatch();
            // Dispatch streak combo: consecutive clean dispatches (no fault in between) bank bonus points.
            int streak = Score.CurrentStreak;
            if (streak >= 2 && Config.ComboBonusPerStreak > 0)
            {
                int bonus = Math.Min(streak - 1, Config.ComboStreakCap) * Config.ComboBonusPerStreak;
                Score.AddComboBonus(bonus);
                Telemetry.Log(SimTime, "SCORE", $"Dispatch streak x{streak} (+{bonus} combo)");
            }
            Guests.RewardDispatch(Config.SatisfactionPerSmoothDispatch);
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
            bool any = false;
            if (Faults.HasFault(FaultType.MaintenanceResetRequired))
            {
                if (Faults.Resolve(FaultType.MaintenanceResetRequired)) any = true;
                else Telemetry.Log(SimTime, "MAINT", "Reset refused — LOTO not engaged");
            }
            if (Faults.HasFault(FaultType.TrainCollision))
            {
                if (Faults.Resolve(FaultType.TrainCollision))
                { any = true; Telemetry.Log(SimTime, "MAINT", "Collision cleared — drive reset accepted"); }
                else Telemetry.Log(SimTime, "MAINT", "Reset refused — inspect the contact point and engage LOTO first");
            }
            return any;
        }

        /// <summary>Technician walks to the track and escorts the intruding guest off.</summary>
        public bool EscortIntruder()
        {
            if (!Faults.Resolve(FaultType.TrackIntrusion)) return false;
            Telemetry.Log(SimTime, "MAINT", "Guest escorted off the track — e-stop cleared");
            return true;
        }

        /// <summary>Technician inspects the collision contact point (prerequisite for drive reset).</summary>
        public bool InspectCollision()
        {
            if (!Faults.HasFault(FaultType.TrainCollision)) return false;
            Faults.MarkInspected(FaultType.TrainCollision);
            Telemetry.Log(SimTime, "MAINT", "Collision damage inspected — LOTO + drive reset to clear");
            return true;
        }

        /// <summary>Technician cranks the evac winch to recover a valleyed train (LOTO required).</summary>
        public bool WinchValleyedTrain()
        {
            if (!Faults.HasFault(FaultType.TrainValleyed)) return false;
            bool ok = Faults.Resolve(FaultType.TrainValleyed);
            Telemetry.Log(SimTime, "MAINT", ok ? "Valleyed train winched to the brake run"
                                               : "Winch refused — LOTO not engaged");
            return ok;
        }

        // ---- Extended fault fix verbs ----

        /// <summary>Block-verify walk: the operator goes out to the ghosting zone sensor and
        /// visually confirms the block is clear (the inspection step of a BlockSensorGhost fix).</summary>
        public bool VerifyBlockClear()
        {
            if (!Faults.HasFault(FaultType.BlockSensorGhost)) return false;
            Faults.MarkInspected(FaultType.BlockSensorGhost);
            Telemetry.Log(SimTime, "OPS", "Block walked — zone visually confirmed clear");
            return true;
        }

        /// <summary>Reset the ghosting zone sensor at the dispatch panel.
        /// Refused until the block-verify walk has been done.</summary>
        public bool ResetBlockSensor()
        {
            if (!Faults.HasFault(FaultType.BlockSensorGhost)) return false;
            bool ok = Faults.Resolve(FaultType.BlockSensorGhost);
            Telemetry.Log(SimTime, "OPS", ok ? "Zone sensor reset — phantom occupancy cleared"
                                             : "Sensor reset refused — verify the block on foot first");
            return ok;
        }

        /// <summary>Technician advances one stage of the brownout staged restart. Each stage must
        /// stabilize for Config.BrownoutStageSeconds before the next press is accepted.</summary>
        public bool AdvanceBrownoutStage()
        {
            var f = Faults.Find(FaultType.PowerBrownout);
            if (f == null) return false;
            float since = SimTime - (f.Stage == 0 ? f.TriggeredAt : f.LastStageAt);
            if (since < Config.BrownoutStageSeconds)
            {
                Telemetry.Log(SimTime, "MAINT",
                    $"Restart stage refused — bus still stabilizing ({Config.BrownoutStageSeconds - since:0.0}s to go)");
                return false;
            }
            f.Stage++;
            f.LastStageAt = SimTime;
            if (f.Stage >= Config.BrownoutStageCount)
            {
                Faults.Resolve(FaultType.PowerBrownout);
                Telemetry.Log(SimTime, "MAINT", "Drive bus restart complete — power restored");
            }
            else
                Telemetry.Log(SimTime, "MAINT", $"Restart stage {f.Stage}/{Config.BrownoutStageCount} energized");
            return true;
        }

        /// <summary>Free a jammed lap bar on a specific car. Only the flagged car clears the fault;
        /// afterwards restraints must be re-verified (mirrors the seat-inspect flow).</summary>
        public bool UnjamRestraint(int carIndex)
        {
            var f = Faults.Find(FaultType.RestraintJammedCar);
            if (f == null) return false;
            if (carIndex != f.TargetIndex)
            {
                Telemetry.Log(SimTime, "OPS", $"Car {carIndex + 1} lap bar seats fine — check the flagged car");
                return false;
            }
            Faults.Resolve(FaultType.RestraintJammedCar);
            PlatformTrain()?.InvalidateRestraints();
            Telemetry.Log(SimTime, "OPS", $"Car {carIndex + 1} lap bar freed — re-verify restraints");
            return true;
        }

        // ---- Client mirror setter (multiplayer presentation only) ----

        /// <summary>Stamp replicated ops state onto this (never-ticked) mirror sim: attraction
        /// state, sim clock and the unload-workflow flags — enough for client HUD/prompts,
        /// including reaching EndOfShift. Fires OnUnloadReleased on the restraints-released
        /// rising edge (when the RV at Unload can be resolved) so the visual layer plays the
        /// guest walk-out, and OnShiftEnded once when the mirrored state first reaches
        /// EndOfShift. Never called when authoritative; determinism untouched.</summary>
        public void MirrorOps(AttractionState state, float simTime,
                              bool unloadDoorOpen, bool restraintsReleased,
                              bool unloadAreaClear, bool guestsUnloading)
        {
            SimTime = simTime < 0f ? 0f : simTime;
            UnloadDoorOpen = unloadDoorOpen;
            bool releasedEdge = restraintsReleased && !RestraintsReleased;
            RestraintsReleased = restraintsReleased;
            UnloadAreaClear = unloadAreaClear;
            GuestsUnloading = guestsUnloading;
            bool endedEdge = state == AttractionState.EndOfShift && State != AttractionState.EndOfShift;
            SetState(state);
            if (releasedEdge)
            {
                var rv = UnloadingTrain();
                if (rv != null) OnUnloadReleased?.Invoke(rv);
            }
            if (endedEdge) OnShiftEnded?.Invoke();
        }

        // ---- Simulation tick ----

        public void Tick(float dt)
        {
            if (State == AttractionState.Closed || State == AttractionState.EndOfShift) return;
            SimTime += dt;

            bool rideDown = Faults.Active.Count > 0 || LotoEngaged;
            if (rideDown) Score.AddDowntime(dt);
            // The advertised travel/repair window must be usable without emergency drain
            // alone exhausting mood. Queue waiting still applies at the normal rate.
            bool protectedResponse = false;
            foreach (var fault in Faults.Active)
            {
                if (!fault.Def.BlocksDispatch && !fault.Def.StopsRide) continue;
                if (fault.DeadlineAt <= SimTime) { protectedResponse = false; break; }
                protectedResponse = true;
            }
            float emergencyDrainScale = Config.TimedFaultsEnabled && protectedResponse ? 0.15f : 1f;

            Management.Tick(dt);
            Guests.Tick(dt, rideDown, SimTime, Management.AdmissionsPaused, emergencyDrainScale);
            Faults.Tick(dt, SimTime, PlatformTrain() != null && PlatformTrain().Riders > 0);

            foreach (var t in Trains)
            {
                bool unloadHeld = Config.SeparateLoadUnload && t.State == TrainState.InStationUnloading
                    && (!UnloadAreaClear || UnloadDoorOpen
                        || (UnloadDoorReadyToDepart != null && !UnloadDoorReadyToDepart()));
                bool completed = t.Tick(dt, Config, RidePowered && !unloadHeld);
                if (completed)
                {
                    if (Config.SeparateLoadUnload)
                    {
                        if (t.Riders > 0)
                        {
                            // RV carrying guests: hold them aboard until the operator opens the door and
                            // releases restraints. Reset the unload workflow for this RV.
                            t.UnloadComplete = false;
                            UnloadDoorOpen = false;
                            RestraintsReleased = false;
                            UnloadAreaClear = false;
                            GuestsUnloading = false;
                            Telemetry.Log(SimTime, "OPS", $"Train {t.Id} arrived at Unload — awaiting door + restraints");
                        }
                        else
                        {
                            // Empty RV (e.g. a staged train's first lap): nothing to unload, so it passes
                            // straight through Unload to the Load block. This is what frees the 2nd RV to
                            // reach Load — without it an empty RV would wait forever for an unload it never needs.
                            t.UnloadComplete = true;
                            UnloadAreaClear = true;
                            GuestsUnloading = false;
                        }
                    }
                    else
                    {
                        int riders = t.Unload(Config);
                        if (riders > 0)
                        {
                            Score.AddRiders(riders);
                            Management.EarnRevenue(riders);
                            Guests.RewardRiders(riders);
                            Telemetry.Log(SimTime, "OPS", $"Train {t.Id} returned, {riders} riders served");
                        }
                    }
                }
            }

            // Unload timer: once restraints are released, guests take UnloadSeconds to clear the area;
            // then the RV is free to crawl to Load and the door may be closed.
            if (GuestsUnloading)
            {
                _unloadTimer -= dt;
                if (_unloadTimer <= 0f)
                {
                    GuestsUnloading = false;
                    UnloadAreaClear = true;
                    var rv = UnloadingTrain();
                    if (rv != null) rv.UnloadComplete = true;
                    Telemetry.Log(SimTime, "OPS", "Unload area clear");
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
            // Stalled-queue mood drain: a platform train loitering past the grace window sours the crowd.
            if (p != null && p.StationIdleSeconds > Config.StallGraceSeconds
                && Config.SatisfactionStallDrainPerSecond > 0f)
                Guests.Penalize(Config.SatisfactionStallDrainPerSecond * dt * emergencyDrainScale);
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

        int FirstClearStagingBlock(int loadBlock)
        {
            for (int b = loadBlock + 1; b < Blocks.BlockCount; b++) if (Blocks.IsClear(b)) return b;
            return Math.Min(loadBlock + 1, Blocks.BlockCount - 1); // degenerate: more trains than blocks (guarded in ctor)
        }

        /// <summary>The RV at the Load block that the operator boards and dispatches.
        /// With a single-block station this is also the RV that is unloading.</summary>
        public TrainSim PlatformTrain()
        {
            int lb = Config.LoadBlock;
            foreach (var t in Trains)
                if (t.CurrentBlock == lb &&
                    (t.State == TrainState.InStationLoading || t.State == TrainState.ReadyToDispatch
                     || (lb == 0 && t.State == TrainState.InStationUnloading)))
                    return t;
            return null;
        }

        /// <summary>The RV currently dropping riders at the Unload block (split station only; else null).</summary>
        public TrainSim UnloadingTrain()
        {
            if (!Config.SeparateLoadUnload) return null;
            foreach (var t in Trains)
                if (t.CurrentBlock == Config.UnloadBlock && t.State == TrainState.InStationUnloading) return t;
            return null;
        }

        /// <summary>Room for another RV? Keeps one spare block so the N+1 buffer still holds.</summary>
        public bool CanAddTrain()
        {
            if (_trains.Length >= Blocks.BlockCount - 1) return false;
            for (int b = 0; b < Blocks.BlockCount; b++) if (Blocks.IsClear(b)) return true;
            return false;
        }

        /// <summary>Add an RV at runtime (HMI "Add RV"). Stages it on a free block and returns it,
        /// or null if there's no room. Fires OnTrainAdded so the visual layer can spawn its body.</summary>
        public TrainSim AddTrain()
        {
            if (!CanAddTrain()) return null;
            int loadBlock = Config.LoadBlock;
            int start = FirstClearStagingBlock(loadBlock);
            if (!Blocks.IsClear(start)) return null;
            float park = Config.SeparateLoadUnload ? Config.StationParkProgress : 0f;
            int id = _trains.Length;
            var t = new TrainSim(id, Blocks, start, loadBlock, park);
            Array.Resize(ref _trains, id + 1);
            _trains[id] = t;
            OnTrainAdded?.Invoke(t);
            Telemetry.Log(SimTime, "OPS", $"RV {id} added to the circuit");
            return t;
        }
    }
}


