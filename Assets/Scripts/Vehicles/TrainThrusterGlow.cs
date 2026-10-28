using System.Collections.Generic;
using UnityEngine;
using ParkShift.Core;
using ParkShift.Attraction;

namespace ParkShift.Vehicles
{
    /// <summary>Read-only thruster FX for the GALAXY EXPRESS: scales the emissive glow of
    /// every child renderer named "Thruster*" with the same sim speed TrainVisual mirrors
    /// (TrainSim.Speed, normalized by FullSpeed like TrainJuice), driven through a shared
    /// MaterialPropertyBlock (NeonPulse pattern — no material instancing, no per-frame
    /// allocation, no sim mutation). Added by TrainCarFactory; TrainId comes from the
    /// sibling TrainVisual. A subtle flicker rides on top so the burn feels alive.</summary>
    public class TrainThrusterGlow : MonoBehaviour
    {
        [Tooltip("Sim speed (block-progress units/s) that counts as full burn.")]
        public float FullSpeed = 0.3f;
        [Tooltip("Thruster emission color at full burn.")]
        public Color GlowColor = new Color(0.25f, 0.85f, 1f);
        [Tooltip("Emission boost while parked (idle pilot glow).")]
        public float IdleBoost = 0.6f;
        [Tooltip("Emission boost at full burn.")]
        public float BurnBoost = 5.5f;
        [Tooltip("How quickly the glow tracks speed changes (1/s).")]
        public float Response = 6f;

        TrainVisual _tv;
        Renderer[] _thrusters;
        MaterialPropertyBlock _mpb;
        float _level;
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void Start()
        {
            _tv = GetComponent<TrainVisual>();
            _mpb = new MaterialPropertyBlock();
            var list = new List<Renderer>();
            CollectThrusters(transform, list);
            _thrusters = list.ToArray();
        }

        static void CollectThrusters(Transform t, List<Renderer> into)
        {
            foreach (Transform c in t)
            {
                if (c.name.StartsWith("Thruster")) into.AddRange(c.GetComponentsInChildren<Renderer>());
                else CollectThrusters(c, into);
            }
        }

        void Update()
        {
            if (_thrusters == null || _thrusters.Length == 0) return;
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null) return;
            int id = _tv != null ? _tv.TrainId : -1;
            if (id < 0 || id >= m.Sim.Trains.Length) return;

            // Exponential tracking so burn changes glide instead of snapping.
            float norm = Mathf.Clamp01(m.Sim.Trains[id].Speed / Mathf.Max(0.01f, FullSpeed));
            _level += (norm - _level) * (1f - Mathf.Exp(-Response * Time.deltaTime));

            float flicker = 1f + 0.07f * _level * Mathf.Sin(Time.time * 39f + id * 2.1f);
            Color e = GlowColor * Mathf.Lerp(IdleBoost, BurnBoost, _level) * flicker;
            for (int i = 0; i < _thrusters.Length; i++)
            {
                var r = _thrusters[i];
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionId, e);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
