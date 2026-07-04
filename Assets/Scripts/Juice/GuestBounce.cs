using UnityEngine;

namespace ParkShift.Juice
{
    /// <summary>
    /// Grounded idle life for queued guests. Replaces the old levitating sine-bob with a
    /// weight-shift model whose feet stay planted: guest roots pivot at the feet, so a
    /// subtle Y squash-and-recover on the root scale reads as breathing/settling instead
    /// of hovering. Layered on top: a slow lateral sway with an occasional stance shift
    /// (every 8-20 s), a small look-around yaw drift every few seconds, and full per-guest
    /// desync (random phase, +/-20% period jitter, amplitude variation) so the queue never
    /// pulses in lockstep. Hop() stays a real celebration jump but gains an anticipation
    /// dip, airborne stretch and a landing-squash recovery; height is randomized per hop.
    /// Fades out while walking (the walk anim covers movement) or seated on a train
    /// (parented to a seat anchor), and guests far from the camera park at the rest pose
    /// and skip the math. Time-based, no coroutines, no per-frame allocations. Component
    /// name and the Hop(power) API are unchanged so GuestVisualManager wiring keeps working.
    /// </summary>
    public class GuestBounce : MonoBehaviour
    {
        [Tooltip("Idle squash amplitude as a fraction of guest height (randomized per guest in Start).")]
        public float Amplitude = 0.03f;
        [Tooltip("Breath/settle cycles per second (jittered +/-20% per guest in Start).")]
        public float Speed = 0.65f;
        [Tooltip("Hop height at full cheer power, local units (randomized +/-15% per hop).")]
        public float HopHeight = 0.22f;
        [Tooltip("Guests beyond this distance from the main camera skip idle animation.")]
        public float FarDistance = 30f;

        const float TwoPi = 6.2831853f;

        Transform _visual;          // first child: capsule "Visual" wrapper or the Dewey rig root
        Vector3 _visualRestPos;
        Quaternion _visualRestRot;
        Vector3 _baseScale;

        float _phase, _speed, _amp;
        float _weight;              // 0 = suppressed (walking/seated), 1 = fully idling
        float _swayBias, _swayTarget, _shuffleTimer;
        float _yaw, _yawTarget, _lookTimer;
        float _hopTimer = -1f;      // <0 = no hop in flight
        float _hopPower, _hopJitter;
        Vector3 _lastPos;
        Transform _cam;
        float _camRetry;
        bool _init, _resting;

        void Start()
        {
            if (transform.childCount > 0) _visual = transform.GetChild(0);
            if (_visual != null)
            {
                _visualRestPos = _visual.localPosition;
                _visualRestRot = _visual.localRotation;
            }
            _baseScale = transform.localScale;
            _phase = Random.value * TwoPi;                 // random phase: no metronome queue
            _speed = Speed * Random.Range(0.8f, 1.2f);     // +/-20% period jitter
            _amp = Amplitude * Random.Range(0.75f, 1.25f); // amplitude variation
            _shuffleTimer = Random.Range(2f, 12f);         // first stance shift comes early-ish
            _lookTimer = Random.Range(1.5f, 5f);
            _lastPos = transform.position;
            _init = true;
        }

        /// <summary>Celebration hop (0..1). Boosts an in-flight hop; decays on its own.</summary>
        public void Hop(float power)
        {
            power = Mathf.Clamp01(power);
            if (_hopTimer >= 0f) { _hopPower = Mathf.Max(_hopPower, power); return; }
            _hopPower = power;
            _hopJitter = Random.Range(0.85f, 1.15f);
            _hopTimer = 0f;
        }

        void OnDisable()
        {
            if (!_init) return;
            RestoreVisual();
            transform.localScale = _baseScale;
            _resting = true;
        }

        void RestoreVisual()
        {
            if (_visual == null) return;
            _visual.localPosition = _visualRestPos;
            _visual.localRotation = _visualRestRot;
        }

        void Update()
        {
            if (!_init) return;
            float dt = Time.deltaTime;
            bool seated = transform.parent != null;   // boarded guests are parented to seat anchors
            Vector3 p = transform.position;
            bool moving = (p - _lastPos).sqrMagnitude > 1e-6f;
            _lastPos = p;

            if (seated)
            {
                // Riding: kill the idle layer. Only the visual child is restored — SetParent
                // re-derived the root's localScale, so writing our cached base would fight it
                // (worst leftover is a <3% squash, invisible under the restraint bar).
                _hopTimer = -1f; _hopPower = 0f;
                _weight = 0f; _yaw = 0f; _swayBias = 0f; _swayTarget = 0f;
                if (!_resting) { RestoreVisual(); _resting = true; }
                return;
            }

            // Distance cull: far guests park at the rest pose and skip the idle math.
            if (_cam == null)
            {
                _camRetry -= dt;
                if (_camRetry <= 0f) { var c = Camera.main; if (c != null) _cam = c.transform; _camRetry = 1f; }
            }
            if (_cam != null && _hopTimer < 0f &&
                (_cam.position - p).sqrMagnitude > FarDistance * FarDistance)
            {
                if (!_resting) { RestoreVisual(); transform.localScale = _baseScale; _resting = true; }
                return;
            }

            _weight = Mathf.MoveTowards(_weight, moving ? 0f : 1f, 3f * dt);

            // Fast path while walking: the walk animation owns the motion.
            if (_weight < 0.001f && _hopTimer < 0f && Mathf.Abs(_yaw) < 0.01f)
            {
                if (!_resting) { RestoreVisual(); transform.localScale = _baseScale; _resting = true; }
                return;
            }

            // ---- breathing squash: pivot is at the feet, so Y compresses into the ground ----
            _phase += dt * _speed * TwoPi;
            float squash = _amp * (0.5f + 0.5f * Mathf.Sin(_phase)) * _weight;

            // ---- weight shift: occasional new stance plus a continuous micro-sway ----
            _shuffleTimer -= dt;
            if (_shuffleTimer <= 0f)
            {
                _swayTarget = Random.Range(-0.03f, 0.03f);
                _shuffleTimer = Random.Range(8f, 20f);
            }
            _swayBias = Mathf.MoveTowards(_swayBias, _swayTarget, 0.08f * dt);
            float sway = (_swayBias + 0.006f * Mathf.Sin(_phase * 0.53f)) * _weight;

            // ---- look around: drift to a new small head/torso yaw every few seconds ----
            _lookTimer -= dt;
            if (_lookTimer <= 0f)
            {
                _yawTarget = Random.Range(-14f, 14f);
                _lookTimer = Random.Range(2.5f, 7f);
            }
            _yaw = Mathf.MoveTowards(_yaw, _yawTarget * _weight, 22f * dt);

            // ---- cheer hop: anticipation dip -> airborne arc -> landing recover ----
            float hopY = 0f, hopSquash = 0f, hopStretch = 0f;
            if (_hopTimer >= 0f)
            {
                _hopTimer += dt;
                const float Dip = 0.13f, Recover = 0.26f;
                float air = 0.30f + 0.14f * _hopPower;
                if (_hopTimer < Dip)
                {
                    // crouch before leaving the ground
                    hopSquash = 0.14f * _hopPower * Mathf.Sin(_hopTimer / Dip * Mathf.PI * 0.5f);
                }
                else if (_hopTimer < Dip + air)
                {
                    // parabolic arc with a touch of stretch through the apex
                    float u = (_hopTimer - Dip) / air;
                    hopY = HopHeight * _hopPower * _hopJitter * 4f * u * (1f - u);
                    hopStretch = 0.09f * _hopPower * Mathf.Sin(u * Mathf.PI);
                }
                else if (_hopTimer < Dip + air + Recover)
                {
                    // landing impact squash springing back to rest
                    float u = (_hopTimer - Dip - air) / Recover;
                    hopSquash = 0.12f * _hopPower * (1f - u) * (1f - u);
                }
                else { _hopTimer = -1f; _hopPower = 0f; }
            }

            // ---- apply: scale from the feet, sway/hop offset + yaw/lean on the visual child ----
            float sy = 1f - squash - hopSquash + hopStretch;
            float sxz = 1f + (squash + hopSquash) * 0.55f - hopStretch * 0.35f;
            transform.localScale = new Vector3(_baseScale.x * sxz, _baseScale.y * sy, _baseScale.z * sxz);
            if (_visual != null)
            {
                _visual.localPosition = _visualRestPos + new Vector3(sway, hopY, 0f);
                _visual.localRotation = _visualRestRot * Quaternion.Euler(0f, _yaw, -sway * 60f);
            }
            _resting = false;
        }
    }
}
