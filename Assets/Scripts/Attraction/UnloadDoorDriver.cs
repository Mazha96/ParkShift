using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Slides the Unload door open/closed to mirror the sim's UnloadDoorOpen flag.
    /// Uses a kinematic panel and pauses motion when a crew member occupies its swept volume.</summary>
    public class UnloadDoorDriver : MonoBehaviour
    {
        public static UnloadDoorDriver Instance { get; private set; }
        public Transform DoorPanel;
        [Tooltip("Local slide offset from the closed pose into the open panel pocket.")]
        public Vector3 OpenOffset = new Vector3(0f, 2.2f, 0f);
        public float Speed = 3.5f;
        Vector3 _closed;
        Rigidbody _body;
        Collider _panelCollider;
        readonly Collider[] _occupants = new Collider[32];
        bool _synced;

        public bool IsFullyOpen => _synced && _body != null
            && (_body.position - WorldTarget(true)).sqrMagnitude < 0.0001f;
        public bool IsFullyClosed => _synced && _body != null
            && (_body.position - WorldTarget(false)).sqrMagnitude < 0.0001f;
        public bool CanClose => _body != null && DoorPanel != null && !MotionBlocked(WorldTarget(false));

        void Awake() { Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; }

        Vector3 WorldTarget(bool open)
        {
            Vector3 target = _closed + (open ? OpenOffset : Vector3.zero);
            return DoorPanel.parent != null ? DoorPanel.parent.TransformPoint(target) : target;
        }

        void Start()
        {
            if (DoorPanel == null) return;
            _closed = DoorPanel.localPosition;
            _panelCollider = DoorPanel.GetComponent<Collider>();
            _body = DoorPanel.GetComponent<Rigidbody>();
            if (_body == null) _body = DoorPanel.gameObject.AddComponent<Rigidbody>();
            _body.isKinematic = true;
            _body.useGravity = false;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
        }

        void FixedUpdate()
        {
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null || DoorPanel == null || _body == null) return;
            bool open = m.Sim.UnloadDoorOpen;
            Vector3 worldTarget = WorldTarget(open);
            if (!_synced)
            {
                if (!MotionBlocked(worldTarget)) _body.position = worldTarget;
                _synced = true;
                return;
            }
            Vector3 next = Vector3.Lerp(_body.position, worldTarget,
                1f - Mathf.Exp(-Mathf.Max(0f, Speed) * Time.fixedDeltaTime));
            if ((next - worldTarget).sqrMagnitude < 0.000001f) next = worldTarget;
            if (MotionBlocked(next)) return;
            _body.MovePosition(next);
        }

        bool MotionBlocked(Vector3 next)
        {
            if (_panelCollider == null || _panelCollider.isTrigger) return false;
            Bounds sweep = _panelCollider.bounds;
            // Bounds come from physics; the interpolated render transform can lag the body.
            Vector3 shift = next - _body.position;
            sweep.Encapsulate(sweep.min + shift);
            sweep.Encapsulate(_panelCollider.bounds.max + shift);
            sweep.Expand(0.08f); // allow the character controller's skin before contact
            int count = Physics.OverlapBoxNonAlloc(sweep.center, sweep.extents, _occupants,
                Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            if (count == _occupants.Length) return true; // crowded/overflowing query: hold safely
            for (int i = 0; i < count; i++)
                if (_occupants[i] is CharacterController
                    || _occupants[i].GetComponentInParent<Player.FirstPersonController>() != null)
                    return true;
            return false;
        }
    }
}
