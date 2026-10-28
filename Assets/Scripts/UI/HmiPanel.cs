using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using ParkShift.Attraction;
using ParkShift.Player;

namespace ParkShift.UI
{
    /// <summary>
    /// Clickable ride HMI opened from the dispatch console screen (press E on the screen).
    /// Shows live ride status + active faults (read faults as the technician) and offers operator
    /// actions (Add RV, Close). Built entirely in code so there is no scene UI to maintain — extend it
    /// by adding more rows/buttons in Build(). While open the cursor is freed and FPS control paused.
    /// </summary>
    public class HmiPanel : MonoBehaviour
    {
        public static HmiPanel Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance._open;
        public static void Toggle() { if (Instance != null) Instance.SetOpen(!Instance._open); }
        public static void ClosePanel() { if (Instance != null) Instance.SetOpen(false); }

        GameObject _root;
        Text _status;
        bool _open;
        FirstPersonController _fps;
        Font _font;

        void Awake() { Instance = this; }

        void Start()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            EnsureEventSystem();
            Build();
            SetOpen(false);
        }

        void Update()
        {
            if (!_open) return;
            RefreshStatus();
            if (Input.GetKeyDown(KeyCode.Escape)) SetOpen(false);
        }

        void SetOpen(bool open)
        {
            _open = open;
            if (_root) _root.SetActive(open);
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
            if (_fps == null && RoleController.Local != null) _fps = RoleController.Local.GetComponent<FirstPersonController>();
            if (_fps != null) _fps.InputEnabled = !open;   // pause look/move so the mouse can click
            if (open) RefreshStatus();
        }

        void RefreshStatus()
        {
            var m = AttractionManager.Instance;
            if (m == null || m.Sim == null || _status == null) return;
            var s = m.Sim;
            var sb = new StringBuilder();
            sb.AppendLine("STATUS:  " + s.State + (s.LotoEngaged ? "   [LOTO ENGAGED]" : ""));
            sb.AppendLine("RVs in service:  " + s.Trains.Length + (s.CanAddTrain() ? "" : "   (at capacity)"));
            sb.AppendLine("Riders served:  " + s.Score.RidersServed + "      Queue:  " + s.Guests.QueueCount);
            sb.AppendLine("Streak:  x" + s.Score.CurrentStreak + "      Best:  x" + s.Score.BestStreak);
            sb.AppendLine("Guest satisfaction:  " + Mathf.RoundToInt(s.Guests.Satisfaction) + "%");
            sb.AppendLine();
            if (s.Faults.Active.Count == 0)
                sb.AppendLine("FAULTS:  none — all systems nominal.");
            else
            {
                sb.AppendLine("ACTIVE FAULTS:");
                foreach (var f in s.Faults.Active)
                {
                    string deadline = f.DeadlineAt <= 0f ? "" : f.DeadlineMissed
                        ? "  [OVERDUE — still repairable]" : "  [" + Mathf.CeilToInt(f.ResponseSecondsRemaining(s.SimTime)) + "s to respond]";
                    sb.AppendLine("• " + f.HmiText + deadline + "\nFIX: " + f.Def.FixHint + "\n");
                }
            }
            _status.text = sb.ToString();
            _status.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(380f, _status.preferredHeight + 12f));
        }

        // ---------------- UI construction ----------------
        void Build()
        {
            var canvasGO = new GameObject("HmiCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);

            _root = Box(canvasGO.transform, "HmiPanel", new Vector2(760, 560), new Color(0.05f, 0.06f, 0.09f, 0.96f));
            Box(_root.transform, "HmiTitleBar", new Vector2(760, 56), new Color(0.12f, 0.45f, 0.7f, 1f))
                .GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 252);
            Label(_root.transform, "RIDE HMI — COSMIC CONVEYOR", new Vector2(0, 252), 26, TextAnchor.MiddleCenter, new Vector2(740, 50));

            var viewport = Box(_root.transform, "FaultReadout", new Vector2(700, 380), Color.clear);
            viewport.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 10);
            viewport.AddComponent<RectMask2D>();
            var scroll = viewport.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true; scroll.scrollSensitivity = 28f;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.viewport = viewport.GetComponent<RectTransform>();
            _status = Label(viewport.transform, "", Vector2.zero, 22, TextAnchor.UpperLeft, new Vector2(680, 380));
            _status.rectTransform.anchorMin = _status.rectTransform.anchorMax = new Vector2(0, 1);
            _status.rectTransform.pivot = new Vector2(0, 1);
            scroll.content = _status.rectTransform;
            Label(_root.transform, "SCROLL TO READ ALL INCIDENTS", new Vector2(0, -197), 14, TextAnchor.MiddleCenter, new Vector2(680, 24));

            Button(_root.transform, "ADD RV", new Vector2(-240, -238), () =>
            {
                // Through the verb funnel so a FishNet client's Add RV executes on the server
                // (the RV count replicates back; solo/host behave exactly as before).
                var m = AttractionManager.Instance;
                bool ok = m != null && m.Submit(OpVerb.AddRv);
                if (!ok) Debug.Log("[HMI] No room to add another RV.");
            });
            Button(_root.transform, "READ FAULTS", new Vector2(0, -238), () =>
            {
                Juice.AudioDirector.Confirm();
                RefreshStatus();
            });
            Button(_root.transform, "CLOSE", new Vector2(240, -238), () =>
            {
                Juice.AudioDirector.Confirm();
                SetOpen(false);
            });
        }

        GameObject Box(Transform parent, string name, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;
            return go;
        }

        Text Label(Transform parent, string text, Vector2 pos, int size, TextAnchor anchor, Vector2 dim)
        {
            var go = new GameObject("Text", typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = _font; t.fontSize = size; t.alignment = anchor; t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.text = text;
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = dim; rt.anchoredPosition = pos;
            return t;
        }

        void Button(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn_" + label, typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.18f, 0.5f, 0.75f, 1f);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(210, 56); rt.anchoredPosition = pos;
            go.GetComponent<Button>().onClick.AddListener(onClick);
            Label(go.transform, label, Vector2.zero, 22, TextAnchor.MiddleCenter, new Vector2(200, 50));
        }

        static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }
    }
}

