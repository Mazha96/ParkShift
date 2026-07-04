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
        Text _role, _timer, _score, _satisfaction, _state, _faults, _hmi, _prompt, _tutorial, _streak;
        PlayerInteractor _interactor;
        RoleController _roleCtl;
        ResultsScreen _results;
        int _tutorialStep;
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

        static readonly string[] TutorialSteps =
        {
            "1) Find the booth and OPEN THE SHIFT",
            "2) Pull the GATE LEVER to let guests in",
            "3) Wave guests aboard at the platform",
            "4) Close gates, then CHECK RESTRAINTS",
            "5) Hit DISPATCH. Keep both trains moving!",
            "Faults will happen. Read the HMI. Tab switches role. H calls remote help."
        };

        public void Bind(PlayerInteractor interactor, RoleController role)
        { _interactor = interactor; _roleCtl = role; }

        void Start()
        {
            var canvasGO = new GameObject("HUDCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGO.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1600, 900);

            _role         = MakeText(canvasGO, "Role", new Vector2(20, -20), TextAnchor.UpperLeft, 26);
            _timer        = MakeText(canvasGO, "Timer", new Vector2(0, -20), TextAnchor.UpperCenter, 34);
            _score        = MakeText(canvasGO, "Score", new Vector2(-20, -20), TextAnchor.UpperRight, 26);
            _satisfaction = MakeText(canvasGO, "Satisfaction", new Vector2(-20, -55), TextAnchor.UpperRight, 22);
            _streak       = MakeText(canvasGO, "Streak", new Vector2(-20, -92), TextAnchor.UpperRight, 30);
            _state        = MakeText(canvasGO, "State", new Vector2(0, -60), TextAnchor.UpperCenter, 20);
            _faults       = MakeText(canvasGO, "Faults", new Vector2(20, -70), TextAnchor.UpperLeft, 20);
            _hmi          = MakeText(canvasGO, "HMI", new Vector2(20, 110), TextAnchor.LowerLeft, 20);
            _prompt       = MakeText(canvasGO, "Prompt", new Vector2(0, 200), TextAnchor.LowerCenter, 26);
            _tutorial     = MakeText(canvasGO, "Tutorial", new Vector2(0, 30), TextAnchor.LowerCenter, 20);

            // crosshair
            var ch = MakeText(canvasGO, "Crosshair", Vector2.zero, TextAnchor.MiddleCenter, 24);
            ch.text = "+"; ch.color = new Color(1, 1, 1, 0.6f);

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

            UpdateStreak(s);

            // Fault banner: slides in from the left on a new fault and pulses alarm-red while
            // any fault is active. (Rich-text colored header keeps its own tint.)
            int faultCount = s.Faults.Active.Count;
            if (faultCount > _lastFaultCount) _bannerT = 0f;
            _lastFaultCount = faultCount;
            _bannerT = Mathf.Min(1f, _bannerT + Time.deltaTime * 3.2f);
            float slide = 1f - Mathf.Pow(1f - _bannerT, 3f);   // ease-out cubic
            _faults.rectTransform.anchoredPosition = _faultsBasePos + new Vector2(-340f * (1f - slide), 0f);
            _faults.color = faultCount > 0
                ? Color.Lerp(Color.white, new Color(1f, 0.45f, 0.4f), 0.5f + 0.5f * Mathf.Sin(Time.time * 7f))
                : Color.white;

            if (faultCount == 0) _faults.text = "";
            else
            {
                var sb = new System.Text.StringBuilder("<color=#ff5544>ACTIVE FAULTS</color>\n");
                foreach (var f in s.Faults.Active) sb.AppendLine("• " + f.HmiText);
                _faults.text = sb.ToString();
            }

            string why;
            bool can = s.CanDispatch(out why);
            string hmi = can ? "<color=#44ff66>HMI: READY TO DISPATCH</color>"
                             : $"HMI: {why ?? "—"}";
            if (s.Faults.Active.Count > 0)
                hmi += "\nFIX: " + s.Faults.Active[0].Def.FixHint;
            if (m.AssistantRemaining > 0) hmi += $"\nRemote tech en route... {m.AssistantRemaining:0.0}s";
            _hmi.text = hmi;

            _prompt.text = _interactor != null && _interactor.CurrentPrompt != null ? _interactor.CurrentPrompt : "";

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

            // simple solo tutorial progression
            if (Networking.NetworkBootstrap.Mode == Networking.NetMode.Solo && _tutorialStep < TutorialSteps.Length)
            {
                if (_tutorialStep == 0 && s.State != AttractionState.Closed) _tutorialStep = 1;
                else if (_tutorialStep == 1 && s.Guests.GatesOpen) _tutorialStep = 2;
                else if (_tutorialStep == 2 && s.PlatformTrain() != null && s.PlatformTrain().Riders > 0) _tutorialStep = 3;
                else if (_tutorialStep == 3 && s.PlatformTrain() != null && s.PlatformTrain().RestraintsVerified) _tutorialStep = 4;
                else if (_tutorialStep == 4 && s.Score.Dispatches > 0) _tutorialStep = 5;
                _tutorial.text = "<color=#ffd34d>" + TutorialSteps[_tutorialStep] + "</color>";
            }
            else _tutorial.text = "";
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
