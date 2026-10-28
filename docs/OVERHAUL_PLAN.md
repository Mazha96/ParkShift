# Park Chaos Overhaul: Audit and Plan

Status: **Phase 0b in progress.** Audit approved 2026-10-27.

## Decisions (owner, 2026-10-27)

1. **Baseline:** commit it, add `.gitattributes`, untrack the `_previews` logs, push to `Mazha96/ParkShift`. Done in `45d13f1` and `bb8332f`.
2. **URP:** yes, on a separate `urp-migration` branch. Merge only after the game looks and runs correctly.
3. **Coaster:** replace GALAXY EXPRESS with the Splines coaster, reusing its loop and evac sections.
4. **Track:** becomes a hand-editable prefab that SceneBuilder references.
5. **Faults:** upgrade the existing dropped item, track intrusion and valleyed train faults into the new flows rather than duplicating them.
6. **Guests:** keep Dewey and add a ragdoll if the rig allows. If it doesn't, report what it would take before switching models. Restyle Dewey to match the Kenney look.
7. **Art:** Kenney.
8. **Order:** Phase 0b before Phase 1.

Further rulings:

- **LOTO** behaves like real lockout/tagout. While applied, every fault is repairable, including ones raised afterwards. Releasing it restores power and blocks repairs.
- **Remote tech (H)** charges only when it actually clears a fault.
- **End of Phase 0b:** give the owner a short LAN test checklist.
Audit date: 2026-10-27, against `main` @ `60b26c3` plus a large uncommitted working tree (see 0.1).

---

## 0. Audit findings

### 0.1 Repo state (read this first)

- **No `CLAUDE.md` exists.** The architecture rules live in `README.md`, `REVAMP.md`,
  `OVERHAUL_NOTES.md`, `BLOCK_ZONES.md` and `Assets/Scripts/Networking/NETWORKING.md`.
  I followed those. The main rule is that `Assets/Scripts/Core` (`ParkShift.Core.asmdef`,
  `noEngineReferences: true`) is the single source of truth, and Unity mirrors it.
- **The whole September 22 revamp is uncommitted.** 80 tracked files are modified and
  roughly 40 scripts and tests are untracked. The untracked files include `ManagementSim.cs`,
  every `*Driver.cs`, `CartoonParkLook`, `InteractableGlow`, `LanSceneSetup`,
  `ParkShift.Editor.asmdef`, `InteractionTargetTests`, `PropMotionTests`, `Tools/Playtest/`
  and `Assets/Resources/`. Git also warns about LF→CRLF on every file.
  One bad checkout would lose all of it. **This needs to be committed as a baseline
  before any overhaul work** (Question Q1).
- `_previews/` holds about 60 untracked logs, PNGs and JSONL files from earlier test runs.
  `files.zip` sits at the root. `Builds/` is gitignored.
- Unity 6000.0.32f1 is installed, and batchmode works here. I can run EditMode and
  PlayMode tests headless myself.

### 0.2 Test results (run today)

| Suite | How | Result |
|---|---|---|
| Core harness | `Tools/CoreTestHarness/run.ps1` (.NET 9) | **23/23 pass** |
| Headless bot playtest | same run, seed 42 | 92 riders, 15 dispatches, 10 faults resolved, 4.2 s downtime, score 1353, **grade S** |
| Unity EditMode | `Unity.exe -batchmode -runTests -testPlatform EditMode` | **27/27 pass**, 0 compile errors |
| Unity PlayMode | none exist | n/a |
| LAN probe | `Tools/Playtest/run-lan.ps1` | not re-run (needs a Windows build; last recorded pass was Sept 22) |

The test coverage has gaps:

- The bot plays **one seed on the default `SimConfig`**: 4 blocks, single station, instant
  motion, timed faults off, extended faults off. That is **not the shipped game**, which
  runs a split station with 5 blocks, timed faults, the escalated preset and the tech catalog.
- The bot has no handler for TrackIntrusion, TrainCollision or TrainValleyed, and
  AutoOperator skips Brownout. None of the major incidents get exercised headlessly.
- The docs disagree about the test count: README says 21, BLOCK_ZONES says 17, the actual count is 23.

### 0.3 Bugs (confirmed in code)

Softlock and correctness bugs in Core:

1. **DispatchTimeout never clears after a dispatch.** `Dispatch()` sets
   `_timeoutFaultLatched = false` without resolving the fault (`Core/AttractionSim.cs:248`).
   The auto-clear only runs while the latch is set (`:504`). The fault stays active, so
   State stays `Faulted` and downtime plus mood drain keep accruing.
   The existing test only covers the trigger (`CoreTestSuite.cs:220`).
2. **LOTO only marks faults that exist when the lever is pulled** (`AttractionSim.cs:259`).
   If LOTO is engaged and *then* MaintReset or Valleyed rolls, the fault can't be fixed
   until the lever is toggled off and on again.
3. **`LotoSatisfied` is never cleared when LOTO is released** (`FaultSystem.cs:267`).
   You can engage LOTO, release it, then do the "locked out" repair with power on.
   Only the UI prompt checks the lever.
4. **Remote-tech assist (H) can fail repeatedly and still charge.**
   - It resolves `Active[0]` and marks LOTO but not `Inspected` (`Attraction/AttractionManager.cs:303`).
   - TrainCollision and BlockSensorGhost therefore never resolve.
   - The penalty is charged anyway, and pressing again repeats the failure.
5. **A RestraintJammedCar fault is unfixable if `CarsPerTrain > 4`.**
   The verbs stop at `UnjamCar3` (`AttractionManager.cs:19`, `PanelInteractable.cs:174`),
   but the asset exposes the field (`SimConfigSO.cs:34`).
6. Faults keep rolling during LOTO. With timed faults off, two ride-stoppers can overlap.
7. `BlockCount` is overridden from the track but `BlockSpeedMultipliers` isn't resized (`AttractionManager.cs:146`).
8. `SimConfigSO.ToConfig` drops these fields: `StationParkProgress`, `UnloadSeconds`,
   `QueueCapacity`, `TechFaultChance`, `IncidentRecovery` and the satisfaction drains
   (`SimConfigSO.cs:74-108`). Assigning the asset therefore silently changes tuning.
9. The host always uses `Coop(2)` tuning regardless of player count (`AttractionManager.cs:128`).
10. Live play is unseeded (`AttractionManager.cs:149`, and seed 0 means `new Random()`).
    Unity ticks Core with variable `Time.deltaTime`, so no bug is reproducible.

Bugs in the Unity layer:

11. **Guest exit target jumps every frame.** When `UnloadExitPath` is empty, `ExitNodePos`
    calls `PickUnloadExit()`, which does `_exitRR++` on every call, per exiter, per frame
    (`Guests/GuestVisualManager.cs:291,337`).
12. **`CameraJuice.Instance` and `LookLabel.BillboardTarget` go to whichever rig spawned last.**
    Both are set on every rig, remote ones included (`Juice/CameraJuice.cs:38`,
    `Player/FirstPersonController.cs:67`, `Interaction/LabelLookSystem.cs`). In LAN play,
    shake and FOV kicks can land on a remote player's disabled camera.
13. `FirstPersonController.Awake` locks the cursor for remote rigs too (`:70`).
14. On clients, riders pop: front-of-queue guests are destroyed and new, differently
    coloured seated guests spawn (`GuestVisualManager.cs:100,256`). Client riders also get no GuestBounce.
15. `JuiceDirector` detects fault edges by count (`:105`), so a resolve and a trigger in the same frame go unnoticed.
16. `MirrorFaults` keys on `FaultType` (`Networking/AttractionNetSync.cs:371`), so two
    faults of the same type merge on clients. More than 4 active faults are silently dropped (`:238`).
17. Docs and code disagree:
    - BLOCK_ZONES says held trains stop at `BrakePoint01 = 0.92`. The built-in config never
      enables `SmoothMotion`, so trains hard-stop at the block exit.
    - The `SimConfig.cs:9` comment has the N+1 rule backwards.
    - README still says "two-train".

### 0.4 Physics problems

- **No game physics at all.** There is no Rigidbody anywhere in gameplay code. There are no
  pushable props and no ragdolls. Guest and crew colliders are deleted at spawn
  (`GuestVisualManager.cs:203,213`, `LanSceneSetup.cs:124`).
- **No physics layers.** TagManager holds only Unity's defaults, and every query uses `~0`.
  Footstep and interact rays hit everything.
- Player (`FirstPersonController`):
  - **What it has:** CharacterController moved in `Update`, `MoveTowards` acceleration,
    coyote time 0.12 s and jump buffer 0.14 s.
  - **Slopes and steps:** no slope handling and no ground snapping, so the player goes
    briefly airborne on downslopes and stair descents. `stepOffset` and `slopeLimit` are
    set only on the LAN prefab (`LanSceneSetup.cs:106`).
  - **Frame-order issues:** the accel branch reads the previous frame's `isGrounded`.
    Head bob reads `_cc.velocity`, which spikes on wall contact.
  - **Network:** yaw is rotated in `Update` on a transform that a default NetworkTransform
    also drives, which is a jitter source in LAN.
  - **Allocations:** footsteps allocate a string per step (`collider.name.ToLowerInvariant()`).
- Trains: they follow the sim deterministically, which is good. The loop flip is fixed,
  because `TrainVisual` uses the parallel-transport `FrameTable`.
  - Clients get **10 Hz pose steps with no snapshot interpolation**, only an exponential
    follow (`TrainVisual.cs:136`).
  - Suspension and `TrainJuice` differentiate that stepped speed, so clients probably get
    false sparks and brake hisses (`TrainJuice.cs:189`).
- Speed has no relation to gravity. Each block uses a fixed traverse time divided by a
  per-block multiplier (`SimConfig.cs:97`). Real m/s depends on block length, which is
  why the long block 3 runs fast.
- Fixed timestep is 0.02 s (default). Nothing uses it yet.

### 0.5 Placeholder art and unfinished work

- Built-in render pipeline with custom cel and ink shaders (`Resources/ParkChaos/*.shader`)
  and PostProcessing v2. **URP is not installed. The custom shaders and PPv2 won't work under URP.**
- `Assets/Prefabs` and `Assets/ScriptableObjects` are empty.
  - SceneBuilder loads FBX files through `AssetDatabase.LoadAssetAtPath` with primitive
    fallbacks, but it does not use `Resources.Load`.
  - It is one roughly 1,230-line method (`SceneBuilder.cs:753-1985`).
- About 11 Blender FBX props (train, console, gates, cabinet, Dewey guest), plus
  primitives for everything else. There's no credits file.
- The track is a custom centripetal Catmull-Rom with **53 hardcoded waypoints**
  (`SceneBuilder.cs:961`) and hardcoded block boundaries (`:1027`). **The Unity Splines
  package is not installed.** Because the scene is regenerated, any hand edit to the
  track is lost on rebuild.
- Audio is fully synthesized at runtime (`AudioSynth`). There are no sound files.
- The scene audit reports 301 overlap findings: 179 solid-solid and 117 train-sweep.
  Many are the two staged trains sitting on top of each other.
- No safety metric, **no per-player attribution** (`OpVerb` has no actor, and the server
  drops `sender` before `Execute`, `AttractionNetSync.cs:467`), and no player-pressable e-stop.
- The fault logic is hardcoded switches spread over about 8 files: FaultSystem pools and
  windows, AttractionSim verbs, HUD text, PanelInteractable, AutoOperator, Playtest,
  FaultPropController and NetSync. Adding a fault means touching all of them.
- NetSync has 4 hand-written train slots and 4 hand-written fault slots, plus bit-packed
  flags with only bits 27-31 free. New synced state needs a restructure (SyncList/struct).
- Role authorization is client-chosen (`AttractionNetSync.cs:484`). That's acceptable for
  friends' LAN, but worth knowing.

### 0.6 What already works and must be kept

The following already work and the plan keeps them:

- **Shift loop:** open shift, gates, board, restraint check, dispatch, unload, results with an S–F grade.
- **Block zones with the N+1 rule,** the split load/unload station, and adding an RV.
- **Faults and scoring:** the 12-fault catalog, the escalated preset, and streak/combo scoring.
- **Roles:** the Operator/Tech/Manager roles with solo auto-operator coverage, and the manager economy.
- **Rides and props:** GALAXY EXPRESS (loop, zero-g, MCBR, evac platforms) and the springy prop drivers.
- **Presentation:** hold-to-interact, look-labels, the juice layer and procedural audio.
- **Multiplayer:** 4-player FishNet LAN with late join.

---

## 1. Phased plan

Each phase ends with all tests run, a summary with a hand-test checklist, a commit, and a stop for your review.
Small commits inside each phase. Out-of-phase bugs go to `docs/BUGS.md`.

### Phase 0b: Baseline and stabilize (small, proposed, needs Q1/Q2)

- Commit the current working tree as the baseline. Add `.gitattributes` so line endings
  stop churning, and gitignore `_previews/*.log|*.xml|*.jsonl|*.png`.
- Fix the Core softlocks: bugs 1–4, plus a test for each. These directly violate "no fault may softlock".
- Make the bot playtest take `--seeds N --config shipped|default`, add handlers for every
  fault, and assert no softlock: State must leave `Faulted` within N seconds of the
  correct fix. This becomes the regression net for every later phase.

### Phase 1: Physics overhaul

1. **Layers and collision matrix:** Player, Guest, Prop, Train, TrackEnvelope, Interactable, Trigger, StaticWorld.
   Every raycast gets a real mask.
2. **Movement tuning asset:** `PlayerMovementTuning` ScriptableObject covering accel/decel,
   air control, jump height, coyote, buffer, slope limit, step offset, ground-snap distance and sprint.
3. **Player controller** (keep the CharacterController, which is deterministic and network-friendly):
   - Ground snap on descents, slope projection of velocity, and a sliding limit.
   - Stair and sleeper smoothing: a step-up with a camera smoothing offset, and smooth
     colliders on queue rails and track ties so the player doesn't snag.
   - Fix the grounded-frame order.
   - Camera pose in `LateUpdate`, owner-only singletons (bug 12/13), and remote rigs
     driven only by NetworkTransform interpolation.
4. **Physics props:** a `PhysicsProp` component plus a `PropTuning` SO (mass, drag, angular
   drag, throw impulse, impact thresholds). The props are a trash can, cone, toolbox and
   guest luggage.
   - Players can push (CC-to-rigidbody push), grab, carry and throw them.
   - Interpolation is on. Props are host-authoritative in multiplayer, using a NetworkObject
     with a server-owned NetworkTransform.
   - A prop impact event goes to Core as a plain verb, so a prop hitting an e-stop can work in Phase 3.
5. **Guest reactions:** keep the kinematic Dewey guests by default.
   - On a bump or impact: a stumble animation and impulse, then **ragdoll** past a threshold.
   - Recovery: blend back to animation and stand up. Comedic, no gore.
   - Guest state stays in `GuestSim`. Ragdoll is presentation only, plus a "guest knocked
     over" event for happiness scoring.
   - Needs Dewey's rig bones checked (Q6).
6. **Ride vehicles:** stay sim-driven. Add client snapshot interpolation (buffered, timestamped)
   in place of the 10 Hz steps. Add visual-only sway, and loose-item rigidbodies parented to
   the car's kinematic body.
7. **Jitter pass:** confirm the fixed timestep, Rigidbody interpolation and camera order.
   Drive `JuiceDirector` from events instead of counts.
8. **Tuning SOs:** a real `SimConfigSO` asset gets assigned in the scene. Fix `ToConfig` (bug 8).
9. **Tests:** introduce PlayMode tests for the controller:
   - Slope descent stays grounded.
   - Step-up over a sleeper with no vertical pop over a threshold.
   - Coyote and buffer windows.
   - A prop pushed and thrown comes to rest.
   - A ragdolled guest recovers.

### Phase 2: Roller coaster on Unity Splines

1. Install `com.unity.splines`. A new `CoasterTrack` component wraps a `SplineContainer`
   and replaces `TrackPath`'s internals. It keeps `TrackPath`'s public API
   (`DistanceOf`, `FrameTable`) as an adapter so `TrainVisual`, mesh and guests keep working.
2. **Layout** (new, or a rebuilt GALAXY EXPRESS per Q3), with these sections:
   1. Station (unload + load)
   2. Chain lift
   3. First drop
   4. Airtime hill
   5. Banked turn
   6. Helix or vertical loop
   7. Mid-course brake run
   8. Final brakes into the station
3. **Core: geometry-free physics.** Unity bakes a `TrackProfile` into Core: per-segment
   arc length, height samples and a section type (Station, Lift, Free, Brake, Launch) as plain floats.
   - `TrainSim` integrates `v += (-g·dh/ds − μg − k·v²)·dt` in free sections.
   - Chain lift and station tires hold a constant speed. Brakes are a governor.
   - Legacy fixed-multiplier mode stays behind a flag, so the existing 23 tests stay byte-identical.
   - A train that doesn't clear a hill **valleys naturally**. That's the existing
     TrainValleyed fault becoming emergent, behind a config flag.
4. **Block zones in Core:**
   - Blocks are bounded by brake/lift sections, and a train cannot leave a block-end brake
     until the next block is clear.
   - Mid-course brakes really hold a train.
   - Unit tests cover:
     - Block gating.
     - The N+1 invariant.
     - Energy: speed at the bottom ≈ √(2gh) minus friction.
     - A held train restarts.
     - No train ever enters an occupied block (fuzzed across seeds).
5. **Banking** comes from curvature (lateral acceleration v²/r, so a coordinated turn).
   It's baked into spline knot rotations, with per-knot override.
6. **Editable layout:**
   - Spline knots are movable in the editor, and the profile re-bakes on spline change
     (edit mode) and on Play.
   - Block boundaries are markers placed on the spline.
   - **The track lives in a prefab or asset that SceneBuilder references rather than
     regenerating, so edits survive rebuilds** (Q4).
   - The track mesh and supports regenerate from the spline.
7. **Tests:**
   - Core: profile physics and block gating.
   - EditMode: baking a known spline gives the expected lengths and heights.
   - PlayMode: a train completes a lap and parks in the station.

### Phase 3: Tasks and faults

1. **Data-driven refactor first:**
   - Each fault becomes a small Core state machine (`IFaultFlow`: stages, allowed verbs,
     stop requirement, timeouts), with no switch spread across 8 files.
   - Verbs carry an **actor id**, so the recap can name players.
   - NetSync faults move to a SyncList of structs, so same-type faults no longer collapse
     and the 4-slot cap goes away.
   - The existing 12 faults are ported first, behaviour-identical and with their tests still passing.
2. **Shared safety primitives** in Core: ride Open/Closed, LOTO (a real lockout that blocks
   power and is required to enter a "track walk" zone), e-stop zones, block-held trains,
   a recovery sequence, and a test-cycle requirement.
3. **Main faults, in order:**
   1. Item on track.
   2. Guest stands or unbuckles.
   3. Accidental e-stop.
   4. Scared kid on the lift (full evac).

   Each one has its notice → stop → fix → reset → recover flow. Each one has a Core unit
   test per stage plus a "never softlocks" fuzz in the bot. A PlayMode test proves the ride
   recovers.
4. **CCTV monitor and HMI:** an extra camera feed in the booth, an HMI restraint alarm,
   and an e-stop zone indicator. A PA verb.
5. **Extra faults** in the order listed: proximity sensor, PLC code, power blip/cold start,
   height-check sneak, rain delay.
6. **Cascades:** a fault-to-fault trigger table in Core. Difficulty becomes spawn rate and
   overlap count over shift time, with the existing ramp reused.
7. **Scoring:**
   - Add safety and happiness as first-class metrics.
   - A funny-stats recap: who hit the e-stop, items recovered or lost, longest evac,
     guests reseated.
8. **Existing faults overlap the new ones:**
   - GuestDroppedItem is a simple version of "item on track".
   - TrackIntrusion is already an e-stop.
   - Valleyed already has evac.
   - **Proposal:** upgrade, don't duplicate. Q5 decides.

### Phase 4: Graphics

1. **URP migration** (Q2):
   - Port the cel and ink shaders to URP. The outline becomes a Renderer Feature.
   - Replace PPv2 with URP Volume post: bloom, color grading, SSAO.
2. Mixed lighting with light probes and a baked park.
   - A day-to-evening cycle driven by shift time.
   - Lamps, neon and ride lights switch on at dusk.
3. **Prefab-aware SceneBuilder:** a `Resources/Prefabs/<Name>` lookup with fallback to the
   current FBX, then to a primitive. The 1,230-line build method gets split per area as it's touched.
4. **CC0 art:** one family, either Kenney or Quaternius (Q7). `docs/CREDITS.md` lists every
   asset and its license.
5. **Readable feedback:** fault icons and colours, spark and leak particles, and screen shake
   on big moments. These extend the existing Juice layer. Real SFX files are optional, with
   the synth as fallback.

### Phase 5: Testing and bugs

- **Core** gets unit tests for block zones, fault states, task completion, scoring and dispatch rules.
  Most of these are written during phases 0b–3; this phase fills the gaps.
- **PlayMode** gets movement, prop physics, and ride recovery for each fault type.
- **Asset validator** (an editor menu plus a batchmode test) checks every prefab for:
  missing scripts and refs, missing materials, bad or absent colliders, wrong layers, and scale outliers.
- **Bot playtest across many seeds** on the shipped config, asserting zero softlocks and zero
  exceptions. A Unity-side `LanPlaytestProbe` run records frame-time p50/p99.
- **Multiplayer:** a 2-process localhost run first, then 4-process, checking that props,
  faults, scoring and the recap agree.

---

## 2. What I can and cannot verify

I **can** run these headless here:

- the Core tests and the bot across many seeds
- Unity EditMode and PlayMode tests in batchmode
- Windows builds
- localhost multi-process LAN runs with log checks
- scripted screenshots

I **cannot** judge any of the following. They need your hands and eyes:

- movement feel
- ragdoll comedy timing
- coaster "believability"
- art cohesion and lighting mood
- audio mix
- separate-PC LAN latency

---

## 3. Questions before Phase 1 (answered; see Decisions at the top)

- **Q1 Baseline commit:** may I commit the current uncommitted working tree (the Sept 22
  revamp, about 120 files) as one baseline commit, add `.gitattributes`, and ignore the
  `_previews/` logs? Nothing gets deleted.
- **Q2 URP:** Phase 4 asks for URP. That means rewriting the cel and ink shaders and
  replacing PPv2. Confirm URP, or stay on built-in with PPv2 for bloom, grading and AO
  (less work, but no Volume or probe-volume workflow).
- **Q3 Coaster:** should the Phase 2 Splines coaster *replace* GALAXY EXPRESS (port its
  loop, zero-g and evac platforms onto the new spline), or be a second ride?
  Recommendation: replace, with GALAXY's sections reused in the new layout.
- **Q4 Track ownership:** may the track become a hand-editable prefab or spline asset that
  SceneBuilder references instead of regenerating? Otherwise "move knots in the editor"
  conflicts with "scenes are generated artifacts".
- **Q5 Overlapping faults:** should the new main faults upgrade the existing GuestDroppedItem,
  TrackIntrusion and TrainValleyed (recommended), or sit alongside them? The other existing
  faults stay either way.
- **Q6 Guests:** should Dewey.fbx stay as the guest model (I'll add a ragdoll if the rig has
  usable bones), or move to the new CC0 family's characters in Phase 4?
- **Q7 Art family:** Kenney (wider prop and park coverage, simpler) or Quaternius (better
  characters)? Recommendation: Kenney for the park and props, so there's one style family.
- **Q8 Phase 0b:** fix the confirmed Core softlocks (bugs 1–4) and the multi-seed bot before
  Phase 1? Recommendation: yes, because every later phase leans on that safety net.
