namespace ParkShift.Networking
{
    /// <summary>
    /// Abstraction seam for Steam relay transport. The slice ships with FishNet's
    /// Tugboat (LAN/localhost). For Steam release, implement this over FishySteamworks
    /// (FishNet community transport wrapping Steam Sockets/Relay) — no game code changes
    /// needed because all verbs already route through AttractionNetSync.
    /// </summary>
    public interface ISteamRelay
    {
        bool IsAvailable { get; }
        void HostLobby(int maxPlayers);
        void JoinLobby(ulong lobbyId);
        void Shutdown();
    }

    /// <summary>Stub used until Steamworks credentials/SDK are wired up.</summary>
    public class SteamRelayStub : ISteamRelay
    {
        public bool IsAvailable => false;
        public void HostLobby(int maxPlayers) { }
        public void JoinLobby(ulong lobbyId) { }
        public void Shutdown() { }
    }
}
