# Park Shift — Networking Notes

## Architecture (decided day one, unchanged)
- **Host-authoritative listen server** using **FishNet** (in `Packages/manifest.json` via OpenUPM).
- `AttractionSim` (pure C#) **ticks ONLY on the server/host**. It is the single source of truth.
- All player verbs funnel through `AttractionManager.Submit(OpVerb)`:
  - Solo/Host: executes directly.
  - Client: `AttractionNetSync` routes the verb as a `ServerRpc`; the server validates by
    running the exact same `Execute()` path as solo (no duplicated rules). Verbs replicate
    as their int value — the `OpVerb` enum is **append-only**.
- State replication: **10 Hz snapshot via SyncVars** (`AttractionNetSync.PushSnapshot`).
  SyncVars only hit the wire when a value changes, and FishNet delivers current values to
  new observers on spawn — which is what makes **late join** work with zero extra code.

## What replicates (Wave 2 sync-gap pass)
| Group | Fields |
|---|---|
| Ops | state, satisfaction, queue count, shift clock, gates open, LOTO, unload door / restraints released / area clear / guests unloading |
| Trains | fixed **4 slots** (`MaxTrainSlots`), per-slot occupancy flag; each slot = pose `Vector4(block, progress, trainState, speed)` + packed int (riders, restraints-verified). Robust for 1..4-RV configs and **runtime-added RVs** (HMI "Add RV" — count replicates, clients call `Sim.AddTrain()` so `OnTrainAdded` spawns the visual body locally) |
| Faults | fixed **4 slots** (`MaxFaultSlots`); each slot = packed int (type, brownout **Stage**, jammed-car **TargetIndex**, **Inspected**, LOTO-satisfied) + the per-instance **`ActiveFault.HmiText`** string (so clients see "CAR 3 LAP BAR NOT SEATING", not the generic catalog line) |
| Score | riders served, dispatches, faults resolved, penalty, **current/best streak**, combo bonus |
| Config (late-join presentation knobs, set once in `OnStartServer`) | train count, block count, **escalated-shift flag**, split-station flag, cars per train, brownout stage count, shift length. Client logs a warning if its local scene config disagrees with the host's. |

## Client-side sim mirroring
Clients never tick the authoritative sim, but all UI (`HmiPanel`, `PanelInteractable`,
prop drivers) reads `AttractionManager.Instance.Sim`. `AttractionNetSync.ApplyMirror()`
therefore reconciles the client's local (never-ticked) Sim against the snapshot at 10 Hz,
**using only existing Core public API** (Core is off-limits to the networking layer):

- Shift open (`OpenShift`), gates (`GuestSim.SetGates`), LOTO (`EngageLoto` — also flips
  Ready/LotoActive and marks LOTO-satisfied faults, same as the server).
- Faults: `FaultSystem.Trigger`/`Resolve` reconcile the active set (stale local faults are
  force-resolved by setting the public `Inspected`/`LotoSatisfied` fields first); full
  instance state (`Stage`, `TargetIndex`, `Inspected`, `Detail`) is stamped onto the local
  `ActiveFault`, so fault prompts and the HMI fault list are truthful on clients.
- Platform train riders (`Board`/`Unload`) and restraint verification
  (`VerifyRestraints`/`InvalidateRestraints`).
- RV count (`AddTrain`, which fires `OnTrainAdded` → visuals spawn client-side).
- Satisfaction (clamped `RewardDispatch`/`Penalize` deltas) and score counters
  (`AddRiders`/`AddFaultResolved`/`AddPenalty`/`AddComboBonus`/`AddDispatch`/`BreakStreak`).

Everything the local sim **cannot** express through public API is exposed raw on
`AttractionNetSync.Instance` via `Mirror*` accessors, `TryGetMirrorTrain(slot, ...)` and
`TryGetMirrorFault(slot, ...)` for a client presentation pass.

## Mirror setters (final-review integration pass)
The reviewer added three narrow Core setters, used ONLY by `ApplyMirror` on
non-authoritative clients (never in solo/host/headless — determinism untouched):
- `TrainSim.MirrorPose(block, progress, state, speed)` — stamps the replicated pose and
  re-registers block occupancy (`BlockSystem.Vacate` + `ForcePlace`) on block changes, so
  client `TrainVisual`s move and `PlatformTrain()`/`UnloadingTrain()` resolve truthfully.
- `GuestSim.MirrorQueue(count)` — steers `QueueCount`, firing `OnGuestSpawned` per added
  guest and one `OnGuestsBoarded` batch on a decrease (guest visuals track the server).
- `AttractionSim.MirrorOps(state, simTime, unload flags…)` — stamps state + shift clock +
  the unload workflow; fires `OnUnloadReleased` on the restraints-released edge (guest
  walk-out plays on clients) and `OnShiftEnded` once on the EndOfShift edge.
The HUD additionally reads `MirrorShiftRemaining`/`MirrorState` (FISHNET-guarded) so the
timer and results screen are exact on clients. HMI "Add RV" routes through the verb
funnel (`OpVerb.AddRv`) so it executes server-side and replicates back.

## Known remaining gaps
- `BestStreak` and `Dispatches` can read low for a late joiner until the next dispatch
  (mirrored via score adders only); raw truth is on `MirrorBestStreak`/`MirrorDispatches`.
- `AssistantRemaining` (remote-tech countdown) is host-local — clients don't see the
  "tech en route" line.
- Trains/faults beyond 4 concurrent are not replicated (warned once, server log).
- Train poses update at 10 Hz with no interpolation — client RVs step visibly at speed
  (TrainVisual's follow smoothing softens it).

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

## Steam Relay plug-in point (unchanged)
Replace Tugboat with **FishySteamworks** transport + Facepunch/Heathen Steamworks lobby:
implement `ISteamRelay` (see `ISteamRelay.cs`), host/join lobbies, and FishNet rides the
Steam datagram relay — no NAT/port forwarding. Requires a Steam AppID; stubbed until then.

## Known scope cuts (intentional)
- No host migration (industry-standard cut; shift ends if host quits).
- 10 Hz coarse snapshots, no interpolation/prediction — fine for primitive visuals and
  panel prompts; revisit if train motion ever replicates pose-for-pose.
