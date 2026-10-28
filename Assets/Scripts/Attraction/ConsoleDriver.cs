using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Animates the booth console: the gate lever swings between rest (gates closed) and
    /// pulled (gates open); the dispatch button visibly depresses into the panel on every dispatch.</summary>
    public class ConsoleDriver : MonoBehaviour
    {
        public Transform Lever, Button;
        [Tooltip("Gate lever 'open' pose, relative to its imported rest pose (pitch about local X).")]
        public Vector3 LeverPulledEuler = new Vector3(-56, 0, 0);
        public float LeverSpeed = 5f;
        [Tooltip("How far the dispatch button travels into the panel on press (m, along its local -Z).")]
        public float ButtonPressDepth = 0.035f;
        [Tooltip("Extra flatten on press so it reads even if the local axis is off (0..0.6).")]
        [Range(0f, 0.6f)] public float ButtonSquash = 0.35f;
        public float ButtonReturnSpeed = 4f;
        Hinge _lever;
        Vector3 _btnRestPos, _btnRestScale;
        bool _btnInit;
        float _pulse;
        int _lastDispatches;
        Renderer _btnRenderer;
        Color _btnBaseColor = Color.white;
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_Color");

        void Start()
        {
            _lever = new Hinge(Lever, LeverPulledEuler);
            _lever.Overshoot = 1.2f;   // gate lever lands with a satisfying spring
            if (Button)
            {
                _btnRestPos = Button.localPosition; _btnRestScale = Button.localScale; _btnInit = true;
                _btnRenderer = Button.GetComponentInChildren<Renderer>();
                if (_btnRenderer != null && _btnRenderer.sharedMaterial != null
                    && _btnRenderer.sharedMaterial.HasProperty(ColorId))
                    _btnBaseColor = _btnRenderer.sharedMaterial.color;
                _mpb = new MaterialPropertyBlock();
            }
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            _lever.Tick(m.Sim.Guests.GatesOpen, Time.deltaTime, LeverSpeed);

            int d = m.Sim.Score.Dispatches;
            if (d != _lastDispatches) { _pulse = 1f; _lastDispatches = d; }
            if (Button && _btnInit)
            {
                _pulse = Mathf.MoveTowards(_pulse, 0f, ButtonReturnSpeed * Time.deltaTime);
                float k = Mathf.Sin(Mathf.Clamp01(_pulse) * Mathf.PI);   // 0 -> 1 -> 0 over the press
                Button.localPosition = _btnRestPos - new Vector3(0f, 0f, k * ButtonPressDepth); // depress into the panel
                Button.localScale = Vector3.Scale(_btnRestScale, new Vector3(1f, 1f, 1f - k * ButtonSquash));
                // Light the button up while it's pressed (property block — no material instancing).
                if (_btnRenderer != null && _mpb != null)
                {
                    _btnRenderer.GetPropertyBlock(_mpb);
                    _mpb.SetColor(ColorId, Color.Lerp(_btnBaseColor, Color.white, k * 0.85f));
                    _btnRenderer.SetPropertyBlock(_mpb);
                }
            }
        }
    }
}
