using System;

namespace ParkShift.Core
{
    /// <summary>Shift resources and guest care. Never changes ride safety interlocks.</summary>
    public sealed class ManagementSim
    {
        public const int GuestCareCost = 40;
        public const float GuestCareCooldownSeconds = 30f;
        public int Budget { get; private set; } = 120;
        public int Revenue { get; private set; }
        public int GuestCareCount { get; private set; }
        public bool AdmissionsPaused { get; private set; }
        public float GuestCareCooldownRemaining { get; private set; }
        readonly AttractionSim _sim;

        public ManagementSim(AttractionSim sim) { _sim = sim; }
        bool ShiftActive => _sim.State != AttractionState.Closed && _sim.State != AttractionState.EndOfShift;

        public bool ToggleAdmissions()
        {
            if (!ShiftActive) return false;
            AdmissionsPaused = !AdmissionsPaused;
            _sim.Telemetry.Log(_sim.SimTime, "MANAGER", AdmissionsPaused ? "Admissions paused; existing queue remains" : "Admissions resumed");
            return true;
        }

        public bool CanOfferGuestCare(out string reason)
        {
            reason = null;
            if (!ShiftActive) reason = "Open a shift first";
            else if (GuestCareCooldownRemaining > 0f) reason = "Guest care team is busy";
            else if (Budget < GuestCareCost) reason = "Need 40 budget";
            else if (_sim.Guests.Satisfaction >= 99f) reason = "Guests are already happy";
            return reason == null;
        }

        public bool OfferGuestCare()
        {
            if (!CanOfferGuestCare(out _)) return false;
            Budget -= GuestCareCost;
            GuestCareCount++;
            GuestCareCooldownRemaining = GuestCareCooldownSeconds;
            _sim.Guests.RewardDispatch(18f);
            _sim.Telemetry.Log(_sim.SimTime, "MANAGER", "Guest care: refreshments and queue hosts (+18 mood, -40 budget)");
            return true;
        }

        internal void EarnRevenue(int riders)
        {
            int income = Math.Max(0, riders) * 5;
            Budget += income;
            Revenue += income;
        }

        /// <summary>Presentation-only snapshot; called on non-authoritative clients.</summary>
        public void MirrorState(int budget, int revenue, int careCount, bool paused, float cooldown)
        {
            Budget = Math.Max(0, budget);
            Revenue = Math.Max(0, revenue);
            GuestCareCount = Math.Max(0, careCount);
            AdmissionsPaused = paused;
            GuestCareCooldownRemaining = Math.Max(0f, cooldown);
        }

        internal void Tick(float dt)
        {
            GuestCareCooldownRemaining = Math.Max(0f, GuestCareCooldownRemaining - dt);
        }
    }
}

