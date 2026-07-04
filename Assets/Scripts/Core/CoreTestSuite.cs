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

        public static int RunAll(Action<string> print)
        {
            Failures.Clear();
            var tests = new Action[]
            {
                Test_StateTransitions, Test_BlockOccupancy, Test_DispatchRules,
                Test_BlockHoldAndStacking, Test_SeparateLoadUnload, Test_UnloadWorkflow, Test_AddTrain,
                Test_FaultTriggerAndResolution,
                Test_DispatchTimeoutFault, Test_ScoreCalculation, Test_SoloTuning,
                Test_FullShiftEndsAndTelemetry
            };
            foreach (var t in tests) { t(); print($"  ran {t.Method.Name}"); }
            if (Failures.Count == 0) { print($"ALL {tests.Length} TESTS PASSED"); return 0; }
            foreach (var f in Failures) print("FAIL: " + f);
            return Failures.Count;
        }
    }
}
