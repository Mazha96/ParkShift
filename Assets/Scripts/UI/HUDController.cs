using UnityEngine;
using UnityEngine.UI;
using ParkShift.Core;
using ParkShift.Attraction;
using ParkShift.Interaction;
using ParkShift.Player;

namespace ParkShift.UI
{
    /// <summary>Builds the whole HUD in code (no scene-serialized UI to maintain).</summary>
    public class HUDController : MonoBehaviour
    {
        Text _management; Image _moodFill;
        Image _holdTrack, _holdFill;
        Text _holdLabel;
        int _holdBeat = -1;
        Text _role, _timer, _score, _satisfaction, _state, _faults, _hmi, _prompt, _tutorial, _streak;
        PlayerInteractor _interactor;
        RoleController _roleCtl;
        ResultsScreen _results;
        bool _resultsShown;   // one-shot guard: OnShiftEnded (solo/host) and the client mirror path

        // ---- HUD juice state ----
        float _shownScore = -1f;      // displayed score eases toward the real one (no instant jumps)
        int _lastStreak;
        float _streakPop;             // 1 -> 0 scale-punch on streak increment
        float _streakLost;            // brief "STREAK LOST" flash when a combo breaks
        int _lastFaultCount;
        float _bannerT = 1f;          // 0 -> 1 slide-in of the fault banner
        float _promptShake;           // 1 -> 0 shake on a blocked/refused interaction
        Vector2 _faultsBasePos, _promptBasePos;


        public void Bind(PlayerInteractor interactor, RoleController role)
        { _interactor = interactor; _roleCtl = role; }

        void Start()
        {
            var canvasGO = new GameObject("HUDCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGO.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1600, 900);

            ComicUI.Card(canvasGO, "ShiftHeader", new Vector2(0.008f, 0.85f), new Vector2(0.992f, 0.988f), new Color(0.055f, 0.075f, 0.15f, 0.92f));
            ComicUI.Card(canvasGO, "Instructions", new Vector2(0.008f, 0.008f), new Vector2(0.992f, 0.065f), new Color(0.055f, 0.075f, 0.15f, 0.92f));
            var mood = ComicUI.Card(canvasGO, "GuestMood", new Vector2(0.77f, 0.90f), new Vector2(0.98f, 0.907f), ComicUI.Ink);
            _moodFill = ComicUI.Card(mood.gameObject, "Fill", Vector2.zero, Vector2.one, ComicUI.Gold);
            _management = MakeText(canvasGO, "Management", new Vector2(20, -170), TextAnchor.UpperLeft, 21);
            _role         = MakeText(canvasGO, "Role", new Vector2(20, -20), TextAnchor.UpperLeft, 26);
            _timer        = MakeText(canvasGO, "Timer", new Vector2(0, -20), TextAnchor.UpperCenter, 34);
            _score        = MakeText(canvasGO, "Score", new Vector2(-20, -20), TextAnchor.UpperRight, 26);
            _satisfaction = MakeText(canvasGO, "Satisfaction", new Vector2(-20, -55), TextAnchor.UpperRight, 22);
            _streak       = MakeText(canvasGO, "Streak", new Vector2(-20, -92), TextAnchor.UpperRight, 30);
            _state        = MakeText(canvasGO, "State", new Vector2(0, -60), TextAnchor.UpperCenter, 20);
            _faults       = MakeText(canvasGO, "Faults", new Vector2(20, -290), TextAnchor.UpperLeft, 20);
            _hmi          = MakeText(canvasGO, "HMI", new Vector2(20, 110), TextAnchor.LowerLeft, 20);
            _prompt       = MakeText(canvasGO, "Prompt", new Vector2(0, 200), TextAnchor.LowerCenter, 26);
            _tutorial     = MakeText(canvasGO, "Tutorial", new Vector2(0, 30), TextAnchor.LowerCenter, 20);
            _holdTrack = ComicUI.Card(canvasGO, "RepairProgress", new Vector2(0.37f, 0.185f), new Vector2(0.63f, 0.197f), ComicUI.Ink);
            _holdFill = ComicUI.Card(_holdTrack.gameObject, "Progress", Vector2.zero, Vector2.one, ComicUI.Gold);
            _holdLabel = MakeText(canvasGO, "RepairAction", new Vector2(0, 110), TextAnchor.LowerCenter, 19);
            _holdLabel.rectTransform.sizeDelta = new Vector2(720, 52);

            // crosshair
            var ch = MakeText(canvasGO, "Crosshair", Vector2.zero, TextAnchor.MiddleCenter, 24);
            ch.text = "+"; ch.color = new Color(1, 1, 1, 0.6f);

            _hmi.rectTransform.sizeDelta = new Vector2(570, 160);
            _hmi.horizontalOverflow = HorizontalWrapMode.Wrap;
            _faults.rectTransform.sizeDelta = new Vector2(550, 250);
            _faults.horizontalOverflow = HorizontalWrapMode.Wrap;
            _prompt.rectTransform.sizeDelta = new Vector2(1200, 60);
            _tutorial.rectTransform.sizeDelta = new Vector2(1520, 50);
            _tutorial.fontSize = 19;
            _tutorial.horizontalOverflow = HorizontalWrapMode.Wrap;
            _faultsBasePos = _faults.rectTransform.anchoredPosition;
            _promptBasePos = _prompt.rectTransform.anchoredPosition;

            _results = gameObject.AddComponent<ResultsScreen>();
            _results.Build(canvasGO);
            AttractionManager.Instance.Sim.OnShiftEnded += () =>
            {
                if (_resultsShown) return;
                _resultsShown = true;
                _results.Show(AttractionManager.Instance.Sim);
            };
        }

        Text MakeText(GameObject parent, string name, Vector2 offset, TextAnchor anchor, int size)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent.transform, false);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = size; t.alignment = anchor; t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = t.rectTransform;
            // anchor mapping from TextAnchor for a simple corner layout
            Vector2 a = anchor == TextAnchor.UpperLeft ? new Vector2(0, 1)
                      : anchor == TextAnchor.UpperRight ? new Vector2(1, 1)
                      : anchor == TextAnchor.UpperCenter ? new Vector2(0.5f, 1)
                      : anchor == TextAnchor.LowerLeft ? new Vector2(0, 0)
                      : anchor == TextAnchor.LowerCenter ? new Vector2(0.5f, 0)
                      : new Vector2(0.5f, 0.5f);
            rt.anchorMin = rt.anchorMax = rt.pivot = a;
            rt.anchoredPosition = offset;
            rt.sizeDelta = new Vector2(700, 60);
            ComicUI.Letter(t);
            return t;
        }

        void Update()
        {
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null) return;
            var s = m.Sim;

            _role.text = $"ROLE: {(_roleCtl ? _roleCtl.Role.ToString().ToUpper() : "?")}  (Tab)"
                       + (m.AutoOpActive ? "\n<color=#7fd4ff>AUTO-OP COVERING THE BOOTH</color>" : "");
            // Shift clock: solo/host read the local (authoritative) sim. On a non-authoritative
            // client the local sim is never ticked, so the clock and EndOfShift come from the
            // replicated snapshot instead. AttractionNetSync only exists under FISHNET, so the
            // usage is guarded identically; solo (no netsync instance) falls back safely.
            float shiftRemaining = s.ShiftRemaining;
#if FISHNET
            var net = Networking.AttractionNetSync.Instance;
            if (net != null && !m.IsAuthoritative)
            {
                shiftRemaining = net.MirrorShiftRemaining;
                if (!_resultsShown && net.MirrorState == AttractionState.EndOfShift)
                {
                    _resultsShown = true;
                    _results.Show(s);   // mirror sim: score/streak/satisfaction are replicated
                }
            }
#endif
            _timer.text = $"{Mathf.FloorToInt(shiftRemaining / 60f):0}:{Mathf.FloorToInt(shiftRemaining % 60f):00}";

            // Score ticks up smoothly instead of snapping — rate scales with the gap so big
            // combo payouts visibly "count up" but never lag behind for long.
            int actualScore = s.Score.Compute(s.Guests.Satisfaction);
            if (_shownScore < 0f) _shownScore = actualScore;
            float tickRate = Mathf.Max(25f, Mathf.Abs(actualScore - _shownScore) * 4f);
            _shownScore = Mathf.MoveTowards(_shownScore, actualScore, tickRate * Time.deltaTime);
            _score.text = $"SCORE {Mathf.RoundToInt(_shownScore)}  |  RIDERS {s.Score.RidersServed}";
            _satisfaction.text = $"GUEST SATISFACTION {s.Guests.Satisfaction:0}%   QUEUE {s.Guests.QueueCount}";
            _state.text = $"COSMIC CONVEYOR — {s.State}" + (s.LotoEngaged ? "  [LOTO]" : "");

            _moodFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(s.Guests.Satisfaction / 100f), 1);
            _moodFill.color = s.Guests.Satisfaction < 35 ? new Color(1f, 0.3f, 0.25f) : ComicUI.Gold;
            bool manager = _roleCtl != null && _roleCtl.Role == PlayerRole.Manager;
            _management.text = manager ? $"PARK OFFICE  |  ${s.Management.Budget}\n[1] Admissions: {(s.Management.AdmissionsPaused ? "PAUSED" : "OPEN")}\n[2] Guest care $40 / +18 mood  |  {(s.Management.GuestCareCooldownRemaining > 0 ? s.Management.GuestCareCooldownRemaining.ToString("0") + "s cooldown" : "Ready")}" : "";
            if (manager && Cursor.lockState == CursorLockMode.Locked && !HmiPanel.IsOpen && s.State != AttractionState.EndOfShift)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1)) ManagementAction(m, OpVerb.ToggleAdmissions);
                if (Input.GetKeyDown(KeyCode.Alpha2)) ManagementAction(m, OpVerb.GuestCare);
            }
            UpdateStreak(s);

            // Fault banner: slides in from the left on a new fault and pulses alarm-red while
            // any fault is active. (Rich-text colored header keeps its own tint.)
            int faultCount = s.Faults.Active.Count;
            if (faultCount > _lastFaultCount) _bannerT = 0f;
            _lastFaultCount = faultCount;
            _bannerT = Mathf.Min(1f, _bannerT + Time.deltaTime * 3.2f);
            float slide = 1f - Mathf.Pow(1f - _bannerT, 3f);   // ease-out cubic
            _faults.rectTransform.anchoredPosition = _faultsBasePos + new Vector2(-340f * (1f - slide), 0f);
            _faults.color = ComicUI.Paper;

            ActiveFault focus = PriorityFault(s);
            if (faultCount == 0) _faults.text = "";
            else
            {
                var sb = new System.Text.StringBuilder("<color=#ff8a65>CREW INCIDENT  /  " + faultCount + " ACTIVE</color>\n");
                sb.AppendLine(focus.HmiText);
                sb.AppendLine("<color=#ffd34d>" + FaultLocation(focus) + "  •  " + ResponseTime(focus, s.SimTime) + "</color>");
                sb.AppendLine(FaultStep(focus, s));
                if (faultCount > 1) sb.Append("+ " + (faultCount - 1) + " more incident(s) — details at HMI");
                _faults.text = sb.ToString();
            }

            string why;
            bool can = s.CanDispatch(out why);
            string hmi = can ? "<color=#44ff66>HMI: READY TO DISPATCH</color>"
                             : $"HMI: {why ?? "—"}";
            if (s.Faults.Active.Count > 0)
                hmi += "\nFIX: " + focus.Def.FixHint;
            if (m.AssistantRemaining > 0) hmi += $"\nRemote tech en route... {m.AssistantRemaining:0.0}s";
            if (!string.IsNullOrEmpty(m.LastCommandStatus)) hmi += "\n" + m.LastCommandStatus;
            _hmi.text = hmi;

            _prompt.text = _interactor != null && _interactor.CurrentPrompt != null ? _interactor.CurrentPrompt : "";
            UpdateHoldFeedback();

            // Blocked-interaction feedback: if the player mashes E on a refused/blocked prompt,
            // the prompt shakes side-to-side for a beat (paired with the refuse buzz).
            if (Input.GetKeyDown(KeyCode.E) && _prompt.text.Length > 0)
            {
                string lp = _prompt.text.ToLowerInvariant();
                if (lp.Contains("blocked") || lp.Contains("refused") || lp.Contains("only")
                    || lp.Contains("wait for") || lp.Contains("first"))
                    _promptShake = 1f;
            }
            _promptShake = Mathf.MoveTowards(_promptShake, 0f, 2.6f * Time.deltaTime);
            _prompt.rectTransform.anchoredPosition = _promptBasePos
                + new Vector2(Mathf.Sin(_promptShake * 42f) * 10f * _promptShake, 0f);

            _tutorial.text = "<color=#ffd34d>" + NextObjective(s) + "</color>";
        }

        string NextObjective(AttractionSim sim)
        {
            if (sim.State == AttractionState.Closed) return "CLOCK IN  /  Find SHIFT START in the booth.   •   E interact   TAB role   SHIFT sprint   SPACE jump";
            if (sim.State == AttractionState.EndOfShift) return "SHIFT COMPLETE  /  The crew's results are ready.";
            if (sim.Faults.Active.Count > 0)
            {
                var fault = PriorityFault(sim);
                return FaultLocation(fault) + "  /  " + FaultStep(fault, sim) + "   •   HOLD E to work   TAB role   H remote help";
            }
            if (_roleCtl != null && _roleCtl.Role == PlayerRole.Manager)
                return sim.Management.AdmissionsPaused ? "ADMISSIONS PAUSED  /  Clear the queue, then press 1 to welcome more guests.   •   2 guest care   TAB role"
                    : "KEEP THE CROWD HAPPY  /  1 pause arrivals   2 guest care ($40)   TAB role   H remote help";
            if (_roleCtl != null && _roleCtl.Role == PlayerRole.Technician)
                return "TECH ON DUTY  /  Watch for incidents and keep the maintenance bay accessible.   •   TAB role   SHIFT sprint";
            if (sim.Config.SeparateLoadUnload && sim.UnloadingTrain() != null && !sim.UnloadAreaClear)
                return !sim.UnloadDoorOpen ? "UNLOAD BERTH  /  Open the unload door for returning guests."
                    : !sim.RestraintsReleased ? "UNLOAD BERTH  /  Release restraints, then let everyone exit."
                    : "UNLOAD BERTH  /  Wait for the guests to clear the train.";
            if (sim.UnloadDoorOpen) return "UNLOAD BERTH  /  Close the unload door before dispatch.";
            var train = sim.PlatformTrain();
            if (train == null) return "KEEP THE PLATFORM CLEAR  /  The next train is on its way.   •   TAB role   SHIFT sprint";
            if (train.Riders == 0) return sim.Guests.GatesOpen ? "LOAD BERTH  /  Wave guests aboard at the platform."
                : "LOAD BERTH  /  Open the gates to welcome the next riders.";
            if (sim.Guests.GatesOpen) return "BUTTON UP  /  Finish loading, then close the gates.";
            if (!train.RestraintsVerified) return "RESTRAINT CHECK  /  Verify the riders before dispatch.";
            return "READY TO ROLL  /  Dispatch when the next block is clear.   •   E interact   TAB role   SHIFT sprint";
        }

        void UpdateHoldFeedback()
        {
            bool holding = _interactor != null && _interactor.IsHolding;
            _holdTrack.gameObject.SetActive(holding);
            _holdLabel.gameObject.SetActive(holding);
            if (!holding) { _holdBeat = -1; return; }
            float progress = Mathf.Clamp01(_interactor.HoldProgress);
            _holdFill.rectTransform.anchorMax = new Vector2(progress, 1f);
            _holdLabel.text = _interactor.CurrentActionLabel + "  " + Mathf.FloorToInt(progress * 100f) + "%\nKeep looking • release E to cancel";
            int beat = Mathf.Min(3, Mathf.FloorToInt(progress * 4f));
            if (beat > _holdBeat) Juice.AudioDirector.RepairTick(beat);
            _holdBeat = beat;
            // Reaching 100% submits the action; acceptance is reported by the host separately.
        }

        static ActiveFault PriorityFault(AttractionSim sim)
        {
            ActiveFault chosen = null;
            foreach (var fault in sim.Faults.Active)
            {
                if (chosen == null || (fault.DeadlineAt > 0f && (chosen.DeadlineAt <= 0f || fault.DeadlineAt < chosen.DeadlineAt)))
                    chosen = fault;
            }
            return chosen;
        }

        static string ResponseTime(ActiveFault fault, float simTime)
        {
            if (fault.DeadlineAt <= 0f) return "REPAIR REQUIRED";
            if (fault.DeadlineMissed || fault.ResponseSecondsRemaining(simTime) <= 0f) return "OVERDUE — STILL REPAIRABLE";
            return "RESPOND IN " + Mathf.CeilToInt(fault.ResponseSecondsRemaining(simTime)) + "s";
        }

        static string FaultLocation(ActiveFault fault)
        {
            switch (fault.Def.Type)
            {
                case FaultType.GuestDroppedItem: return "STATION EDGE";
                case FaultType.RestraintNotVerified: return "LOAD BERTH / FLAGGED SEAT";
                case FaultType.RestraintJammedCar: return "LOAD BERTH / CAR " + (fault.TargetIndex + 1);
                case FaultType.TrackIntrusion: return "TRACK / ZONE 1";
                case FaultType.TrainCollision: return fault.Inspected ? "MAINTENANCE BAY" : "BRAKE RUN / CONTACT POINT";
                case FaultType.TrainValleyed: return "COURSE / EVAC WINCH";
                case FaultType.BlockSensorGhost: return fault.Inspected ? "DISPATCH BOOTH" : "COURSE / ZONE 2 SENSOR";
                case FaultType.PowerBrownout: return "MAINTENANCE / DRIVE CABINET";
                case FaultType.MaintenanceResetRequired: return "MAINTENANCE BAY";
                default: return "DISPATCH BOOTH";
            }
        }

        static string FaultStep(ActiveFault fault, AttractionSim sim)
        {
            if (fault.Def.RequiresTechnician && Player.RoleController.Local != null
                && !Player.RoleController.Local.CanUseMaintenance) return "Switch to Technician or Manager (TAB).";
            if (fault.Def.Type == FaultType.TrainCollision && !fault.Inspected) return "Inspect the train contact point.";
            if (fault.Def.RequiresLoto && !sim.LotoEngaged) return "Engage the LOTO lever before repair.";
            switch (fault.Def.Type)
            {
                case FaultType.PowerBrownout: return "Restart stage " + (fault.Stage + 1) + " / " + sim.Config.BrownoutStageCount + "; allow the bus to stabilize.";
                case FaultType.BlockSensorGhost: return fault.Inspected ? "Block checked. Reset the sensor at dispatch." : "Inspect zone 2 and confirm the block is clear.";
                case FaultType.RestraintNotVerified: return fault.Inspected ? "Seat inspected. Re-verify restraints." : "Inspect the flagged seat, then re-verify.";
                case FaultType.TrainValleyed: return "Operate the recovery winch; release LOTO after repair.";
                case FaultType.MaintenanceResetRequired:
                case FaultType.TrainCollision: return "Reset the drive; release LOTO after repair.";
                case FaultType.TrackIntrusion: return "Escort the guest to safety.";
                case FaultType.GuestDroppedItem: return "Collect the dropped item beside the station.";
                case FaultType.RestraintJammedCar: return "Free the flagged lap bar, then re-verify.";
                case FaultType.GateSensorFault: return "Cycle the gates to re-seat the sensor.";
                default: return "Service and dispatch the platform train.";
            }
        }

        void ManagementAction(AttractionManager manager, OpVerb verb)
        {
            manager.Submit(verb);
        }

        /// <summary>Streak counter pops/scales on every increment and flashes when a combo breaks.</summary>
        void UpdateStreak(AttractionSim s)
        {
            int streak = s.Score.CurrentStreak;
            if (streak > _lastStreak) _streakPop = 1f;
            if (streak == 0 && _lastStreak >= 2) _streakLost = 1.2f;
            _lastStreak = streak;

            _streakPop = Mathf.MoveTowards(_streakPop, 0f, 3f * Time.deltaTime);
            _streakLost = Mathf.MoveTowards(_streakLost, 0f, Time.deltaTime);
            float pop = _streakPop * _streakPop;                       // ease the punch
            _streak.rectTransform.localScale = Vector3.one * (1f + 0.5f * pop);

            if (streak >= 2)
            {
                _streak.text = "STREAK x" + streak;
                _streak.color = streak >= 5
                    ? Color.Lerp(new Color(1f, 0.55f, 0.15f), Color.white, pop)   // hot
                    : Color.Lerp(new Color(1f, 0.83f, 0.3f), Color.white, pop);   // warming up
            }
            else if (_streakLost > 0f)
            {
                _streak.text = "STREAK LOST";
                _streak.color = new Color(1f, 0.35f, 0.3f, Mathf.Clamp01(_streakLost));
            }
            else _streak.text = "";
        }
    }
}



