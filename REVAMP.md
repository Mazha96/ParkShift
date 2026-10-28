# Park Chaos — cartoon co-op revamp

Agreed direction: Windows PC first, four-player LAN co-op before online lobbies,
with a complete solo role-switching path. Bright theme-park colors, ink outlines,
exaggerated props, physical comedy and escalating emergencies. Preserve the
operator / technician / manager attraction-running core.

## Physics and interactions — September 22 update

- Doors use controlled kinematic motion, stop for crew in their swept path, and
  gate the unload workflow on physical clearance. The unload doorway is taller
  and slides sideways beneath the canopy, clearing a standing player.
- Hinges preserve momentum when reversed and settle consistently across frame
  rates. Repeated button presses depress immediately without a release flicker.
- Closing boarding gates lowers the bars; hold E to tug/check them. Reopening
  gates invalidates verification. Returning riders stay restrained until the
  unload door finishes opening. The seat-fault marker follows the actual loaded RV.
- Small controls have modest aim assistance with solid-wall occlusion. Lap-bar
  check volumes keep their intended world size instead of inheriting floor-tile
  scaling. Hold E for inspections and repairs; release or look away to cancel.
  A fresh press is needed for the next action. Success sounds follow host acceptance.
- Incidents get 60–110 seconds according to their repair route. The director leaves
  an opening grace period and recovery time between shutdowns, and avoids generating
  a repair too late in the shift to finish. Missing the response deadline applies
  one score/mood penalty; the incident remains repairable. Emergency/stall mood
  drain is reduced while the crew is still within that response window; queue
  pressure remains, so the manager still has a job during repairs.
- HUD/HMI show the incident location, next step, response time, and hold progress.
  Collision recovery now exposes the required drive-reset control.

Response windows are authored for the current park layout and include travel and
repair time; they are not dynamically computed navigation estimates. Holds are
local input affordances; resulting commands and fault deadlines use the existing
host authority. All crew must use this updated build together.

## Implemented

- Built-in renderer cel-paint shader plus camera depth/normal outlines, including
  silhouettes and geometry creases. Warm cosmic sunset lighting; oversized plaza
  balloon bouquets appear when scenes are rebuilt. Shared scene-owned materials
  support dynamically spawned guests, trains and crew without camera-lifetime coupling.
- Comic menu and HUD, guest mood bar, role-specific next-step guidance and a LAN
  host-address field. Interaction input is suppressed while the pointer is unlocked.
- Accelerated movement, Shift sprint, jump buffering, surface footsteps, speed FOV,
  braking hiss and bounded train suspension nods. Train motion remains under the
  authoritative block simulation; suspension is visual, not a replacement ride physics engine.
- Manager starts with $120, earns $5 per returned rider, can pause admissions, and
  spends $40 for +18 guest satisfaction with a 30-second cooldown. Paused/full queues
  never accumulate phantom arrivals. Solo manager and technician get automatic booth
  coverage, including the complete separate-unload workflow.
- FishNet 4.7.3 pinned to commit `dad140e6f3016402602825d2ade2faadd8786397`.
  Scene generation configures Tugboat, four spawn points, a registered network crew
  prefab and attraction replication. Owners alone receive input, camera and HUD.
  Server stores role choices and validates restricted commands. Late-join snapshots
  include riders, exact score, downtime, manager resources and incident state.
- Failed connections have an explicit return-to-menu route. LAN results use
  **Leave session**; solo results retain **Next shift**. Online lobbies and coordinated
  in-session rematches are not implemented.

## Open and prepare

1. Activate the Unity Editor license through Unity Hub and open this project with
   Unity **6000.0.32f1**. Let Package Manager finish resolving the pinned FishNet Git package.
2. Run **ParkShift → Build All Scenes**. This rebuilds the generated scenes and creates
   `Assets/GeneratedLAN` with the crew prefab and registered spawn collection.
3. Open `Assets/Scenes/MainMenu.unity`, press Play, and choose Solo or Host.
4. To make a Windows build, run **ParkShift → Build Windows LAN Playtest**.
   Output is `Builds/Windows/ParkChaos.exe`.

## Controls

WASD move; mouse look; E controls / hold E inspections and repairs; Shift sprint; Space jump; Tab switch role;
H remote help; Esc release/relock cursor. Manager: 1 toggle admissions, 2 guest care.
Open the ride HMI by interacting with its screen, then use the mouse to click.

## LAN playtest

Use the same build on all machines. Host chooses **Host co-op**. Up to three other
players enter that machine's LAN IPv4 address in **Host address** and choose Join.
For two processes on one PC, use `localhost`. Allow the game's local-network access
through Windows Firewall if prompted; transport is UDP port 7770.

Verify these observable acceptance checks before calling multiplayer ready:

- One camera, audio listener and controllable body per client; all four bodies visible
  to their teammates; a fifth connection is rejected.
- Open/load/check/dispatch/unload actions agree on host and clients. Restraints, gates,
  block occupancy and lockout still prevent unsafe dispatch.
- Technician incidents occur when a technician is on the crew; manager resources,
  admissions and cooldown agree across clients. H calls help on a joining client.
- Join during a running ride and after a broken streak: seated riders, score, best
  streak, downtime and shift time match. End-of-shift results match.
- Disconnect a client, then reconnect. A failed join and a host departure provide
  recovery rather than an unresponsive screen. Leaving results returns to the menu.
- Run a solo shift as technician/manager: the helper completes repeated load/unload
  cycles while the player resolves major incidents.

## Physics update verification (September 22)

- 27 Unity regression tests passed, including live CharacterController doorway
  obstruction, spring reversal/rest-pose stability, small-target aiming through
  clear space versus solid walls, restraint safety, timed incident recovery and
  protected response-window mood pressure.
- Windows development build regenerated the scenes and passed compilation.
- Four localhost instances completed a shift, including a 15-second late join:
  all four agreed on results (64 riders, 10 dispatches), with one camera/listener
  each and zero runtime errors. Every client matched host incident deadlines.
- Standalone solo Manager completed with 67 riders, 10 dispatches, and zero runtime errors.
- Runtime geometry checks found all four lap-bar interaction volumes at their
  intended one-metre height.

These are automated local checks. Separate-PC latency and hands-on interaction
feel still need human playtesting. The controls use guided kinematic/spring motion;
this update does not replace the ride simulation with unconstrained rigidbodies.

## Initial overhaul verification (September 8)

21 engine-independent regression tests and a complete bot shift pass. The suite
includes manager spending/revenue/cooldowns, queue arrival debt, late-join snapshot
idempotence and a complete split-station automatic shift. Run it with:

```powershell
./Tools/CoreTestHarness/run.ps1
```

Core, runtime and Editor source compile separately. Unity import, FishNet IL
postprocessing, generated scene/prefab serialization and the Windows development
build have passed. All 21 tests also passed in Unity's EditMode runner.

Live executable verification:

- Two localhost processes: a complete shift, zero runtime errors, matching 449
  score, 64 riders and 8 dispatches.
- Four localhost processes, including Manager/Technician and a 15-second late join:
  zero runtime errors, one camera/listener each, matching 104 score, 48 riders,
  8 dispatches, $320 budget and one guest-care purchase. Admissions pause/resume
  and guest care were sent by the remote manager and observed by the host.
- A standalone solo Manager shift used the actual automatic operator, completed with zero runtime errors, and served 67 riders across 10 dispatches (score 739, one guest-care purchase).
- A fifth attempted connection could not join while the four-member crew was full.
- Cartoon shaders compiled and actual game frames were rendered and inspected.
  The overview image is a temporary developer camera angle, not a gameplay camera.
- Fixed the overlapping dispatch/HMI triggers, the booth wall blocking the ramp,
  and a tree obscuring the maintenance cabinet. The conservative scene audit still
  reports editor-staged train overlaps and track-envelope AABB candidates; those
  are not a claim that every geometry intersection has been eliminated.

Run the live test again with:

```powershell
./Tools/Playtest/run-lan.ps1 -Players 4 -LateJoinSeconds 15
```

The opt-in development probe uses 4x simulation time and legal simulation commands;
normal play is unaffected. Separate-PC LAN/firewall behavior, human movement and
interaction feel, and disconnect/rehost UX still need hands-on playtesting. Online
lobbies, Steam invitations, host migration and coordinated rematches are later scope.

