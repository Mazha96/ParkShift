using UnityEngine;
using ParkShift.Player;

namespace ParkShift.Interaction
{
    /// <summary>Camera raycast interaction. Single input (E), 3m range — honors the 5-input cap.
    /// All-hits scan (non-alloc): trigger-only helper volumes in front (e.g. look-label proxy
    /// colliders on decorative props) never mask an interactable behind them, while solid
    /// geometry still occludes it.</summary>
    public class PlayerInteractor : MonoBehaviour
    {
        public float Range = 3f;
        public Camera Cam;
        public string CurrentPrompt { get; private set; }

        RoleController _role;
        readonly RaycastHit[] _hits = new RaycastHit[24];   // cached — no per-frame allocation

        void Awake()
        {
            _role = GetComponent<RoleController>();
            if (Cam == null) Cam = GetComponentInChildren<Camera>();
        }

        void Update()
        {
            CurrentPrompt = null;
            if (Cam == null) return;
            var ray = new Ray(Cam.transform.position, Cam.transform.forward);
            // Interaction colliders are triggers (so they never shove the player). Force the ray to
            // hit triggers regardless of the project-wide "Queries Hit Triggers" physics setting.
            int n = Physics.RaycastNonAlloc(ray, _hits, Range, ~0, QueryTriggerInteraction.Collide);
            IInteractable best = null;
            float bestDist = float.MaxValue, blockerDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                var it = h.collider.GetComponentInParent<IInteractable>();
                if (it != null)
                {
                    if (h.distance < bestDist) { bestDist = h.distance; best = it; }
                }
                else if (!h.collider.isTrigger && h.distance < blockerDist)
                {
                    blockerDist = h.distance;   // walls and other solids occlude interactables
                }
            }
            if (best != null && bestDist <= blockerDist)
            {
                string p = best.GetPrompt(_role);
                if (!string.IsNullOrEmpty(p))
                {
                    CurrentPrompt = "[E] " + p;
                    if (Input.GetKeyDown(KeyCode.E)) best.Interact(_role);
                }
            }
        }
    }
}
