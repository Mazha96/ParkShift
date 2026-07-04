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
}
