#if FISHNET
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Connection;
using FishNet.Object.Synchronizing;
using UnityEngine;
using ParkShift.Attraction;
using ParkShift.Core;

namespace ParkShift.Networking
{
    /// <summary>
    /// Server-authoritative replication of the attraction.
    /// - Server: ticks AttractionSim (via AttractionManager) and pushes 10 Hz snapshots.
    /// - Clients: verbs route here as ServerRpcs; server validates via the same
    ///   AttractionManager.Execute() path used in solo (single source of truth).
    /// - Clients additionally MIRROR the snapshot into their local (never-ticked) Sim
    ///   through the Core public API, so HmiPanel / PanelInteractable / prop drivers
    ///   that read AttractionManager.Instance.Sim stay truthful without UI changes.
    ///
    /// Replication layout (SyncVars only — FishNet sends them on change and delivers
    /// current values to late joiners on spawn, so a join mid-shift converges on the
    /// first ApplyMirror pass):
    ///   - Ops:    state, satisfaction, queue, shift clock, gates, LOTO, unload workflow.
    ///   - Trains: fixed MaxTrainSlots (4) slots — pose Vector4 (block, progress, state,
    ///             speed) + packed int (riders | verified bit); -1 int = slot empty.
    ///             Covers runtime-added RVs (HMI "Add RV") and 1..4-train configs.
    ///   - Faults: fixed MaxFaultSlots (4) slots — packed int (type | stage | target |
    ///             inspected | loto) + per-instance HMI text (f.HmiText, e.g. the
    ///             jammed-car line); -1 = slot empty.
    ///   - Score:  riders, dispatches, faults resolved, penalty, streak, best, combo.
    ///   - Config: effective presentation knobs (train/block count, escalated flag,
    ///             split station, cars per train, brownout stages, shift length).
    /// Everything a snapshot writes is unchanged most ticks, so the 10 Hz push costs
    /// nearly nothing on the wire (SyncVars only dirty on value change).
    /// </summary>
    public class AttractionNetSync : NetworkBehaviour
    {
        public const int MaxTrainSlots = 4;
        public const int MaxFaultSlots = 4;
        const float SnapshotInterval = 0.1f; // 10 Hz

        public static AttractionNetSync Instance { get; private set; }

        // ---- Ops / station state ----
        readonly SyncVar<int> _state = new SyncVar<int>();
        readonly SyncVar<float> _satisfaction = new SyncVar<float>();
        readonly SyncVar<int> _queueCount = new SyncVar<int>();
        readonly SyncVar<float> _shiftRemaining = new SyncVar<float>();
        readonly SyncVar<bool> _gatesOpen = new SyncVar<bool>();
        readonly SyncVar<bool> _lotoEngaged = new SyncVar<bool>();
        readonly SyncVar<bool> _unloadDoorOpen = new SyncVar<bool>();
        readonly SyncVar<bool> _restraintsReleased = new SyncVar<bool>();
        readonly SyncVar<bool> _unloadAreaClear = new SyncVar<bool>();
        readonly SyncVar<bool> _guestsUnloading = new SyncVar<bool>();

        readonly SyncVar<int> _managementBudget = new SyncVar<int>(120);
        readonly SyncVar<int> _managementRevenue = new SyncVar<int>();
        readonly SyncVar<int> _managementCareCount = new SyncVar<int>();
        readonly SyncVar<bool> _admissionsPaused = new SyncVar<bool>();
        readonly SyncVar<float> _careCooldown = new SyncVar<float>();
        readonly SyncVar<float> _assistantRemaining = new SyncVar<float>(-1f);
        readonly Dictionary<NetworkConnection, PlayerRole> _roles = new Dictionary<NetworkConnection, PlayerRole>();

        // ---- Train slots (fixed max, per-slot occupancy via flags == -1) ----
        // Pose: (block, progress, trainState, speed). Flags: riders | (verified ? 1<<8 : 0).
        readonly SyncVar<Vector4> _train0Pose = new SyncVar<Vector4>();
        readonly SyncVar<Vector4> _train1Pose = new SyncVar<Vector4>();
        readonly SyncVar<Vector4> _train2Pose = new SyncVar<Vector4>();
        readonly SyncVar<Vector4> _train3Pose = new SyncVar<Vector4>();
        readonly SyncVar<int> _train0Flags = new SyncVar<int>(-1);
        readonly SyncVar<int> _train1Flags = new SyncVar<int>(-1);
        readonly SyncVar<int> _train2Flags = new SyncVar<int>(-1);
        readonly SyncVar<int> _train3Flags = new SyncVar<int>(-1);
        readonly SyncVar<int> _trainCount = new SyncVar<int>();

        // ---- Fault slots (fixed max) ----
        // Packed: type | stage<<8 | (targetIndex+1)<<16 | inspected<<24 | loto<<25. -1 = empty.
        readonly SyncVar<int> _fault0 = new SyncVar<int>(-1);
        readonly SyncVar<int> _fault1 = new SyncVar<int>(-1);
        readonly SyncVar<int> _fault2 = new SyncVar<int>(-1);
        readonly SyncVar<int> _fault3 = new SyncVar<int>(-1);
        readonly SyncVar<float> _fault0Deadline = new SyncVar<float>();
        readonly SyncVar<float> _fault1Deadline = new SyncVar<float>();
        readonly SyncVar<float> _fault2Deadline = new SyncVar<float>();
        readonly SyncVar<float> _fault3Deadline = new SyncVar<float>();
        readonly SyncVar<string> _fault0Text = new SyncVar<string>("");
        readonly SyncVar<string> _fault1Text = new SyncVar<string>("");
        readonly SyncVar<string> _fault2Text = new SyncVar<string>("");
        readonly SyncVar<string> _fault3Text = new SyncVar<string>("");

        // ---- Score / streak ----
        readonly SyncVar<int> _ridersServed = new SyncVar<int>();
        readonly SyncVar<int> _dispatches = new SyncVar<int>();
        readonly SyncVar<int> _faultsResolved = new SyncVar<int>();
        readonly SyncVar<int> _penaltyPoints = new SyncVar<int>();
        readonly SyncVar<int> _currentStreak = new SyncVar<int>();
        readonly SyncVar<int> _bestStreak = new SyncVar<int>();
        readonly SyncVar<int> _comboBonus = new SyncVar<int>();
        readonly SyncVar<float> _downtime = new SyncVar<float>();

        // ---- Effective config (set once server-side; late joiners get it on spawn) ----
        readonly SyncVar<int> _cfgTrainCount = new SyncVar<int>();
        readonly SyncVar<int> _cfgBlockCount = new SyncVar<int>();
        readonly SyncVar<bool> _cfgEscalatedShift = new SyncVar<bool>();
        readonly SyncVar<bool> _cfgSeparateLoadUnload = new SyncVar<bool>();
        readonly SyncVar<int> _cfgCarsPerTrain = new SyncVar<int>();
        readonly SyncVar<int> _cfgBrownoutStageCount = new SyncVar<int>();
        readonly SyncVar<float> _cfgShiftLength = new SyncVar<float>();

        // Keep SyncVars as concrete fields: FishNet ILPP treats SyncVar arrays as SyncTypes.
        SyncVar<Vector4> TrainPose(int slot)
        {
            switch (slot) { case 0: return _train0Pose; case 1: return _train1Pose;
                case 2: return _train2Pose; default: return _train3Pose; }
        }
        SyncVar<int> TrainFlags(int slot)
        {
            switch (slot) { case 0: return _train0Flags; case 1: return _train1Flags;
                case 2: return _train2Flags; default: return _train3Flags; }
        }
        SyncVar<int> FaultData(int slot)
        {
            switch (slot) { case 0: return _fault0; case 1: return _fault1;
                case 2: return _fault2; default: return _fault3; }
        }
        SyncVar<string> FaultText(int slot)
        {
            switch (slot) { case 0: return _fault0Text; case 1: return _fault1Text;
                case 2: return _fault2Text; default: return _fault3Text; }
        }
        SyncVar<float> FaultDeadline(int slot)
        {
            switch (slot) { case 0: return _fault0Deadline; case 1: return _fault1Deadline;
                case 2: return _fault2Deadline; default: return _fault3Deadline; }
        }

        float _pushTimer;
        bool _slotWarned;

        void Awake()
        {
            Instance = this;
        }

        public bool HasTechnician
        {
            get
            {
                foreach (var role in _roles.Values) if (role == PlayerRole.Technician) return true;
                return false;
            }
        }

        void OnRemoteConnectionState(NetworkConnection connection, FishNet.Transporting.RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState == FishNet.Transporting.RemoteConnectionState.Stopped)
                _roles.Remove(connection);
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null) return;
            m.IsAuthoritative = true;
            var s = m.Sim;
            _cfgTrainCount.Value = s.Trains.Length;
            _cfgBlockCount.Value = s.Config.BlockCount;
            _cfgEscalatedShift.Value = m.EscalatedShift;
            _cfgSeparateLoadUnload.Value = s.Config.SeparateLoadUnload;
            _cfgCarsPerTrain.Value = s.Config.CarsPerTrain;
            _cfgBrownoutStageCount.Value = s.Config.BrownoutStageCount;
            _cfgShiftLength.Value = s.Config.ShiftLengthSeconds;
            PushSnapshot(); // seed everything immediately so early joiners never see defaults
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (Player.RoleController.Local != null) SelectRole(Player.RoleController.Local.Role);
            if (IsServerInitialized) return; // host: local sim is already authoritative
            AttractionManager.Instance.VerbRouter = v => { ServerSubmitVerb((int)v); return true; };
            WarnOnConfigMismatch();
        }

        void Update()
        {
            _pushTimer += Time.deltaTime;
            if (_pushTimer < SnapshotInterval) return; // 10 Hz both directions
            _pushTimer = 0f;
            if (IsServerInitialized) PushSnapshot();
            else if (IsClientInitialized) ApplyMirror();
        }

        // ================= SERVER: sim -> SyncVars =================

        void PushSnapshot()
        {
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null) return;
            var s = m.Sim;

            _managementBudget.Value = s.Management.Budget;
            _managementRevenue.Value = s.Management.Revenue;
            _managementCareCount.Value = s.Management.GuestCareCount;
            _admissionsPaused.Value = s.Management.AdmissionsPaused;
            _careCooldown.Value = s.Management.GuestCareCooldownRemaining;
            _assistantRemaining.Value = m.AssistantRemaining;
            _state.Value = (int)s.State;
            _satisfaction.Value = s.Guests.Satisfaction;
            _queueCount.Value = s.Guests.QueueCount;
            _shiftRemaining.Value = s.ShiftRemaining;
            _gatesOpen.Value = s.Guests.GatesOpen;
            _lotoEngaged.Value = s.LotoEngaged;
            _unloadDoorOpen.Value = s.UnloadDoorOpen;
            _restraintsReleased.Value = s.RestraintsReleased;
            _unloadAreaClear.Value = s.UnloadAreaClear;
            _guestsUnloading.Value = s.GuestsUnloading;

            // Trains: N-robust — fill slots for however many RVs exist (1..MaxTrainSlots),
            // mark the rest empty. Runtime-added RVs (Sim.AddTrain) appear the tick they spawn.
            _trainCount.Value = s.Trains.Length;
            for (int i = 0; i < MaxTrainSlots; i++)
            {
                if (i < s.Trains.Length)
                {
                    var t = s.Trains[i];
                    TrainPose(i).Value = new Vector4(t.CurrentBlock, t.Progress, (int)t.State, t.Speed);
                    TrainFlags(i).Value = (t.Riders & 0xFF) | (t.RestraintsVerified ? 1 << 8 : 0);
                }
                else
                {
                    TrainFlags(i).Value = -1;
                }
            }
            if (s.Trains.Length > MaxTrainSlots && !_slotWarned)
            {
                _slotWarned = true;
                Debug.LogWarning("[Net] " + s.Trains.Length + " RVs but only " + MaxTrainSlots +
                                 " replicated train slots — raise MaxTrainSlots (and add SyncVars) to cover them.");
            }

            // Faults: full instance state so client prompts (stage, inspected, flagged car)
            // and the HMI line (f.HmiText — per-instance detail, e.g. the jammed car) are truthful.
            for (int i = 0; i < MaxFaultSlots; i++)
            {
                if (i < s.Faults.Active.Count)
                {
                    var f = s.Faults.Active[i];
                    FaultData(i).Value = EncodeFault(f);
                    FaultText(i).Value = f.HmiText ?? "";
                    FaultDeadline(i).Value = f.DeadlineAt;
                }
                else
                {
                    FaultData(i).Value = -1;
                    FaultText(i).Value = "";
                    FaultDeadline(i).Value = 0f;
                }
            }

            _ridersServed.Value = s.Score.RidersServed;
            _dispatches.Value = s.Score.Dispatches;
            _faultsResolved.Value = s.Score.FaultsResolved;
            _penaltyPoints.Value = s.Score.PenaltyPoints;
            _currentStreak.Value = s.Score.CurrentStreak;
            _bestStreak.Value = s.Score.BestStreak;
            _comboBonus.Value = s.Score.ComboBonusPoints;
            _downtime.Value = s.Score.DowntimeSeconds;
        }

        static int EncodeFault(ActiveFault f)
        {
            int v = (int)f.Def.Type & 0xFF;
            v |= (f.Stage & 0xFF) << 8;
            v |= ((f.TargetIndex + 1) & 0xFF) << 16; // +1 so -1 (n/a) encodes as 0
            if (f.Inspected) v |= 1 << 24;
            if (f.LotoSatisfied) v |= 1 << 25;
            if (f.DeadlineMissed) v |= 1 << 26;
            return v;
        }

        // ================= CLIENT: SyncVars -> local mirror sim =================

        /// <summary>
        /// Reconcile the client's local (never-ticked) Sim against the latest snapshot,
        /// using existing Core public API plus the narrow Mirror* setters added for this
        /// purpose (MirrorPose / MirrorQueue / MirrorOps — used ONLY here, never when
        /// authoritative). Idempotent — a late joiner converges on the first pass (opens
        /// shift, adds RVs, raises faults, sets gates/LOTO, stamps poses, tops up score).
        /// </summary>
        void ApplyMirror()
        {
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null || m.IsAuthoritative) return;
            var s = m.Sim;

            m.MirrorAssistant(_assistantRemaining.Value);
            s.Management.MirrorState(_managementBudget.Value, _managementRevenue.Value,
                _managementCareCount.Value, _admissionsPaused.Value, _careCooldown.Value);

            // 1) Shift opened on the server -> open the mirror (drives Closed -> Ready).
            if ((AttractionState)_state.Value != AttractionState.Closed && s.State == AttractionState.Closed)
                s.OpenShift();

            // 2) RV count: adding trains locally fires Sim.OnTrainAdded, so the visual
            //    layer spawns bodies for runtime-added RVs and for all RVs on late join.
            int guard = 0;
            while (s.Trains.Length < _trainCount.Value && guard++ < MaxTrainSlots * 2)
                if (s.AddTrain() == null) break;

            // 3) Train poses: stamp block/progress/state/speed straight onto each RV
            //    (TrainSim.MirrorPose re-registers block occupancy on block changes), so
            //    TrainVisuals move and PlatformTrain()/UnloadingTrain() resolve truthfully
            //    on clients.
            for (int i = 0; i < s.Trains.Length && i < MaxTrainSlots; i++)
            {
                int block; float progress; TrainState tState; float tSpeed; int tRiders; bool tVerified;
                if (TryGetMirrorTrain(i, out block, out progress, out tState, out tSpeed,
                                      out tRiders, out tVerified))
                {
                    s.Trains[i].MirrorPose(block, progress, tState, tSpeed);
                    s.Trains[i].MirrorOccupants(tRiders, tVerified);
                }
            }

            // 4) Faults BEFORE satisfaction/score: Trigger/Resolve side effects (penalty,
            //    streak break, Faulted/Ready state flips) mimic the server's own handlers,
            //    and the mirrors below then correct any residual drift.
            MirrorFaults(s);

            // 5) Station/ops toggles (public Core API).
            if (s.Guests.GatesOpen != _gatesOpen.Value) s.Guests.SetGates(_gatesOpen.Value);
            if (s.LotoEngaged != _lotoEngaged.Value) s.EngageLoto(_lotoEngaged.Value);

            // 6) Platform train riders + restraint verification (public TrainSim API),
            //    THEN the queue count — MirrorQueue fires OnGuestsBoarded for the decrease,
            //    and the guest visuals seat the boarded guests on the (already-mirrored)
            //    platform train.
            s.Guests.MirrorQueue(_queueCount.Value);

            // 7) Satisfaction: steer to the snapshot with the public clamped add/subtract.
            float dSat = _satisfaction.Value - s.Guests.Satisfaction;
            if (dSat > 0.01f) s.Guests.RewardDispatch(dSat);
            else if (dSat < -0.01f) s.Guests.Penalize(-dSat);

            // Exact snapshots preserve totals even when a client joins after a streak broke.
            s.Score.MirrorSnapshot(_ridersServed.Value, _dispatches.Value, _faultsResolved.Value,
                _downtime.Value, _penaltyPoints.Value, _currentStreak.Value, _bestStreak.Value, _comboBonus.Value);

            // 9) Ops stamp LAST so the replicated state wins over any transitional flips the
            //    steps above produced. Also drives the shift clock, the unload workflow flags
            //    (OnUnloadReleased edge -> guest walk-out) and EndOfShift (OnShiftEnded edge).
            float shiftLen = _cfgShiftLength.Value > 0f ? _cfgShiftLength.Value : s.Config.ShiftLengthSeconds;
            s.MirrorOps((AttractionState)_state.Value,
                        shiftLen - _shiftRemaining.Value,
                        _unloadDoorOpen.Value, _restraintsReleased.Value,
                        _unloadAreaClear.Value, _guestsUnloading.Value);
        }

        void MirrorFaults(AttractionSim s)
        {
            // Resolve local faults the server no longer has (force LOTO/inspection
            // satisfied first — ActiveFault exposes those as public fields).
            List<FaultType> stale = null;
            for (int i = 0; i < s.Faults.Active.Count; i++)
            {
                var f = s.Faults.Active[i];
                if (!ServerHasFault(f.Def.Type))
                {
                    if (stale == null) stale = new List<FaultType>();
                    stale.Add(f.Def.Type);
                }
            }
            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    var f = s.Faults.Find(stale[i]);
                    if (f != null) { f.LotoSatisfied = true; f.Inspected = true; }
                    s.Faults.Resolve(stale[i]);
                }
            }

            // Trigger/refresh server faults, then stamp full instance state onto the
            // local ActiveFault (Stage / TargetIndex / Inspected / LOTO / HMI text) so
            // PanelInteractable prompts and the HMI read per-instance truth.
            for (int i = 0; i < MaxFaultSlots; i++)
            {
                int packed = FaultData(i).Value;
                if (packed < 0) continue;
                var type = (FaultType)(packed & 0xFF);
                var f = s.Faults.Find(type);
                if (f == null) f = s.Faults.Trigger(type, s.SimTime);
                if (f == null) continue;
                f.Stage = (packed >> 8) & 0xFF;
                f.TargetIndex = ((packed >> 16) & 0xFF) - 1;
                f.Inspected = (packed & (1 << 24)) != 0;
                f.LotoSatisfied = (packed & (1 << 25)) != 0;
                f.DeadlineAt = FaultDeadline(i).Value;
                f.DeadlineMissed = (packed & (1 << 26)) != 0;
                string text = FaultText(i).Value;
                if (!string.IsNullOrEmpty(text)) f.Detail = text;
            }
        }

        bool ServerHasFault(FaultType type)
        {
            for (int i = 0; i < MaxFaultSlots; i++)
            {
                int packed = FaultData(i).Value;
                if (packed >= 0 && (FaultType)(packed & 0xFF) == type) return true;
            }
            return false;
        }

        void MirrorPlatformTrain(AttractionSim s)
        {
            var pt = s.PlatformTrain();
            if (pt == null || pt.Id >= MaxTrainSlots) return;
            int flags = TrainFlags(pt.Id).Value;
            if (flags < 0) return;
            int riders = flags & 0xFF;
            bool verified = (flags & (1 << 8)) != 0;
            if (riders > pt.Riders) pt.Board(riders - pt.Riders);
            else if (riders == 0 && pt.Riders > 0) pt.Unload(s.Config);
            if (verified && !pt.RestraintsVerified) pt.VerifyRestraints();
            else if (!verified && pt.RestraintsVerified) pt.InvalidateRestraints();
        }

        void WarnOnConfigMismatch()
        {
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null) return;
            var c = m.Sim.Config;
            if (c.BlockCount != _cfgBlockCount.Value || m.EscalatedShift != _cfgEscalatedShift.Value
                || c.SeparateLoadUnload != _cfgSeparateLoadUnload.Value)
                Debug.LogWarning("[Net] Client scene config differs from host (blocks " + c.BlockCount + " vs "
                    + _cfgBlockCount.Value + ", escalated " + m.EscalatedShift + " vs " + _cfgEscalatedShift.Value
                    + ", split station " + c.SeparateLoadUnload + " vs " + _cfgSeparateLoadUnload.Value
                    + ") — make sure both builds use the same scene/preset.");
        }

        // ================= Verb routing (client -> server) =================

        [ServerRpc(RequireOwnership = false)]
        void ServerSubmitVerb(int verb, NetworkConnection sender = null)
        {
            if (sender == null) return;
            if (!System.Enum.IsDefined(typeof(OpVerb), verb)) { TargetCommandResult(sender, "Unknown command"); return; }
            if ((OpVerb)verb == OpVerb.ToggleAdmissions || (OpVerb)verb == OpVerb.GuestCare)
            {
                if (!_roles.TryGetValue(sender, out var role) || role != PlayerRole.Manager)
                { TargetCommandResult(sender, "Manager role required"); return; }
            }
            var operation = (OpVerb)verb;
            bool maintenance = operation == OpVerb.ToggleLoto || operation == OpVerb.MaintenanceReset
                || operation == OpVerb.EscortIntruder || operation == OpVerb.InspectCollision
                || operation == OpVerb.WinchTrain || operation == OpVerb.AdvanceBrownoutStage;
            if (maintenance && (sender == null || !_roles.TryGetValue(sender, out var crewRole)
                || (crewRole != PlayerRole.Technician && crewRole != PlayerRole.Manager)))
            { TargetCommandResult(sender, "Technician or Manager role required"); return; }
            // Safety validation remains inside the authoritative simulation.
            var manager = AttractionManager.Instance;
            bool accepted = manager.Execute(operation);
            TargetCommandResult(sender, accepted ? "Host processed command" : manager.CommandRejectionReason(operation));
        }

        [TargetRpc]
        void TargetCommandResult(NetworkConnection target, string message)
        {
            AttractionManager.Instance?.ReportCommandStatus(message);
        }

        /// <summary>Roles remain freely selectable hats, but the server stores the accepted choice.</summary>
        public void SelectRole(PlayerRole role)
        {
            if (NetworkObject != null && IsClientInitialized) ServerSelectRole((int)role);
        }

        [ServerRpc(RequireOwnership = false)]
        void ServerSelectRole(int role, NetworkConnection sender = null)
        {
            if (sender == null || !System.Enum.IsDefined(typeof(PlayerRole), role)) return;
            _roles[sender] = (PlayerRole)role;
        }

        public override void OnStopServer()
        {
            ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            _roles.Clear();
            if (AttractionManager.Instance != null) AttractionManager.Instance.IsAuthoritative = false;
            base.OnStopServer();
        }

        // ================= Mirror accessors (raw snapshot reads) =================
        // Complete read surface for anything the local-sim mirror cannot express yet
        // (train poses, queue count, shift clock, unload workflow, EndOfShift, best streak).

        public AttractionState MirrorState => (AttractionState)_state.Value;
        public float MirrorSatisfaction => _satisfaction.Value;
        public int MirrorQueue => _queueCount.Value;
        public float MirrorShiftRemaining => _shiftRemaining.Value;
        public bool MirrorGatesOpen => _gatesOpen.Value;
        public bool MirrorLotoEngaged => _lotoEngaged.Value;
        public bool MirrorUnloadDoorOpen => _unloadDoorOpen.Value;
        public bool MirrorRestraintsReleased => _restraintsReleased.Value;
        public bool MirrorUnloadAreaClear => _unloadAreaClear.Value;
        public bool MirrorGuestsUnloading => _guestsUnloading.Value;
        public int MirrorTrainCount => _trainCount.Value;
        public string MirrorFault => FaultData(0).Value >= 0 ? FaultText(0).Value : "";
        public int MirrorRidersServed => _ridersServed.Value;
        public int MirrorDispatches => _dispatches.Value;
        public int MirrorCurrentStreak => _currentStreak.Value;
        public int MirrorBestStreak => _bestStreak.Value;
        public int MirrorComboBonus => _comboBonus.Value;
        public int MirrorConfigTrainCount => _cfgTrainCount.Value;
        public int MirrorConfigBlockCount => _cfgBlockCount.Value;
        public bool MirrorConfigEscalatedShift => _cfgEscalatedShift.Value;
        public bool MirrorConfigSeparateLoadUnload => _cfgSeparateLoadUnload.Value;
        public int MirrorConfigCarsPerTrain => _cfgCarsPerTrain.Value;
        public int MirrorConfigBrownoutStageCount => _cfgBrownoutStageCount.Value;
        public float MirrorConfigShiftLength => _cfgShiftLength.Value;

        /// <summary>Kept for older call sites: (block, progress) of train 0/1.</summary>
        public Vector2 MirrorTrain0 { get { var p = _train0Pose.Value; return new Vector2(p.x, p.y); } }
        public Vector2 MirrorTrain1 { get { var p = _train1Pose.Value; return new Vector2(p.x, p.y); } }

        /// <summary>Full train slot read. Returns false when the slot is empty.</summary>
        public bool TryGetMirrorTrain(int slot, out int block, out float progress,
                                      out TrainState state, out float speed,
                                      out int riders, out bool restraintsVerified)
        {
            block = 0; progress = 0f; state = TrainState.InStationLoading; speed = 0f;
            riders = 0; restraintsVerified = false;
            if (slot < 0 || slot >= MaxTrainSlots || TrainFlags(slot).Value < 0) return false;
            var p = TrainPose(slot).Value;
            block = (int)p.x; progress = p.y; state = (TrainState)(int)p.z; speed = p.w;
            int flags = TrainFlags(slot).Value;
            riders = flags & 0xFF;
            restraintsVerified = (flags & (1 << 8)) != 0;
            return true;
        }

        /// <summary>Full fault slot read. Returns false when the slot is empty.</summary>
        public bool TryGetMirrorFault(int slot, out FaultType type, out int stage,
                                      out int targetIndex, out bool inspected,
                                      out bool lotoSatisfied, out string hmiText)
        {
            type = FaultType.RestraintNotVerified; stage = 0; targetIndex = -1;
            inspected = false; lotoSatisfied = false; hmiText = "";
            if (slot < 0 || slot >= MaxFaultSlots) return false;
            int packed = FaultData(slot).Value;
            if (packed < 0) return false;
            type = (FaultType)(packed & 0xFF);
            stage = (packed >> 8) & 0xFF;
            targetIndex = ((packed >> 16) & 0xFF) - 1;
            inspected = (packed & (1 << 24)) != 0;
            lotoSatisfied = (packed & (1 << 25)) != 0;
            hmiText = FaultText(slot).Value;
            return true;
        }
    }
}
#endif






