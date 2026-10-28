# Park Chaos (repo: ParkShift)

1–4 player co-op theme park ride-ops game, Unity 6000.0.32f1, built-in RP (URP migration pending on branch `urp-migration`), FishNet 4.7.3 LAN. Players are the crew: run the ride, fix faults, handle guests.

**Current work:** a phased overhaul. Before starting any task, read [docs/OVERHAUL_PLAN.md](docs/OVERHAUL_PLAN.md) for the active phase, the decisions already made, and what's next. Log bugs found outside the active phase in [docs/BUGS.md](docs/BUGS.md) and keep going.

## Architecture rules

- **Core is the single source of truth.** `Assets/Scripts/Core` (`ParkShift.Core.asmdef`, `noEngineReferences: true`) holds all simulation:
  - state machine, block zones, trains, guests, faults, scoring, management, telemetry
  - plain C#, no `UnityEngine`
  - Game rules, fault state machines and block logic go here, with tests in `CoreTestSuite`.
  - Unity data reaches Core as plain values; for example, bake track geometry into float arrays on `SimConfig`.
- **Unity mirrors Core.** Presentation (`Attraction/`, `Vehicles/`, `Guests/`, `Juice/`, `UI/`) reads sim state and animates it. It never decides gameplay outcomes. Physics is presentation unless it raises an explicit verb or event into Core.
- **Verb funnel.** Every player action goes through `AttractionManager.Submit(OpVerb)` → `Execute`.
  - In multiplayer, clients send verbs as ServerRpcs. The host runs the same `Execute`. State mirrors back through `AttractionNetSync` at 10 Hz.
  - A new action means a new `OpVerb` routed through that funnel, never a direct sim call from UI.
- **Host authoritative.** Only the host ticks the sim (`AttractionManager.IsAuthoritative`). Clients hold a never-ticked mirror sim fed by `AttractionNetSync`, through Core `Mirror*` setters.
  - Any new synced state needs a NetSync field and a late-join snapshot.
  - Current fault/train slot cap is 4 each, hand-written; a restructure is planned in Phase 3.
- **Scenes are generated.** `Assets/Scripts/Editor/SceneBuilder.cs` is the source of truth for `MainMenu.unity` and `PrototypeAttraction.unity` (menu: ParkShift → Build All Scenes). Hand edits to those scenes are lost on rebuild.
  - Exception, once Phase 2 lands: the coaster track is a hand-editable prefab that SceneBuilder references.
  - Props load from FBX/prefab and fall back to primitives if missing.
- **Lockout/tagout is ride-level.** `FaultSystem.LockoutApplied` mirrors the LOTO lever.
  - While it's applied, every LOTO repair is allowed, including for faults raised after the lock went on. Releasing it blocks those repairs again.
  - `ForceResolve` bypasses preconditions. Use it only for the remote tech and client mirroring.
- **Condition alerts** (`FaultSystem.IsConditionFault`: BlockOccupiedStacking, DispatchTimeout) are raised and cleared by `AttractionSim` from live ride state. There is no repair verb for them.
- **No fault may softlock.** Every fault has a reachable fix path from any ride state. Prove it with a Core test, and with the multi-seed bot once that exists.

## Testing

Run all three before every commit:

1. **Core + bot playtest** (no Unity, needs the .NET 9 SDK): `powershell -File Tools/CoreTestHarness/run.ps1 [-Seeds N]`. It must print `ALL N TESTS PASSED` and `BOT SWEEP PASSED` twice (normal and 6x-fault stress sweeps of the shipped config). A failing seed prints its last telemetry lines.
2. **Unity EditMode** (headless; the editor must be closed):
   `"C:/Program Files/Unity/Hub/Editor/6000.0.32f1/Editor/Unity.exe" -batchmode -nographics -projectPath "<repo>" -runTests -testPlatform EditMode -testResults <out.xml> -logFile <out.log>`
   NUnit wrappers in `Assets/Tests/EditMode/CoreSystemTests.cs` call `CoreTestSuite` tests. Add a wrapper line for every new Core test.
3. **LAN:** `Tools/Playtest/run-lan.ps1 -Players N` (needs a Windows build: ParkShift → Build Windows LAN Playtest).

Core tests use the `Check(cond, what)` style inside `CoreTestSuite`. Register each new test in `RunAll`.

## Working rules (set by the owner)

- **One phase at a time.** At the end of a phase:
  1. Run all tests.
  2. Write a short summary of what changed and what to test by hand.
  3. Commit.
  4. Stop for review.
- **Small focused commits.** Ask before deleting any working feature.
- **Ambiguous or big design choices:** ask the owner.
- **Report honestly what you couldn't verify:** feel, visuals, audio mix and separate-PC LAN need human eyes.
- **Line endings:** `.gitattributes` normalizes to LF. Git's CRLF→LF warnings on Windows are expected.

## Background docs (read when touching that area)

- [README.md](README.md): controls, shift flow, the fault table, roles, manager economy.
- [REVAMP.md](REVAMP.md): cartoon/LAN revamp, physics/interaction rules for doors, hinges and hold-to-interact, LAN acceptance checks.
- [BLOCK_ZONES.md](BLOCK_ZONES.md): GALAXY EXPRESS block layout, N+1 rule, loop frame table. Caveat: the shipped config runs `SmoothMotion=false`, so held trains stop at the block exit, not at `BrakePoint01`.
- [Assets/Scripts/Networking/NETWORKING.md](Assets/Scripts/Networking/NETWORKING.md): FishNet setup, replication gaps.
- [OVERHAUL_NOTES.md](OVERHAUL_NOTES.md), [PROP_ANIMATIONS.md](PROP_ANIMATIONS.md): earlier agent waves and the juice layer, prop drivers.
