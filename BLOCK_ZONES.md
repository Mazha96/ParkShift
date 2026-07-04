# Block Zones & Multi-RV Cycling

This extends the **existing** block model rather than adding a parallel one. The occupancy
authority is still `BlockSystem`; the N+1 gating still lives in `TrainSim`; the spline is still
`TrackPath`. What's new: coast-to-stop braking, a data-driven block layout with gizmos, and a
configurable RV count so you can tune the testbed in the inspector.

## What changed

| File | Change |
|------|--------|
| `Core/SimConfig.cs` | New tunables: `TrainCount`, `SmoothMotion`, `BrakePoint01`, `BrakeDecel`, `LaunchAccel`. Core defaults are behavior-preserving (instant motion, hard stop at exit) so the headless tests stay deterministic. |
| `Core/TrainSim.cs` | `Tick` now takes `SimConfig`. New **brake governor** (`StepEased`): RVs ramp up from a stop and coast to a halt *exactly* on the block's brake point when the next block is occupied. Legacy path (`StepInstant`) is kept byte-for-byte for `SmoothMotion = false`. Added `MayAdvance()` and a `Speed` readout for FX. |
| `Core/AttractionSim.cs` | Builds `cfg.TrainCount` RVs (was hard-coded to 2). Staging reproduces the old `{0, N-1}` layout for the default and stages extras toward the brake run. |
| `Core/BlockSystem.cs` | Added `Vacate(trainId)` — the unregister-on-despawn match for `ForcePlace` (register-on-spawn). |
| `Vehicles/TrackPath.cs` | Data-driven `BlockStarts` (normalized arc positions), `BlockLabels`, `BrakePoint01`, and **scene-view gizmos** drawing each block (colored ribbon + boundary post + label) and its red brake-point marker. `DistanceOf` uses `BlockStarts` when set; the legacy equal-slice mapping is untouched when it's empty. |
| `Attraction/SimConfigSO.cs` | Exposes the layout/RV/feel knobs to the inspector and maps them in `ToConfig()`. |
| `Attraction/AttractionManager.cs` | Uses a `SimConfigSO` asset when assigned; otherwise keeps the built-in 4-block / 2-RV defaults (non-breaking). |
| `Attraction/SimConfig_Testbed6.asset` | Ready-made config asset with the 6-block layout below. |

## The block layout I chose (6 blocks)

```
        [0] STATION ──▶ [1] LIFT ──▶ [2] COURSE A ──▶ [3] COURSE B ──▶ [4] COURSE C ──▶ [5] BRAKE RUN ──┐
          ▲                                                                                              │
          └──────────────────────────────────────────────────────────────────────────────────────────┘
```

| Block | Name | Speed × | Role |
|------:|------|--------:|------|
| 0 | Station | 1.00 | Load / unload; only one RV at a time |
| 1 | Lift | 1.60 | Fast launch out of the station |
| 2 | Course A | 1.15 | Course |
| 3 | Course B | 1.15 | Course |
| 4 | Course C | 1.15 | Course |
| 5 | Brake Run | 0.55 | Slow approach — where RVs stack when over-dispatched |

**N+1 rule:** N RVs need at least N+1 blocks. With 6 blocks, **3 RVs cycle cleanly**; a **4th**
creates deliberate stacking pressure feeding the brake run.

## Tuning (all in the inspector)

On the **`SimConfig_Testbed6`** asset (or your own `ParkShift/Sim Config` asset):

- `BlockCount` / `TrainCount` — change the loop size or RV count. Set `TrainCount = 4` to feel the stacking.
- `BlockSpeedMultipliers` — one entry per block; >1 faster, <1 slower.
- `SmoothMotion` — turn the coast-to-stop feel on/off.
- `BrakePoint01` — where a gated RV parks inside its block (0.92 = parks just short of the exit).
- `BrakeDecel` — lower = longer, lazier coast onto the brake run; higher = later, harder braking.
- `LaunchAccel` — lower = softer roll-out of the station.

On the **`TrackPath`** component (the spline):

- `BlockStarts` — normalized arc positions (0..1) of each block's start. Drag to move/resize blocks.
  Right-click the component → **"Distribute Blocks Evenly"** to seed 6 even values, then nudge them.
- `BlockLabels` — names shown on the gizmos (Station, Lift, Course A/B/C, Brake Run).
- `BrakePoint01` — keep matched to the config value so the red gizmo lines up with where RVs stop.

Gizmo legend: each block is a colored ribbon along the spline, a vertical post + sphere marks its
start boundary, and a short **red** line marks the brake point.

## Scene setup (one-time, since the scene asset can't be edited from code)

1. Select the GameObject with **`TrackPath`** and either right-click → *Distribute Blocks Evenly*
   (then tune) or paste the 6 values, e.g. `0, 0.16, 0.34, 0.52, 0.70, 0.86`. Set `BlockLabels`
   to the names above. The gizmos will draw immediately.
2. Make sure there are **enough `TrainVisual` instances** for `TrainCount` (one per RV), each with a
   unique `TrainId` (0..N-1) and the `Path` reference set. Add 1–2 more for 3–4 RVs.
3. On **`AttractionManager`**, assign **`ConfigAsset = SimConfig_Testbed6`**.
4. Press play. Switch `TrainCount` between 3 and 4 on the asset to compare clean cycling vs stacking.

## Verification

`_previews/verify_blocks.py` ports the new gating/easing math (no Unity/C# compiler needed) and
confirms:

- **Coast-to-stop** halts exactly at `BrakePoint01` (0.92) with smooth, monotone deceleration and no overshoot.
- **N+1 safety:** no two RVs ever occupy the same block, for both 3 and 4 RVs.
- **Cycling:** 3 RVs run with ~0s of holding (clean); a 4th piles up ~320s of holding concentrated
  in the back half (Course C → Brake Run), i.e. visible over-dispatch stacking.

Run: `python3 _previews/verify_blocks.py`

The default `SimConfig` (`SmoothMotion=false`, 4 blocks, 2 RVs) keeps the legacy instant-motion path
and the original `{0, N-1}` staging, so the existing core test suite is unaffected.
