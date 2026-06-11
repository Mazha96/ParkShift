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
        MaintenanceResetRequired
    }
    public enum FaultSeverity { Minor, Major }
    public enum PlayerRole { Operator, Technician, Manager }
}
