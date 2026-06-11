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
                Test_BlockHoldAndStacking, Test_FaultTriggerAndResolution,
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
