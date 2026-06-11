namespace ParkShift.Core
{
    /// <summary>All tunables in one place. The Unity layer can override via ScriptableObject.</summary>
    public class SimConfig
    {
        public float ShiftLengthSeconds = 180f;     // 3-minute vertical slice
        public int SeatsPerTrain = 8;
        public int BlockCount = 4;                  // 0=Station, 1=Launch, 2=Course, 3=BrakeRun
        public float BlockTraverseSeconds = 6f;     // time to cross one block at full speed
        public float GuestSpawnInterval = 1.6f;     // seconds per guest into queue
        public int QueueCapacity = 40;
        public float GuestBoardSeconds = 0.35f;     // per guest boarding time
        public float DispatchTimeoutSeconds = 35f;  // loitering in station triggers fault
        public float RandomFaultChancePerSecond = 0.022f;
        public float SatisfactionDrainPerQueued = 0.05f;  // per guest per second while waiting
        public float SatisfactionDrainFaulted = 1.2f;     // per second while ride is down
        public float SatisfactionPerRider = 0.8f;         // gain per completed rider
        public float AssistantFixSeconds = 8f;            // "call remote tech" auto-fix delay

        public static SimConfig Solo()
        {
            return new SimConfig
            {
                RandomFaultChancePerSecond = 0.012f,
                GuestSpawnInterval = 2.2f,
                DispatchTimeoutSeconds = 50f,
                SatisfactionDrainFaulted = 0.8f
            };
        }

        public static SimConfig Coop(int playerCount)
        {
            var c = new SimConfig();
            c.RandomFaultChancePerSecond *= 1f + 0.35f * (playerCount - 1);
            c.GuestSpawnInterval /= 1f + 0.2f * (playerCount - 1);
            return c;
        }
    }
}
