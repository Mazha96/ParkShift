using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Light + feel driver for the arcade-style dispatch push button. Strictly
    /// read-only on the sim: polls AttractionManager.Sim.CanDispatch at ~10 Hz (keeps the
    /// reason-string work off the per-frame path). Dispatch allowed → cap glows bright green
    /// with a gentle invite pulse; blocked → dim and still. The moment a dispatch actually
    /// fires (train leaving) the cap flashes once, depresses ~1.5cm and springs back
    /// (PropDrivers press pattern), then settles dim until the next train is ready.
    /// Emission through a MaterialPropertyBlock — no material instancing.</summary>
    public class DispatchButtonLight : MonoBehaviour
    {
        [Tooltip("The green dome cap (moves on press; its renderer carries the glow).")]
        public Transform Cap;
        [Tooltip("HDR emission tint of the cap (keep 0..1-ish; intensities scale it).")]
        public Color GoColor = new Color(0.25f, 1f, 0.35f);
        public float GoIntensity = 2.4f;
        [Range(0f, 1f)] public float GoPulseAmount = 0.25f;
        public float GoPulseSpeed = 3f;
        [Tooltip("Dim emission level while dispatch is blocked (dark but readable as a button).")]
        public float BlockedIntensity = 0.15f;
        [Tooltip("Extra emission spike on the actual dispatch, decaying over ~0.4s.")]
        public float FlashIntensity = 6f;
        [Tooltip("How far the cap sinks on a dispatch press (m).")]
        public float PressDepth = 0.015f;
        public float PressReturnSpeed = 5f;
        public float PollInterval = 0.1f;

        Renderer _r;
        MaterialPropertyBlock _mpb;
        Vector3 _capRest;
        bool _capInit;
        bool _canDispatch;
        float _pollTimer;
        float _press;               // 1 -> 0 press pulse
        float _flash;               // 1 -> 0 flash decay
        float _lit;                 // smoothed 0 (blocked) -> 1 (go)
        int _lastDispatches = -1;
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void Start()
        {
            if (Cap != null)
            {
                _r = Cap.GetComponentInChildren<Renderer>();
                _capRest = Cap.localPosition;
                _capInit = true;
            }
            _mpb = new MaterialPropertyBlock();
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null) return;

            _pollTimer -= Time.deltaTime;
            if (_pollTimer <= 0f)
            {
                _pollTimer = PollInterval;
                _canDispatch = m.Sim.CanDispatch(out _);
            }

            // Actual dispatch edge (same signal ConsoleDriver uses): flash + press once.
            int d = m.Sim.Score.Dispatches;
            if (_lastDispatches < 0) _lastDispatches = d;
            if (d != _lastDispatches) { _flash = 1f; _press = 1f; _lastDispatches = d; }

            _flash = Mathf.MoveTowards(_flash, 0f, 2.5f * Time.deltaTime);
            _press = Mathf.MoveTowards(_press, 0f, PressReturnSpeed * Time.deltaTime);
            _lit = Mathf.MoveTowards(_lit, _canDispatch ? 1f : 0f, 5f * Time.deltaTime);

            if (_capInit)
            {
                float k = Mathf.Sin(Mathf.Clamp01(_press) * Mathf.PI);   // 0 -> 1 -> 0 spring
                Cap.localPosition = _capRest + Vector3.down * (k * PressDepth);
            }
            if (_r != null && _mpb != null)
            {
                float invite = GoIntensity * (1f + GoPulseAmount * Mathf.Sin(Time.time * GoPulseSpeed));
                float intensity = Mathf.Lerp(BlockedIntensity, invite, _lit) + _flash * FlashIntensity;
                _r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionId, GoColor * intensity);
                _r.SetPropertyBlock(_mpb);
            }
        }
    }
}
