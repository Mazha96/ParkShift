using System;
using System.Collections.Generic;

namespace ParkShift.Core
{
    public sealed class FaultDefinition
    {
        public FaultType Type;
        public FaultSeverity Severity;
        public string HmiMessage;
        public string FixHint;
        public bool BlocksDispatch;       // station-level hold
        public bool StopsRide;            // full ride stop (motion frozen)
        public bool RequiresTechnician;   // must be fixed at maintenance panel
        public bool RequiresLoto;         // LOTO lever must be engaged before fix
        public bool RequiresInspection;   // a physical inspect step must happen before resolve
        public float SatisfactionHit;     // one-time hit when triggered
        public int ScorePenalty;
    }

    public sealed class ActiveFault
    {
        public FaultDefinition Def;
        public float TriggeredAt;
        public bool LotoSatisfied;
        public bool Inspected;
    }

    /// <summary>Generates and tracks faults. Random faults roll per second; condition faults
    /// (stacking, dispatch timeout) are raised by AttractionSim.</summary>
    public sealed class FaultSystem
    {
        public static readonly Dictionary<FaultType, FaultDefinition> Catalog = new Dictionary<FaultType, FaultDefinition>
        {
            { FaultType.RestraintNotVerified, new FaultDefinition {
                Type = FaultType.RestraintNotVerified, Severity = FaultSeverity.Minor,
                HmiMessage = "RESTRAINT FAULT — SEAT SENSOR OPEN, ROW 3",
                FixHint = "Inspect the flagged seat on the train, then re-verify restraints.",
                BlocksDispatch = true, SatisfactionHit = 2f, ScorePenalty = 10 } },
            { FaultType.GateSensorFault, new FaultDefinition {
                Type = FaultType.GateSensorFault, Severity = FaultSeverity.Minor,
                HmiMessage = "STATION GATE SENSOR FAULT — GATE 2 NOT CONFIRMED",
                FixHint = "Cycle the gates at the dispatch panel to re-seat the sensor.",
                BlocksDispatch = true, SatisfactionHit = 2f, ScorePenalty = 10 } },
            { FaultType.BlockOccupiedStacking, new FaultDefinition {
                Type = FaultType.BlockOccupiedStacking, Severity = FaultSeverity.Minor,
                HmiMessage = "BLOCK HOLD — TRAIN WAITING ON OCCUPIED BLOCK",
                FixHint = "Clear the station: unload, load, and dispatch the platform train.",
                BlocksDispatch = false, SatisfactionHit = 3f, ScorePenalty = 15 } },
            { FaultType.DispatchTimeout, new FaultDefinition {
                Type = FaultType.DispatchTimeout, Severity = FaultSeverity.Minor,
                HmiMessage = "DISPATCH INTERVAL EXCEEDED — THROUGHPUT ALERT",
                FixHint = "Get the platform train verified and dispatched.",
                BlocksDispatch = false, SatisfactionHit = 4f, ScorePenalty = 20 } },
            { FaultType.GuestDroppedItem, new FaultDefinition {
                Type = FaultType.GuestDroppedItem, Severity = FaultSeverity.Minor,
                HmiMessage = "GUEST ITEM ON TRACK — VISUAL SWEEP REQUIRED",
                FixHint = "Walk to the glowing item near the station edge and pick it up.",
                BlocksDispatch = true, SatisfactionHit = 2f, ScorePenalty = 10 } },
            { FaultType.MaintenanceResetRequired, new FaultDefinition {
                Type = FaultType.MaintenanceResetRequired, Severity = FaultSeverity.Major,
                HmiMessage = "DRIVE CONTROLLER TRIP — MAINTENANCE RESET REQUIRED",
                FixHint = "Technician: engage LOTO lever, reset at maintenance panel, release LOTO.",
                BlocksDispatch = true, StopsRide = true, RequiresTechnician = true, RequiresLoto = true,
                SatisfactionHit = 8f, ScorePenalty = 40 } },
            { FaultType.TrackIntrusion, new FaultDefinition {
                Type = FaultType.TrackIntrusion, Severity = FaultSeverity.Major,
                HmiMessage = "E-STOP — PERSON DETECTED ON TRACK, ZONE 1",
                FixHint = "Technician: go to the track and escort the guest back to the platform.",
                BlocksDispatch = true, StopsRide = true, RequiresTechnician = true,
                SatisfactionHit = 10f, ScorePenalty = 50 } },
            { FaultType.TrainCollision, new FaultDefinition {
                Type = FaultType.TrainCollision, Severity = FaultSeverity.Major,
                HmiMessage = "COLLISION — TRAIN CONTACT ON BRAKE RUN APPROACH",
                FixHint = "Technician: inspect the contact point, engage LOTO, then drive reset in the bay.",
                BlocksDispatch = true, StopsRide = true, RequiresTechnician = true,
                RequiresLoto = true, RequiresInspection = true,
                SatisfactionHit = 15f, ScorePenalty = 80 } },
            { FaultType.TrainValleyed, new FaultDefinition {
                Type = FaultType.TrainValleyed, Severity = FaultSeverity.Major,
                HmiMessage = "TRAIN VALLEYED — STALLED IN COURSE SECTION",
                FixHint = "Technician: engage LOTO, crank the evac winch out on the course, release LOTO.",
                BlocksDispatch = true, StopsRide = true, RequiresTechnician = true, RequiresLoto = true,
                SatisfactionHit = 12f, ScorePenalty = 60 } },
        };

        readonly List<ActiveFault> _active = new List<ActiveFault>();
        public IReadOnlyList<ActiveFault> Active => _active;
        public event Action<ActiveFault> OnFaultTriggered;
        public event Action<ActiveFault> OnFaultResolved;

        readonly Random _rng;
        readonly SimConfig _cfg;
        static readonly FaultType[] RandomPool =
        {
            FaultType.RestraintNotVerified, FaultType.GateSensorFault,
            FaultType.GuestDroppedItem, FaultType.MaintenanceResetRequired
        };

        /// <summary>Major incidents rolled only while an auto-operator runs the booth
        /// (technician scenario mode) — the tech's job is to clear these.</summary>
        static readonly FaultType[] TechScenarioPool =
        {
            FaultType.TrackIntrusion, FaultType.TrainCollision,
            FaultType.TrainValleyed, FaultType.MaintenanceResetRequired
        };

        /// <summary>When true, major incident faults roll in addition to the base pool.</summary>
        public bool TechScenarioMode;

        public FaultSystem(SimConfig cfg, int seed = 0)
        {
            _cfg = cfg;
            _rng = seed == 0 ? new Random() : new Random(seed);
        }

        public bool HasFault(FaultType t) => _active.Exists(f => f.Def.Type == t);
        public bool AnyBlocksDispatch => _active.Exists(f => f.Def.BlocksDispatch);
        public bool AnyStopsRide => _active.Exists(f => f.Def.StopsRide);

        public void Tick(float dt, float simTime)
        {
            if (_rng.NextDouble() < _cfg.RandomFaultChancePerSecond * dt && _active.Count < 2)
            {
                var t = RandomPool[_rng.Next(RandomPool.Length)];
                Trigger(t, simTime);
            }
            // Technician scenario: one major incident at a time, on its own clock
            if (TechScenarioMode && !AnyStopsRide
                && _rng.NextDouble() < _cfg.TechFaultChancePerSecond * dt)
            {
                var t = TechScenarioPool[_rng.Next(TechScenarioPool.Length)];
                Trigger(t, simTime);
            }
        }

        public void MarkInspected(FaultType type)
        {
            var f = _active.Find(a => a.Def.Type == type);
            if (f != null) f.Inspected = true;
        }

        public ActiveFault Trigger(FaultType type, float simTime)
        {
            if (HasFault(type)) return null; // no duplicate stacking of same fault
            var af = new ActiveFault { Def = Catalog[type], TriggeredAt = simTime };
            _active.Add(af);
            OnFaultTriggered?.Invoke(af);
            return af;
        }

        public void MarkLoto(FaultType type)
        {
            var f = _active.Find(a => a.Def.Type == type);
            if (f != null) f.LotoSatisfied = true;
        }

        /// <summary>Attempt to resolve. Fails if LOTO is required but not engaged.</summary>
        public bool Resolve(FaultType type)
        {
            var f = _active.Find(a => a.Def.Type == type);
            if (f == null) return false;
            if (f.Def.RequiresLoto && !f.LotoSatisfied) return false;
            if (f.Def.RequiresInspection && !f.Inspected) return false;
            _active.Remove(f);
            OnFaultResolved?.Invoke(f);
            return true;
        }
    }
}
