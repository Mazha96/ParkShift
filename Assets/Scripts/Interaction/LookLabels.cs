using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Interaction
{
    /// <summary>
    /// Look-to-reveal nameplate. Lives on the prop ROOT (so a hit on any child collider
    /// resolves to it via GetComponentInParent) and owns the world-space TextMesh built by
    /// SceneBuilder.Label(). Hidden by default; the player's <see cref="LabelLookSystem"/>
    /// calls <see cref="SetLooked"/> and the label fades in (~0.12s), billboards to the
    /// camera every frame while visible, and fades back out (~0.25s) when looked away.
    /// Zero per-frame work while fully hidden.
    /// </summary>
    public class LookLabel : MonoBehaviour
    {
        [Tooltip("The nameplate TextMesh (child object, assigned by SceneBuilder.Label).")]
        public TextMesh Text;
        [Tooltip("Variant flag: skip look-to-reveal and keep this label always on (rare; decor only).")]
        public bool AlwaysVisible = false;
        public float FadeInTime = 0.12f;
        public float FadeOutTime = 0.25f;

        /// <summary>Camera transform labels billboard toward. Set by LabelLookSystem.</summary>
        public static Transform BillboardTarget;

        float _alpha;
        bool _looked;
        bool _init;
        Color _baseColor = Color.white;

        /// <summary>Called by LabelLookSystem when this prop gains/loses the player's gaze.</summary>
        public void SetLooked(bool looked) { _looked = looked; }

        void Update()
        {
            if (Text == null) return;
            if (!_init) { _baseColor = Text.color; _init = true; }

            float target = (_looked || AlwaysVisible) ? 1f : 0f;
            if (_alpha <= 0f && target <= 0f) return;   // fully hidden and staying hidden — no work

            float rate = target > _alpha ? 1f / Mathf.Max(FadeInTime, 0.01f)
                                         : 1f / Mathf.Max(FadeOutTime, 0.01f);
            _alpha = Mathf.MoveTowards(_alpha, target, rate * Time.deltaTime);

            var go = Text.gameObject;
            bool show = _alpha > 0.001f;
            if (go.activeSelf != show) go.SetActive(show);
            if (!show) return;

            Color c = _baseColor;
            c.a = _baseColor.a * _alpha;
            Text.color = c;

            // Billboard: face away from the camera so the glyphs read left-to-right.
            var camT = BillboardTarget;
            if (camT != null)
            {
                Vector3 fwd = Text.transform.position - camT.position;
                if (fwd.sqrMagnitude > 1e-6f)
                    Text.transform.rotation = Quaternion.LookRotation(fwd);
            }
        }
    }

    /// <summary>
    /// Interactable affordance glow. Lives on the same GameObject as the PanelInteractable
    /// (the interactor part), drives _EmissionColor on its child renderers through a shared
    /// MaterialPropertyBlock (NeonPulse pattern — no material instancing, no per-frame
    /// allocation). Idles with a very subtle emission "breath" so interactables read as
    /// interactable at a glance, and brightens smoothly while the player's gaze rests on it.
    /// </summary>
    public class InteractableGlow : MonoBehaviour
    {
        [Tooltip("HDR highlight tint (kept 0..1-ish; intensities scale it).")]
        public Color GlowColor = new Color(0.55f, 0.95f, 1f);
        [Tooltip("Idle emission level — the always-on 'I am interactable' breathing.")]
        public float IdleIntensity = 0.16f;
        [Range(0f, 1f)] public float IdleBreathAmount = 0.5f;
        public float BreathSpeed = 1.4f;
        [Tooltip("Emission level while the player is looking at this interactable.")]
        public float LookIntensity = 1.1f;
        [Range(0f, 1f)] public float LookPulseAmount = 0.15f;
        [Tooltip("How fast the highlight eases in/out when gaze arrives/leaves.")]
        public float FadeSpeed = 6f;

        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        Juice.FaultStrobe _strobe;   // alarm strobes own the emission channel while flashing
        float _strobeRecheck;        // FaultStrobe is attached at runtime — re-scan at ~2 Hz until found
        float _boost;   // 0 = idle, 1 = looked at
        float _phase;
        bool _looked;
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
        // Property-block emission only renders when the material's _EMISSION keyword is on.
        // Enable it once per shared material; the material's own emission color stays black,
        // so other users of the same material are visually unaffected.
        static readonly HashSet<Material> EmissionEnabled = new HashSet<Material>();

        /// <summary>Called by LabelLookSystem when this interactable gains/loses the gaze.</summary>
        public void SetLooked(bool looked) { _looked = looked; }

        void Awake()
        {
            _phase = Random.value * 6.28f;   // stagger so panels don't breathe in lockstep
            var all = GetComponentsInChildren<Renderer>(true);
            var list = new List<Renderer>(all.Length);
            foreach (var r in all)
            {
                if (r.GetComponent<TextMesh>() != null) continue;   // never tint the nameplate
                list.Add(r);
            }
            _renderers = list.ToArray();
            _mpb = new MaterialPropertyBlock();
            _strobe = GetComponentInChildren<Juice.FaultStrobe>(true);
            foreach (var r in _renderers)
            {
                var mats = r.sharedMaterials;
                foreach (var m in mats)
                {
                    if (m == null || !m.HasProperty(EmissionId) || EmissionEnabled.Contains(m)) continue;
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None; // realtime only, never baked
                    EmissionEnabled.Add(m);
                }
            }
        }

        void Update()
        {
            if (_renderers == null || _renderers.Length == 0) return;
            // FaultPropController attaches FaultStrobe to fault props at runtime; while one is
            // flashing on this prop, let the alarm own the emission channel instead of fighting it.
            if (_strobe == null)
            {
                _strobeRecheck -= Time.deltaTime;
                if (_strobeRecheck <= 0f)
                {
                    _strobeRecheck = 0.5f;
                    _strobe = GetComponentInChildren<Juice.FaultStrobe>(true);
                }
            }
            if (_strobe != null && _strobe.isActiveAndEnabled) return;
            _boost = Mathf.MoveTowards(_boost, _looked ? 1f : 0f, FadeSpeed * Time.deltaTime);
            float idle = IdleIntensity * (1f + IdleBreathAmount * Mathf.Sin(Time.time * BreathSpeed + _phase));
            float look = LookIntensity * (1f + LookPulseAmount * Mathf.Sin(Time.time * 6f));
            Color c = GlowColor * Mathf.Lerp(idle, look, _boost);
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionId, c);
                r.SetPropertyBlock(_mpb);
            }
        }
    }

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
