using System.IO;
using System.Collections.Generic;
using UnityEngine;
using ParkShift.Core;
using ParkShift.Vehicles;
using ParkShift.Guests;

namespace ParkShift.Attraction
{
    // NOTE: verbs are replicated as their int value (AttractionNetSync casts to/from int),
    // so only ever APPEND here — never reorder or remove.
    public enum OpVerb { OpenShift, ToggleGates, CycleGates, LoadTrain, VerifyRestraints, Dispatch,
                         InspectSeat, ClearItem, ToggleLoto, MaintenanceReset, CallAssistant,
                         EscortIntruder, InspectCollision, WinchTrain,
                         OpenUnloadDoor, CloseUnloadDoor, ReleaseRestraints,
                         // Extended fault catalog (Wave 1). UnjamCar0..3 are one verb per car so
                         // the verb funnel stays a plain enum (network-safe, no payload needed).
                         VerifyBlockClear, ResetBlockSensor, AdvanceBrownoutStage,
                         UnjamCar0, UnjamCar1, UnjamCar2, UnjamCar3,
                         // Wave 2 (final review): HMI "Add RV" routed through the funnel so a
                         // FishNet client's Add RV runs server-side (the count then replicates).
                         AddRv, ToggleAdmissions, GuestCare }

    /// <summary>
    /// Unity bridge for the engine-agnostic AttractionSim.
    /// In solo/host the sim ticks here (authoritative). In a FishNet client build,
    /// verbs are routed to the server via AttractionNetSync and this manager mirrors state.
    /// </summary>
    public class AttractionManager : MonoBehaviour
    {
        public static AttractionManager Instance { get; private set; }

        public AttractionSim Sim { get; private set; }
        public AutoOperator AutoOp { get; private set; }
        /// <summary>True while the auto-operator covers the booth (Technician or Manager, solo).</summary>
        public bool AutoOpActive => AutoOp != null && AutoOp.Enabled;
        public bool IsAuthoritative { get; set; } = true; // solo & host = true; FishNet client = false
        public System.Func<OpVerb, bool> VerbRouter;       // set by AttractionNetSync on clients

        [Header("Config")]
        [Tooltip("Optional designer config asset. When assigned it drives block count, RV count and motion feel " +
                 "(e.g. the 6-block testbed). Leave empty to keep the built-in 4-block / 2-RV defaults.")]
        public SimConfigSO ConfigAsset;

        [Header("Station (built-in path only)")]
        [Tooltip("Two-position station: block 0 = Unload, block 1 = Load, so two RVs park at once.")]
        public bool SeparateLoadUnload = false;
        [Tooltip("Number of RVs when using the built-in config (no asset).")]
        public int RvCount = 2;

        [Header("Escalated shift (scene preset)")]
        [Tooltip("Layers the Wave-1 content onto whatever config is in use: extended fault catalog, " +
                 "fault/arrival ramps and guest surge waves. Applied here (Unity layer) so the Core " +
                 "defaults stay behavior-preserving for the headless tests.")]
        public bool EscalatedShift = false;
        public float FaultRampMultiplier = 2f;
        public float GuestArrivalRampMultiplier = 1.75f;
        public bool SurgeWavesEnabled = true;

        [Header("Runtime RV spawning (HMI 'Add RV')")]
        [Tooltip("CoasterTrain prefab instantiated when an RV is added at runtime.")]
        public GameObject RvPrefab;
        [Tooltip("The spline the spawned RV follows.")]
        public TrackPath Track;
        [Tooltip("Optional accent material applied to added RVs (matches PS_TrainBody).")]
        public Material AddedRvMaterial;
        [Tooltip("Guest visual manager, so the new RV's seats/unload are handled.")]
        public GuestVisualManager GuestVisuals;

        /// <summary>Player-facing 'Add RV' (HMI). Returns false if there's no room.</summary>
        public bool AddRv()
        {
#if FISHNET
            if (Networking.NetworkBootstrap.Mode != Networking.NetMode.Solo && Sim != null
                && Sim.Trains.Length >= Networking.AttractionNetSync.MaxTrainSlots) return false;
#endif
            return Sim != null && Sim.AddTrain() != null;
        }

        [Header("Assistant (solo helper)")]
        public float AssistantPenalty = 30;
        float _assistantTimer = -1f;
        public float AssistantRemaining => _assistantTimer;
        string _lastCommandStatus = "";
        float _commandStatusUntil;
        public string LastCommandStatus => Time.unscaledTime < _commandStatusUntil ? _lastCommandStatus : "";
        public void ReportCommandStatus(string message)
        {
            _lastCommandStatus = message;
            _commandStatusUntil = Time.unscaledTime + 4f;
            if (message == "Command processed" || message == "Host processed command") Juice.AudioDirector.Confirm();
            else if (!string.IsNullOrEmpty(message) && message != "Waiting for host...") Juice.AudioDirector.Refuse();
        }
        public void MirrorAssistant(float seconds)
        {
            if (!IsAuthoritative) _assistantTimer = seconds;
        }
        public string CommandRejectionReason(OpVerb verb)
        {
            if (verb == OpVerb.Dispatch && !Sim.CanDispatch(out var dispatchReason)) return dispatchReason;
            if (verb == OpVerb.GuestCare && !Sim.Management.CanOfferGuestCare(out var careReason)) return careReason;
            if (verb == OpVerb.AddRv) return "No available RV slot";
            if (verb == OpVerb.CallAssistant) return _assistantTimer > 0f ? "Assistant already en route" : "No fault needs an assistant";
            if (verb >= OpVerb.UnjamCar0 && verb <= OpVerb.UnjamCar3)
            {
                var jam = Sim.Faults.Find(FaultType.RestraintJammedCar);
                return jam == null ? "No jammed lap bar needs repair" : "Wrong car — repair flagged car " + (jam.TargetIndex + 1);
            }
            if (verb == OpVerb.OpenUnloadDoor) return Sim.UnloadingTrain() == null ? "Wait for a train at the unload berth" : "Unload door is already open";
            if (verb == OpVerb.CloseUnloadDoor) return !Sim.UnloadDoorOpen ? "Unload door is already closed" : "Wait for guests and the doorway to clear";
            if (verb == OpVerb.ReleaseRestraints) return Sim.UnloadingTrain() == null ? "Wait for a train at the unload berth"
                : Sim.RestraintsReleased ? "Restraints already released" : "Wait until the unload door is fully open";
            return "Action unavailable — check ride state and safety requirements";
        }

        void Awake()
        {
            Instance = this;
            SimConfig cfg;
            if (ConfigAsset != null)
            {
                // Designer asset drives block layout, RV count and motion feel (e.g. the 6-block testbed).
                cfg = ConfigAsset.ToConfig();
            }
            else
            {
                cfg = Networking.NetworkBootstrap.Mode == Networking.NetMode.Solo
                    ? SimConfig.Solo() : SimConfig.Coop(2);
                cfg.SeparateLoadUnload = SeparateLoadUnload;
                cfg.TrainCount = Mathf.Max(1, RvCount);
                // Feel: slow station(s), fast launch, full course, slow brake run.
                cfg.BlockSpeedMultipliers = SeparateLoadUnload
                    ? new[] { 1f, 1f, 1.7f, 1.15f, 0.6f }   // Unload, Load, Launch, Course, Brake
                    : new[] { 1f, 1.7f, 1.15f, 0.6f };
            }
            if (EscalatedShift)
            {
                cfg.ExtendedFaultsEnabled = true;
                cfg.FaultRampMultiplier = FaultRampMultiplier;
                cfg.GuestArrivalRampMultiplier = GuestArrivalRampMultiplier;
                cfg.SurgeWavesEnabled = SurgeWavesEnabled;
            }
            // Keep the sim's block count locked to the scene's track so the RVs never map onto the
            // wrong segments (which would make them overlap). The track is the single source of truth.
            var track = Object.FindObjectOfType<Vehicles.TrackPath>();
            if (track != null) cfg.BlockCount = track.EffectiveBlockCount;

            cfg.TimedFaultsEnabled = true;
            Sim = new AttractionSim(cfg);
            // Physical interlocks also apply to the solo auto-operator and server commands.
            Sim.UnloadDoorClearToClose = () => UnloadDoorDriver.Instance == null || UnloadDoorDriver.Instance.CanClose;
            Sim.UnloadDoorReadyToRelease = () => UnloadDoorDriver.Instance == null || UnloadDoorDriver.Instance.IsFullyOpen;
            Sim.UnloadDoorReadyToDepart = () => UnloadDoorDriver.Instance == null || UnloadDoorDriver.Instance.IsFullyClosed;
            AutoOp = new AutoOperator(Sim);
            Sim.Telemetry.OnEntry += e => Debug.Log("[TELEMETRY] " + e);
            if (Track == null) Track = track;
            Sim.OnTrainAdded += SpawnTrainVisual;   // spawn a body when an RV is added from the HMI
        }

        /// <summary>Instantiate a visual body for an RV added at runtime and hook it into the guest system.</summary>
        void SpawnTrainVisual(Core.TrainSim t)
        {
            if (RvPrefab == null || Track == null)
            {
                Debug.LogWarning("[AttractionManager] Added RV " + t.Id + " but no RvPrefab/Track set — visual not spawned.");
                return;
            }
            var wrapper = new GameObject("Train" + t.Id);
            var inst = Instantiate(RvPrefab);
            inst.name = "Train" + t.Id + "_Model";
            inst.transform.SetParent(wrapper.transform, false); // preserve the prefab's import pose

            var seats = new Transform[8];
            for (int s = 0; s < 8; s++) seats[s] = FindDeep(wrapper.transform, "Seat" + s);

            var bars = new List<Transform>();
            for (int b = 0; b < 4; b++) { var bar = FindDeep(wrapper.transform, "RestraintBar" + b); if (bar != null) bars.Add(bar); }
            var rd = wrapper.AddComponent<TrainRestraintDriver>();
            rd.TrainId = t.Id; rd.Bars = bars.ToArray();

            if (AddedRvMaterial != null) SwapMaterial(wrapper, "PS_TrainBody", AddedRvMaterial);

            var tv = wrapper.AddComponent<TrainVisual>();
            tv.TrainId = t.Id; tv.Path = Track; tv.SeatAnchors = seats;

            ParkShift.Environment.CartoonScenePalette.Paint(wrapper);
            if (GuestVisuals != null) GuestVisuals.RegisterTrain(tv);
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root) { var r = FindDeep(c, name); if (r != null) return r; }
            return null;
        }

        static void SwapMaterial(GameObject go, string contains, Material replacement)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials; bool dirty = false;
                for (int k = 0; k < mats.Length; k++)
                    if (mats[k] != null && mats[k].name.Contains(contains)) { mats[k] = replacement; dirty = true; }
                if (dirty) r.sharedMaterials = mats;
            }
        }

        void Update()
        {
            if (Sim == null) return;
            if (Input.GetKeyDown(KeyCode.H)) Submit(OpVerb.CallAssistant);
            if (!IsAuthoritative) return;

            // Solo: while the player wears the Technician hat, an auto-operator covers
            // the booth (keeps dispatching) and major incidents start rolling for the tech.
            bool techCover = Networking.NetworkBootstrap.Mode == Networking.NetMode.Solo
                          && Player.RoleController.Local != null
                          && Player.RoleController.Local.Role == PlayerRole.Technician;
            AutoOp.Enabled = techCover || (Networking.NetworkBootstrap.Mode == Networking.NetMode.Solo
                && Player.RoleController.Local != null && Player.RoleController.Local.Role == PlayerRole.Manager);
            Sim.Faults.TechScenarioMode = techCover;
#if FISHNET
            if (Networking.NetworkBootstrap.Mode != Networking.NetMode.Solo)
                Sim.Faults.TechScenarioMode = Networking.AttractionNetSync.Instance != null
                    && Networking.AttractionNetSync.Instance.HasTechnician;
#endif
            AutoOp.Tick(Time.deltaTime);

            Sim.Tick(Time.deltaTime);

            if (_assistantTimer > 0f)
            {
                _assistantTimer -= Time.deltaTime;
                if (_assistantTimer <= 0f) AssistantResolve();
            }


        }

        /// <summary>All player verbs funnel through here so multiplayer can intercept.</summary>
        public bool Submit(OpVerb verb)
        {
            if (verb == OpVerb.ToggleAdmissions || verb == OpVerb.GuestCare)
            {
                if (Player.RoleController.Local == null || Player.RoleController.Local.Role != PlayerRole.Manager)
                { ReportCommandStatus("Manager role required"); return false; }
            }
            if (!IsAuthoritative)
            {
                ReportCommandStatus(VerbRouter != null ? "Waiting for host..." : "Not connected to host");
                return VerbRouter != null && VerbRouter(verb);
            }
            bool accepted = Execute(verb);
            ReportCommandStatus(accepted ? "Command processed" : CommandRejectionReason(verb));
            return accepted;
        }

        public bool Execute(OpVerb verb)
        {
            switch (verb)
            {
                case OpVerb.OpenShift: Sim.OpenShift(); return true;
                case OpVerb.ToggleGates: Sim.SetGates(!Sim.Guests.GatesOpen); return true;
                case OpVerb.CycleGates: Sim.CycleGates(); return true;
                case OpVerb.LoadTrain: return Sim.LoadPlatformTrain() > 0;
                case OpVerb.VerifyRestraints: return Sim.VerifyRestraints();
                case OpVerb.Dispatch: return Sim.Dispatch();
                case OpVerb.InspectSeat: Sim.InspectFaultySeat(); return true;
                case OpVerb.ClearItem: Sim.ClearDroppedItem(); return true;
                case OpVerb.ToggleLoto: Sim.EngageLoto(!Sim.LotoEngaged); return true;
                case OpVerb.MaintenanceReset: return Sim.MaintenanceReset();
                case OpVerb.EscortIntruder: return Sim.EscortIntruder();
                case OpVerb.InspectCollision: return Sim.InspectCollision();
                case OpVerb.WinchTrain: return Sim.WinchValleyedTrain();
                case OpVerb.OpenUnloadDoor: return Sim.OpenUnloadDoor();
                case OpVerb.CloseUnloadDoor: return Sim.CloseUnloadDoor();
                case OpVerb.ReleaseRestraints: return Sim.ReleaseRestraints();
                case OpVerb.VerifyBlockClear: return Sim.VerifyBlockClear();
                case OpVerb.ResetBlockSensor: return Sim.ResetBlockSensor();
                case OpVerb.AdvanceBrownoutStage: return Sim.AdvanceBrownoutStage();
                case OpVerb.UnjamCar0: return Sim.UnjamRestraint(0);
                case OpVerb.UnjamCar1: return Sim.UnjamRestraint(1);
                case OpVerb.UnjamCar2: return Sim.UnjamRestraint(2);
                case OpVerb.UnjamCar3: return Sim.UnjamRestraint(3);
                case OpVerb.AddRv: return AddRv();
                case OpVerb.ToggleAdmissions: return Sim.Management.ToggleAdmissions();
                case OpVerb.GuestCare: return Sim.Management.OfferGuestCare();
                case OpVerb.CallAssistant:
                    if (_assistantTimer <= 0f && Sim.HasRemoteTechWork)
                    {
                        _assistantTimer = Sim.Config.AssistantFixSeconds;
                        Sim.Telemetry.Log(Sim.SimTime, "ASSIST", "Remote tech called (score penalty on success)");
                        return true;
                    }
                    return false;
            }
            return false;
        }

        void AssistantResolve()
        {
            // Penalty only lands if the tech actually clears something on arrival.
            Sim.RemoteTechFix((int)AssistantPenalty);
        }

        void OnDestroy()
        {
            if (Sim == null) return;
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "parkshift_telemetry.txt");
                File.WriteAllText(path, Sim.Telemetry.Dump());
                Debug.Log("Telemetry written: " + path);
            }
            catch { /* non-fatal */ }
        }
    }
}





