# Park Chaos LAN implementation

Target: up to four PC players on one LAN, with an independent Solo mode. Online relay, lobbies, and host migration are not implemented.

## Scene generation

Once FishNet resolves, run the prototype scene builder or **ParkShift > Configure Four Player LAN** in Unity. `LanSceneSetup.ConfigureCurrentScene()` creates:

- A scene-local NetworkManager and Tugboat transport.
- A registered `CrewPlayer` prefab with owner-authoritative NetworkTransform, camera, controller, interactor, and visible remote capsule.
- Four separated spawn points and FishNet PlayerSpawner.
- A scene NetworkObject with AttractionNetSync.

Save the scene after running the standalone setup command. The prototype builder calls setup before its save. Generated prefabs live in `Assets/GeneratedLAN`. These assets must be generated in Unity before a build; editing source alone does not rewrite an existing scene.

## Session flow

Host opens UDP port 7770 and joins locally. Join uses the menu's host address. Tugboat allows four connections including the host. Only the server ticks the attraction. The owning player gets camera/input/HUD; remote players use interpolated transforms. Solo retains its local player and role coverage.

Connection status remains visible without a spawned player, with a return-to-menu action for failure/disconnect. LAN results offer Leave Session; this disconnects cleanly and returns to the menu. It is not a synchronized next-shift restart. Solo retains Next Shift.

## Authority and replication

All client actions route through ServerSubmitVerb. The simulation still enforces dispatch, block, restraint, and lockout rules. Manager actions and maintenance actions additionally require the server's accepted role. Roles remain freely selectable hats. Disconnect removes the role; a connected Technician enables major technician scenarios.

Attraction snapshots update at 10 Hz. Manager budget, revenue, care count, admissions pause, and cooldown replicate alongside ride state. Budget starts at 120; completed riders earn 5 each. Guest care costs 40, restores 18 mood, and has a 30-second cooldown. Admission pause retains the existing queue.

## Validation boundary

Unity import, FishNet code generation, scene generation and the Windows development build pass. All 21 Unity EditMode regression tests pass. Two local executable instances completed a synchronized shift without runtime errors. A four-process localhost test with Operator/Manager/Technician roles and a 15-second late join also passed: all clients agreed on score 104, 48 riders, 8 dispatches, budget 320 and one guest-care purchase. Remote admissions pause/resume was observed. Each connected player had one camera and one audio listener. A fifth connection did not join the full crew.

The automated probe runs at 4x simulation speed and uses legal operator/helper commands. It proves real local UDP transport and state replication, not separate-machine firewall configuration, subjective gameplay feel, anti-cheat, or WAN connectivity. Human movement/interaction sessions on separate PCs and disconnect/rehost UX remain manual acceptance checks. Reproduce the automated test with `Tools/Playtest/run-lan.ps1 -Players 4 -LateJoinSeconds 15`.

## Scope and behavior

- No online matchmaking, relay, Steam integration, or host migration.
- Fixed four train/fault snapshot slots; adding RVs in LAN is capped at four to prevent unreplicated vehicles.
- Assistant countdown replicates at 10 Hz.
- Remote verbs return host processing/rejection feedback; HUD exposes the latest response for four seconds.
- Movement trusts each owner for LAN play; it is not an anti-cheat implementation.


