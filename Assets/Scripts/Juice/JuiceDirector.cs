using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Juice
{
    /// <summary>
    /// The conductor of game feel: watches the (locally mirrored) sim state every frame and
    /// converts observable edges into juice — dispatch chimes/combo arpeggios/confetti/cheers,
    /// gate servos, fault klaxon + camera trauma, resolve dings, shift-open power-up, and the
    /// generic panel confirm/refuse blips on interaction. Deliberately event-source agnostic:
    /// it never subscribes to authoritative sim callbacks, only polls public state — a dispatch
    /// is detected either from the score counter or from any train's ReadyToDispatch→Running
    /// edge (deduped by a short cooldown), so the same feedback plays for host, solo, and
    /// non-authoritative multiplayer clients. Also keeps a TrainJuice on every TrainVisual
    /// (covers RVs added at runtime from the HMI).
    /// </summary>
    public class JuiceDirector : MonoBehaviour
    {
        [Tooltip("Confetti/cheer origin (the Load berth). Falls back to a sensible default.")]
        public Transform StationCenter;
        [Tooltip("Crowd cheer origin (middle of the queue).")]
        public Transform QueueCenter;
        [Tooltip("Gate servo audio origin (the gate row).")]
        public Transform GateCenter;

        [Tooltip("Dispatch streak at which the confetti cannon comes out.")]
        public int ConfettiStreak = 3;

        int _lastDispatches = -1;
        int _lastFaultCount = -1;
        int _lastResolved = -1;
        int _lastState = -1;
        bool _lastGates;
        bool _gatesInit;
        int _knownTrainCount = -1;
        int[] _prevTrainStates = new int[0];
        float _dispatchFxCooldown;
        float _refindTimer;

        Interaction.PlayerInteractor _interactor;
        Guests.GuestVisualManager _guests;

        Vector3 StationPos()
        {
            return StationCenter != null ? StationCenter.position : new Vector3(3f, 0.7f, 2.2f);
        }

        Vector3 QueuePos()
        {
            return QueueCenter != null ? QueueCenter.position : new Vector3(0f, 1f, -5.5f);
        }

        Vector3 GatePos()
        {
            return GateCenter != null ? GateCenter.position : new Vector3(0f, 0.6f, 1.2f);
        }

        void Update()
        {
            var m = Attraction.AttractionManager.Instance;
            if (m == null || m.Sim == null) return;
            var s = m.Sim;
            _dispatchFxCooldown -= Time.deltaTime;

            EnsureTrainJuice(s);
            RefreshSceneRefs();

            // ---- Dispatch: score-counter edge OR any train leaving ReadyToDispatch under
            // power (the state edge is what a client actually observes). Cooldown dedupes
            // when both fire on the same dispatch. ----
            bool launched = false;
            var trains = s.Trains;
            if (_prevTrainStates.Length != trains.Length)
            {
                _prevTrainStates = new int[trains.Length];
                for (int i = 0; i < trains.Length; i++) _prevTrainStates[i] = (int)trains[i].State;
            }
            for (int i = 0; i < trains.Length; i++)
            {
                int cur = (int)trains[i].State;
                if ((TrainState)_prevTrainStates[i] == TrainState.ReadyToDispatch
                    && (TrainState)cur == TrainState.Running)
                    launched = true;
                _prevTrainStates[i] = cur;
            }
            int d = s.Score.Dispatches;
            if (_lastDispatches < 0) _lastDispatches = d;
            if ((d > _lastDispatches || launched) && _dispatchFxCooldown <= 0f)
            {
                OnDispatch(s);
                _dispatchFxCooldown = 0.5f;
            }
            _lastDispatches = d;

            // ---- Gate toggles: servo whir at the gate row ----
            bool gates = s.Guests.GatesOpen;
            if (!_gatesInit) { _lastGates = gates; _gatesInit = true; }
            if (gates != _lastGates)
            {
                AudioDirector.GateServo(GatePos());
                _lastGates = gates;
            }

            // ---- Shift lifecycle ----
            int st = (int)s.State;
            if (_lastState >= 0 && st != _lastState
                && (AttractionState)_lastState == AttractionState.Closed
                && s.State == AttractionState.Ready)
                AudioDirector.ShiftOpen();
            _lastState = st;

            // ---- Faults: klaxon level + trigger/resolve edges ----
            int fc = s.Faults.Active.Count;
            if (_lastFaultCount < 0) _lastFaultCount = fc;
            if (fc > _lastFaultCount) OnFaultTriggered(s);
            _lastFaultCount = fc;

            int res = s.Score.FaultsResolved;
            if (_lastResolved >= 0 && res > _lastResolved) AudioDirector.ResolveDing();
            _lastResolved = res;

            AudioDirector.SetKlaxon(KlaxonLevel(s));

            // ---- Generic interact feedback: confirm blip vs refuse buzz on E ----
            if (Input.GetKeyDown(KeyCode.E) && _interactor != null
                && !string.IsNullOrEmpty(_interactor.CurrentPrompt))
            {
                if (LooksRefused(_interactor.CurrentPrompt)) AudioDirector.Refuse();
                else AudioDirector.Confirm();
            }
        }

        void OnDispatch(AttractionSim s)
        {
            int streak = s.Score.CurrentStreak;
            AudioDirector.Chime();
            if (streak >= 2) AudioDirector.ComboArp(streak);

            // The queue celebrates every launch — louder as the streak grows.
            AudioDirector.CrowdCheer(QueuePos(), Mathf.Clamp01(0.45f + 0.12f * streak));
            if (_guests != null) _guests.Cheer();

            if (streak >= ConfettiStreak)
                JuiceFx.Confetti(StationPos() + Vector3.up * 1.4f);

            // FOV kick + a whisper of shake, scaled down with distance from the station.
            float fall = CameraJuice.Falloff(StationPos(), 6f, 26f);
            if (fall > 0.01f)
            {
                CameraJuice.KickFov(3.2f * fall);
                CameraJuice.AddTrauma(0.14f * fall);
            }
        }

        void OnFaultTriggered(AttractionSim s)
        {
            bool major = false;
            for (int i = 0; i < s.Faults.Active.Count; i++)
                if (s.Faults.Active[i].Def.Severity == FaultSeverity.Major) major = true;
            CameraJuice.AddTrauma(major ? 0.34f : 0.2f);
        }

        static int KlaxonLevel(AttractionSim s)
        {
            if (s.Faults.Active.Count == 0) return 0;
            for (int i = 0; i < s.Faults.Active.Count; i++)
                if (s.Faults.Active[i].Def.Severity == FaultSeverity.Major) return 2;
            return 1;
        }

        static bool LooksRefused(string prompt)
        {
            string p = prompt.ToLowerInvariant();
            return p.Contains("refused") || p.Contains("blocked")
                || p.Contains("only") || p.Contains("wait for")
                || p.Contains("first");
        }

        void EnsureTrainJuice(AttractionSim s)
        {
            if (s.Trains.Length == _knownTrainCount) return;
            _knownTrainCount = s.Trains.Length;
            var visuals = Object.FindObjectsOfType<Vehicles.TrainVisual>();
            for (int i = 0; i < visuals.Length; i++)
            {
                var tv = visuals[i];
                if (tv == null || tv.GetComponent<TrainJuice>() != null) continue;
                var tj = tv.gameObject.AddComponent<TrainJuice>();
                tj.TrainId = tv.TrainId;
            }
        }

        void RefreshSceneRefs()
        {
            if (_interactor != null && _guests != null) return;
            _refindTimer -= Time.deltaTime;
            if (_refindTimer > 0f) return;
            _refindTimer = 1f;
            if (_interactor == null) _interactor = Object.FindObjectOfType<Interaction.PlayerInteractor>();
            if (_guests == null) _guests = Object.FindObjectOfType<Guests.GuestVisualManager>();
        }
    }
}
