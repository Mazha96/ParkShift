using UnityEngine;
using ParkShift.Player;
using ParkShift.Attraction;

namespace ParkShift.Interaction
{
    public class PlayerInteractor : MonoBehaviour
    {
        public float Range = 3f;
        public Camera Cam;
        public string CurrentPrompt { get; private set; }
        public float HoldProgress { get; private set; }
        public bool IsHolding { get; private set; }
        public string CurrentActionLabel { get; private set; }
        RoleController _role;
        readonly RaycastHit[] _hits = new RaycastHit[64];
        readonly RaycastHit[] _occlusion = new RaycastHit[64];
        IInteractable _held;
        string _heldPrompt;
        float _elapsed;

        void Awake()
        {
            _role = GetComponent<RoleController>();
            if (Cam == null) Cam = GetComponentInChildren<Camera>();
        }

        void CancelHold()
        {
            _held = null; _elapsed = 0f; HoldProgress = 0f; IsHolding = false;
        }

        void OnDisable() { CancelHold(); CurrentPrompt = null; }

        void Update()
        {
            CurrentPrompt = null; CurrentActionLabel = null;
            if (Cam == null || _role == null || Cursor.lockState != CursorLockMode.Locked || ParkShift.UI.HmiPanel.IsOpen)
            { CancelHold(); return; }
            var ray = new Ray(Cam.transform.position, Cam.transform.forward);
            var best = FindTarget(ray, false) ?? FindTarget(ray, true);
            string prompt = best?.GetPrompt(_role);
            if (string.IsNullOrEmpty(prompt)) { CancelHold(); return; }
            var panel = best as PanelInteractable;
            float duration = panel != null ? panel.HoldSeconds(_role) : 0f;
            CurrentActionLabel = prompt;
            CurrentPrompt = (duration > 0f ? "[HOLD E] " : "[E] ") + prompt;
            // Fresh press after cancellation/completion: never chain repairs.
            if (Input.GetKeyDown(KeyCode.E))
            {
                CancelHold();
                if (duration <= 0f) { best.Interact(_role); return; }
                _held = best; _heldPrompt = prompt;
            }
            if (!Input.GetKey(KeyCode.E) || !ReferenceEquals(_held, best) || _heldPrompt != prompt)
            { CancelHold(); return; }
            IsHolding = true;
            _elapsed += Time.deltaTime;
            HoldProgress = Mathf.Clamp01(_elapsed / Mathf.Max(0.01f, duration));
            if (HoldProgress >= 1f) { best.Interact(_role); CancelHold(); }
        }

        IInteractable FindTarget(Ray ray, bool assist)
        {
            int count = assist
                ? Physics.SphereCastNonAlloc(ray, 0.12f, _hits, Range, ~0, QueryTriggerInteraction.Collide)
                : Physics.RaycastNonAlloc(ray, _hits, Range, ~0, QueryTriggerInteraction.Collide);
            if (count == _hits.Length) return null;
            IInteractable best = null;
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider.transform.IsChildOf(transform)) continue;
                var candidate = hit.collider.GetComponentInParent<IInteractable>();
                if (candidate == null || string.IsNullOrEmpty(candidate.GetPrompt(_role)) || hit.distance >= nearest) continue;
                Vector3 point = assist ? hit.point : ray.GetPoint(hit.distance);
                if (!Visible(candidate, point)) continue;
                best = candidate; nearest = hit.distance;
            }
            return best;
        }

        bool Visible(IInteractable target, Vector3 point)
        {
            Vector3 delta = point - Cam.transform.position;
            int count = Physics.RaycastNonAlloc(Cam.transform.position, delta.normalized, _occlusion,
                delta.magnitude + 0.015f, ~0, QueryTriggerInteraction.Ignore);
            if (count == _occlusion.Length) return false;
            for (int i = 0; i < count; i++)
            {
                var collider = _occlusion[i].collider;
                if (collider.transform.IsChildOf(transform)) continue;
                if (!ReferenceEquals(collider.GetComponentInParent<IInteractable>(), target)) return false;
            }
            return true;
        }
    }
}
