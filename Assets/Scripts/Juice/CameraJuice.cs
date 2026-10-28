using UnityEngine;

namespace ParkShift.Juice
{
    /// <summary>
    /// Trauma-based camera shake + FOV kick, applied additively in LateUpdate on top of
    /// whatever the FirstPersonController posed this frame. Deliberately conservative:
    /// trauma is squared and capped, max amplitudes are tiny, and everything decays fast —
    /// feedback, never nausea. Safe when the controller skips a frame (offsets are stripped
    /// before being re-applied, so nothing accumulates).
    /// </summary>
    public class CameraJuice : MonoBehaviour
    {
        public static CameraJuice Instance { get; private set; }

        [Tooltip("Trauma lost per second (trauma is 0..1; shake amplitude uses trauma^2).")]
        public float TraumaDecay = 1.7f;
        public float MaxShakeDegrees = 1.5f;
        public float MaxShakeOffset = 0.022f;
        [Tooltip("How quickly an FOV kick relaxes back to the base FOV.")]
        public float FovRelax = 6f;
        [Tooltip("Additional FOV requested by locomotion. Set to zero to disable speed widening.")]
        public float SpeedFov;
        float _speedFov;

        float _trauma;
        float _fovKick;
        Camera _cam;
        float _baseFov = -1f;
        float _seed;

        Vector3 _lastBasePos, _lastFinalPos;
        Quaternion _lastBaseRot = Quaternion.identity, _lastFinalRot = Quaternion.identity;
        bool _applied;

        void OnEnable()
        {
            Instance = this;
            _cam = GetComponent<Camera>();
            _seed = Random.value * 97f;
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Add shake energy (0..1-ish; small events ~0.15, big hits ~0.4). Null-safe.</summary>
        public static void AddTrauma(float amount)
        {
            if (Instance != null)
                Instance._trauma = Mathf.Clamp01(Instance._trauma + Mathf.Max(0f, amount));
        }

        /// <summary>Punch the FOV out by some degrees; it eases back on its own. Null-safe.</summary>
        public static void KickFov(float degrees)
        {
            if (Instance != null)
                Instance._fovKick = Mathf.Clamp(Instance._fovKick + degrees, -8f, 8f);
        }

        /// <summary>Distance falloff helper for world-space events (1 at the event, 0 far away).</summary>
        public static float Falloff(Vector3 eventPos, float fullRange, float zeroRange)
        {
            if (Instance == null) return 0f;
            float d = Vector3.Distance(Instance.transform.position, eventPos);
            if (zeroRange <= fullRange) return d <= zeroRange ? 1f : 0f;
            return Mathf.Clamp01(1f - (d - fullRange) / (zeroRange - fullRange));
        }

        void LateUpdate()
        {
            _trauma = Mathf.Max(0f, _trauma - TraumaDecay * Time.deltaTime);
            _fovKick = Mathf.Lerp(_fovKick, 0f, 1f - Mathf.Exp(-FovRelax * Time.deltaTime));
            _speedFov = Mathf.Lerp(_speedFov, Mathf.Clamp(SpeedFov, 0f, 6f), 1f - Mathf.Exp(-5f * Time.deltaTime));

            if (_cam != null)
            {
                if (_baseFov < 0f) _baseFov = _cam.fieldOfView;
                _cam.fieldOfView = _baseFov + _fovKick + _speedFov;
            }

            float sh = _trauma * _trauma;
            Vector3 posOff = Vector3.zero;
            Quaternion rotOff = Quaternion.identity;
            if (sh > 0.0001f)
            {
                float t = Time.time * 21f;
                rotOff = Quaternion.Euler(
                    (Mathf.PerlinNoise(_seed, t) * 2f - 1f) * MaxShakeDegrees * sh,
                    (Mathf.PerlinNoise(_seed + 11f, t) * 2f - 1f) * MaxShakeDegrees * sh,
                    (Mathf.PerlinNoise(_seed + 23f, t) * 2f - 1f) * MaxShakeDegrees * 0.6f * sh);
                posOff = new Vector3(
                    (Mathf.PerlinNoise(_seed + 37f, t) * 2f - 1f),
                    (Mathf.PerlinNoise(_seed + 53f, t) * 2f - 1f), 0f) * (MaxShakeOffset * sh);
            }

            // If nobody re-posed the camera since our last write, strip our previous offset
            // first so offsets never stack up across frames.
            Vector3 basePos = transform.localPosition;
            Quaternion baseRot = transform.localRotation;
            if (_applied)
            {
                if ((basePos - _lastFinalPos).sqrMagnitude < 1e-10f) basePos = _lastBasePos;
                if (Mathf.Abs(Quaternion.Dot(baseRot, _lastFinalRot)) > 0.99999994f) baseRot = _lastBaseRot;
            }
            _lastBasePos = basePos;
            _lastBaseRot = baseRot;
            _lastFinalPos = basePos + posOff;
            _lastFinalRot = baseRot * rotOff;
            transform.localPosition = _lastFinalPos;
            transform.localRotation = _lastFinalRot;
            _applied = true;
        }
    }
}
