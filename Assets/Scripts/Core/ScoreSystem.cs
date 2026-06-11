namespace ParkShift.Core
{
    public sealed class ScoreSystem
    {
        public int RidersServed { get; private set; }
        public int Dispatches { get; private set; }
        public int FaultsResolved { get; private set; }
        public float DowntimeSeconds { get; private set; }
        public int PenaltyPoints { get; private set; }

        public void AddRiders(int n) { RidersServed += n; }
        public void AddDispatch() { Dispatches++; }
        public void AddFaultResolved() { FaultsResolved++; }
        public void AddDowntime(float dt) { DowntimeSeconds += dt; }
        public void AddPenalty(int pts) { PenaltyPoints += pts; }

        public int Compute(float finalSatisfaction)
        {
            int score = RidersServed * 10
                      + FaultsResolved * 25
                      + (int)(finalSatisfaction * 5f)
                      - (int)(DowntimeSeconds * 2f)
                      - PenaltyPoints;
            return score < 0 ? 0 : score;
        }

        public string Grade(int score)
        {
            if (score >= 900) return "S — Shift Legend";
            if (score >= 700) return "A — Crew Lead Material";
            if (score >= 500) return "B — Solid Shift";
            if (score >= 300) return "C — Rough Day";
            if (score >= 150) return "D — The Queue Remembers";
            return "F — Park Reputation Damaged";
        }
    }
}
