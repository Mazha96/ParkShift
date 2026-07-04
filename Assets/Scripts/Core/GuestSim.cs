using System;

namespace ParkShift.Core
{
    /// <summary>Aggregate queue model. Individual visual guests are a Unity-layer concern.
    /// Arrival ramp + surge waves are computed here from sim time (deterministic, no RNG).</summary>
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

        /// <summary>True while a surge window is open (guests arriving at the surge rate).</summary>
        public bool SurgeActive { get; private set; }

        /// <summary>Tick the queue. simTime drives the arrival ramp and surge windows; the default
        /// config (ramp multiplier 1, surges off) leaves the legacy spawn math byte-identical.</summary>
        public void Tick(float dt, bool rideDown, float simTime = 0f)
        {
            float interval = _cfg.GuestSpawnInterval;
            if (_cfg.GuestArrivalRampMultiplier != 1f && _cfg.ShiftLengthSeconds > 0f)
            {
                float p = simTime >= _cfg.ShiftLengthSeconds ? 1f : simTime / _cfg.ShiftLengthSeconds;
                if (p < 0f) p = 0f;
                interval = _cfg.GuestSpawnInterval / (1f + (_cfg.GuestArrivalRampMultiplier - 1f) * p);
            }
            SurgeActive = false;
            if (_cfg.SurgeWavesEnabled && _cfg.SurgeWavePeriodSeconds > 0f)
            {
                // Deterministic time-based windows: a surge opens at the end of every period.
                float phase = simTime % _cfg.SurgeWavePeriodSeconds;
                if (phase >= _cfg.SurgeWavePeriodSeconds - _cfg.SurgeWaveDurationSeconds)
                {
                    SurgeActive = true;
                    interval /= Math.Max(1f, _cfg.SurgeWaveSpawnMultiplier);
                }
            }
            _spawnTimer += dt;
            while (_spawnTimer >= interval && QueueCount < _cfg.QueueCapacity)
            {
                _spawnTimer -= interval;
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
        /// <summary>Smooth-operation reward: a dispatch visibly lifts the crowd's mood.</summary>
        public void RewardDispatch(float amount) { if (amount > 0f) Satisfaction = Clamp(Satisfaction + amount, 0f, 100f); }
        public void Penalize(float amount) { Satisfaction = Clamp(Satisfaction - amount, 0f, 100f); }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
