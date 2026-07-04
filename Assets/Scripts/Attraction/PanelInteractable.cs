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
                           UnloadDoorOpen, UnloadDoorClose, ReleaseRestraints, HmiScreen }
        public Kind PanelKind;

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
                    if (t != null && t.Riders > 0 && !t.RestraintsVerified) return "Check restraints";
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
                    if (!S.Faults.HasFault(FaultType.MaintenanceResetRequired)) return null;
                    if (!role.CanUseMaintenance) return "Maintenance reset (Technician only — Tab)";
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
                    return S.UnloadAreaClear ? "Close unload door" : "Close door — wait for guests to clear";
                case Kind.ReleaseRestraints:
                    if (S.UnloadingTrain() == null || S.RestraintsReleased) return null;
                    return S.UnloadDoorOpen ? "Release restraints" : "Release restraints — open the door first";
                case Kind.HmiScreen:
                    return UI.HmiPanel.IsOpen ? "Close HMI" : "Read ride HMI / add RVs";
            }
            return null;
        }

        public void Interact(RoleController role)
        {
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
            }
        }
    }
}
