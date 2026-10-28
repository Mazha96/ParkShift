# Known bugs (outside the active phase)

Numbering matches the audit in `OVERHAUL_PLAN.md` §0.3. Each entry says which phase is expected to fix it.

| # | Bug | Where | Planned fix |
|---|---|---|---|
| 1–4 | DispatchTimeout stuck, LOTO latch, remote-tech charge | Core | **Fixed** in `4186ac6` |
| 5 | RestraintJammedCar unfixable when `CarsPerTrain > 4` (verbs stop at `UnjamCar3`) | `AttractionManager.cs:19`, `PanelInteractable.cs:174` | Phase 3 (data-driven faults) |
| 6 | With timed faults off, two ride-stoppers can overlap | `FaultSystem.Tick` | Phase 3 |
| 7 | `BlockSpeedMultipliers` not resized when `BlockCount` comes from the track | `AttractionManager.cs:146` | Phase 2 |
| 8 | `SimConfigSO.ToConfig` drops several fields | `SimConfigSO.cs:74-108` | Phase 1 (tuning SOs) |
| 9 | Host always uses `Coop(2)` tuning | `AttractionManager.cs:128` | Phase 3 |
| 10 | Live play unseeded; variable-dt sim tick | `AttractionManager.cs:149,227` | Phase 1 (fixed tick) |
| 11 | Guest exit target jumps each frame when `UnloadExitPath` empty | `GuestVisualManager.cs:291,337` | Phase 1 |
| 12 | `CameraJuice.Instance` / `LookLabel.BillboardTarget` last-rig-wins | `CameraJuice.cs:38`, `LabelLookSystem.cs` | Phase 1 |
| 13 | Remote rigs lock the cursor in `Awake` | `FirstPersonController.cs:70` | Phase 1 |
| 14 | Client riders pop (destroyed and respawned in a new colour) | `GuestVisualManager.cs:100,256` | Phase 1 |
| 15 | JuiceDirector misses a same-frame resolve+trigger | `JuiceDirector.cs:105` | Phase 1 |
| 16 | NetSync merges same-type faults; more than 4 faults dropped silently | `AttractionNetSync.cs:238,371` | Phase 3 |
| 17 | Docs drift (BrakePoint01, N+1 comment, "two-train") | docs, `SimConfig.cs:9` | Opportunistic |
| 18 | Scene rebuild is not byte-stable: every Build All Scenes rewrites ~106k lines of fileID churn in the scenes and the Dewey controller, with no content change. Revert the churn instead of committing it. | `SceneBuilder.cs` | Phase 4 (prefab-aware SceneBuilder) |
