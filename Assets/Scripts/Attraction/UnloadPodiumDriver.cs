using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>Unload control podium feedback: depresses each button on its action's rising edge
    /// (Open door, Close door, Release restraints), reading the sim's unload-workflow flags.</summary>
    public class UnloadPodiumDriver : MonoBehaviour
    {
        public Transform OpenButton, CloseButton, ReleaseButton;
        public PressPulse OpenPress = new PressPulse();
        public PressPulse ClosePress = new PressPulse();
        public PressPulse ReleasePress = new PressPulse();
        bool _door, _released;

        void Start() { OpenPress.T = OpenButton; ClosePress.T = CloseButton; ReleasePress.T = ReleaseButton; }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null) return;
            var s = m.Sim;
            if (s.UnloadDoorOpen && !_door) OpenPress.Trigger();
            if (!s.UnloadDoorOpen && _door) ClosePress.Trigger();
            _door = s.UnloadDoorOpen;
            if (s.RestraintsReleased && !_released) ReleasePress.Trigger();
            _released = s.RestraintsReleased;
            OpenPress.Tick(Time.deltaTime);
            ClosePress.Tick(Time.deltaTime);
            ReleasePress.Tick(Time.deltaTime);
        }
    }
}
