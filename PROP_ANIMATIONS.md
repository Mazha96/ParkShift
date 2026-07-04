# Lever & button assets + motion

Brings the two new Blender levers into the game and makes the previously-static controls move.
Follows the project's existing convention: model a named moving part in Blender, drive it in Unity
with the code-based `Hinge` / `PressPulse` helpers (no Animator clips for props).

## Blender (done, saved)

In `GameAssets` (dewey_character_v7_stitch.blend) the green `OpenShiftLever` and red `EvacLever`
were already modelled but never exported. For each I:

- Added a pivot empty (`OpenShiftLever_Pivot`, `EvacLever_Pivot`) at the lever pin and parented the
  arm + grip to it, so a single transform rotation swings the whole lever about its hinge.
- Exported `Assets/Models/OpenShiftLever.fbx` and `Assets/Models/EvacLever.fbx`, centered at the
  origin (so in-scene placement is clean), matching the other props' export axes.
- Saved the .blend (pivots persist; the scene-layout positions were left where you had them).

Grips already use `PS_Green` / `PS_Red`, so no material work was needed.

## Unity code (done)

`Attraction/PropDrivers.cs`

- `PressPulse` — reusable button-depress (squash + spring back), shared by all buttons.
- `ShiftLeverDriver` — throws the green lever to "on" when the shift opens (sim leaves `Closed`).
- `EvacLeverDriver` — throws + gently cranks the red lever while a valleyed train is being recovered
  (stall fault active with LOTO engaged), then springs back.
- `CabinetDriver` — now also depresses `ResetBtn` the moment a drive/collision fault clears.
- `PodiumDriver` — depresses the podium button on its action (Load podium per guest boarded,
  Restraint podium when restraints are verified).

`Editor/SceneBuilder.cs`

- The `SHIFT START` box is replaced by the `OpenShiftLever` prop (interactor on the grip,
  `ShiftLeverDriver` wired to the pivot); box fallback kept if the FBX is missing.
- The `EVAC WINCH` box is replaced by the `EvacLever` prop (interactor on the grip,
  `EvacLeverDriver` wired); box fallback kept.
- Podiums get a `PodiumDriver`; the drive cabinet's `ResetButton` is wired.

## To see it in Unity

1. Let Unity import the two new FBX (focus the editor; it picks them up automatically).
2. Run **ParkShift → Build All Scenes** (the scene is rebuilt from `SceneBuilder`, the source of truth).
3. Open `Assets/Scenes/PrototypeAttraction.unity` and press play. Open the shift → the green lever
   throws. Trigger an evac (valley a train, engage LOTO, winch) → the red lever cranks. Boarding,
   restraint checks, dispatch and drive-reset all move their buttons/levers now.

## Tuning notes

- Lever throw axis/amount lives on the drivers: `ShiftLeverDriver.OnEuler`, `EvacLeverDriver.CrankEuler`
  (default a pitch about local X, matching the existing gate lever). If a lever swings on the wrong
  axis or direction after the first build, adjust these in the inspector and re-build to bake the new
  default into `SceneBuilder`.
- If a new FBX imports at the wrong scale, copy the Scale Factor from `Console.fbx`'s import settings.
- Button press depth/speed: `PressPulse.Depth` / `Speed` on each driver.
