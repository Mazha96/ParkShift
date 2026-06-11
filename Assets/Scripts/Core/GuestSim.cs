using System;

namespace ParkShift.Core
{
    /// <summary>Aggregate queue model. Individual visual guests are a Unity-layer concern.</summary>
    public sealed class GuestSim
    {
        public int QueueCount { get; private set; }
        public float Satisfaction { get; private set; } = 100f;
        public bool GatesOpen { get; private set; }
        public int TotalSpawned { get; private set; }

        float _spawnTimer;
        readonly SimConfig _cfg;
        public event Action OnGuestSpawned;
        public event Action<int> OnGuestsBoarded;

        public GuestSim(SimConfig cfg) { _cfg = cfg; }

        public void SetGates(bool open) { GatesOpen = open; }

        public void Tick(float dt, bool rideDown)
        {
            _spawnTimer += dt;
            while (_spawnTimer >= _cfg.GuestSpawnInterval && QueueCount < _cfg.QueueCapacity)
            {
                _spawnTimer -= _cfg.GuestSpawnInterval;
                QueueCount++; TotalSpawned++;
                OnGuestSpawned?.Invoke();
            }
            Satisfaction -= QueueCount * _cfg.SatisfactionDrainPerQueued * dt;
            if (rideDown) Satisfaction -= _cfg.SatisfactionDrainFaulted * dt;
            Satisfaction = Clamp(Satisfaction, 0f, 100f);
        }

        /// <summary>Board up to 'seats' guests if gates are open. Returns boarded count.</summary>
        public int Board(int seats)
        {
            if (!GatesOpen || QueueCount == 0) return 0;
            int n = Math.Min(seats, QueueCount);
            QueueCount -= n;
            OnGuestsBoarded?.Invoke(n);
            return n;
        }

        public void RewardRiders(int riders) { Satisfaction = Clamp(Satisfaction + riders * _cfg.SatisfactionPerRider, 0f, 100f); }
        public void Penalize(float amount) { Satisfaction = Clamp(Satisfaction - amount, 0f, 100f); }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
