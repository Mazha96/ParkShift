#if FISHNET
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using ParkShift.Attraction;
using ParkShift.Core;

namespace ParkShift.Networking
{
    /// <summary>
    /// Server-authoritative replication of the attraction.
    /// - Server: ticks AttractionSim (via AttractionManager) and pushes snapshots.
    /// - Clients: verbs route here as ServerRpcs; server validates via the same
    ///   AttractionManager.Execute() path used in solo (single source of truth).
    /// Snapshot replication is intentionally coarse for the slice (state ints +
    /// train block/progress at 10Hz via SyncVars) — good enough for primitive visuals.
    /// </summary>
    public class AttractionNetSync : NetworkBehaviour
    {
        readonly SyncVar<int> _state = new SyncVar<int>();
        readonly SyncVar<float> _satisfaction = new SyncVar<float>();
        readonly SyncVar<int> _queueCount = new SyncVar<int>();
        readonly SyncVar<float> _shiftRemaining = new SyncVar<float>();
        readonly SyncVar<Vector2> _train0 = new SyncVar<Vector2>(); // (block, progress)
        readonly SyncVar<Vector2> _train1 = new SyncVar<Vector2>();
        readonly SyncVar<string> _faultLine = new SyncVar<string>();

        float _pushTimer;

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!IsServerInitialized)
                AttractionManager.Instance.VerbRouter = v => { ServerSubmitVerb((int)v); return true; };
        }

        void Update()
        {
            if (!IsServerInitialized) return;
            _pushTimer += Time.deltaTime;
            if (_pushTimer < 0.1f) return; // 10 Hz snapshot
            _pushTimer = 0f;
            var s = AttractionManager.Instance.Sim;
            _state.Value = (int)s.State;
            _satisfaction.Value = s.Guests.Satisfaction;
            _queueCount.Value = s.Guests.QueueCount;
            _shiftRemaining.Value = s.ShiftRemaining;
            _train0.Value = new Vector2(s.Trains[0].CurrentBlock, s.Trains[0].Progress);
            _train1.Value = new Vector2(s.Trains[1].CurrentBlock, s.Trains[1].Progress);
            _faultLine.Value = s.Faults.Active.Count > 0 ? s.Faults.Active[0].Def.HmiMessage : "";
        }

        [ServerRpc(RequireOwnership = false)]
        void ServerSubmitVerb(int verb)
        {
            // Validation happens inside the sim itself (CanDispatch etc.) — server is authority.
            AttractionManager.Instance.Execute((OpVerb)verb);
        }

        // Client-side mirror accessors for a future client HUD pass:
        public AttractionState MirrorState => (AttractionState)_state.Value;
        public float MirrorSatisfaction => _satisfaction.Value;
        public int MirrorQueue => _queueCount.Value;
        public Vector2 MirrorTrain0 => _train0.Value;
        public Vector2 MirrorTrain1 => _train1.Value;
        public string MirrorFault => _faultLine.Value;
    }
}
#endif
