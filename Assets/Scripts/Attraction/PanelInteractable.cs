using UnityEngine;
using ParkShift.Core;
using ParkShift.Interaction;
using ParkShift.Player;

namespace ParkShift.Attraction
{
    /// <summary>One configurable interactable covers every panel/lever/button in the slice.
    /// Keeps the scene builder simple and avoids a class per prop.</summary>
    public class PanelInteractable : MonoBehaviour, IInteractable
    {
        public enum Kind { ShiftStart, GateLever, LoadHelper, RestraintCheck, DispatchButton,
                           SeatInspect, DroppedItem, LotoLever, MaintenancePanel, AssistantPhone,
                           IntruderEscort, CollisionInspect, WinchPanel,
                           UnloadDoorOpen, UnloadDoorClose, ReleaseRestraints, HmiScreen,
                           BlockVerify, SensorReset, BrownoutStage, LapBarUnjam }
        public Kind PanelKind;
        [Tooltip("LapBarUnjam only: which car's lap bar this interactable frees (0-based).")]
        public int CarIndex;

        AttractionManager M => AttractionManager.Instance;
        AttractionSim S => M.Sim;

        public string GetPrompt(RoleController role)
        {
            if (M == null || S == null) return null;
            switch (PanelKind)
            {
                case Kind.ShiftStart:
                    return S.State == AttractionState.Closed ? "Open the shift" : null;
                case Kind.GateLever:
                    if (S.Faults.HasFault(FaultType.GateSensorFault)) return "Cycle gates (re-seat sensor)";
                    return S.Guests.GatesOpen ? "Close gates" : "Open gates";
                case Kind.LoadHelper:
                    return S.Guests.GatesOpen ? "Wave guests aboard" : null;
                case Kind.RestraintCheck:
                {
                    var t = S.PlatformTrain();
                    if (t != null && t.Riders > 0 && !t.RestraintsVerified)
                    {
                        if (S.Guests.GatesOpen) return "Close boarding gates before checking bars";
                        if (S.Faults.HasFault(FaultType.RestraintJammedCar)) return "Free the flagged lap bar first";
                        if (S.Faults.HasFault(FaultType.RestraintNotVerified)) return "Inspect the flagged seat first";
                        return "Tug lap bars and verify restraints";
                    }
                    return null;
                }
                case Kind.DispatchButton:
                {
                    string why;
                    return S.CanDispatch(out why) ? "DISPATCH" : $"Dispatch blocked: {why}";
                }
                case Kind.SeatInspect:
                    return S.Faults.HasFault(FaultType.RestraintNotVerified) ? "Inspect flagged seat" : null;
                case Kind.DroppedItem:
                    return S.Faults.HasFault(FaultType.GuestDroppedItem) ? "Pick up guest item" : null;
                case Kind.LotoLever:
                    if (!role.CanUseMaintenance) return "LOTO lever (Technician only — Tab to switch)";
                    return S.LotoEngaged ? "Release LOTO + remove tag" : "Pull LOTO lever + hang tag";
                case Kind.MaintenancePanel:
                    if (!S.Faults.HasFault(FaultType.MaintenanceResetRequired) && !S.Faults.HasFault(FaultType.TrainCollision)) return null;
                    if (!role.CanUseMaintenance) return "Maintenance reset (Technician only — Tab)";
                    var collision = S.Faults.Find(FaultType.TrainCollision);
                    if (collision != null && !collision.Inspected) return "Inspect the collision contact point first";
                    return S.LotoEngaged ? "Reset drive controller" : "Reset refused — LOTO required";
                case Kind.AssistantPhone:
                    return S.Faults.Active.Count > 0 && M.AssistantRemaining <= 0f
                        ? "Call remote tech (auto-fix, score penalty)" : null;
                case Kind.IntruderEscort:
                    if (!S.Faults.HasFault(FaultType.TrackIntrusion)) return null;
                    return role.CanUseMaintenance ? "Escort guest off the track"
                                                  : "Guest on track (Technician only — Tab)";
                case Kind.CollisionInspect:
                    if (!S.Faults.HasFault(FaultType.TrainCollision)) return null;
                    return role.CanUseMaintenance ? "Inspect collision contact point"
                                                  : "Collision (Technician only — Tab)";
                case Kind.WinchPanel:
                    if (!S.Faults.HasFault(FaultType.TrainValleyed)) return null;
                    if (!role.CanUseMaintenance) return "Evac winch (Technician only — Tab)";
                    return S.LotoEngaged ? "Crank evac winch" : "Winch refused — engage LOTO first";
                case Kind.UnloadDoorOpen:
                    if (S.UnloadingTrain() == null) return null;
                    return S.UnloadDoorOpen ? null : "Open unload door";
                case Kind.UnloadDoorClose:
                    if (!S.UnloadDoorOpen) return null;
                    if (!S.UnloadAreaClear) return "Wait for guests to clear the exit";
                    return UnloadDoorDriver.Instance != null && !UnloadDoorDriver.Instance.CanClose
                        ? "Doorway occupied — stand clear" : "Close unload door";
                case Kind.ReleaseRestraints:
                    if (S.UnloadingTrain() == null || S.RestraintsReleased) return null;
                    if (!S.UnloadDoorOpen) return "Release restraints — open the door first";
                    return UnloadDoorDriver.Instance != null && !UnloadDoorDriver.Instance.IsFullyOpen
                        ? "Wait for the unload door to finish opening" : "Release restraints";
                case Kind.HmiScreen:
                    return UI.HmiPanel.IsOpen ? "Close HMI" : "Read ride HMI / add RVs";
                case Kind.BlockVerify:
                {
                    var f = S.Faults.Find(FaultType.BlockSensorGhost);
                    if (f == null) return null;
                    return f.Inspected ? "Zone verified — reset the sensor at the booth"
                                       : "Verify block clear (visual check)";
                }
                case Kind.SensorReset:
                {
                    var f = S.Faults.Find(FaultType.BlockSensorGhost);
                    if (f == null) return null;
                    return f.Inspected ? "Reset zone sensor"
                                       : "Reset refused — verify the block on foot first";
                }
                case Kind.BrownoutStage:
                {
                    var f = S.Faults.Find(FaultType.PowerBrownout);
                    if (f == null) return null;
                    if (!role.CanUseMaintenance) return "Drive bus restart (Technician only — Tab)";
                    return $"Energize bus stage {Mathf.Min(f.Stage + 1, S.Config.BrownoutStageCount)}/{S.Config.BrownoutStageCount}";
                }
                case Kind.LapBarUnjam:
                    // Same prompt on every car — the HMI names the flagged one; wrong cars are refused.
                    return S.Faults.HasFault(FaultType.RestraintJammedCar)
                        ? $"Check car {CarIndex + 1} lap bar" : null;
            }
            return null;
        }

        public float HoldSeconds(RoleController role)
        {
            if (M == null || role == null) return 0f;
            switch (PanelKind)
            {
                case Kind.RestraintCheck:
                    return !S.Guests.GatesOpen && !S.Faults.HasFault(FaultType.RestraintNotVerified)
                        && !S.Faults.HasFault(FaultType.RestraintJammedCar) ? 1.2f : 0f;
                case Kind.SeatInspect: case Kind.BlockVerify: return 1.5f;
                case Kind.DroppedItem: return 0.65f;
                case Kind.LapBarUnjam: return 1.8f;
                case Kind.IntruderEscort: case Kind.CollisionInspect:
                    return role.CanUseMaintenance ? 2f : 0f;
                case Kind.MaintenancePanel: case Kind.WinchPanel:
                    return role.CanUseMaintenance && S.LotoEngaged ? 2f : 0f;
                case Kind.BrownoutStage: return role.CanUseMaintenance ? 1f : 0f;
                default: return 0f;
            }
        }

        public void Interact(RoleController role)
        {
            if (M == null || role == null || string.IsNullOrEmpty(GetPrompt(role))) return;
            switch (PanelKind)
            {
                case Kind.ShiftStart: M.Submit(OpVerb.OpenShift); break;
                case Kind.GateLever:
                    if (S.Faults.HasFault(FaultType.GateSensorFault)) M.Submit(OpVerb.CycleGates);
                    else M.Submit(OpVerb.ToggleGates);
                    break;
                case Kind.LoadHelper: M.Submit(OpVerb.LoadTrain); break;
                case Kind.RestraintCheck: M.Submit(OpVerb.VerifyRestraints); break;
                case Kind.DispatchButton: M.Submit(OpVerb.Dispatch); break;
                case Kind.SeatInspect: M.Submit(OpVerb.InspectSeat); break;
                case Kind.DroppedItem: M.Submit(OpVerb.ClearItem); break;
                case Kind.LotoLever: if (role.CanUseMaintenance) M.Submit(OpVerb.ToggleLoto); break;
                case Kind.MaintenancePanel: if (role.CanUseMaintenance) M.Submit(OpVerb.MaintenanceReset); break;
                case Kind.AssistantPhone: M.Submit(OpVerb.CallAssistant); break;
                case Kind.IntruderEscort: if (role.CanUseMaintenance) M.Submit(OpVerb.EscortIntruder); break;
                case Kind.CollisionInspect: if (role.CanUseMaintenance) M.Submit(OpVerb.InspectCollision); break;
                case Kind.WinchPanel: if (role.CanUseMaintenance) M.Submit(OpVerb.WinchTrain); break;
                case Kind.UnloadDoorOpen: M.Submit(OpVerb.OpenUnloadDoor); break;
                case Kind.UnloadDoorClose: M.Submit(OpVerb.CloseUnloadDoor); break;
                case Kind.ReleaseRestraints: M.Submit(OpVerb.ReleaseRestraints); break;
                case Kind.HmiScreen: UI.HmiPanel.Toggle(); break;   // client-side UI, not a sim verb
                case Kind.BlockVerify: M.Submit(OpVerb.VerifyBlockClear); break;
                case Kind.SensorReset: M.Submit(OpVerb.ResetBlockSensor); break;
                case Kind.BrownoutStage: if (role.CanUseMaintenance) M.Submit(OpVerb.AdvanceBrownoutStage); break;
                case Kind.LapBarUnjam:
                    M.Submit(CarIndex <= 0 ? OpVerb.UnjamCar0
                           : CarIndex == 1 ? OpVerb.UnjamCar1
                           : CarIndex == 2 ? OpVerb.UnjamCar2 : OpVerb.UnjamCar3);
                    break;
            }
        }
    }
}
