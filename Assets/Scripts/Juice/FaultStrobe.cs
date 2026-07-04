using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Juice
{
    /// <summary>
    /// Hard on/off alarm strobe for fault props (NeonPulse's angry cousin). Flashes both the
    /// base color and the emission color of every child renderer through a MaterialPropertyBlock
    /// (no material instancing), skipping TextMesh labels. All strobes count phase from
    /// Time.time at SyncHz — the same period the AudioSynth klaxon loop uses — so every alarm
    /// light in the park blinks in sync with the klaxon. Attach to a fault prop; it only runs
    /// while the prop is active (FaultPropController toggles the GameObjects), and property
    /// blocks are cleared on disable so props look normal next time they appear.
    /// </summary>
    public class FaultStrobe : MonoBehaviour
    {
        /// <summary>Shared strobe/klaxon frequency (flashes per second).</summary>
        public const float SyncHz = 1.6f;

        public Color FlashColor = new Color(1f, 0.25f, 0.18f);
        [Range(0f, 1f)] public float DutyCycle = 0.42f;

        Renderer[] _renderers;
        Color[] _baseColors;
        MaterialPropertyBlock _mpb;
        bool _wasOn;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void OnEnable()
        {
            if (_renderers == null)
            {
                var list = new List<Renderer>();
                foreach (var r in GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    if (r.GetComponent<TextMesh>() != null) continue;   // leave labels readable
                    list.Add(r);
                }
                _renderers = list.ToArray();
                _baseColors = new Color[_renderers.Length];
                for (int i = 0; i < _renderers.Length; i++)
                {
                    var m = _renderers[i].sharedMaterial;
                    _baseColors[i] = m != null && m.HasProperty(ColorId) ? m.color : Color.white;
                }
                _mpb = new MaterialPropertyBlock();
            }
            _wasOn = false;
        }

        void OnDisable()
        {
            if (_renderers == null) return;
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].SetPropertyBlock(null);
        }

        void Update()
        {
            if (_renderers == null || _mpb == null) return;
            bool on = Mathf.Repeat(Time.time * SyncHz, 1f) < DutyCycle;
            if (on == _wasOn) return;   // only touch blocks on the edge
            _wasOn = on;
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(ColorId, on ? FlashColor : _baseColors[i]);
                _mpb.SetColor(EmissionId, on ? FlashColor * 2.2f : Color.black);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
