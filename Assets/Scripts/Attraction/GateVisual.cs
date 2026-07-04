using UnityEngine;

namespace ParkShift.Attraction
{
    /// <summary>Slides gate cubes open/closed to mirror sim gate state.</summary>
    public class GateVisual : MonoBehaviour
    {
        public Transform[] GatePanels;
        public float SlideDistance = 1.2f;
        Vector3[] _closedPos;
        bool _lastOpen;
        bool _openInit;
        float _settleTimer = -1f;   // counts down to the "doors landed" puff after a toggle

        void Start()
        {
            _closedPos = new Vector3[GatePanels.Length];
            for (int i = 0; i < GatePanels.Length; i++) _closedPos[i] = GatePanels[i].localPosition;
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            bool open = m.Sim.Guests.GatesOpen;
            if (!_openInit) { _lastOpen = open; _openInit = true; }
            if (open != _lastOpen) { _settleTimer = 0.4f; _lastOpen = open; }
            for (int i = 0; i < GatePanels.Length; i++)
            {
                if (GatePanels[i] == null) continue;
                Vector3 target = _closedPos[i] + (open ? Vector3.up * SlideDistance : Vector3.zero);
                GatePanels[i].localPosition = Vector3.Lerp(GatePanels[i].localPosition, target, Time.deltaTime * 6f);
            }
            // Fallback-gate juice: once the slide has mostly finished, click + puff at each panel.
            if (_settleTimer > 0f)
            {
                _settleTimer -= Time.deltaTime;
                if (_settleTimer <= 0f && GatePanels.Length > 0 && GatePanels[0] != null)
                {
                    Juice.AudioDirector.GateClick(GatePanels[0].position);
                    for (int i = 0; i < GatePanels.Length; i++)
                        if (GatePanels[i] != null)
                            Juice.JuiceFx.DustPuff(GatePanels[i].position + Vector3.down * 0.3f);
                }
            }
        }
    }
}
