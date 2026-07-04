using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Eases a hinge between its imported rest pose and rest * offsetEuler.
    /// Code-driven (not Animator) so independent parts on one prop never fight.</summary>
    [System.Serializable]
    public class Hinge
    {
        public Transform T;
        public Vector3 OffsetEuler;
        [Tooltip("Springy overshoot on arrival (0 = classic smoothstep, 1 = standard ease-out-back).")]
        public float Overshoot = 0f;
        Quaternion _rest;
        float _t;
        bool _init;

        public Hinge(Transform t, Vector3 offsetEuler) { T = t; OffsetEuler = offsetEuler; }

        /// <summary>Normalized throw position (0 = rest, 1 = engaged) — for completion edges.</summary>
        public float Position { get { return _t; } }

        public void Tick(bool engaged, float dt, float speed = 3.5f)
        {
            if (T == null) return;
            if (!_init) { _rest = T.localRotation; _init = true; }
            _t = Mathf.MoveTowards(_t, engaged ? 1f : 0f, speed * dt);
            float k = Overshoot > 0.001f ? EaseOutBack(_t, Overshoot) : Mathf.SmoothStep(0f, 1f, _t);
            T.localRotation = _rest * Quaternion.Euler(OffsetEuler * k);
        }

        /// <summary>Ease-out-back: swings a little past the target, then settles — the springy
        /// lever feel. s scales the standard overshoot constant (1 ≈ 10% overshoot).</summary>
        static float EaseOutBack(float t, float s)
        {
            float c1 = 1.70158f * Mathf.Clamp(s, 0f, 3f);
            float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }
    }

    /// <summary>Swings both doors of one station gate unit with the sim's gate state.</summary>
    public class GateUnitDriver : MonoBehaviour
    {
        public Transform DoorL, DoorR;
        public Vector3 OpenEulerL = new Vector3(0, 0, -100);
        public Vector3 OpenEulerR = new Vector3(0, 0, 100);
        Hinge _l, _r;
        float _lastPos;

        void Start()
        {
            _l = new Hinge(DoorL, OpenEulerL);
            _r = new Hinge(DoorR, OpenEulerR);
            _l.Overshoot = 1f;   // doors swing with a springy settle instead of a robotic stop
            _r.Overshoot = 1f;
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            bool open = m.Sim.Guests.GatesOpen;
            _l.Tick(open, Time.deltaTime);
            _r.Tick(open, Time.deltaTime);

            // Completion edges: a click + dust puff when the doors land fully open or closed.
            float pos = _l.Position;
            if (_lastPos < 0.98f && pos >= 0.98f) OnDoorsSettled();
            else if (_lastPos > 0.02f && pos <= 0.02f) OnDoorsSettled();
            _lastPos = pos;
        }

        void OnDoorsSettled()
        {
            Vector3 p = DoorL != null ? DoorL.position : transform.position;
            Juice.JuiceFx.DustPuff(p + Vector3.down * 0.2f);
            Juice.AudioDirector.GateClick(p);
        }
    }

    /// <summary>Lowers/raises the lap bars to mirror the sim train's restraint state.</summary>
    public class TrainRestraintDriver : MonoBehaviour
    {
        public int TrainId;
        public Transform[] Bars;
        public Vector3 ClosedEuler = new Vector3(95, 0, 0);
        Hinge[] _h;

        void Start()
        {
            _h = new Hinge[Bars.Length];
            for (int i = 0; i < Bars.Length; i++)
            {
                _h[i] = new Hinge(Bars[i], ClosedEuler);
                _h[i].Overshoot = 0.7f;   // bars land with a little clack-bounce
            }
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            var t = m.Sim.Trains[TrainId];
            bool barsUp = (t.State == TrainState.InStationLoading || t.State == TrainState.InStationUnloading)
                          && !t.RestraintsVerified;
            foreach (var h in _h) h.Tick(!barsUp, Time.deltaTime, 2.5f);
        }
    }

    /// <summary>Animates the booth console: the gate lever swings between rest (gates closed) and
    /// pulled (gates open); the dispatch button visibly depresses into the panel on every dispatch.</summary>
    public class ConsoleDriver : MonoBehaviour
    {
        public Transform Lever, Button;
        [Tooltip("Gate lever 'open' pose, relative to its imported rest pose (pitch about local X).")]
        public Vector3 LeverPulledEuler = new Vector3(-56, 0, 0);
        public float LeverSpeed = 5f;
        [Tooltip("How far the dispatch button travels into the panel on press (m, along its local -Z).")]
        public float ButtonPressDepth = 0.035f;
        [Tooltip("Extra flatten on press so it reads even if the local axis is off (0..0.6).")]
        [Range(0f, 0.6f)] public float ButtonSquash = 0.35f;
        public float ButtonReturnSpeed = 4f;
        Hinge _lever;
        Vector3 _btnRestPos, _btnRestScale;
        bool _btnInit;
        float _pulse;
        int _lastDispatches;
        Renderer _btnRenderer;
        Color _btnBaseColor = Color.white;
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_Color");

        void Start()
        {
            _lever = new Hinge(Lever, LeverPulledEuler);
            _lever.Overshoot = 1.2f;   // gate lever lands with a satisfying spring
            if (Button)
            {
                _btnRestPos = Button.localPosition; _btnRestScale = Button.localScale; _btnInit = true;
                _btnRenderer = Button.GetComponentInChildren<Renderer>();
                if (_btnRenderer != null && _btnRenderer.sharedMaterial != null
                    && _btnRenderer.sharedMaterial.HasProperty(ColorId))
                    _btnBaseColor = _btnRenderer.sharedMaterial.color;
                _mpb = new MaterialPropertyBlock();
            }
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            _lever.Tick(m.Sim.Guests.GatesOpen, Time.deltaTime, LeverSpeed);

            int d = m.Sim.Score.Dispatches;
            if (d != _lastDispatches) { _pulse = 1f; _lastDispatches = d; }
            if (Button && _btnInit)
            {
                _pulse = Mathf.MoveTowards(_pulse, 0f, ButtonReturnSpeed * Time.deltaTime);
                float k = Mathf.Sin(Mathf.Clamp01(_pulse) * Mathf.PI);   // 0 -> 1 -> 0 over the press
                Button.localPosition = _btnRestPos - new Vector3(0f, 0f, k * ButtonPressDepth); // depress into the panel
                Button.localScale = Vector3.Scale(_btnRestScale, new Vector3(1f, 1f, 1f - k * ButtonSquash));
                // Light the button up while it's pressed (property block — no material instancing).
                if (_btnRenderer != null && _mpb != null)
                {
                    _btnRenderer.GetPropertyBlock(_mpb);
                    _mpb.SetColor(ColorId, Color.Lerp(_btnBaseColor, Color.white, k * 0.85f));
                    _btnRenderer.SetPropertyBlock(_mpb);
                }
            }
        }
    }

    /// <summary>Drive cabinet: door opens while there is technician business (LOTO engaged or a
    /// drive/collision fault); breaker lever drops when LOTO is engaged.</summary>
    public class CabinetDriver : MonoBehaviour
    {
        public Transform Door, Breaker, ResetButton;
        public Vector3 DoorOpenEuler = new Vector3(0, 0, 115);
        public Vector3 BreakerOffEuler = new Vector3(-70, 0, 0);
        public PressPulse ResetPress = new PressPulse();
        Hinge _door, _breaker;
        bool _hadDriveFault;

        void Start()
        {
            _door = new Hinge(Door, DoorOpenEuler);
            _breaker = new Hinge(Breaker, BreakerOffEuler);
            ResetPress.T = ResetButton;
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            var s = m.Sim;
            bool busy = s.LotoEngaged
                     || s.Faults.HasFault(FaultType.MaintenanceResetRequired)
                     || s.Faults.HasFault(FaultType.TrainCollision);
            _door.Tick(busy, Time.deltaTime, 2.5f);
            _breaker.Tick(s.LotoEngaged, Time.deltaTime, 5f);

            // Reset button depresses the moment a drive/collision fault clears (the reset itself).
            bool driveFault = s.Faults.HasFault(FaultType.MaintenanceResetRequired)
                           || s.Faults.HasFault(FaultType.TrainCollision);
            if (_hadDriveFault && !driveFault) ResetPress.Trigger();
            _hadDriveFault = driveFault;
            ResetPress.Tick(Time.deltaTime);
        }
    }

    /// <summary>Reusable "button depress" feedback: squashes a part on its local Z when triggered,
    /// then springs back. Shared by every console/cabinet/podium button so presses read consistently.</summary>
    [System.Serializable]
    public class PressPulse
    {
        public Transform T;
        [Range(0f, 0.9f)] public float Depth = 0.45f;  // how far it squashes on press
        public float Speed = 4f;                        // spring-back rate
        Vector3 _rest;
        bool _init;
        float _p;
        Renderer _r;
        Color _baseColor = Color.white;
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public void Trigger() { _p = 1f; }

        public void Tick(float dt)
        {
            if (T == null) return;
            if (!_init)
            {
                _rest = T.localScale;
                _r = T.GetComponentInChildren<Renderer>();
                if (_r != null && _r.sharedMaterial != null && _r.sharedMaterial.HasProperty(ColorId))
                    _baseColor = _r.sharedMaterial.color;
                _mpb = new MaterialPropertyBlock();
                _init = true;
            }
            _p = Mathf.MoveTowards(_p, 0f, Speed * dt);
            float k = Mathf.Sin(Mathf.Clamp01(_p) * Mathf.PI);
            T.localScale = Vector3.Scale(_rest, new Vector3(1f, 1f, 1f - k * Depth));
            // Press = light up: buttons flash toward white while depressed, then fade home.
            if (_r != null && _mpb != null)
            {
                _r.GetPropertyBlock(_mpb);
                _mpb.SetColor(ColorId, Color.Lerp(_baseColor, Color.white, k * 0.85f));
                _r.SetPropertyBlock(_mpb);
            }
        }
    }

    /// <summary>Green open-shift lever: throws to the "on" position when the shift opens
    /// (sim leaves the Closed state) and stays there for the rest of the shift.</summary>
    public class ShiftLeverDriver : MonoBehaviour
    {
        public Transform Pivot;
        public Vector3 OnEuler = new Vector3(-55, 0, 0);
        public float Speed = 5f;
        Hinge _h;

        void Start() { _h = new Hinge(Pivot, OnEuler); _h.Overshoot = 1.3f; }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            bool shiftOpen = m.Sim.State != Core.AttractionState.Closed;
            _h.Tick(shiftOpen, Time.deltaTime, Speed);
        }
    }

    /// <summary>Red evac lever: thrown (and gently cranked) while the technician is recovering a
    /// valleyed train — i.e. the stall fault is active with LOTO engaged — then springs back up.</summary>
    public class EvacLeverDriver : MonoBehaviour
    {
        public Transform Pivot;
        public Vector3 CrankEuler = new Vector3(-68, 0, 0);
        public float Speed = 4f;
        public float CrankWobbleDegrees = 6f;
        Hinge _h;
        float _phase;

        void Start() { _h = new Hinge(Pivot, CrankEuler); _h.Overshoot = 0.8f; }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || Pivot == null) return;
            var s = m.Sim;
            bool engaged = s.Faults.HasFault(FaultType.TrainValleyed) && s.LotoEngaged;
            _h.Tick(engaged, Time.deltaTime, Speed);
            if (engaged)
            {
                _phase += Time.deltaTime * 6f;
                Pivot.localRotation *= Quaternion.Euler(Mathf.Sin(_phase) * CrankWobbleDegrees, 0f, 0f);
            }
        }
    }

    /// <summary>Slides the Unload door open/closed to mirror the sim's UnloadDoorOpen flag.
    /// A trigger door (no collider blocking) — purely a visual state cue for the unload workflow.</summary>
    public class UnloadDoorDriver : MonoBehaviour
    {
        public Transform DoorPanel;
        [Tooltip("Local offset applied when the door is open (default: slides up out of the frame).")]
        public Vector3 OpenOffset = new Vector3(0f, 2.2f, 0f);
        public float Speed = 3.5f;
        Vector3 _closed;
        bool _init;

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || DoorPanel == null) return;
            if (!_init) { _closed = DoorPanel.localPosition; _init = true; }
            bool open = m.Sim.UnloadDoorOpen;
            Vector3 target = _closed + (open ? OpenOffset : Vector3.zero);
            DoorPanel.localPosition = Vector3.Lerp(DoorPanel.localPosition, target, Time.deltaTime * Speed);
        }
    }

    /// <summary>Unload control podium feedback: depresses each button on its action's rising edge
    /// (Open door, Close door, Release restraints), reading the sim's unload-workflow flags.</summary>
    public class UnloadPodiumDriver : MonoBehaviour
    {
        public Transform OpenButton, CloseButton, ReleaseButton;
        public PressPulse OpenPress = new PressPulse();
        public PressPulse ClosePress = new PressPulse();
        public PressPulse ReleasePress = new PressPulse();
        bool _door, _released;

        void Start() { OpenPress.T = OpenButton; ClosePress.T = CloseButton; ReleasePress.T = ReleaseButton; }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            var s = m.Sim;
            if (s.UnloadDoorOpen && !_door) OpenPress.Trigger();
            if (!s.UnloadDoorOpen && _door) ClosePress.Trigger();
            _door = s.UnloadDoorOpen;
            if (s.RestraintsReleased && !_released) ReleasePress.Trigger();
            _released = s.RestraintsReleased;
            OpenPress.Tick(Time.deltaTime);
            ClosePress.Tick(Time.deltaTime);
            ReleasePress.Tick(Time.deltaTime);
        }
    }

    /// <summary>Platform podium: depresses its button when the attendant acts —
    /// Load podium on each guest boarded, Restraint podium when restraints are verified.</summary>
    public class PodiumDriver : MonoBehaviour
    {
        public enum Mode { Load, Restraint }
        public Mode PodiumMode = Mode.Load;
        public Transform Button;
        public PressPulse Press = new PressPulse();
        int _lastRiders = -1;
        bool _lastVerified;

        void Start() { Press.T = Button; }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            var t = m.Sim.PlatformTrain();
            if (t != null)
            {
                if (PodiumMode == Mode.Load)
                {
                    if (_lastRiders < 0) _lastRiders = t.Riders;
                    if (t.Riders > _lastRiders) Press.Trigger();
                    _lastRiders = t.Riders;
                }
                else
                {
                    if (t.RestraintsVerified && !_lastVerified) Press.Trigger();
                    _lastVerified = t.RestraintsVerified;
                }
            }
            else { _lastRiders = -1; _lastVerified = false; }
            Press.Tick(Time.deltaTime);
        }
    }
}
