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
        /// <summary>Fault-specific target (e.g. the jammed car index). -1 when not applicable.</summary>
        public int TargetIndex = -1;
        /// <summary>Staged-fix progress (power brownout restart stages). 0 = not started.</summary>
        public int Stage;
        /// <summary>Sim time of the last accepted stage advance (staged fixes).</summary>
        public float LastStageAt;
        /// <summary>Optional instance-specific HMI text (e.g. names the jammed car).</summary>
        public string Detail;
        public float DeadlineAt;
        public bool DeadlineMissed;
        public float ResponseSecondsRemaining(float simTime) => Math.Max(0f, DeadlineAt - simTime);
        /// <summary>What the HMI should display for this instance (falls back to the catalog message).</summary>
        public string HmiText { get { return Detail ?? Def.HmiMessage; } }
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
            { FaultType.BlockSensorGhost, new FaultDefinition {
                Type = FaultType.BlockSensorGhost, Severity = FaultSeverity.Minor,
                HmiMessage = "BLOCK SENSOR GHOST — PHANTOM OCCUPANCY, ZONE 2",
                FixHint = "Walk the course to zone 2, visually confirm the block is clear, then reset the sensor at the dispatch panel.",
                BlocksDispatch = true, RequiresInspection = true,
                SatisfactionHit = 3f, ScorePenalty = 15 } },
            { FaultType.PowerBrownout, new FaultDefinition {
                Type = FaultType.PowerBrownout, Severity = FaultSeverity.Major,
                HmiMessage = "POWER BROWNOUT — DRIVE BUS UNDERVOLT, STAGED RESTART REQUIRED",
                FixHint = "Technician: staged restart at the drive cabinet — energize each of the 3 bus stages in order, letting each stabilize.",
                BlocksDispatch = true, StopsRide = true, RequiresTechnician = true,
                SatisfactionHit = 9f, ScorePenalty = 45 } },
            { FaultType.RestraintJammedCar, new FaultDefinition {
                Type = FaultType.RestraintJammedCar, Severity = FaultSeverity.Minor,
                HmiMessage = "RESTRAINT JAM — LAP BAR NOT SEATING (SEE HMI FOR CAR)",
                FixHint = "Walk the train, free the lap bar on the flagged car, then re-verify restraints.",
                BlocksDispatch = true, SatisfactionHit = 3f, ScorePenalty = 15 } },
        };

        readonly List<ActiveFault> _active = new List<ActiveFault>();
        public IReadOnlyList<ActiveFault> Active => _active;
        public event Action<ActiveFault> OnFaultTriggered;
        public event Action<ActiveFault> OnFaultResolved;
        public event Action<ActiveFault> OnDeadlineMissed;
        float _simTime;
        float _nextIncidentAt = 15f;
        bool _restraintTargetAvailable = true;

        readonly Random _rng;
        readonly SimConfig _cfg;
        static readonly FaultType[] RandomPool =
        {
            FaultType.RestraintNotVerified, FaultType.GateSensorFault,
            FaultType.GuestDroppedItem, FaultType.MaintenanceResetRequired
        };

        /// <summary>Base pool plus the three extended faults — used only when
        /// SimConfig.ExtendedFaultsEnabled so legacy runs keep the exact RNG stream.</summary>
        static readonly FaultType[] ExtendedRandomPool =
        {
            FaultType.RestraintNotVerified, FaultType.GateSensorFault,
            FaultType.GuestDroppedItem, FaultType.MaintenanceResetRequired,
            FaultType.BlockSensorGhost, FaultType.RestraintJammedCar, FaultType.PowerBrownout
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

        public void Tick(float dt, float simTime, bool restraintTargetAvailable = true)
        {
            _simTime = simTime;
            _restraintTargetAvailable = restraintTargetAvailable;
            if (_cfg.TimedFaultsEnabled)
            {
                foreach (var fault in _active)
                {
                    if (fault.DeadlineAt <= 0f || fault.DeadlineMissed || simTime < fault.DeadlineAt) continue;
                    fault.DeadlineMissed = true;
                    OnDeadlineMissed?.Invoke(fault);
                }
                // A repair never competes with a second randomly generated shutdown.
                // Condition alerts may coexist, but do not prevent the crew recovering.
                if (AnyBlocksDispatch || simTime < _nextIncidentAt) return;
            }
            // Escalation ramp: chance scales linearly toward base*FaultRampMultiplier at shift end.
            // Guarded so the default (multiplier 1) leaves the legacy math byte-identical.
            float chance = _cfg.RandomFaultChancePerSecond;
            if (_cfg.FaultRampMultiplier != 1f && _cfg.ShiftLengthSeconds > 0f)
            {
                float p = simTime >= _cfg.ShiftLengthSeconds ? 1f : simTime / _cfg.ShiftLengthSeconds;
                if (p < 0f) p = 0f;
                chance *= 1f + (_cfg.FaultRampMultiplier - 1f) * p;
            }
            if (_rng.NextDouble() < chance * dt && (_cfg.TimedFaultsEnabled || _active.Count < 2))
            {
                var pool = _cfg.ExtendedFaultsEnabled ? ExtendedRandomPool : RandomPool;
                var t = pool[_rng.Next(pool.Length)];
                TrySchedule(t, simTime);
            }
            // Technician scenario: one major incident at a time, on its own clock
            if (TechScenarioMode && !AnyStopsRide
                && _rng.NextDouble() < _cfg.TechFaultChancePerSecond * dt)
            {
                var t = TechScenarioPool[_rng.Next(TechScenarioPool.Length)];
                if (!_cfg.TimedFaultsEnabled || !AnyBlocksDispatch) TrySchedule(t, simTime);
            }
        }

        void TrySchedule(FaultType type, float simTime)
        {
            if (_cfg.TimedFaultsEnabled && !_restraintTargetAvailable
                && (type == FaultType.RestraintNotVerified || type == FaultType.RestraintJammedCar)) return;
            if (_cfg.TimedFaultsEnabled && _cfg.ShiftLengthSeconds - simTime < ResponseWindow(type) + 5f) return;
            Trigger(type, simTime);
        }

        public static float ResponseWindow(FaultType type)
        {
            switch (type)
            {
                case FaultType.BlockOccupiedStacking:
                case FaultType.DispatchTimeout: return 0f;
                case FaultType.TrainValleyed: return 110f;
                case FaultType.TrainCollision: return 90f;
                case FaultType.BlockSensorGhost:
                case FaultType.MaintenanceResetRequired:
                case FaultType.PowerBrownout: return 75f;
                case FaultType.TrackIntrusion: return 60f;
                default: return 60f;
            }
        }

        public void MarkInspected(FaultType type)
        {
            var f = _active.Find(a => a.Def.Type == type);
            if (f != null) f.Inspected = true;
        }

        /// <summary>The active instance of a fault type, or null.</summary>
        public ActiveFault Find(FaultType type) => _active.Find(a => a.Def.Type == type);

        public ActiveFault Trigger(FaultType type, float simTime)
        {
            if (HasFault(type)) return null; // no duplicate stacking of same fault
            _simTime = Math.Max(_simTime, simTime);
            var af = new ActiveFault { Def = Catalog[type], TriggeredAt = simTime };
            if (_cfg.TimedFaultsEnabled && ResponseWindow(type) > 0f)
                af.DeadlineAt = simTime + ResponseWindow(type);
            if (type == FaultType.RestraintJammedCar)
            {
                // Seeded pick of the jammed car. Only consumes RNG for this type, so the
                // legacy stream is untouched unless the extended catalog is in play.
                af.TargetIndex = _rng.Next(Math.Max(1, _cfg.CarsPerTrain));
                af.Detail = string.Format("RESTRAINT JAM — CAR {0} LAP BAR NOT SEATING", af.TargetIndex + 1);
            }
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
            if (_cfg.TimedFaultsEnabled && f.Def.BlocksDispatch)
                _nextIncidentAt = _simTime + Math.Max(0f, _cfg.IncidentRecoverySeconds);
            OnFaultResolved?.Invoke(f);
            return true;
        }
    }
}
