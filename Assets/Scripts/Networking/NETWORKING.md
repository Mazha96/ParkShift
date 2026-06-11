# Park Shift — Networking Notes

## Architecture (decided day one)
- **Host-authoritative listen server** using **FishNet** (in `Packages/manifest.json` via OpenUPM).
- `AttractionSim` (pure C#) runs ONLY on the server/host. It is the single source of truth.
- All player verbs funnel through `AttractionManager.Submit(OpVerb)`:
  - Solo/Host: executes directly.
  - Client: `AttractionNetSync` routes the verb as a `ServerRpc`; the server validates by
    running the exact same `Execute()` path (no duplicated rules).
- State replication: 10 Hz coarse snapshot via SyncVars (state, satisfaction, queue,
  train block/progress, headline fault). Primitive visuals tolerate this easily.

## The FISHNET define
`Assets/Scripts/ParkShift.Game.asmdef` declares a `versionDefines` entry: when the
`com.firstgeargames.fishnet` package resolves, Unity sets `FISHNET` automatically and the
real networking code compiles in. Without the package the project still compiles and
Host/Join fall back to a local session. Nothing to configure by hand.

## Scene setup for real MP testing (5 minutes)
1. Let the FishNet package import (manifest already requests it).
2. In `PrototypeAttraction`, add FishNet's **NetworkManager** (Tugboat transport, default port).
3. Add `AttractionNetSync` to a NetworkObject in the scene; register the player prefab
   (capsule rig + `NetworkedPlayer`) as the spawnable player.
4. Build a player build, run editor as Host, build as Join → localhost.

## Steam Relay plug-in point
Replace Tugboat with **FishySteamworks** transport + Facepunch/Heathen Steamworks lobby:
implement `ISteamRelay` (see `ISteamRelay.cs`), host/join lobbies, and FishNet rides the
Steam datagram relay — no NAT/port forwarding. Requires a Steam AppID; stubbed until then.

## Known scope cuts (intentional)
- No host migration (industry-standard cut; shift ends if host quits).
- Clients currently mirror coarse state; the client HUD reads the local sim in solo/host
  and would read `AttractionNetSync.Mirror*` in a dedicated client HUD pass.
