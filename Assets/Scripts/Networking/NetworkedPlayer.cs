#if FISHNET
using FishNet.Object;
using ParkShift.Player;
using UnityEngine;

namespace ParkShift.Networking
{
    /// <summary>Enables input/camera only for the owning client; remote players render as bodies.</summary>
    public class NetworkedPlayer : NetworkBehaviour
    {
        public override void OnStartClient()
        {
            base.OnStartClient();
            bool owner = IsOwner;
            var fpc = GetComponent<FirstPersonController>();
            if (fpc != null) fpc.InputEnabled = owner;
            var cam = GetComponentInChildren<Camera>(true);
            if (cam != null) cam.gameObject.SetActive(owner);
            var listener = GetComponentInChildren<AudioListener>(true);
            if (listener != null) listener.enabled = owner;
        }
    }
}
#endif
