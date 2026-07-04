# PARK SHIFT — "Cosmic Conveyor" Vertical Slice

A first-person co-op chaos/job-sim about **operating** a theme park attraction:
open the shift, load guests, verify restraints, dispatch trains, keep two trains
moving through block zones, fight faults, and survive a 3-minute shift.

Everything is fictional. No real parks, rides, or procedures.

---

## Open & Run (5 minutes)

1. **Open the project** in Unity 6 (built against `6000.0.32f1`; any 6000.x should work).
   On first open, the Package Manager resolves **FishNet** from OpenUPM
   (already declared in `Packages/manifest.json`). The project compiles
   **with or without** FishNet — networking code is guarded by a `FISHNET`
   define that activates automatically when the package resolves.
2. Menu bar → **ParkShift → Build All Scenes**. This constructs
   `MainMenu.unity` and `PrototypeAttraction.unity` from primitives and adds
   them to Build Settings. (Scenes are generated artifacts;
   `Assets/Scripts/Editor/SceneBuilder.cs` is the source of truth — rebuild anytime.)
3. Open `Assets/Scenes/PrototypeAttraction.unity` and press **Play**
   (or play `MainMenu.unity` for the Solo/Host/Join flow).

## Controls

| Input | Action |
|---|---|
| WASD + mouse | Move / look |
| **E** | Interact with whatever you're looking at |
| **Tab** | Switch role: Operator → Technician → Manager |
| **H** | Call remote tech (auto-fixes a fault, score penalty) |
| Esc | Toggle mouse lock |

## How to play a shift

1. In the booth: **SHIFT START** panel.
2. **GATES** lever → guests flood the platform. *Wave aboard* speeds loading.
3. Close gates → **RESTRAINT CHECK** → **DISPATCH**.
4. Keep the second train moving — a train parked in the station stacks the
   other one on the brake run (block hold fault).
5. When the HMI screams, read the fix hint. Minor faults are operator fixes;
   the **DRIVE CONTROLLER TRIP** needs Technician role (Tab), the **LOCKOUT
   lever**, then **DRIVE RESET** in the maintenance bay, then release lockout.
6. Survive 3 minutes. Results screen grades you S–F. **NEXT SHIFT** to retry.

## Testing

**In Unity:** Window → General → Test Runner → EditMode → Run All
(9 tests in `Assets/Tests/EditMode/CoreSystemTests.cs`).

**Without Unity (CI/headless):** the simulation core is pure C# with zero
engine references, so it runs anywhere:

```bash
# from repo root, with mono installed
mkdir -p /tmp/coretest && cd /tmp/coretest
# Program.cs / Playtest.cs shipped under Tools/CoreTestHarness in this repo
mcs -langversion:7.2 -out:coretest.exe \
    Tools/CoreTestHarness/*.cs Assets/Scripts/Core/*.cs
mono coretest.exe   # runs all 9 tests + a scripted 180s bot playtest
```

## Architecture (the 60-second tour)

- `Assets/Scripts/Core/` — **engine-agnostic simulation** (`noEngineReferences: true`):
  state machine, block zones, two-train sim, guest queue, 6-fault catalog,
  scoring, telemetry. This is the single source of truth and what the tests cover.
- `Assets/Scripts/Attraction/AttractionManager.cs` — Unity bridge. Ticks the sim
  when authoritative (solo/host). Every player verb funnels through
  `Submit(OpVerb)` so multiplayer can intercept.
- `Assets/Scripts/Networking/` — FishNet host-authoritative scaffold:
  clients send verbs as ServerRpcs, server validates through the *same*
  `Execute()` path as solo, state mirrors via 10 Hz SyncVar snapshots.
  Steam Relay plug-in point documented in `NETWORKING.md`.
- UI is built entirely at runtime (`HUDController`, `MainMenuUI`,
  `ResultsScreen`) — nothing scene-serialized to maintain.
- Telemetry writes to `Application.persistentDataPath/parkshift_telemetry.txt`
  on scene exit and mirrors to the Console live.

## The 9 faults

| Fault | Trigger | Consequence | Fix |
|---|---|---|---|
| Restraint Not Verified | random | dispatch hold | inspect flagged seat on train, re-verify |
| Gate Sensor Fault | random | gates won't open, dispatch hold | cycle gates at lever |
| Block Occupied / Stacking | train holds >4 s | satisfaction drain | clear the station, dispatch |
| Dispatch Timeout | platform train idle too long | satisfaction drain | get the train out |
| Guest Dropped Item | random | dispatch hold | walk to item, pick up |
| Maintenance Reset Required | random (major) | **ride stops** | Tech role → LOTO lever → drive reset → release |
| Track Intrusion | tech mode (major) | **e-stop** | Tech: walk to the track, escort the guest off |
| Train Collision | tech mode (major) | **ride stops** | Tech: inspect contact point → LOTO → drive reset → release |
| Train Valleyed | tech mode (major) | **ride stops** | Tech: LOTO → crank EVAC WINCH out on the course → release |

## Props

Station gates, coaster trains (with animated lap bars), the booth dispatch console
(lever + button), the drive cabinet (door swings open for maintenance, breaker drops on
LOTO), and queue stanchions are Blender-authored FBXs in `Assets/Models/`, placed by the
scene builder and animated by code drivers in `Assets/Scripts/Attraction/PropDrivers.cs`
that mirror sim state. Every prop falls back to the old primitive version if its FBX is
missing. If a hinge swings the wrong way after a re-export, flip the sign on the driver's
`*Euler` field in the scene (they're public).

## Guests: Dewey

Guests are instances of `Assets/Models/Dewey.fbx` (exported from the Dewey Blender file)
with randomized hoodie tints and an animator (Idle / Walk / Sit — clips authored in
Blender, looped on import by the scene builder). The track-intrusion incident also uses
Dewey. If the FBX is missing, the game falls back to capsule guests. Tunables live on
the `Queue` object's `GuestVisualManager`: `GuestScale`, `MoveSpeed`, `SitOffset`.

## Graphics

Materials are PBR-textured from `Assets/Textures/{set}_albedo.png` + `{set}_normal.png`
(sets: `concrete`, `asphalt`, `metal`, `paint`, `seat`, `wood`). The bundled textures are
procedurally generated; to upgrade, download any CC0 texture pack (e.g. from
**ambientcg.com** or **polyhaven.com**), rename the color map to `{set}_albedo.png` and
the normal map to `{set}_normal.png`, drop them in `Assets/Textures/`, and re-run
**ParkShift → Build All Scenes**. Bloom/ACES/vignette/AO post-processing activates
automatically once the `com.unity.postprocessing` package resolves (already in the manifest).

## Technician mode (solo)

Tab to **Technician** and an auto-operator covers the booth: it keeps loading,
verifying, and dispatching trains, and quietly clears operator-level faults.
In exchange, **major incidents start rolling** (the three tech-mode faults above,
plus drive trips) — one at a time, and they're yours. The HUD shows
"AUTO-OP COVERING THE BOOTH" while it's active; Tab back to Operator and the
booth is yours again (and major incidents stop rolling).
