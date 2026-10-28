using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Swings both doors of one station gate unit with the sim's gate state.</summary>
    public class GateUnitDriver : MonoBehaviour
    {
        public Transform DoorL, DoorR;
        public Vector3 OpenEulerL = new Vector3(0, 0, -100);
        public Vector3 OpenEulerR = new Vector3(0, 0, 100);
        Hinge _l, _r;
        bool _synced, _lastOpen, _moving;

        void Start()
        {
            _l = new Hinge(DoorL, OpenEulerL);
            _r = new Hinge(DoorR, OpenEulerR);
            _l.Overshoot = 1f;   // doors swing with a springy settle instead of a robotic stop
            _r.Overshoot = 1f;
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null) return;
            bool open = m.Sim.Guests.GatesOpen;
            if (!_synced)
            {
                _l.Snap(open); _r.Snap(open);
                _lastOpen = open;
                _synced = true;
                return;
            }
            if (open != _lastOpen) { _moving = true; _lastOpen = open; }
            _l.Tick(open, Time.deltaTime);
            _r.Tick(open, Time.deltaTime);

            // Completion edges: a click + dust puff when the doors land fully open or closed.
            if (_moving && _l.IsSettled && _r.IsSettled)
            {
                _moving = false;
                OnDoorsSettled();
            }
        }

        void OnDoorsSettled()
        {
            Vector3 p = DoorL != null ? DoorL.position : transform.position;
            Juice.JuiceFx.DustPuff(p + Vector3.down * 0.2f);
            Juice.AudioDirector.GateClick(p);
        }
    }
}
