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
        Text _role, _timer, _score, _satisfaction, _state, _faults, _hmi, _prompt, _tutorial;
        PlayerInteractor _interactor;
        RoleController _roleCtl;
        ResultsScreen _results;
        int _tutorialStep;

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
            _state        = MakeText(canvasGO, "State", new Vector2(0, -60), TextAnchor.UpperCenter, 20);
            _faults       = MakeText(canvasGO, "Faults", new Vector2(20, -70), TextAnchor.UpperLeft, 20);
            _hmi          = MakeText(canvasGO, "HMI", new Vector2(20, 110), TextAnchor.LowerLeft, 20);
            _prompt       = MakeText(canvasGO, "Prompt", new Vector2(0, 200), TextAnchor.LowerCenter, 26);
            _tutorial     = MakeText(canvasGO, "Tutorial", new Vector2(0, 30), TextAnchor.LowerCenter, 20);

            // crosshair
            var ch = MakeText(canvasGO, "Crosshair", Vector2.zero, TextAnchor.MiddleCenter, 24);
            ch.text = "+"; ch.color = new Color(1, 1, 1, 0.6f);

            _results = gameObject.AddComponent<ResultsScreen>();
            _results.Build(canvasGO);
            AttractionManager.Instance.Sim.OnShiftEnded += () => _results.Show(AttractionManager.Instance.Sim);
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

            _role.text = $"ROLE: {(_roleCtl ? _roleCtl.Role.ToString().ToUpper() : "?")}  (Tab)";
            _timer.text = $"{Mathf.FloorToInt(s.ShiftRemaining / 60f):0}:{Mathf.FloorToInt(s.ShiftRemaining % 60f):00}";
            _score.text = $"SCORE {s.Score.Compute(s.Guests.Satisfaction)}  |  RIDERS {s.Score.RidersServed}";
            _satisfaction.text = $"GUEST SATISFACTION {s.Guests.Satisfaction:0}%   QUEUE {s.Guests.QueueCount}";
            _state.text = $"COSMIC CONVEYOR — {s.State}" + (s.LotoEngaged ? "  [LOTO]" : "");

            if (s.Faults.Active.Count == 0) _faults.text = "";
            else
            {
                var sb = new System.Text.StringBuilder("<color=#ff5544>ACTIVE FAULTS</color>\n");
                foreach (var f in s.Faults.Active) sb.AppendLine("• " + f.Def.HmiMessage);
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
    }
}
