using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Interaction
{
    /// <summary>
    /// Player-side gaze detector for nameplates and interactable highlights. Mirrors
    /// PlayerInteractor's camera ray but reaches farther (~6m) and follows the precise ray
    /// with a small-radius spherecast so labels are forgiving to aim at. Non-alloc physics
    /// queries against a cached buffer — no per-frame garbage. Activates at most one
    /// <see cref="LookLabel"/> and one <see cref="InteractableGlow"/> at a time.
    /// </summary>
    public class LabelLookSystem : MonoBehaviour
    {
        public float Range = 6f;
        [Tooltip("Forgiveness radius for the fallback spherecast when the thin ray misses.")]
        public float AssistRadius = 0.22f;
        public Camera Cam;

        readonly RaycastHit[] _hits = new RaycastHit[32];
        LookLabel _label;
        InteractableGlow _glow;

        void Awake()
        {
            if (Cam == null) Cam = GetComponentInChildren<Camera>();
            if (Cam != null) LookLabel.BillboardTarget = Cam.transform;
        }

        void Update()
        {
            LookLabel bestLabel = null;
            InteractableGlow bestGlow = null;

            if (Cam != null)
            {
                if (LookLabel.BillboardTarget == null) LookLabel.BillboardTarget = Cam.transform;
                var ray = new Ray(Cam.transform.position, Cam.transform.forward);

                // Precise pass: single closest hit, so solid geometry naturally occludes.
                if (Physics.Raycast(ray, out var hit, Range, ~0, QueryTriggerInteraction.Collide))
                {
                    bestLabel = hit.collider.GetComponentInParent<LookLabel>();
                    bestGlow = hit.collider.GetComponentInParent<InteractableGlow>();
                }

                // Forgiving pass: the thin ray found nothing labeled — sweep a small sphere and
                // take the nearest labeled hit that isn't hidden behind nearer solid geometry.
                if (bestLabel == null && bestGlow == null)
                {
                    int n = Physics.SphereCastNonAlloc(ray, AssistRadius, _hits, Range, ~0, QueryTriggerInteraction.Collide);
                    float labelDist = float.MaxValue, glowDist = float.MaxValue, blockerDist = float.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        var h = _hits[i];
                        var ll = h.collider.GetComponentInParent<LookLabel>();
                        var g = h.collider.GetComponentInParent<InteractableGlow>();
                        if (ll == null && g == null)
                        {
                            if (!h.collider.isTrigger && h.distance < blockerDist) blockerDist = h.distance;
                            continue;
                        }
                        if (ll != null && h.distance < labelDist) { labelDist = h.distance; bestLabel = ll; }
                        if (g != null && h.distance < glowDist) { glowDist = h.distance; bestGlow = g; }
                    }
                    if (labelDist > blockerDist + 0.3f) bestLabel = null;   // wall in the way
                    if (glowDist > blockerDist + 0.3f) bestGlow = null;
                }
            }

            if (bestLabel != _label)
            {
                if (_label != null) _label.SetLooked(false);
                _label = bestLabel;
                if (_label != null) _label.SetLooked(true);
            }
            if (bestGlow != _glow)
            {
                if (_glow != null) _glow.SetLooked(false);
                _glow = bestGlow;
                if (_glow != null) _glow.SetLooked(true);
            }
        }
    }
}
