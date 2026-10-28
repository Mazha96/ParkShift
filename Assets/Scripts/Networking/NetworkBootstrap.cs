using UnityEngine;

namespace ParkShift.Networking
{
    public enum NetMode { Solo, Host, Client }

    /// <summary>Explicit LAN entry point. Missing networking never impersonates a connected session.</summary>
    public class NetworkBootstrap : MonoBehaviour
    {
        public static NetMode Mode = NetMode.Solo;
        public static string Address = "localhost";
        public const ushort Port = 7770;
        public static string ConnectionStatus { get; private set; } = "Solo";

        public static void LeaveSession()
        {
#if FISHNET
            var network = FishNet.InstanceFinder.NetworkManager;
            if (network != null)
            {
                network.ClientManager.StopConnection();
                network.ServerManager.StopConnection(true);
            }
#endif
            Mode = NetMode.Solo;
            ConnectionStatus = "Solo";
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
        }

        void OnGUI()
        {
            if (Mode == NetMode.Solo) return;
            bool noPlayer = Player.RoleController.Local == null;
            if (!noPlayer) return;
            GUILayout.BeginArea(new Rect(16, 16, 390, noPlayer ? 130 : 58), GUI.skin.box);
            GUILayout.Label(ConnectionStatus);
            if (noPlayer)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                GUILayout.Label("LAN: UDP port 7770 • maximum four crew");
                if (GUILayout.Button("RETURN TO MENU")) LeaveSession();
            }
            GUILayout.EndArea();
        }

        void Awake()
        {
            var manager = FindObjectOfType<Attraction.AttractionManager>();
            if (manager != null) manager.IsAuthoritative = Mode == NetMode.Solo;
        }

        void Start()
        {
            var manager = Attraction.AttractionManager.Instance;
            if (Mode == NetMode.Solo) { manager.IsAuthoritative = true; ConnectionStatus = "Solo"; return; }
#if FISHNET
            var network = FishNet.InstanceFinder.NetworkManager;
            if (network == null)
            {
                ConnectionStatus = "LAN scene setup missing. Rebuild the prototype scene.";
                Debug.LogError(ConnectionStatus);
                return;
            }
            network.TransportManager.Transport.SetPort(Port);
            network.TransportManager.Transport.SetMaximumClients(4);
            network.ClientManager.OnClientConnectionState += OnClientState;
            if (Mode == NetMode.Host)
            {
                ConnectionStatus = "Starting four-player LAN host...";
                network.ServerManager.StartConnection();
                network.ClientManager.StartConnection("localhost");
            }
            else
            {
                ConnectionStatus = "Connecting to " + Address + ":" + Port;
                network.ClientManager.StartConnection(Address);
            }
#else
            ConnectionStatus = "FishNet package unavailable. LAN is disabled.";
            Debug.LogError(ConnectionStatus);
#endif
        }

#if FISHNET
        void OnClientState(FishNet.Transporting.ClientConnectionStateArgs args)
        {
            ConnectionStatus = args.ConnectionState == FishNet.Transporting.LocalConnectionState.Started
                ? (Mode == NetMode.Host ? "LAN host • up to four crew" : "Connected to " + Address)
                : "LAN connection: " + args.ConnectionState;
        }
        void OnDestroy()
        {
            var network = FishNet.InstanceFinder.NetworkManager;
            if (network != null) network.ClientManager.OnClientConnectionState -= OnClientState;
        }
#endif
    }
}


