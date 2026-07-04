namespace ParkShift.Core
{
    /// <summary>
    /// A simulated booth operator: runs the load/verify/dispatch cycle and clears
    /// operator-level (minor) faults with human-ish delays. Enabled while the local
    /// player is wearing the Technician hat in solo, so the ride keeps running and
    /// keeps generating major incidents for the tech to fix.
    /// Pure C# — no engine references; drives the sim through the same public verbs
    /// a player would, so multiplayer/server authority is unaffected.
    /// </summary>
    public sealed class AutoOperator
    {
        public bool Enabled;
        public float ActionCooldownSeconds = 0.9f;  // human cadence between booth actions
        public float MinorFixDelaySeconds = 5f;     // reaction time on operator-level faults

        readonly AttractionSim _sim;
        float _cooldown;

        public AutoOperator(AttractionSim sim) { _sim = sim; }

        public void Tick(float dt)
        {
            if (!Enabled) return;
            if (_sim.State == AttractionState.Closed || _sim.State == AttractionState.EndOfShift) return;

            _cooldown -= dt;
            if (_cooldown > 0f) return;

            if (TryFixMinorFault()) { _cooldown = ActionCooldownSeconds; return; }

            var t = _sim.PlatformTrain();
            if (t == null)
            {
                // nothing in the station: keep gates shut while a train comes around
                if (_sim.Guests.GatesOpen) { _sim.SetGates(false); _cooldown = ActionCooldownSeconds; }
                return;
            }

            if (t.State == TrainState.InStationLoading)
            {
                bool wantMore = t.Riders < _sim.Config.SeatsPerTrain && _sim.Guests.QueueCount > 0;
                if (wantMore)
                {
                    if (!_sim.Guests.GatesOpen) { _sim.SetGates(true); _cooldown = ActionCooldownSeconds; return; }
                    if (_sim.LoadPlatformTrain() > 0) { _cooldown = ActionCooldownSeconds; return; }
                }
                if (t.Riders > 0)
                {
                    // full train (or queue exhausted): button up and check restraints
                    if (_sim.Guests.GatesOpen) { _sim.SetGates(false); _cooldown = ActionCooldownSeconds; return; }
                    if (_sim.VerifyRestraints()) { _cooldown = ActionCooldownSeconds; return; }
                }
                return; // empty train, empty queue: wait for guests
            }

            if (t.State == TrainState.ReadyToDispatch)
            {
                if (_sim.Guests.GatesOpen) { _sim.SetGates(false); _cooldown = ActionCooldownSeconds; return; }
                if (_sim.Dispatch()) _cooldown = ActionCooldownSeconds;
            }
        }

        /// <summary>Clears one operator-level fault per action beat (techs handle majors).</summary>
        bool TryFixMinorFault()
        {
            var active = _sim.Faults.Active;
            for (int i = 0; i < active.Count; i++)
            {
                var f = active[i];
                if (f.Def.RequiresTechnician) continue;
                if (_sim.SimTime - f.TriggeredAt < MinorFixDelaySeconds) continue;
                switch (f.Def.Type)
                {
                    case FaultType.GateSensorFault: _sim.CycleGates(); return true;
                    case FaultType.RestraintNotVerified: _sim.InspectFaultySeat(); return true;
                    case FaultType.GuestDroppedItem: _sim.ClearDroppedItem(); return true;
                }
            }
            return false;
        }
    }
}
