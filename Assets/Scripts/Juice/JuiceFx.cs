using UnityEngine;

namespace ParkShift.Juice
{
    /// <summary>
    /// Code-configured, pooled particle effects: gate dust puffs, wheel/brake sparks,
    /// launch speed lines, and celebration confetti. Each kind is a small round-robin pool
    /// of ParticleSystems built entirely in code (no prefabs/assets); emitting just moves a
    /// pooled system to the event position and calls Emit. All entry points are static and
    /// null-guarded, so callers never care whether the pool exists in the scene.
    /// </summary>
    public class JuiceFx : MonoBehaviour
    {
        public static JuiceFx Instance { get; private set; }

        ParticleSystem[] _dust, _sparks, _lines, _confetti;
        int _dustI, _sparkI, _lineI, _confettiI;
        Material _mat;

        void Awake()
        {
            Instance = this;
            var shader = Shader.Find("Sprites/Default");   // vertex-color particle-friendly, built-in RP
            if (shader != null) _mat = new Material(shader);

            _dust = new ParticleSystem[6];
            for (int i = 0; i < _dust.Length; i++) _dust[i] = MakeDust("DustPuff" + i);
            _sparks = new ParticleSystem[6];
            for (int i = 0; i < _sparks.Length; i++) _sparks[i] = MakeSparks("Sparks" + i);
            _lines = new ParticleSystem[4];
            for (int i = 0; i < _lines.Length; i++) _lines[i] = MakeSpeedLines("SpeedLines" + i);
            _confetti = new ParticleSystem[3];
            for (int i = 0; i < _confetti.Length; i++) _confetti[i] = MakeConfetti("Confetti" + i);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------
        // Static API
        // ------------------------------------------------------------------

        /// <summary>Small ground-level dust pop (gates thumping open/closed, small impacts).</summary>
        public static void DustPuff(Vector3 pos)
        {
            if (Instance != null) Instance.EmitAt(Instance._dust, ref Instance._dustI, pos, Quaternion.identity, 9);
        }

        /// <summary>Hot orange sparks flying roughly along dir (wheel level, brake shoes).</summary>
        public static void Sparks(Vector3 pos, Vector3 dir, int count)
        {
            if (Instance == null) return;
            Quaternion rot = dir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(dir) : Quaternion.identity;
            Instance.EmitAt(Instance._sparks, ref Instance._sparkI, pos, rot, count);
        }

        /// <summary>Streaky launch burst along dir (dispatch acceleration).</summary>
        public static void SpeedBurst(Vector3 pos, Vector3 dir)
        {
            if (Instance == null) return;
            Quaternion rot = dir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(dir) : Quaternion.identity;
            Instance.EmitAt(Instance._lines, ref Instance._lineI, pos, rot, 14);
        }

        /// <summary>Rainbow confetti fountain (high-combo dispatch celebration).</summary>
        public static void Confetti(Vector3 pos)
        {
            if (Instance != null)
                Instance.EmitAt(Instance._confetti, ref Instance._confettiI, pos, Quaternion.LookRotation(Vector3.up), 46);
        }

        void EmitAt(ParticleSystem[] pool, ref int idx, Vector3 pos, Quaternion rot, int count)
        {
            if (pool == null || pool.Length == 0) return;
            var ps = pool[idx % pool.Length];
            idx = (idx + 1) % pool.Length;
            if (ps == null) return;
            ps.transform.position = pos;
            ps.transform.rotation = rot;
            ps.Emit(count);
        }

        // ------------------------------------------------------------------
        // Builders (all module structs configured in code)
        // ------------------------------------------------------------------

        ParticleSystem NewSystem(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 256;

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;   // Emit() only

            var psr = go.GetComponent<ParticleSystemRenderer>();
            if (psr != null && _mat != null) psr.material = _mat;
            return ps;
        }

        static void FadeAlpha(ParticleSystem ps, Color tint)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(tint, 0f), new GradientColorKey(tint, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.4f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        ParticleSystem MakeDust(string name)
        {
            var ps = NewSystem(name);
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.14f, 0.32f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.72f, 0.66f, 0.72f, 0.4f), new Color(0.55f, 0.5f, 0.6f, 0.35f));
            main.gravityModifier = -0.02f;   // dust drifts up a touch

            var sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Hemisphere;
            sh.radius = 0.22f;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.55f, 1f, 1.5f));

            FadeAlpha(ps, Color.white);
            return ps;
        }

        ParticleSystem MakeSparks(string name)
        {
            var ps = NewSystem(name);
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.45f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 5.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.07f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.85f, 0.35f, 1f), new Color(1f, 0.55f, 0.15f, 1f));
            main.gravityModifier = 1.1f;

            var sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle = 32f;
            sh.radius = 0.06f;

            var psr = ps.GetComponent<ParticleSystemRenderer>();
            if (psr != null)
            {
                psr.renderMode = ParticleSystemRenderMode.Stretch;
                psr.velocityScale = 0.045f;
                psr.lengthScale = 0.9f;
            }

            FadeAlpha(ps, Color.white);
            return ps;
        }

        ParticleSystem MakeSpeedLines(string name)
        {
            var ps = NewSystem(name);
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.32f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(7f, 12f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.7f, 0.95f, 1f, 0.8f), new Color(1f, 1f, 1f, 0.6f));

            var sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle = 7f;
            sh.radius = 0.35f;

            var psr = ps.GetComponent<ParticleSystemRenderer>();
            if (psr != null)
            {
                psr.renderMode = ParticleSystemRenderMode.Stretch;
                psr.velocityScale = 0.12f;
                psr.lengthScale = 1.4f;
            }

            FadeAlpha(ps, Color.white);
            return ps;
        }

        ParticleSystem MakeConfetti(string name)
        {
            var ps = NewSystem(name);
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.2f, 4.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.1f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.gravityModifier = 0.65f;

            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.35f, 0.35f), 0f),
                    new GradientColorKey(new Color(1f, 0.85f, 0.25f), 0.25f),
                    new GradientColorKey(new Color(0.35f, 0.95f, 0.5f), 0.5f),
                    new GradientColorKey(new Color(0.3f, 0.7f, 1f), 0.75f),
                    new GradientColorKey(new Color(0.9f, 0.4f, 1f), 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            var mmg = new ParticleSystem.MinMaxGradient(g);
            mmg.mode = ParticleSystemGradientMode.RandomColor;
            main.startColor = mmg;

            var sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle = 38f;
            sh.radius = 0.25f;

            var rol = ps.rotationOverLifetime;
            rol.enabled = true;
            rol.z = new ParticleSystem.MinMaxCurve(-9f, 9f);

            FadeAlpha(ps, Color.white);
            return ps;
        }
    }
}
