using System;
using ParkShift.Core;
using System.Linq;

/// Headless scripted playtest: a competent bot operator runs a full shift.
static class Playtest
{
    public static void Run()
    {
        var cfg = new SimConfig(); // default chaos, 180s shift
        var sim = new AttractionSim(cfg, seed: 42);
        sim.OpenShift();
        float dt = 0.1f;
        bool gatesWanted = true;
        while (sim.State != AttractionState.EndOfShift)
        {
            // Bot operator policy
            foreach (var f in sim.Faults.Active.ToArray())
            {
                switch (f.Def.Type)
                {
                    case FaultType.RestraintNotVerified: sim.InspectFaultySeat(); break;
                    case FaultType.GateSensorFault: sim.CycleGates(); gatesWanted = true; break;
                    case FaultType.GuestDroppedItem: sim.ClearDroppedItem(); break;
                    case FaultType.MaintenanceResetRequired:
                        if (!sim.LotoEngaged) sim.EngageLoto(true);
                        else { sim.MaintenanceReset(); sim.EngageLoto(false); }
                        break;
                    // Extended-catalog faults (inert on default config — pool is unchanged there)
                    case FaultType.BlockSensorGhost:
                        if (!f.Inspected) sim.VerifyBlockClear(); else sim.ResetBlockSensor();
                        break;
                    case FaultType.RestraintJammedCar: sim.UnjamRestraint(f.TargetIndex); break;
                    case FaultType.PowerBrownout: sim.AdvanceBrownoutStage(); break;
                }
            }
            var p = sim.PlatformTrain();
            if (p != null && p.State == TrainState.InStationLoading)
            {
                if (!sim.Guests.GatesOpen && gatesWanted) sim.SetGates(true);
                sim.LoadPlatformTrain();
                if (p.Riders >= cfg.SeatsPerTrain || (p.Riders > 0 && sim.Guests.QueueCount == 0))
                { sim.SetGates(false); sim.VerifyRestraints(); }
            }
            string why;
            if (sim.CanDispatch(out why)) sim.Dispatch();
            sim.Tick(dt);
        }
        int score = sim.Score.Compute(sim.Guests.Satisfaction);
        Console.WriteLine($"\n=== HEADLESS PLAYTEST RESULT ===");
        Console.WriteLine($"Riders served:   {sim.Score.RidersServed}");
        Console.WriteLine($"Dispatches:      {sim.Score.Dispatches}");
        Console.WriteLine($"Faults resolved: {sim.Score.FaultsResolved}");
        Console.WriteLine($"Best streak:     x{sim.Score.BestStreak}");
        Console.WriteLine($"Combo bonus:     +{sim.Score.ComboBonusPoints}");
        Console.WriteLine($"Downtime:        {sim.Score.DowntimeSeconds:0.0}s");
        Console.WriteLine($"Satisfaction:    {sim.Guests.Satisfaction:0.0}");
        Console.WriteLine($"SCORE: {score}  GRADE: {sim.Score.Grade(score)}");
        Console.WriteLine($"\nTelemetry ({sim.Telemetry.Entries.Count} entries, first 25):");
        for (int i = 0; i < Math.Min(25, sim.Telemetry.Entries.Count); i++)
            Console.WriteLine("  " + sim.Telemetry.Entries[i]);
    }

    /// The config PrototypeAttraction actually ships with. Mirrors AttractionManager.Awake
    /// (no SimConfigSO assigned) plus the SceneBuilder inspector values: split station,
    /// 2 RVs, 5 track blocks, escalated shift, timed faults. Keep in sync with those two.
    static SimConfig ShippedConfig(bool solo)
    {
        var cfg = solo ? SimConfig.Solo() : SimConfig.Coop(2);
        cfg.SeparateLoadUnload = true;
        cfg.TrainCount = 2;
        cfg.BlockSpeedMultipliers = new[] { 1f, 1f, 1.7f, 1.15f, 0.6f };
        cfg.ExtendedFaultsEnabled = true;
        cfg.FaultRampMultiplier = 2f;
        cfg.GuestArrivalRampMultiplier = 1.75f;
        cfg.SurgeWavesEnabled = true;
        cfg.BlockCount = 5;
        cfg.TimedFaultsEnabled = true;
        return cfg;
    }

    /// A fault still active this long past its trigger means the crew could not fix it.
    const float SoftlockSeconds = 150f;
    /// Crew travel/reaction time before the tech starts on a major incident.
    const float TechReactionSeconds = 8f;

    /// Runs the shipped config across many seeds with an auto-operator in the booth and a
    /// bot technician on major incidents. Returns the number of failed seeds.
    /// Fails a seed on: exception, softlock (fault outlives SoftlockSeconds, or Faulted with
    /// no active fault), shift never ending, or zero riders served.
    public static int RunShipped(int seeds, float chaos = 1f)
    {
        int failed = 0;
        int totalFaults = 0, totalRiders = 0;
        var seen = new System.Collections.Generic.HashSet<FaultType>();
        Console.WriteLine($"\n=== SHIPPED-CONFIG BOT SWEEP ({seeds} seeds x solo/coop, fault rate x{chaos}) ===");
        foreach (bool solo in new[] { true, false })
        for (int seed = 1; seed <= seeds; seed++)
        {
            string fail = null;
            AttractionSim sim = null;
            try
            {
                var cfg = ShippedConfig(solo);
                cfg.RandomFaultChancePerSecond *= chaos;
                cfg.TechFaultChancePerSecond *= chaos;
                sim = new AttractionSim(cfg, seed);
                sim.Faults.TechScenarioMode = true; // a technician is on the crew: majors roll
                sim.Faults.OnFaultTriggered += f => seen.Add(f.Def.Type);
                var op = new AutoOperator(sim) { Enabled = true };
                sim.OpenShift();
                const float dt = 0.1f;
                float limit = cfg.ShiftLengthSeconds + 60f;
                while (sim.State != AttractionState.EndOfShift && sim.SimTime < limit)
                {
                    TechTick(sim);
                    op.Tick(dt);
                    sim.Tick(dt);
                    foreach (var f in sim.Faults.Active)
                        if (sim.SimTime - f.TriggeredAt > SoftlockSeconds)
                            fail = $"softlock: {f.Def.Type} active {sim.SimTime - f.TriggeredAt:0}s";
                    if (sim.State == AttractionState.Faulted && sim.Faults.Active.Count == 0)
                        fail = "softlock: Faulted with no active fault";
                    if (fail != null) break;
                }
                if (fail == null && sim.State != AttractionState.EndOfShift) fail = "shift never ended";
                if (fail == null && sim.Score.RidersServed == 0) fail = "no riders served";
            }
            catch (Exception e) { fail = "exception: " + e.GetType().Name + ": " + e.Message; }

            if (sim != null) { totalFaults += sim.Score.FaultsResolved; totalRiders += sim.Score.RidersServed; }
            if (fail != null)
            {
                failed++;
                Console.WriteLine($"  FAIL {(solo ? "solo" : "coop")} seed {seed} @ {sim?.SimTime:0.0}s: {fail}");
                if (sim != null)
                    foreach (var e in sim.Telemetry.Entries.Skip(Math.Max(0, sim.Telemetry.Entries.Count - 12)))
                        Console.WriteLine("    " + e);
            }
        }
        int runs = seeds * 2;
        Console.WriteLine($"Runs: {runs}  failed: {failed}  avg riders: {totalRiders / (float)runs:0.0}  avg faults resolved: {totalFaults / (float)runs:0.0}");
        var missing = Enum.GetValues(typeof(FaultType)).Cast<FaultType>().Where(t => !seen.Contains(t)).ToArray();
        Console.WriteLine("Fault types never seen: " + (missing.Length == 0 ? "none" : string.Join(", ", missing)));
        Console.WriteLine(failed == 0 ? "BOT SWEEP PASSED" : "BOT SWEEP FAILED");
        return failed;
    }

    /// Bot technician: handles every fault the auto-operator leaves alone (RequiresTechnician),
    /// applying lockout when a repair needs it and releasing it once nothing does.
    static void TechTick(AttractionSim sim)
    {
        bool needsLoto = false;
        foreach (var f in sim.Faults.Active.ToArray())
        {
            if (!f.Def.RequiresTechnician) continue;
            if (sim.SimTime - f.TriggeredAt < TechReactionSeconds) { needsLoto |= f.Def.RequiresLoto; continue; }
            if (f.Def.RequiresLoto) { needsLoto = true; if (!sim.LotoEngaged) { sim.EngageLoto(true); return; } }
            switch (f.Def.Type)
            {
                case FaultType.MaintenanceResetRequired: sim.MaintenanceReset(); break;
                case FaultType.TrainCollision:
                    if (!f.Inspected) sim.InspectCollision(); else sim.MaintenanceReset();
                    break;
                case FaultType.TrainValleyed: sim.WinchValleyedTrain(); break;
                case FaultType.TrackIntrusion: sim.EscortIntruder(); break;
                case FaultType.PowerBrownout: sim.AdvanceBrownoutStage(); break;
            }
        }
        if (!needsLoto && sim.LotoEngaged) sim.EngageLoto(false);
    }
}
