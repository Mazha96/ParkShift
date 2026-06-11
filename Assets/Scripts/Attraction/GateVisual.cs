using UnityEngine;

namespace ParkShift.Attraction
{
    /// <summary>Slides gate cubes open/closed to mirror sim gate state.</summary>
    public class GateVisual : MonoBehaviour
    {
        public Transform[] GatePanels;
        public float SlideDistance = 1.2f;
        Vector3[] _closedPos;

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
            for (int i = 0; i < GatePanels.Length; i++)
            {
                Vector3 target = _closedPos[i] + (open ? Vector3.up * SlideDistance : Vector3.zero);
                GatePanels[i].localPosition = Vector3.Lerp(GatePanels[i].localPosition, target, Time.deltaTime * 6f);
            }
        }
    }
}
