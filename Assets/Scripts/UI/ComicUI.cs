using UnityEngine;
using UnityEngine.UI;

namespace ParkShift.UI
{
    /// <summary>Shared ink, paper and accent treatment for the park's runtime UI.</summary>
    public static class ComicUI
    {
        public static readonly Color Ink = new Color(0.055f, 0.075f, 0.15f);
        public static readonly Color Paper = new Color(1f, 0.96f, 0.83f);
        public static readonly Color Gold = new Color(1f, 0.75f, 0.19f);
        public static void Letter(Text text, bool dark = false)
        {
            text.raycastTarget = false;
            text.color = dark ? Ink : Paper;
            text.fontStyle = FontStyle.Bold;
            var outline = text.gameObject.AddComponent<Outline>();
            outline.effectColor = dark ? Color.clear : Ink;
            outline.effectDistance = new Vector2(1.5f, -1.5f);
        }
        public static Image Card(GameObject parent, string name, Vector2 min, Vector2 max, Color color)
        {
            var go = new GameObject(name, typeof(Image), typeof(Outline));
            go.transform.SetParent(parent.transform, false);
            var image = go.GetComponent<Image>();
            image.color = color; image.raycastTarget = false;
            var rt = image.rectTransform;
            rt.anchorMin = min; rt.anchorMax = max;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var outline = go.GetComponent<Outline>();
            outline.effectColor = Ink; outline.effectDistance = new Vector2(3, -3);
            return image;
        }
    }
}
