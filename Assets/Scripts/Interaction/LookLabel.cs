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
}
