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
        [Tooltip("Spring damping (0 = no overshoot, 1 = a small mechanical settle).")]
        public float Overshoot = 0f;
        Quaternion _rest;
        Transform _bound;
        float _position, _velocity;
        bool _target;

        public Hinge(Transform t, Vector3 offsetEuler)
        {
            T = t;
            OffsetEuler = offsetEuler;
            Bind();
        }

        public float Position => Mathf.Clamp01(_position);
        public bool IsSettled => Mathf.Abs(_position - (_target ? 1f : 0f)) < 0.001f
                              && Mathf.Abs(_velocity) < 0.01f;

        bool Bind()
        {
            if (T == null) return false;
            if (_bound != T)
            {
                _bound = T;
                _rest = T.localRotation;
                _position = _velocity = 0f;
            }
            return true;
        }

        public void Snap(bool engaged)
        {
            if (!Bind()) return;
            _target = engaged;
            _position = engaged ? 1f : 0f;
            _velocity = 0f;
            T.localRotation = _rest * Quaternion.Euler(OffsetEuler * _position);
        }

        public void Tick(bool engaged, float dt, float speed = 3.5f)
        {
            if (!Bind() || dt <= 0f || speed <= 0f) return;
            _target = engaged;
            float target = engaged ? 1f : 0f;
            float omega = speed * 7f;
            float damping = Mathf.Lerp(1f, 0.65f, Mathf.Clamp01(Overshoot));
            float displacement = _position - target;
            float decay = Mathf.Exp(-damping * omega * dt);
            // Closed-form damped spring: stable at long frames and keeps momentum on reversal.
            if (damping >= 0.999f)
            {
                float c = _velocity + omega * displacement;
                _position = target + (displacement + c * dt) * decay;
                _velocity = (_velocity - omega * c * dt) * decay;
            }
            else
            {
                float frequency = omega * Mathf.Sqrt(1f - damping * damping);
                float cosine = Mathf.Cos(frequency * dt);
                float sine = Mathf.Sin(frequency * dt);
                float c = (_velocity + damping * omega * displacement) / frequency;
                _position = target + decay * (displacement * cosine + c * sine);
                _velocity = decay * (_velocity * cosine
                    - (damping * omega * _velocity + omega * omega * displacement) / frequency * sine);
            }
            if (IsSettled) { _position = target; _velocity = 0f; }
            T.localRotation = _rest * Quaternion.Euler(OffsetEuler * _position);
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
            float k = Mathf.Clamp01(_p);
            _p *= Mathf.Exp(-Mathf.Max(0f, Speed) * 5f * Mathf.Max(0f, dt));
            if (_p < 0.001f) _p = 0f;
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

}
