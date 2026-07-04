namespace ParkShift.Core
{
    public enum AttractionState { Closed, Ready, Faulted, LotoActive, EndOfShift }
    public enum TrainState { InStationUnloading, InStationLoading, ReadyToDispatch, Running, HoldingAtBlock }
    public enum FaultType
    {
        RestraintNotVerified,
        GateSensorFault,
        BlockOccupiedStacking,
        DispatchTimeout,
        GuestDroppedItem,
        MaintenanceResetRequired,
        TrackIntrusion,        // person detected on the track — e-stop, tech escorts them off
        TrainCollision,        // train contact on the brake run — inspect + LOTO + drive reset
        TrainValleyed          // train stalled mid-course — LOTO + evac winch
    }
    public enum FaultSeverity { Minor, Major }
    public enum PlayerRole { Operator, Technician, Manager }
}
