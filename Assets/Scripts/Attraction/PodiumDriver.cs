using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Platform podium: depresses its button when the attendant acts —
    /// Load podium on each guest boarded, Restraint podium when restraints are verified.</summary>
    public class PodiumDriver : MonoBehaviour
    {
        public enum Mode { Load, Restraint }
        public Mode PodiumMode = Mode.Load;
        public Transform Button;
        public PressPulse Press = new PressPulse();
        int _lastRiders = -1;
        bool _lastVerified;

        void Start() { Press.T = Button; }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            var t = m.Sim.PlatformTrain();
            if (t != null)
            {
                if (PodiumMode == Mode.Load)
                {
                    if (_lastRiders < 0) _lastRiders = t.Riders;
                    if (t.Riders > _lastRiders) Press.Trigger();
                    _lastRiders = t.Riders;
                }
                else
                {
                    if (t.RestraintsVerified && !_lastVerified) Press.Trigger();
                    _lastVerified = t.RestraintsVerified;
                }
            }
            else { _lastRiders = -1; _lastVerified = false; }
            Press.Tick(Time.deltaTime);
        }
    }
}
