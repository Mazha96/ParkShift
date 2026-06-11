using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using ParkShift.Networking;

namespace ParkShift.UI
{
    /// <summary>Runtime-built main menu: Solo / Host / Join / Quit.</summary>
    public class MainMenuUI : MonoBehaviour
    {
        void Start()
        {
            var canvasGO = new GameObject("MenuCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            Cursor.lockState = CursorLockMode.None;

            MakeText(canvasGO, "COSMIC CONVEYOR", 54, new Vector2(0.5f, 0.85f));
            MakeText(canvasGO, "a Park Shift vertical slice — run the ride, survive the shift", 22, new Vector2(0.5f, 0.76f));

            MakeButton(canvasGO, "PLAY SOLO SHIFT", 0.58f, () => StartGame(NetMode.Solo));
            MakeButton(canvasGO, "HOST CO-OP (LAN/local)", 0.46f, () => StartGame(NetMode.Host));
            MakeButton(canvasGO, "JOIN CO-OP (localhost)", 0.34f, () => StartGame(NetMode.Client));
            MakeButton(canvasGO, "QUIT", 0.22f, Application.Quit);
        }

        void StartGame(NetMode mode)
        {
            NetworkBootstrap.Mode = mode;
            SceneManager.LoadScene("PrototypeAttraction");
        }

        void MakeText(GameObject parent, string text, int size, Vector2 anchor)
        {
            var go = new GameObject("T", typeof(Text));
            go.transform.SetParent(parent.transform, false);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = text; t.fontSize = size; t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = t.rectTransform; rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(900, 80);
        }

        void MakeButton(GameObject parent, string label, float y, UnityEngine.Events.UnityAction act)
        {
            var go = new GameObject(label, typeof(Image), typeof(Button));
            go.transform.SetParent(parent.transform, false);
            go.GetComponent<Image>().color = new Color(0.15f, 0.35f, 0.6f);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.35f, y - 0.045f); rt.anchorMax = new Vector2(0.65f, y + 0.045f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            go.GetComponent<Button>().onClick.AddListener(act);
            var tg = new GameObject("L", typeof(Text)); tg.transform.SetParent(go.transform, false);
            var t = tg.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = label; t.fontSize = 24; t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
            var trt = t.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        }
    }
}
