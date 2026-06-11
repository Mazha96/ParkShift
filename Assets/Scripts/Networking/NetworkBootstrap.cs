using UnityEngine;

namespace ParkShift.Networking
{
    public enum NetMode { Solo, Host, Client }

    /// <summary>
    /// Multiplayer entry point. Architecture decided day one:
    /// host-authoritative listen server over FishNet; AttractionSim runs only on the
    /// server; clients send verbs via ServerRpc and mirror replicated state.
    ///
    /// When the FishNet package is present, the FISHNET define (set via asmdef
    /// versionDefines) activates the real networking path. Without it, the same code
    /// compiles and Host/Client gracefully fall back to a local session so the slice
    /// is always runnable.
    /// </summary>
    public class NetworkBootstrap : MonoBehaviour
    {
        public static NetMode Mode = NetMode.Solo;

        void Start()
        {
            var mgr = Attraction.AttractionManager.Instance;

#if FISHNET
            var nm = FishNet.InstanceFinder.NetworkManager;
            if (nm == null)
            {
                Debug.LogWarning("[Net] No FishNet NetworkManager in scene — add the FishNet " +
                                 "NetworkManager prefab (Tugboat transport). Falling back to local.");
                mgr.IsAuthoritative = true;
                return;
            }
            switch (Mode)
            {
                case NetMode.Host:
                    nm.ServerManager.StartConnection();
                    nm.ClientManager.StartConnection();
                    mgr.IsAuthoritative = true;
                    Debug.Log("[Net] Hosting on Tugboat (default port 7770). " +
                              "Steam Relay: see Assets/Scripts/Networking/NETWORKING.md");
                    break;
                case NetMode.Client:
                    nm.ClientManager.StartConnection("localhost");
                    mgr.IsAuthoritative = false; // server owns the sim
                    Debug.Log("[Net] Joining localhost...");
                    break;
                default:
                    mgr.IsAuthoritative = true;
                    break;
            }
#else
            if (Mode != NetMode.Solo)
                Debug.LogWarning("[Net] FishNet package not installed yet — Host/Join run as a " +
                                 "local session. Install com.firstgeargames.fishnet (already in " +
                                 "manifest.json) and the FISHNET define activates automatically.");
            mgr.IsAuthoritative = true;
#endif
        }
    }
}
