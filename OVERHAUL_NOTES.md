# ParkShift — Multi-Agent Overhaul Notes (Waves 1 & 2)

Two autonomous agent waves on top of the vertical slice, each reviewed, reconciled and
committed by a final integrator. Core stayed pure C# and deterministic throughout —
`bash /tmp/verify.sh` (syntax parse of all scripts + 17 headless core tests + bot
playtest) is green at every commit.

## What changed

### Wave 1 — visuals & content (commit e48dc5e)
- **Track & vehicles**: stylized low-poly visual overhaul (Blender FBX props, PS_* material
  set, articulated coaster train with per-car wheels/restraint bars).
- **Environment**: park dressing, lighting, skyline hills, NeonPulse signage.
- **Content**: 3 new faults (BlockSensorGhost, PowerBrownout, RestraintJammedCar) with
  multi-step fixes, escalated-shift preset (fault/arrival ramps + guest surge waves),
  dispatch-streak combo scoring with best-streak/combo stats on the results screen.

### Wave 2A — game feel / juice (`Assets/Scripts/Juice/`)
All procedural, no asset files; everything observes sim state (client-safe, no authoritative
callbacks, no sim mutation):
- **AudioSynth / AudioDirector** — every sound synthesized at runtime (UI blips, restraint
  clunks, gate servos, dispatch chime + streak arpeggios, crowd walla/grumble loops,
  seamless coaster rumble, klaxon phase-locked to the alarm strobes, grade-themed results
  fanfare). Single `Master` gain constant.
- **CameraJuice** — trauma-based shake + FOV kick on the player camera (conservative caps,
  offsets never accumulate).
- **JuiceFx** — pooled code-built particles: gate dust, wheel/brake sparks, launch speed
  lines, high-combo confetti.
- **TrainJuice** (per RV) — speed-driven rumble, launch anticipation squash + springy
  stretch, brake/launch sparks, restraint clunk + bar flash.
- **JuiceDirector** — polls sim edges (dispatch, gates, faults, resolve, shift open) and
  conducts all of the above; **GuestBounce** queue idle-bob + cheer hops; **FaultStrobe**
  klaxon-synced alarm strobes on fault props.
- Touched: FirstPersonController (head bob, landing thump), PropDrivers (springy
  overshoot hinges, button light-up), GateVisual, FaultPropController (strobes),
  GuestVisualManager (bounce/cheer), HUDController (score tick-up, streak punch, fault
  banner slide, prompt shake), ResultsScreen (fanfare), HmiPanel (blips), SceneBuilder
  ("Juice" root wiring + TrainJuice per train).

### Wave 2B — multiplayer state replication (`Networking/AttractionNetSync.cs`)
- N-train replication (4 slots: pose Vector4 + packed flags), full ops state (gates, LOTO,
  unload workflow), 4 fault slots with complete instance state (Stage/TargetIndex/
  Inspected/HmiText), score + streak + combo, config snapshot for late joiners.
- Idempotent `ApplyMirror` reconciles the client's never-ticked local sim at 10 Hz.

### Final review integration (this commit)
- Core mirror setters (client-only; solo/host/headless byte-identical):
  `TrainSim.MirrorPose` (re-registers block occupancy), `GuestSim.MirrorQueue` (fires the
  spawn/board events for guest visuals), `AttractionSim.MirrorOps` (state, shift clock,
  unload flags; fires OnUnloadReleased / OnShiftEnded edges).
- HUD reads `MirrorShiftRemaining` / `MirrorState` on clients (FISHNET-guarded) — timer
  and results screen now correct client-side.
- Bug fix: HMI **Add RV** now routes through the verb funnel (`OpVerb.AddRv` appended) —
  previously a FishNet client would have added a ghost RV to its local mirror only.
- NETWORKING.md updated (gaps closed / remaining).

## How to see it
1. Open the project in **Unity 6 (6000.0.32f1)** (built-in RP).
2. Menu **ParkShift → Build All Scenes** (regenerates `PrototypeAttraction.unity` from
   `SceneBuilder.cs` — REQUIRED: the committed scene predates Wave 2 wiring).
3. Play **PrototypeAttraction**. The **escalated shift is ON by default** (extended fault
   catalog, arrival/fault ramps, surge waves). Booth → open shift, gate lever, board,
   verify restraints, dispatch; E interacts, Tab swaps role, H calls the remote tech,
   E on the console screen opens the clickable HMI.

## Co-op smoke test (FishNet)
1. Let `com.firstgeargames.fishnet` resolve (already in `Packages/manifest.json`); the
   `FISHNET` define activates automatically via asmdef versionDefines.
2. In `PrototypeAttraction`: add FishNet **NetworkManager** (Tugboat, default port 7770);
   add `AttractionNetSync` on a scene NetworkObject; register the player prefab
   (capsule rig + `NetworkedPlayer`) as the spawnable player.
3. Editor = Host, player build = Join (localhost). Then verify:
   - Client sees trains moving, queue filling, gates/lever/props animating.
   - Client verbs (gate lever, load, verify, dispatch, LOTO, fault fixes, HMI Add RV)
     execute on the host and replicate back; host validates illegal actions.
   - Fault on host shows banner/strobes/klaxon + correct per-instance HMI text (e.g.
     jammed-car line) on the client; fixing from either side clears both.
   - Late join mid-shift converges on first snapshot (trains, faults, score, clock).
   - Shift end: results screen appears on BOTH machines with matching score.

## Known risks / gaps — needs human eyes
1. **Scene rebuild required** — run ParkShift → Build All Scenes before judging anything;
   the committed scene predates the Juice wiring and Wave-1 prop placement.
2. **Interactable placement sanity (in-editor)** — zone sensor post at (14.2, 0, 10);
   lap-bar checkpoints x 1.65–4.35, z 1.5; sensor-reset button at (11.35, 1.0, −3.9).
   Confirm prompts are reachable and don't overlap other triggers.
3. **Escalated difficulty fairness** — 2× fault ramp + 1.75× arrival ramp + surge waves
   is untuned against real play; late-shift may be swamp-y solo.
4. **Look & mix pass** — NeonPulse/strobe emission intensity (built-in RP, no bloom
   guarantee), procedural audio levels (Master=0.5, klaxon vs rumble vs walla balance),
   head-bob/shake comfort.
5. **Client-side residuals** — BestStreak/Dispatches can read low for late joiners;
   AssistantRemaining is host-local; >4 concurrent trains/faults unreplicated; train
   poses step at 10 Hz (no interpolation); no host migration.
