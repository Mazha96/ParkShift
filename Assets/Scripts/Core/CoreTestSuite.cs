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
            sim.VerifyRestraints();
            Check(!sim.CanDispatch(out r) && r.Contains("gates"), "deny gates open");
            sim.SetGates(false);
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
            sim.LoadPlatformTrain(); sim.VerifyRestraints(); sim.SetGates(false);
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
            Check(sim.ReleaseRestraints(), "operator releases restraints");
            Check(rv.Riders == 0, "riders drop on release");
            Check(!sim.CloseUnloadDoor(), "door cannot close mid-unload");

            RunUntil(sim, () => sim.UnloadAreaClear, 5f);
            Check(sim.UnloadAreaClear, "unload area clears after the unload time");
            Check(sim.CloseUnloadDoor(), "door closes once guests have cleared");

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
                    fs.MarkLoto(a.Def.Type); fs.MarkInspected(a.Def.Type);
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
                    fs.MarkLoto(a.Def.Type); fs.MarkInspected(a.Def.Type);
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
                Test_EscalationRampAndSurge, Test_ComboScoringAndSatisfaction
            };
            foreach (var t in tests) { t(); print($"  ran {t.Method.Name}"); }
            if (Failures.Count == 0) { print($"ALL {tests.Length} TESTS PASSED"); return 0; }
            foreach (var f in Failures) print("FAIL: " + f);
            return Failures.Count;
        }
    }
}
