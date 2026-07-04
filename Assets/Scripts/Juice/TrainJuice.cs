using System.Collections.Generic;
using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Juice
{
    /// <summary>
    /// Per-train FX layer (pure presentation, added alongside TrainVisual — never touches the
    /// wave-1 mesh/articulation code). Drives:
    /// - a 3D coaster-rumble loop whose pitch/volume follow TrainSim.Speed,
    /// - dispatch anticipation squash + springy launch stretch with speed lines,
    /// - launch-block sparks while accelerating hard and brake sparks when braking hot,
    /// - restraint clunk/clack audio and a brief click-flash on the RestraintBar meshes.
    /// Everything is observed from mirrored sim state (no authoritative callbacks), so the
    /// same juice plays on non-authoritative multiplayer clients.
    /// </summary>
    public class TrainJuice : MonoBehaviour
    {
        public int TrainId = -1;

        [Tooltip("Sim speed (block-progress units/s) that counts as 'full speed' for the mix.")]
        public float FullSpeed = 0.3f;
        public float RumbleVolume = 0.55f;

        Vehicles.TrainVisual _tv;
        AudioSource _rumble;
        Vector3 _baseScale;
        bool _scaleInit;
        float _launchTimer = -1f;
        int _prevState = -1;
        bool _prevVerified;
        bool _verInit;
        float _prevSpeed;
        float _sparkCooldown;

        Renderer[] _barRenderers;
        Color[] _barBaseColors;
        float _barFlash;
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void Start()
        {
            _tv = GetComponent<Vehicles.TrainVisual>();
            if (TrainId < 0 && _tv != null) TrainId = _tv.TrainId;
            _baseScale = transform.localScale;
            _scaleInit = true;
            _mpb = new MaterialPropertyBlock();

            var bars = new List<Renderer>();
            CollectBarRenderers(transform, bars);
            _barRenderers = bars.ToArray();
            _barBaseColors = new Color[_barRenderers.Length];
            for (int i = 0; i < _barRenderers.Length; i++)
            {
                var m = _barRenderers[i] != null ? _barRenderers[i].sharedMaterial : null;
                _barBaseColors[i] = m != null && m.HasProperty(ColorId) ? m.color : Color.white;
            }
        }

        static void CollectBarRenderers(Transform t, List<Renderer> into)
        {
            foreach (Transform c in t)
            {
                if (c.name.StartsWith("RestraintBar"))
                    into.AddRange(c.GetComponentsInChildren<Renderer>());
                else
                    CollectBarRenderers(c, into);
            }
        }

        TrainSim Sim()
        {
            var m = Attraction.AttractionManager.Instance;
            if (m == null || m.Sim == null) return null;
            if (TrainId < 0 || TrainId >= m.Sim.Trains.Length) return null;
            return m.Sim.Trains[TrainId];
        }

        void EnsureRumble()
        {
            if (_rumble != null || AudioDirector.Instance == null) return;
            var go = new GameObject("RumbleLoop");
            go.transform.SetParent(transform, false);
            _rumble = go.AddComponent<AudioSource>();
            _rumble.clip = AudioSynth.RumbleLoop();
            _rumble.loop = true;
            _rumble.playOnAwake = false;
            _rumble.spatialBlend = 1f;
            _rumble.dopplerLevel = 0f;
            _rumble.rolloffMode = AudioRolloffMode.Linear;
            _rumble.minDistance = 4f;
            _rumble.maxDistance = 45f;
            _rumble.volume = 0f;
            // Desync the loop start per train so two rumbles never phase in lockstep.
            _rumble.time = Random.value * Mathf.Max(0.01f, _rumble.clip != null ? _rumble.clip.length : 1f) * 0.95f;
            _rumble.Play();
        }

        void Update()
        {
            var t = Sim();
            if (t == null) return;
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);

            EnsureRumble();
            float speedNorm = Mathf.Clamp01(t.Speed / Mathf.Max(0.01f, FullSpeed));
            if (_rumble != null)
            {
                _rumble.volume = Mathf.MoveTowards(_rumble.volume,
                    AudioDirector.Master * RumbleVolume * speedNorm, 2.5f * dt);
                _rumble.pitch = 0.72f + 0.55f * speedNorm;
            }

            // ---- State edges ----
            int st = (int)t.State;
            if (_prevState >= 0 && st != _prevState)
            {
                // Real dispatch launch: leaving ReadyToDispatch under power.
                if ((TrainState)_prevState == TrainState.ReadyToDispatch && t.State == TrainState.Running)
                    OnLaunch();
            }
            _prevState = st;

            // Restraint latch edges (drivers animate the bars; we add the click).
            if (!_verInit) { _prevVerified = t.RestraintsVerified; _verInit = true; }
            if (t.RestraintsVerified != _prevVerified)
            {
                if (t.RestraintsVerified)
                {
                    AudioDirector.Clunk(transform.position);
                    _barFlash = 1f;
                }
                else if (t.State == TrainState.InStationLoading || t.State == TrainState.InStationUnloading)
                {
                    AudioDirector.Clack(transform.position);
                }
                _prevVerified = t.RestraintsVerified;
            }

            // ---- Squash & stretch launch timeline ----
            if (_launchTimer >= 0f && _scaleInit)
            {
                _launchTimer += dt;
                float p = _launchTimer;
                float sy = 1f, sz = 1f;
                if (p < 0.16f)
                {
                    // anticipation: quick squash down before the surge
                    float k = Mathf.Sin(Mathf.Clamp01(p / 0.16f) * Mathf.PI);
                    sy = 1f - 0.09f * k;
                    sz = 1f + 0.07f * k;
                }
                else
                {
                    // springy release: damped wobble back to rest
                    float q = p - 0.16f;
                    float k = Mathf.Exp(-q * 5.5f) * Mathf.Sin(q * 17f);
                    sy = 1f + 0.05f * k;
                    sz = 1f - 0.045f * k;
                }
                transform.localScale = Vector3.Scale(_baseScale, new Vector3(1f, sy, sz));
                if (p > 1.1f)
                {
                    transform.localScale = _baseScale;
                    _launchTimer = -1f;
                }
            }

            // ---- Accel / brake sparks (wheel level) ----
            float accel = (t.Speed - _prevSpeed) / dt;
            _prevSpeed = t.Speed;
            _sparkCooldown -= dt;
            var m = Attraction.AttractionManager.Instance;
            int lastBlock = m != null && m.Sim != null ? m.Sim.Config.BlockCount - 1 : -1;
            Vector3 wheelPos = transform.position + transform.up * 0.08f;
            if (_sparkCooldown <= 0f)
            {
                if (accel < -0.14f && t.Speed > 0.03f && (t.CurrentBlock == lastBlock || t.State == TrainState.Running))
                {
                    // braking hot — spark shower trailing behind the wheels
                    JuiceFx.Sparks(wheelPos, -transform.forward * 0.7f + Vector3.up * 0.5f, 8);
                    _sparkCooldown = 0.07f;
                }
                else if (accel > 0.14f && speedNorm > 0.35f)
                {
                    // hard launch acceleration — streaks + a few sparks off the drive tires
                    JuiceFx.SpeedBurst(wheelPos, transform.forward);
                    JuiceFx.Sparks(wheelPos, -transform.forward * 0.4f + Vector3.up * 0.3f, 3);
                    _sparkCooldown = 0.12f;
                }
            }

            // ---- Restraint click flash ----
            if (_barFlash > 0f && _barRenderers != null && _mpb != null)
            {
                _barFlash = Mathf.MoveTowards(_barFlash, 0f, 3.2f * dt);
                float k = _barFlash * _barFlash;
                for (int i = 0; i < _barRenderers.Length; i++)
                {
                    var r = _barRenderers[i];
                    if (r == null) continue;
                    r.GetPropertyBlock(_mpb);
                    _mpb.SetColor(ColorId, Color.Lerp(_barBaseColors[i], Color.white, k));
                    _mpb.SetColor(EmissionId, new Color(1f, 0.9f, 0.6f) * (1.6f * k));
                    r.SetPropertyBlock(_mpb);
                    if (_barFlash <= 0f) r.SetPropertyBlock(null);   // fully restore when done
                }
            }
        }

        void OnLaunch()
        {
            _launchTimer = 0f;
            AudioDirector.Whoosh(transform.position);
            JuiceFx.SpeedBurst(transform.position + transform.up * 0.1f, transform.forward);
            JuiceFx.Sparks(transform.position + transform.up * 0.05f,
                           -transform.forward * 0.5f + Vector3.up * 0.4f, 6);
        }
    }
}
