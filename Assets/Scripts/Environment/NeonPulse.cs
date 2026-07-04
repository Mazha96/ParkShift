using UnityEngine;

namespace ParkShift.Environment
{
    /// <summary>
    /// Gentle emissive pulse for neon trim strips, lamp bulbs and cosmic crystals.
    /// Drives _EmissionColor on every child renderer through a shared
    /// MaterialPropertyBlock (no material instancing, no per-frame allocation) and
    /// optionally follows with a realtime Light. One component on a parent runs a
    /// whole strip of segments in sync; stagger <see cref="Phase"/> between strips
    /// so the park doesn't blink in lockstep. Realtime-only — nothing here touches
    /// baked GI.
    /// </summary>
    public class NeonPulse : MonoBehaviour
    {
        [Tooltip("HDR emission tint. Multiplied by Intensity, so keep this 0..1-ish.")]
        public Color EmissionColor = new Color(0.25f, 0.95f, 1f);
        [Tooltip("Base HDR multiplier for the emission color.")]
        public float Intensity = 2f;
        [Range(0f, 1f)] public float PulseAmount = 0.2f;
        public float Speed = 1.5f;
        [Tooltip("Phase offset so neighboring strips don't pulse together.")]
        public float Phase = 0f;
        [Tooltip("Optional realtime light whose intensity follows the pulse.")]
        public Light LinkedLight;
        public float LightIntensity = 1.2f;

        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void Awake()
        {
            _renderers = GetComponentsInChildren<Renderer>();
            _mpb = new MaterialPropertyBlock();
        }

        void Update()
        {
            float wave = 1f + PulseAmount * Mathf.Sin(Time.time * Speed + Phase);
            Color c = EmissionColor * (Intensity * wave);
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionId, c);
                r.SetPropertyBlock(_mpb);
            }
            if (LinkedLight != null)
                LinkedLight.intensity = LightIntensity * wave;
        }
    }
}
