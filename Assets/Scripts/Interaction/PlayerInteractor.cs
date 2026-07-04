using UnityEngine;
using ParkShift.Player;

namespace ParkShift.Interaction
{
    /// <summary>Camera raycast interaction. Single input (E), 3m range — honors the 5-input cap.</summary>
    public class PlayerInteractor : MonoBehaviour
    {
        public float Range = 3f;
        public Camera Cam;
        public string CurrentPrompt { get; private set; }

        RoleController _role;

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
            if (Physics.Raycast(ray, out var hit, Range, ~0, QueryTriggerInteraction.Collide))
            {
                var it = hit.collider.GetComponentInParent<IInteractable>();
                if (it != null)
                {
                    string p = it.GetPrompt(_role);
                    if (!string.IsNullOrEmpty(p))
                    {
                        CurrentPrompt = "[E] " + p;
                        if (Input.GetKeyDown(KeyCode.E)) it.Interact(_role);
                    }
                }
            }
        }
    }
}
