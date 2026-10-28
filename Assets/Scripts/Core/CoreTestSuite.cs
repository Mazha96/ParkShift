using System;
using System.Collections.Generic;

namespace ParkShift.Core.Testing
{
    /// <summary>
    /// Engine-agnostic test suite. Run two ways:
    /// 1) Unity Test Runner via the NUnit wrappers in Assets/Tests/EditMode
    /// 2) Plain console harness (mono/dotnet) for CI without Unity
    /// </summary>
    public static class CoreTestSuite
    {
        public static readonly List<string> Failures = new List<string>();
        static string _current;

        static void Check(bool cond, string what)
        { if (!cond) Failures.Add($"{_current}: {what}"); }

        static AttractionSim NewSim(Action<SimConfig> mutate = null, int seed = 1234)
        {
            var cfg = new SimConfig { RandomFaultChancePerSecond = 0f }; // deterministic by default
            mutate?.Invoke(cfg);
            var sim = new AttractionSim(cfg, seed);
            sim.OpenShift();
            return sim;
        }

        static void RunUntil(AttractionSim sim, Func<bool> done, float maxSeconds, float dt = 0.1f)
        {
            float t = 0;
            while (!done() && t < maxSeconds) { sim.Tick(dt); t += dt; }
        }

        // ---------------- Tests ----------------

        public static void Test_StateTransitions()
        {
            _current = nameof(Test_StateTransitions);
            var sim = NewSim();
            Check(sim.State == AttractionState.Ready, "opens to Ready");
            sim.Faults.Trigger(FaultType.GateSensorFault, 0);
            Check(sim.State == AttractionState.Faulted, "fault -> Faulted");
            sim.CycleGates();
            Check(sim.State == AttractionState.Ready, "resolve -> Ready");
            sim.EngageLoto(true);
            Check(sim.State == AttractionState.LotoActive, "LOTO -> LotoActive");
            sim.EngageLoto(false);
            Check(sim.State == AttractionState.Ready, "release -> Ready");
        }

        public static void Test_BlockOccupancy()
        {
            _current = nameof(Test_BlockOccupancy);
            var b = new BlockSystem(4);
            Check(b.TryEnter(1, 7), "enter clear block");
            Check(!b.TryEnter(1, 8), "deny occupied block");
            b.Leave(1, 7);
            Check(b.IsClear(1), "leave clears");
            Check(b.NextBlock(3) == 0, "route wraps to station");
        }

        public static void Test_DispatchRules()
        {
            _current = nameof(Test_DispatchRules);
            var sim = NewSim();
            string r;
            Check(!sim.CanDispatch(out r) && r.Contains("empty"), "deny empty train: " + r);
            sim.SetGates(true);
            RunUntil(sim, () => sim.Guests.QueueCount >= 4, 30f);
            sim.LoadPlatformTrain();
            Check(!sim.CanDispatch(out r) && r.Contains("Restraints"), "deny unverified");
            Check(!sim.VerifyRestraints(), "cannot verify while gates open");
            sim.SetGates(false);
            Check(sim.VerifyRestraints(), "verify after gates close");
            Check(!sim.VerifyRestraints(), "reject repeated verification");
            sim.SetGates(true);
            Check(!sim.PlatformTrain().RestraintsVerified && !sim.CanDispatch(out r), "reopening gates invalidates restraint check");
            sim.SetGates(false);
            Check(sim.VerifyRestraints(), "recheck required after reopening");
            Check(sim.CanDispatch(out r), "allow when ready: " + r);
            Check(sim.Dispatch(), "dispatch succeeds");
            Check(sim.Trains[0].State == TrainState.Running, "train running");
        }

        public static void Test_BlockHoldAndStacking()
        {
            _current = nameof(Test_BlockHoldAndStacking);
            var sim = NewSim();
            // Train 1 starts staged on the brake run (last block). Without dispatching
            // the platform train, the station stays occupied -> train 1 must hold and
            // a stacking fault must latch.
            sim.SetGates(true);
            RunUntil(sim, () => sim.Faults.HasFault(FaultType.BlockOccupiedStacking), 40f);
            Check(sim.Trains[1].State == TrainState.HoldingAtBlock, "staged train holds at block");
            Check(sim.Faults.HasFault(FaultType.BlockOccupiedStacking), "stacking fault latched");
            // Now run the platform: load, verify, dispatch -> station frees -> hold clears
            RunUntil(sim, () => sim.Guests.QueueCount >= 2, 30f);
            sim.LoadPlatformTrain(); sim.SetGates(false); sim.VerifyRestraints();
            Check(sim.Dispatch(), "platform dispatch clears path");
            RunUntil(sim, () => !sim.Faults.HasFault(FaultType.BlockOccupiedStacking), 30f);
            Check(!sim.Faults.HasFault(FaultType.BlockOccupiedStacking), "stacking fault auto-clears");
        }

        public static void Test_SeparateLoadUnload()
        {
            _current = nameof(Test_SeparateLoadUnload);
            var sim = NewSim(c => { c.SeparateLoadUnload = true; c.BlockCount = 5; });

            Check(sim.Config.UnloadBlock == 0 && sim.Config.LoadBlock == 1, "split station block indices");
            var plat = sim.PlatformTrain();
            Check(plat != null && plat.CurrentBlock == 1, "boarding RV parks at the Load block (1)");

            bool arrivedUnload = false;
            foreach (var t in sim.Trains) t.OnArrivedStation += tr => { if (tr.CurrentBlock == 0) arrivedUnload = true; };

            // Simple operator: board, verify and dispatch the Load-block RV whenever it is ready.
            bool overlap = false;
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < 4000; i++)
            {
                var p = sim.PlatformTrain();
                if (p != null && p.State == TrainState.InStationLoading)
                {
                    if (p.Riders == 0) p.Board(sim.Config.SeatsPerTrain);
                    sim.SetGates(false);
                    sim.VerifyRestraints();
                }
                if (sim.CanDispatch(out _)) sim.Dispatch();
                sim.Tick(0.1f);

                seen.Clear();
                foreach (var t in sim.Trains) if (!seen.Add(t.CurrentBlock)) overlap = true; // one RV per block
            }
            Check(!overlap, "two RVs never share a block (no overlap)");
            Check(arrivedUnload, "an RV completed a circuit and dropped riders at Unload (block 0)");
        }

        public static void Test_UnloadWorkflow()
        {
            _current = nameof(Test_UnloadWorkflow);
            var sim = NewSim(c => { c.SeparateLoadUnload = true; c.BlockCount = 5; c.TrainCount = 1; c.UnloadSeconds = 1f; });
            var rv = sim.Trains[0];

            // Board + verify + dispatch the Load RV, then let it circuit back to Unload.
            rv.Board(sim.Config.SeatsPerTrain);
            sim.SetGates(false);
            sim.VerifyRestraints();
            Check(sim.Dispatch(), "dispatch load RV");
            RunUntil(sim, () => rv.CurrentBlock == 0 && rv.State == TrainState.InStationUnloading, 90f);
            Check(rv.CurrentBlock == 0 && rv.State == TrainState.InStationUnloading, "RV returns to Unload");
            Check(rv.Riders > 0, "guests stay aboard until restraints released");

            Check(!sim.ReleaseRestraints(), "release blocked while the door is closed");
            Check(sim.OpenUnloadDoor(), "operator opens the unload door");
            sim.UnloadDoorReadyToRelease = () => false;
            Check(!sim.ReleaseRestraints() && rv.Riders > 0, "wait for physical door travel before release");
            sim.UnloadDoorReadyToRelease = () => true;
            Check(sim.ReleaseRestraints(), "operator releases restraints");
            Check(rv.Riders == 0, "riders drop on release");
            Check(!sim.CloseUnloadDoor(), "door cannot close mid-unload");

            RunUntil(sim, () => sim.UnloadAreaClear, 5f);
            Check(sim.UnloadAreaClear, "unload area clears after the unload time");
            sim.Tick(2f);
            Check(rv.State == TrainState.InStationUnloading, "RV remains parked after guests clear while door is open");
            sim.UnloadDoorClearToClose = () => false;
            Check(!sim.CloseUnloadDoor(), "physical doorway obstruction prevents closing");
            sim.UnloadDoorClearToClose = () => true;
            sim.UnloadDoorReadyToDepart = () => false;
            Check(sim.CloseUnloadDoor(), "door closes once guests have cleared");
            sim.Tick(2f);
            Check(rv.State == TrainState.InStationUnloading, "RV waits for physical closing travel");
            sim.UnloadDoorReadyToDepart = () => true;

            RunUntil(sim, () => rv.CurrentBlock == 1, 30f);
            Check(rv.CurrentBlock == 1, "RV crawls Unload -> Load once clear");
        }

        public static void Test_AddTrain()
        {
            _current = nameof(Test_AddTrain);
            var sim = NewSim(c => { c.SeparateLoadUnload = true; c.BlockCount = 5; c.TrainCount = 2; });
            Check(sim.Trains.Length == 2, "starts with 2 RVs");
            var added = sim.AddTrain();
            Check(added != null && sim.Trains.Length == 3, "AddTrain grows the fleet");

            var seen = new System.Collections.Generic.HashSet<int>();
            bool overlap = false;
            foreach (var t in sim.Trains) if (!seen.Add(t.CurrentBlock)) overlap = true;
            Check(!overlap, "added RV placed on a free block (no overlap)");

            int guard = 0;
            while (sim.AddTrain() != null && guard++ < 10) { }
            Check(sim.Trains.Length <= sim.Blocks.BlockCount - 1, "respects N+1 capacity (BlockCount-1)");
        }

        public static void Test_FaultTriggerAndResolution()
        {
            _current = nameof(Test_FaultTriggerAndResolution);
            var sim = NewSim();
            sim.Faults.Trigger(FaultType.MaintenanceResetRequired, 0);
            Check(!sim.RidePowered, "major fault stops ride");
            Check(!sim.MaintenanceReset(), "reset refused without LOTO");
            sim.EngageLoto(true);
            Check(sim.MaintenanceReset(), "reset works with LOTO");
            sim.EngageLoto(false);
            Check(sim.State == AttractionState.Ready, "ride recoverable after LOTO release");

            sim.Faults.Trigger(FaultType.GuestDroppedItem, 0);
            string r; Check(!sim.CanDispatch(out r), "dropped item blocks dispatch");
            sim.ClearDroppedItem();
            Check(!sim.Faults.HasFault(FaultType.GuestDroppedItem), "item cleared");

            sim.Faults.Trigger(FaultType.RestraintNotVerified, 0);
            Check(!sim.VerifyRestraints(), "verify refused during restraint fault");
            sim.InspectFaultySeat();
            Check(!sim.Faults.HasFault(FaultType.RestraintNotVerified), "seat inspect resolves");
        }

        public static void Test_DispatchTimeoutFault()
        {
            _current = nameof(Test_DispatchTimeoutFault);
            var sim = NewSim(c => c.DispatchTimeoutSeconds = 5f);
            RunUntil(sim, () => sim.Faults.HasFault(FaultType.DispatchTimeout), 20f);
            Check(sim.Faults.HasFault(FaultType.DispatchTimeout), "timeout fault triggers from loitering");

            // Dispatching is the fix: the alert must clear and the ride must leave Faulted.
            RunUntil(sim, () => sim.Guests.QueueCount > 0, 30f);
            sim.SetGates(true); sim.LoadPlatformTrain(); sim.SetGates(false); sim.VerifyRestraints();
            Check(sim.Dispatch(), "dispatch accepted during timeout alert");
            sim.Tick(0.1f);
            Check(!sim.Faults.HasFault(FaultType.DispatchTimeout), "dispatch clears the timeout alert");
            // The second train may still be stacked behind; that alert clears once it rolls in.
            RunUntil(sim, () => sim.Faults.Active.Count == 0, 20f);
            Check(sim.State == AttractionState.Ready, "ride leaves Faulted once alerts clear");
        }

        public static void Test_LockoutTagout()
        {
            _current = nameof(Test_LockoutTagout);
            // Lockout applied BEFORE the fault appears still makes it safe to repair.
            var sim = NewSim();
            sim.EngageLoto(true);
            sim.Faults.Trigger(FaultType.MaintenanceResetRequired, sim.SimTime);
            Check(sim.MaintenanceReset(), "fault raised under lockout is repairable");
            sim.Faults.Trigger(FaultType.TrainValleyed, sim.SimTime);
            Check(sim.WinchValleyedTrain(), "valleyed train raised under lockout is winchable");
            sim.EngageLoto(false);
            Check(sim.State == AttractionState.Ready, "release with nothing active returns to Ready");

            // Releasing the lockout restores power and blocks repairs again.
            sim.Faults.Trigger(FaultType.MaintenanceResetRequired, sim.SimTime);
            sim.EngageLoto(true);
            sim.EngageLoto(false);
            Check(!sim.MaintenanceReset(), "repair refused after lockout released");
            Check(sim.Faults.HasFault(FaultType.MaintenanceResetRequired), "fault still active after refused repair");
            sim.EngageLoto(true);
            Check(sim.MaintenanceReset(), "re-applying lockout allows the repair");
            sim.EngageLoto(false);
            Check(sim.State == AttractionState.Ready, "ride recovers after lockout cycle");
        }

        public static void Test_RemoteTechChargesOnlyOnSuccess()
        {
            _current = nameof(Test_RemoteTechChargesOnlyOnSuccess);
            var sim = NewSim();
            int before = sim.Score.PenaltyPoints;
            Check(!sim.HasRemoteTechWork, "nothing to fix with no faults");
            Check(!sim.RemoteTechFix(30), "no fix with no faults");
            Check(sim.Score.PenaltyPoints == before, "no charge with no faults");

            // Condition alerts clear themselves; the remote tech has nothing to do.
            sim.Faults.Trigger(FaultType.DispatchTimeout, sim.SimTime);
            before = sim.Score.PenaltyPoints; // triggering charges the catalog penalty
            Check(!sim.HasRemoteTechWork, "condition alert is not remote-tech work");
            Check(!sim.RemoteTechFix(30), "condition alert not 'fixed' by remote tech");
            Check(sim.Score.PenaltyPoints == before, "no charge for condition alert");
            sim.Faults.ForceResolve(FaultType.DispatchTimeout);

            // Collision needs inspect + lockout; the remote tech does the whole job and charges once.
            sim.Faults.Trigger(FaultType.TrainCollision, sim.SimTime);
            before = sim.Score.PenaltyPoints;
            Check(sim.HasRemoteTechWork, "collision is remote-tech work");
            Check(sim.RemoteTechFix(30), "remote tech clears a collision");
            Check(!sim.Faults.HasFault(FaultType.TrainCollision), "collision gone");
            Check(sim.Score.PenaltyPoints == before + 30, "charged exactly once on success");
            Check(!sim.RemoteTechFix(30), "second call finds nothing");
            Check(sim.Score.PenaltyPoints == before + 30, "no charge on the empty second call");
        }

        public static void Test_ScoreCalculation()
        {
            _current = nameof(Test_ScoreCalculation);
            var s = new ScoreSystem();
            s.AddRiders(20); s.AddFaultResolved(); s.AddDowntime(10f); s.AddPenalty(15);
            int score = s.Compute(80f);
            // 200 + 25 + 400 - 20 - 15 = 590
            Check(score == 590, $"score math expected 590 got {score}");
            Check(s.Grade(950).StartsWith("S"), "S grade");
            Check(s.Grade(10).StartsWith("F"), "F grade");
            var empty = new ScoreSystem(); empty.AddDowntime(1000f);
            Check(empty.Compute(0f) == 0, "score floors at zero");
        }

        public static void Test_SoloTuning()
        {
            _current = nameof(Test_SoloTuning);
            var solo = SimConfig.Solo(); var coop = new SimConfig();
            Check(solo.RandomFaultChancePerSecond < coop.RandomFaultChancePerSecond, "solo fewer random faults");
            Check(solo.GuestSpawnInterval > coop.GuestSpawnInterval, "solo slower guest pressure");
            Check(solo.DispatchTimeoutSeconds > coop.DispatchTimeoutSeconds, "solo generous timeout");
            var four = SimConfig.Coop(4);
            Check(four.RandomFaultChancePerSecond > coop.RandomFaultChancePerSecond, "4P escalates chaos");
        }

        public static void Test_FullShiftEndsAndTelemetry()
        {
            _current = nameof(Test_FullShiftEndsAndTelemetry);
            var sim = NewSim(c => c.ShiftLengthSeconds = 8f);
            bool ended = false; sim.OnShiftEnded += () => ended = true;
            RunUntil(sim, () => ended, 20f);
            Check(ended, "shift ends");
            Check(sim.State == AttractionState.EndOfShift, "EndOfShift state");
            Check(sim.Telemetry.Entries.Count > 0, "telemetry recorded");
        }

        // ---------------- Extended content tests ----------------

        public static void Test_BlockSensorGhostFault()
        {
            _current = nameof(Test_BlockSensorGhostFault);
            var sim = NewSim();
            sim.Faults.Trigger(FaultType.BlockSensorGhost, 0);
            Check(sim.Faults.AnyBlocksDispatch, "ghost occupancy holds dispatch");
            Check(!sim.ResetBlockSensor(), "sensor reset refused before the block-verify walk");
            Check(sim.Faults.HasFault(FaultType.BlockSensorGhost), "fault persists until walked");
            Check(sim.VerifyBlockClear(), "block-verify walk performed");
            Check(sim.ResetBlockSensor(), "sensor reset accepted after the walk");
            Check(!sim.Faults.HasFault(FaultType.BlockSensorGhost), "phantom occupancy cleared");
            Check(sim.State == AttractionState.Ready, "ride returns to Ready");
        }

        public static void Test_PowerBrownoutStagedRestart()
        {
            _current = nameof(Test_PowerBrownoutStagedRestart);
            var sim = NewSim(c => c.BrownoutStageSeconds = 1f);
            sim.Tick(0.1f);
            sim.Faults.Trigger(FaultType.PowerBrownout, sim.SimTime);
            Check(!sim.RidePowered, "brownout stops the ride");
            Check(!sim.AdvanceBrownoutStage(), "stage refused before the bus stabilizes");
            RunUntil(sim, () => false, 1.2f);
            Check(sim.AdvanceBrownoutStage(), "stage 1 accepted after dwell");
            Check(!sim.AdvanceBrownoutStage(), "stage 2 refused immediately (must stabilize)");
            RunUntil(sim, () => false, 1.2f);
            Check(sim.AdvanceBrownoutStage(), "stage 2 accepted");
            RunUntil(sim, () => false, 1.2f);
            Check(sim.AdvanceBrownoutStage(), "stage 3 accepted");
            Check(!sim.Faults.HasFault(FaultType.PowerBrownout), "brownout resolves after the full staged restart");
            Check(sim.RidePowered, "power restored");
        }

        public static void Test_RestraintJamTargetsCar()
        {
            _current = nameof(Test_RestraintJamTargetsCar);
            var sim = NewSim(null, 77);
            var f = sim.Faults.Trigger(FaultType.RestraintJammedCar, 0);
            Check(f != null && f.TargetIndex >= 0 && f.TargetIndex < sim.Config.CarsPerTrain,
                  "jam targets a specific car");
            Check(f.HmiText.Contains("CAR " + (f.TargetIndex + 1)), "HMI text names the flagged car");
            Check(sim.Faults.AnyBlocksDispatch, "jam holds dispatch");
            int wrong = (f.TargetIndex + 1) % sim.Config.CarsPerTrain;
            Check(!sim.UnjamRestraint(wrong), "wrong car refused");
            Check(sim.Faults.HasFault(FaultType.RestraintJammedCar), "still jammed after wrong car");
            Check(sim.UnjamRestraint(f.TargetIndex), "flagged car frees the bar");
            Check(!sim.Faults.HasFault(FaultType.RestraintJammedCar), "jam cleared");
            // Determinism: the same seed must flag the same car.
            var sim2 = NewSim(null, 77);
            var f2 = sim2.Faults.Trigger(FaultType.RestraintJammedCar, 0);
            Check(f2 != null && f2.TargetIndex == f.TargetIndex, "seeded RNG reproduces the jammed car");
        }

        public static void Test_EscalationRampAndSurge()
        {
            _current = nameof(Test_EscalationRampAndSurge);
            // Guest arrival ramp: a ramped config spawns more guests over the shift than a flat one.
            var flatCfg = new SimConfig { RandomFaultChancePerSecond = 0f, QueueCapacity = 100000 };
            var rampCfg = new SimConfig { RandomFaultChancePerSecond = 0f, QueueCapacity = 100000, GuestArrivalRampMultiplier = 3f };
            var gFlat = new GuestSim(flatCfg); var gRamp = new GuestSim(rampCfg);
            for (float t = 0f; t < 180f; t += 0.1f) { gFlat.Tick(0.1f, false, t); gRamp.Tick(0.1f, false, t); }
            Check(gRamp.TotalSpawned > gFlat.TotalSpawned,
                  $"ramped arrivals spawn more ({gRamp.TotalSpawned} vs {gFlat.TotalSpawned})");

            // Default multiplier (1) must be behavior-preserving vs the legacy no-simTime path.
            var gDefA = new GuestSim(new SimConfig { QueueCapacity = 100000 });
            var gDefB = new GuestSim(new SimConfig { QueueCapacity = 100000 });
            for (float t = 0f; t < 60f; t += 0.1f) { gDefA.Tick(0.1f, false, t); gDefB.Tick(0.1f, false); }
            Check(gDefA.TotalSpawned == gDefB.TotalSpawned, "default ramp is behavior-preserving");

            // Surge waves: deterministic time windows that boost arrivals.
            var surgeCfg = new SimConfig { RandomFaultChancePerSecond = 0f, QueueCapacity = 100000,
                SurgeWavesEnabled = true, SurgeWavePeriodSeconds = 20f,
                SurgeWaveDurationSeconds = 5f, SurgeWaveSpawnMultiplier = 4f };
            var gSurge = new GuestSim(surgeCfg);
            var gCalm = new GuestSim(new SimConfig { QueueCapacity = 100000 });
            bool surgeSeen = false, calmSeen = false;
            for (float t = 0f; t < 40f; t += 0.1f)
            {
                gSurge.Tick(0.1f, false, t); gCalm.Tick(0.1f, false, t);
                if (gSurge.SurgeActive) surgeSeen = true; else calmSeen = true;
            }
            Check(surgeSeen && calmSeen, "surge windows toggle on and off");
            Check(gSurge.TotalSpawned > gCalm.TotalSpawned,
                  $"surge waves add guests ({gSurge.TotalSpawned} vs {gCalm.TotalSpawned})");

            // Fault ramp: same seed + same RNG stream, the ramped threshold is never lower — more faults roll.
            Check(CountFaults(new SimConfig(), 9, 180f) < CountFaults(new SimConfig { FaultRampMultiplier = 6f }, 9, 180f),
                  "fault ramp rolls more faults over the shift");

            // Extended pool determinism: two seeded systems produce the identical fault sequence.
            var seqA = FaultSequence(new SimConfig { ExtendedFaultsEnabled = true, RandomFaultChancePerSecond = 0.2f }, 5, 120f);
            var seqB = FaultSequence(new SimConfig { ExtendedFaultsEnabled = true, RandomFaultChancePerSecond = 0.2f }, 5, 120f);
            Check(seqA == seqB && seqA.Length > 0, "extended fault pool is seed-deterministic");
        }

        static int CountFaults(SimConfig cfg, int seed, float seconds)
        {
            var fs = new FaultSystem(cfg, seed);
            int count = 0;
            fs.OnFaultTriggered += f => count++;
            for (float t = 0f; t < seconds; t += 0.1f)
            {
                fs.Tick(0.1f, t);
                while (fs.Active.Count > 0)
                {
                    var a = fs.Active[0];
                    fs.LockoutApplied = true; fs.MarkInspected(a.Def.Type);
                    fs.Resolve(a.Def.Type);
                }
            }
            return count;
        }

        static string FaultSequence(SimConfig cfg, int seed, float seconds)
        {
            var fs = new FaultSystem(cfg, seed);
            var sb = new System.Text.StringBuilder();
            fs.OnFaultTriggered += f => sb.Append(f.Def.Type).Append(f.TargetIndex).Append(';');
            for (float t = 0f; t < seconds; t += 0.1f)
            {
                fs.Tick(0.1f, t);
                while (fs.Active.Count > 0)
                {
                    var a = fs.Active[0];
                    fs.LockoutApplied = true; fs.MarkInspected(a.Def.Type);
                    fs.Resolve(a.Def.Type);
                }
            }
            return sb.ToString();
        }

        public static void Test_ComboScoringAndSatisfaction()
        {
            _current = nameof(Test_ComboScoringAndSatisfaction);
            // Pure score mechanics: streak counting, break, bonus feeding the total.
            var s = new ScoreSystem();
            s.AddDispatch(); s.AddDispatch(); s.AddDispatch();
            Check(s.CurrentStreak == 3 && s.BestStreak == 3, "streak counts consecutive dispatches");
            s.BreakStreak();
            Check(s.CurrentStreak == 0 && s.BestStreak == 3, "break resets current, keeps best");
            s.AddComboBonus(40);
            Check(s.Compute(0f) == 40, "combo bonus feeds the score");

            // Satisfaction recovery: a dispatch lifts the crowd's mood (checked away from the 100 cap).
            var simSat = NewSim();
            simSat.Guests.Penalize(20f);
            simSat.SetGates(true);
            RunUntil(simSat, () => simSat.Guests.QueueCount >= 2, 30f);
            simSat.LoadPlatformTrain(); simSat.SetGates(false); simSat.VerifyRestraints();
            float beforeSat = simSat.Guests.Satisfaction;
            Check(simSat.Dispatch(), "dispatch for the satisfaction check");
            Check(simSat.Guests.Satisfaction > beforeSat, "smooth dispatch recovers satisfaction");

            // Sim level: fast clean circuits earn combo bonus; a fault breaks the streak.
            var sim = NewSim(c => c.BlockTraverseSeconds = 0.5f);
            int guard = 0;
            while (sim.Score.Dispatches < 3 && guard++ < 4000)
            {
                var p = sim.PlatformTrain();
                if (p != null && p.State == TrainState.InStationLoading)
                {
                    if (p.Riders == 0) p.Board(sim.Config.SeatsPerTrain);
                    sim.SetGates(false);
                    sim.VerifyRestraints();
                }
                if (sim.CanDispatch(out _)) sim.Dispatch();
                sim.Tick(0.1f);
            }
            Check(sim.Score.Dispatches >= 3, "policy completed 3 dispatch cycles");
            Check(sim.Score.CurrentStreak >= 3, "clean cycles build a streak");
            Check(sim.Score.ComboBonusPoints > 0, "streak banks combo bonus points");
            sim.Faults.Trigger(FaultType.GateSensorFault, sim.SimTime);
            Check(sim.Score.CurrentStreak == 0, "fault breaks the streak");
            Check(sim.Score.BestStreak >= 3, "best streak is kept for the results screen");

            // Stalled platform drains satisfaction faster than the same shift without the stall drain.
            var stall = NewSim(c => c.StallGraceSeconds = 2f);
            var noStall = NewSim(c => { c.StallGraceSeconds = 2f; c.SatisfactionStallDrainPerSecond = 0f; });
            for (int i = 0; i < 250; i++) { stall.Tick(0.1f); noStall.Tick(0.1f); }
            Check(stall.Guests.Satisfaction < noStall.Guests.Satisfaction,
                  "stalled platform drains satisfaction");
        }

        public static void Test_ManagerShiftResources()
        {
            _current = nameof(Test_ManagerShiftResources);
            var sim = NewSim(c => { c.TrainCount = 1; c.RandomFaultChancePerSecond = 0f; });
            sim.Guests.Penalize(40f);
            sim.EngageLoto(true);
            Check(sim.Management.OfferGuestCare(), "guest care succeeds during a breakdown");
            Check(sim.Management.Budget == 80 && sim.Guests.Satisfaction == 78f, "care spends budget and restores mood");
            Check(!sim.Management.OfferGuestCare() && sim.Management.Budget == 80, "cooldown rejects duplicate spending");
            Check(sim.LotoEngaged && !sim.RidePowered, "manager action never bypasses lockout");
            sim.Management.ToggleAdmissions();
            sim.Tick(10f);
            Check(sim.Guests.QueueCount == 0, "paused admissions stop arrivals");
            sim.Management.ToggleAdmissions();
            sim.Tick(0.1f);
            Check(sim.Guests.QueueCount == 0, "paused arrivals do not accumulate a reopening burst");
            sim.Tick(30f);
            Check(sim.Management.GuestCareCooldownRemaining == 0f, "care cooldown expires with sim time");
            sim.Management.EarnRevenue(8);
            Check(sim.Management.Budget == 120 && sim.Management.Revenue == 40, "served riders fund management");
            var revenueSim = NewSim(c => { c.TrainCount = 1; c.BlockTraverseSeconds = 0.5f; });
            revenueSim.PlatformTrain().Board(4);
            revenueSim.VerifyRestraints();
            Check(revenueSim.Dispatch(), "revenue test dispatches safely");
            Check(revenueSim.Management.Revenue == 0, "dispatch alone does not earn revenue");
            RunUntil(revenueSim, () => revenueSim.Score.RidersServed == 4, 40f);
            Check(revenueSim.Management.Revenue == 20 && revenueSim.Management.Budget == 140, "completed ride earns revenue once");
            revenueSim.Tick(1f);
            Check(revenueSim.Management.Revenue == 20, "station tick never repeats revenue");
            var closed = new AttractionSim(new SimConfig());
            Check(!closed.Management.ToggleAdmissions() && !closed.Management.OfferGuestCare(), "closed shift rejects management actions");
            var ended = NewSim(c => c.ShiftLengthSeconds = 1f);
            ended.Tick(1f);
            Check(!ended.Management.ToggleAdmissions() && !ended.Management.OfferGuestCare(), "ended shift rejects management actions");
        }

        public static void Test_QueueCapacityNoArrivalDebt()
        {
            _current = nameof(Test_QueueCapacityNoArrivalDebt);
            var guests = new GuestSim(new SimConfig { QueueCapacity = 2, GuestSpawnInterval = 1f });
            guests.Tick(100f, false);
            guests.SetGates(true);
            Check(guests.Board(2) == 2, "full queue boards existing guests");
            guests.Tick(0.1f, false);
            Check(guests.QueueCount == 0, "full queue does not bank phantom arrivals");
            guests.Tick(1f, false);
            Check(guests.QueueCount == 1, "normal arrivals resume after capacity frees");
        }
        public static void Test_LateJoinSnapshot()
        {
            _current = nameof(Test_LateJoinSnapshot);
            var score = new ScoreSystem();
            score.MirrorSnapshot(64, 14, 3, 12.5f, 30, 0, 7, 160);
            Check(score.Dispatches == 14 && score.CurrentStreak == 0 && score.BestStreak == 7,
                "late join after broken streak preserves dispatch total and best");
            int expected = 640 + 75 + 160 + 400 - 25 - 30;
            Check(score.Compute(80) == expected, "client score includes replicated downtime");
            score.MirrorSnapshot(64, 14, 3, 12.5f, 30, 0, 7, 160);
            Check(score.Compute(80) == expected && score.Dispatches == 14, "repeated snapshot is idempotent");
            var sim = NewSim();
            var train = sim.Trains[0];
            train.MirrorPose(2, 0.4f, TrainState.Running, 0.2f);
            train.MirrorOccupants(8, true);
            Check(train.Riders == 8 && train.RestraintsVerified && train.State == TrainState.Running,
                "late joining riders appear on a running train without boarding side effects");
            train.MirrorOccupants(0, false);
            Check(train.Riders == 0 && !train.RestraintsVerified && train.CurrentBlock == 2,
                "unload snapshot clears riders without moving the train");
        }

        public static void Test_AutoOperatorSplitStation()
        {
            _current = nameof(Test_AutoOperatorSplitStation);
            var sim = NewSim(c =>
            {
                c.SeparateLoadUnload = true;
                c.BlockCount = 5;
                c.TrainCount = 2;
                c.BlockTraverseSeconds = 2f;
                c.GuestSpawnInterval = 0.4f;
                c.ShiftLengthSeconds = 180f;
                c.TimedFaultsEnabled = true;
            });
            var auto = new AutoOperator(sim) { Enabled = true };
            bool sawWalkingGuests = false;
            bool sawClosedAfterUnload = false;
            int missedDeadlines = 0;
            sim.Faults.OnDeadlineMissed += f => missedDeadlines++;
            for (int i = 0; i < 1801; i++)
            {
                if (i == 200) sim.Faults.Trigger(FaultType.GateSensorFault, sim.SimTime);
                if (i == 900) sim.Faults.Trigger(FaultType.GuestDroppedItem, sim.SimTime);
                auto.Tick(0.1f);
                if (sim.GuestsUnloading)
                {
                    sawWalkingGuests = true;
                    Check(sim.UnloadDoorOpen && !sim.UnloadAreaClear, "unload door remains open while guests walk out");
                }
                if (sawWalkingGuests && !sim.UnloadDoorOpen) sawClosedAfterUnload = true;
                sim.Tick(0.1f);
            }
            Check(sim.State == AttractionState.EndOfShift, "split auto shift completes");
            Check(sawWalkingGuests && sawClosedAfterUnload, "auto operator performs complete unload cycle");
            Check(sim.Score.RidersServed >= 16 && sim.Score.Dispatches >= 4, "split station sustains repeated service");
            Check(sim.Management.Revenue == sim.Score.RidersServed * 5, "split completed riders fund manager exactly once");
            Check(sim.Score.FaultsResolved >= 2 && missedDeadlines == 0,
                "solo coverage repairs timed operator incidents without losing throughput");
        }
        public static void Test_TimedFaultFairness()
        {
            _current = nameof(Test_TimedFaultFairness);
            var cfg = new SimConfig { TimedFaultsEnabled = true, ShiftLengthSeconds = 600f,
                RandomFaultChancePerSecond = 100f, TechFaultChancePerSecond = 100f };
            var faults = new FaultSystem(cfg, 123) { TechScenarioMode = true };
            faults.Tick(1f, 1f);
            Check(faults.Active.Count == 0, "opening breathing room");
            faults.Tick(1f, 16f);
            Check(faults.Active.Count == 1, "base and technician rolls produce only one repair");
            var first = faults.Active[0];
            int missed = 0;
            faults.OnDeadlineMissed += f => missed++;
            faults.Tick(200f, 216f);
            faults.Tick(1f, 217f);
            Check(first.DeadlineMissed && missed == 1, "deadline consequence fires once");
            Check(faults.Active.Count == 1, "overdue fault never creates a pileup");
            faults.MarkInspected(first.Def.Type); faults.LockoutApplied = true;
            Check(faults.Resolve(first.Def.Type), "overdue repair remains possible");
            faults.Tick(1f, 218f);
            Check(faults.Active.Count == 0, "recovery breathing room");
            faults.Tick(1f, 230f);
            Check(faults.Active.Count == 1, "challenge resumes after recovery");
            var late = new FaultSystem(cfg, 123) { TechScenarioMode = true };
            late.Tick(1f, 580f);
            Check(late.Active.Count == 0, "no impossible last-second generated incident");
            var alert = late.Trigger(FaultType.DispatchTimeout, 580f);
            Check(alert.DeadlineAt == 0f, "condition alerts do not add competing repair deadlines");
            var queuedAlerts = new FaultSystem(cfg, 123);
            queuedAlerts.Trigger(FaultType.DispatchTimeout, 0f);
            queuedAlerts.Trigger(FaultType.BlockOccupiedStacking, 0f);
            queuedAlerts.Tick(1f, 16f);
            Check(queuedAlerts.Active.Count == 3 && queuedAlerts.AnyBlocksDispatch,
                "throughput alerts do not suppress all future repair incidents");
            var remoteSeats = new FaultSystem(cfg, 999);
            for (int i = 16; i < 400; i += 15)
            {
                remoteSeats.Tick(1f, i, false);
                Check(!remoteSeats.HasFault(FaultType.RestraintNotVerified) && !remoteSeats.HasFault(FaultType.RestraintJammedCar),
                    "no restraint deadline without a reachable occupied platform RV");
                if (remoteSeats.Active.Count > 0)
                {
                    var type = remoteSeats.Active[0].Def.Type;
                    remoteSeats.LockoutApplied = true; remoteSeats.MarkInspected(type); remoteSeats.Resolve(type);
                }
            }
            var sim = NewSim(c => c.TimedFaultsEnabled = true);
            sim.PlatformTrain().Board(2);
            sim.Faults.Trigger(FaultType.RestraintJammedCar, 0f);
            Check(!sim.VerifyRestraints(), "jammed bar cannot be verified");
            sim.Faults.Trigger(FaultType.MaintenanceResetRequired, 0f);
            sim.Faults.Tick(80f, 80f);
            Check(!sim.RidePowered && !sim.MaintenanceReset(), "deadline never bypasses lockout");
        }
        public static void Test_ResponseWindowMoodFairness()
        {
            _current = nameof(Test_ResponseWindowMoodFairness);
            var sim = NewSim(c => { c.TimedFaultsEnabled = true; c.TrainCount = 1; c.DispatchTimeoutSeconds = 999f; });
            sim.Management.ToggleAdmissions();
            var fault = sim.Faults.Trigger(FaultType.TrainValleyed, 0f);
            for (int i = 0; i < 100; i++) sim.Tick(1f);
            Check(sim.Guests.Satisfaction > 60f, "100 seconds of allowed response travel does not exhaust mood without a queue");
            sim.Tick(11f);
            float before = sim.Guests.Satisfaction;
            sim.Tick(1f);
            Check(fault.DeadlineMissed && before - sim.Guests.Satisfaction > 1.3f,
                "full emergency and stall drain resumes after deadline");
            var guests = new GuestSim(new SimConfig { GuestSpawnInterval = 1f, SatisfactionDrainPerQueued = 1f });
            guests.Tick(2f, false);
            float queueBefore = guests.Satisfaction;
            guests.Tick(1f, false, 0f, true, 0.15f);
            Check(queueBefore - guests.Satisfaction == 2f, "protected response never discounts queue waiting");
        }
        public static int RunAll(Action<string> print)
        {
            Failures.Clear();
            var tests = new Action[]
            {
                Test_StateTransitions, Test_BlockOccupancy, Test_DispatchRules,
                Test_BlockHoldAndStacking, Test_SeparateLoadUnload, Test_UnloadWorkflow, Test_AddTrain,
                Test_FaultTriggerAndResolution,
                Test_DispatchTimeoutFault, Test_ScoreCalculation, Test_SoloTuning,
                Test_FullShiftEndsAndTelemetry,
                Test_BlockSensorGhostFault, Test_PowerBrownoutStagedRestart, Test_RestraintJamTargetsCar,
                Test_EscalationRampAndSurge, Test_ComboScoringAndSatisfaction, Test_ManagerShiftResources, Test_QueueCapacityNoArrivalDebt,
                Test_LateJoinSnapshot, Test_AutoOperatorSplitStation, Test_TimedFaultFairness, Test_ResponseWindowMoodFairness,
                Test_LockoutTagout, Test_RemoteTechChargesOnlyOnSuccess
            };
            foreach (var t in tests) { t(); print($"  ran {t.Method.Name}"); }
            if (Failures.Count == 0) { print($"ALL {tests.Length} TESTS PASSED"); return 0; }
            foreach (var f in Failures) print("FAIL: " + f);
            return Failures.Count;
        }
    }
}





