using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Interaction
{
    /// <summary>
    /// Interactable affordance glow. Lives on the same GameObject as the PanelInteractable
    /// (the interactor part), drives _EmissionColor on its child renderers through a shared
    /// MaterialPropertyBlock (NeonPulse pattern — no material instancing, no per-frame
    /// allocation). Idles with a very subtle emission "breath" so interactables read as
    /// interactable at a glance, and brightens smoothly while the player's gaze rests on it.
    /// </summary>
    public class InteractableGlow : MonoBehaviour
    {
        [Tooltip("HDR highlight tint (kept 0..1-ish; intensities scale it).")]
        public Color GlowColor = new Color(0.55f, 0.95f, 1f);
        [Tooltip("Idle emission level — the always-on 'I am interactable' breathing.")]
        public float IdleIntensity = 0.16f;
        [Range(0f, 1f)] public float IdleBreathAmount = 0.5f;
        public float BreathSpeed = 1.4f;
        [Tooltip("Emission level while the player is looking at this interactable.")]
        public float LookIntensity = 1.1f;
        [Range(0f, 1f)] public float LookPulseAmount = 0.15f;
        [Tooltip("How fast the highlight eases in/out when gaze arrives/leaves.")]
        public float FadeSpeed = 6f;

        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        Juice.FaultStrobe _strobe;   // alarm strobes own the emission channel while flashing
        float _strobeRecheck;        // FaultStrobe is attached at runtime — re-scan at ~2 Hz until found
        float _boost;   // 0 = idle, 1 = looked at
        float _phase;
        bool _looked;
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
        // Property-block emission only renders when the material's _EMISSION keyword is on.
        // Enable it once per shared material; the material's own emission color stays black,
        // so other users of the same material are visually unaffected.
        static readonly HashSet<Material> EmissionEnabled = new HashSet<Material>();

        /// <summary>Called by LabelLookSystem when this interactable gains/loses the gaze.</summary>
        public void SetLooked(bool looked) { _looked = looked; }

        void Awake()
        {
            _phase = Random.value * 6.28f;   // stagger so panels don't breathe in lockstep
            var all = GetComponentsInChildren<Renderer>(true);
            var list = new List<Renderer>(all.Length);
            foreach (var r in all)
            {
                if (r.GetComponent<TextMesh>() != null) continue;   // never tint the nameplate
                list.Add(r);
            }
            _renderers = list.ToArray();
            _mpb = new MaterialPropertyBlock();
            _strobe = GetComponentInChildren<Juice.FaultStrobe>(true);
            foreach (var r in _renderers)
            {
                var mats = r.sharedMaterials;
                foreach (var m in mats)
                {
                    if (m == null || !m.HasProperty(EmissionId) || EmissionEnabled.Contains(m)) continue;
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None; // realtime only, never baked
                    EmissionEnabled.Add(m);
                }
            }
        }

        void Update()
        {
            if (_renderers == null || _renderers.Length == 0) return;
            // FaultPropController attaches FaultStrobe to fault props at runtime; while one is
            // flashing on this prop, let the alarm own the emission channel instead of fighting it.
            if (_strobe == null)
            {
                _strobeRecheck -= Time.deltaTime;
                if (_strobeRecheck <= 0f)
                {
                    _strobeRecheck = 0.5f;
                    _strobe = GetComponentInChildren<Juice.FaultStrobe>(true);
                }
            }
            if (_strobe != null && _strobe.isActiveAndEnabled) return;
            _boost = Mathf.MoveTowards(_boost, _looked ? 1f : 0f, FadeSpeed * Time.deltaTime);
            float idle = IdleIntensity * (1f + IdleBreathAmount * Mathf.Sin(Time.time * BreathSpeed + _phase));
            float look = LookIntensity * (1f + LookPulseAmount * Mathf.Sin(Time.time * 6f));
            Color c = GlowColor * Mathf.Lerp(idle, look, _boost);
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionId, c);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
