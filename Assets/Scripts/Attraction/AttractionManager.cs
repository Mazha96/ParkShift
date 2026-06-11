using System.IO;
using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    public enum OpVerb { OpenShift, ToggleGates, CycleGates, LoadTrain, VerifyRestraints, Dispatch,
                         InspectSeat, ClearItem, ToggleLoto, MaintenanceReset, CallAssistant }

    /// <summary>
    /// Unity bridge for the engine-agnostic AttractionSim.
    /// In solo/host the sim ticks here (authoritative). In a FishNet client build,
    /// verbs are routed to the server via AttractionNetSync and this manager mirrors state.
    /// </summary>
    public class AttractionManager : MonoBehaviour
    {
        public static AttractionManager Instance { get; private set; }

        public AttractionSim Sim { get; private set; }
        public bool IsAuthoritative { get; set; } = true; // solo & host = true; FishNet client = false
        public System.Func<OpVerb, bool> VerbRouter;       // set by AttractionNetSync on clients

        [Header("Assistant (solo helper)")]
        public float AssistantPenalty = 30;
        float _assistantTimer = -1f;
        public float AssistantRemaining => _assistantTimer;

        void Awake()
        {
            Instance = this;
            var cfg = Networking.NetworkBootstrap.Mode == Networking.NetMode.Solo
                ? SimConfig.Solo() : SimConfig.Coop(2);
            Sim = new AttractionSim(cfg);
            Sim.Telemetry.OnEntry += e => Debug.Log("[TELEMETRY] " + e);
        }

        void Update()
        {
            if (!IsAuthoritative || Sim == null) return;
            Sim.Tick(Time.deltaTime);

            if (_assistantTimer > 0f)
            {
                _assistantTimer -= Time.deltaTime;
                if (_assistantTimer <= 0f) AssistantResolve();
            }

            if (Input.GetKeyDown(KeyCode.H)) Submit(OpVerb.CallAssistant);
        }

        /// <summary>All player verbs funnel through here so multiplayer can intercept.</summary>
        public bool Submit(OpVerb verb)
        {
            if (!IsAuthoritative && VerbRouter != null) return VerbRouter(verb);
            return Execute(verb);
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
                case OpVerb.CallAssistant:
                    if (_assistantTimer <= 0f && Sim.Faults.Active.Count > 0)
                    {
                        _assistantTimer = Sim.Config.AssistantFixSeconds;
                        Sim.Telemetry.Log(Sim.SimTime, "ASSIST", "Remote tech called (score penalty)");
                    }
                    return true;
            }
            return false;
        }

        void AssistantResolve()
        {
            if (Sim.Faults.Active.Count == 0) return;
            var f = Sim.Faults.Active[0];
            if (f.Def.RequiresLoto) Sim.Faults.MarkLoto(f.Def.Type);
            Sim.Faults.Resolve(f.Def.Type);
            Sim.Score.AddPenalty((int)AssistantPenalty);
            Sim.Telemetry.Log(Sim.SimTime, "ASSIST", $"Remote tech cleared {f.Def.Type}");
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
