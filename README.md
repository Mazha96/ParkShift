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

## The 6 faults

| Fault | Trigger | Consequence | Fix |
|---|---|---|---|
| Restraint Not Verified | random | dispatch hold | inspect flagged seat on train, re-verify |
| Gate Sensor Fault | random | gates won't open, dispatch hold | cycle gates at lever |
| Block Occupied / Stacking | train holds >4 s | satisfaction drain | clear the station, dispatch |
| Dispatch Timeout | platform train idle too long | satisfaction drain | get the train out |
| Guest Dropped Item | random | dispatch hold | walk to item, pick up |
| Maintenance Reset Required | random (major) | **ride stops** | Tech role → LOTO lever → drive reset → release |
