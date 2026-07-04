using UnityEngine;

namespace ParkShift.Juice
{
    /// <summary>
    /// Subtle idle bounce for queued guests: sine-bobs the guest's visual child with a
    /// randomized phase/speed so the queue shimmers instead of pulsing in lockstep. The
    /// bounce fades out while the guest is walking (the walk animation covers movement)
    /// or seated on a train (parented to a seat anchor). Hop() gives a short celebratory
    /// jump burst — used when a train they cheered for dispatches.
    /// </summary>
    public class GuestBounce : MonoBehaviour
    {
        public float Amplitude = 0.032f;
        [Tooltip("Bounces per second (randomized a little per guest in Start).")]
        public float Speed = 2.6f;
        [Tooltip("World-space hop height at full cheer power (m).")]
        public float HopHeight = 0.2f;

        Transform _visual;
        Vector3 _restLocal;
        float _phase;
        float _weight;
        float _hop;
        Vector3 _lastPos;
        bool _init;

        void Start()
        {
            if (transform.childCount > 0) _visual = transform.GetChild(0);
            if (_visual != null) _restLocal = _visual.localPosition;
            _phase = Random.value * 6.2831853f;
            Speed = Random.Range(2.1f, 3.2f);
            _lastPos = transform.position;
            _init = true;
        }

        /// <summary>Celebration hop (0..1). Stacks safely; decays on its own.</summary>
        public void Hop(float power)
        {
            _hop = Mathf.Max(_hop, Mathf.Clamp01(power));
        }

        void OnDisable()
        {
            if (_init && _visual != null) _visual.localPosition = _restLocal;
        }

        void Update()
        {
            if (!_init || _visual == null) return;
            bool seated = transform.parent != null;   // boarded guests are parented to seat anchors
            Vector3 p = transform.position;
            bool moving = (p - _lastPos).sqrMagnitude > 1e-6f;
            _lastPos = p;

            float target = (!seated && !moving) ? 1f : 0f;
            _weight = Mathf.MoveTowards(_weight, target, 3f * Time.deltaTime);
            _hop = Mathf.MoveTowards(_hop, 0f, 2.1f * Time.deltaTime);

            float hopAmp = seated ? 0f : HopHeight * _hop;
            _phase += Time.deltaTime * Speed * 6.2831853f * (1f + 1.6f * _hop);
            float bob = Mathf.Abs(Mathf.Sin(_phase)) * (Amplitude * _weight + hopAmp);
            _visual.localPosition = _restLocal + Vector3.up * bob;
        }
    }
}
