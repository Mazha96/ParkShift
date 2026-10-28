using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using ParkShift.Networking;

namespace ParkShift.UI
{
    /// <summary>Runtime-built main menu: Solo / Host / Join / Quit.</summary>
    public class MainMenuUI : MonoBehaviour
    {
        InputField _address;
        void Start()
        {
            if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
                new GameObject("MenuEventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
            var canvasGO = new GameObject("MenuCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            ComicUI.Card(canvasGO, "Backdrop", Vector2.zero, Vector2.one, ComicUI.Ink);
            ComicUI.Card(canvasGO, "Ticket", new Vector2(0.18f, 0.12f), new Vector2(0.82f, 0.94f), new Color(0.13f, 0.22f, 0.33f));

            MakeText(canvasGO, "PARK CHAOS", 72, new Vector2(0.5f, 0.85f));
            MakeText(canvasGO, "COSMIC CONVEYOR  /  Run the ride. Rescue the shift.", 22, new Vector2(0.5f, 0.76f));

            MakeAddress(canvasGO);
            MakeButton(canvasGO, "PLAY SOLO SHIFT", 0.54f, () => StartGame(NetMode.Solo));
            MakeButton(canvasGO, "HOST CO-OP / 4 CREW", 0.42f, () => StartGame(NetMode.Host));
            MakeButton(canvasGO, "JOIN CO-OP / LAN", 0.30f, () => StartGame(NetMode.Client));
            MakeButton(canvasGO, "QUIT", 0.18f, Application.Quit);
        }

        void StartGame(NetMode mode)
        {
#if !FISHNET
            if (mode != NetMode.Solo) return;
#endif
            NetworkBootstrap.Mode = mode;
            NetworkBootstrap.Address = string.IsNullOrWhiteSpace(_address.text) ? "localhost" : _address.text.Trim();
            SceneManager.LoadScene("PrototypeAttraction");
        }

        void MakeAddress(GameObject parent)
        {
            var label = new GameObject("AddressLabel", typeof(Text));
            label.transform.SetParent(parent.transform, false);
            var text = label.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = "HOST ADDRESS"; text.fontSize = 19; text.alignment = TextAnchor.MiddleRight;
            ComicUI.Letter(text);
            var rt = text.rectTransform;
            rt.anchorMin = new Vector2(0.30f, 0.64f); rt.anchorMax = new Vector2(0.46f, 0.69f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var field = new GameObject("HostAddress", typeof(Image), typeof(InputField));
            field.transform.SetParent(parent.transform, false);
            field.GetComponent<Image>().color = ComicUI.Paper;
            rt = field.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.48f, 0.64f); rt.anchorMax = new Vector2(0.70f, 0.69f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var content = new GameObject("Text", typeof(Text));
            content.transform.SetParent(field.transform, false);
            var value = content.GetComponent<Text>();
            value.font = text.font; value.fontSize = 22; value.color = ComicUI.Ink;
            value.alignment = TextAnchor.MiddleLeft; value.supportRichText = false;
            value.rectTransform.anchorMin = Vector2.zero; value.rectTransform.anchorMax = Vector2.one;
            value.rectTransform.offsetMin = new Vector2(12, 0); value.rectTransform.offsetMax = new Vector2(-12, 0);
            _address = field.GetComponent<InputField>();
            _address.textComponent = value; _address.characterLimit = 253;
            _address.text = NetworkBootstrap.Address;
        }

        void MakeText(GameObject parent, string text, int size, Vector2 anchor)
        {
            var go = new GameObject("T", typeof(Text));
            go.transform.SetParent(parent.transform, false);
            var t = go.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = text; t.fontSize = size; t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            ComicUI.Letter(t);
            var rt = t.rectTransform; rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(900, 80);
        }

        void MakeButton(GameObject parent, string label, float y, UnityEngine.Events.UnityAction act)
        {
            var go = new GameObject(label, typeof(Image), typeof(Button));
            go.transform.SetParent(parent.transform, false);
            go.GetComponent<Image>().color = ComicUI.Gold;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.35f, y - 0.045f); rt.anchorMax = new Vector2(0.65f, y + 0.045f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            go.GetComponent<Button>().onClick.AddListener(act);
#if !FISHNET
            if (label.Contains("CO-OP"))
            {
                go.GetComponent<Button>().interactable = false;
                label = "CO-OP / SETUP REQUIRED";
            }
#endif
            var tg = new GameObject("L", typeof(Text)); tg.transform.SetParent(go.transform, false);
            var t = tg.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = label; t.fontSize = 24; t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
            ComicUI.Letter(t, true);
            var trt = t.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
        }
    }
}


