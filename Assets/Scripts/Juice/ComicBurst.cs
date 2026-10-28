using UnityEngine;

namespace ParkShift.Juice
{
    /// <summary>Four reusable world-space exclamations; purely local presentation.</summary>
    public sealed class ComicBurst : MonoBehaviour
    {
        static readonly ComicBurst[] pool = new ComicBurst[4];
        static int next;
        TextMesh[] lettering;
        Vector3 origin;
        Color fill;
        float age;
        const float Lifetime = 1f;

        public static void Show(string word, Vector3 position, Color color)
        {
            var camera = Camera.main;
            if (camera == null || (camera.transform.position - position).sqrMagnitude > 900f) return;
            Vector3 viewport = camera.WorldToViewportPoint(position);
            if (viewport.z <= 0 || viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) return;
            if (Physics.Linecast(camera.transform.position, position, ~0, QueryTriggerInteraction.Ignore)) return;

            int slot = next;
            next = (next + 1) % pool.Length;
            if (pool[slot] == null)
            {
                var root = new GameObject("ComicBurst");
                pool[slot] = root.AddComponent<ComicBurst>();
                pool[slot].CreateLettering();
            }
            var burst = pool[slot];
            burst.origin = position;
            burst.fill = color;
            burst.age = 0;
            foreach (var text in burst.lettering) text.text = word;
            burst.gameObject.SetActive(true);
            burst.Pose(camera);
        }

        void CreateLettering()
        {
            lettering = new TextMesh[5];
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            for (int i = 0; i < lettering.Length; i++)
            {
                var child = new GameObject(i == 4 ? "Paint" : "Ink");
                child.transform.SetParent(transform, false);
                if (i < 4)
                    child.transform.localPosition = new Vector3(i % 2 == 0 ? -0.025f : 0.025f,
                        i < 2 ? -0.025f : 0.025f, 0.008f);
                var text = child.AddComponent<TextMesh>();
                text.font = font;
                text.fontSize = 64;
                text.fontStyle = FontStyle.Bold;
                text.characterSize = 0.07f;
                text.anchor = TextAnchor.MiddleCenter;
                text.alignment = TextAlignment.Center;
                child.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                lettering[i] = text;
            }
        }

        void LateUpdate()
        {
            age += Time.deltaTime;
            if (age >= Lifetime) { gameObject.SetActive(false); return; }
            var camera = Camera.main;
            if (camera == null) { gameObject.SetActive(false); return; }
            Pose(camera);
        }

        void Pose(Camera camera)
        {
            float t = age / Lifetime;
            transform.position = origin + Vector3.up * (t * 0.65f);
            transform.rotation = camera.transform.rotation * Quaternion.Euler(0, 0, -7f + 4f * t);
            float pop = Mathf.Lerp(0.65f, 1f, Mathf.Clamp01(t * 9f));
            transform.localScale = Vector3.one * pop;
            float alpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1f, t));
            for (int i = 0; i < lettering.Length; i++)
            {
                Color color = i == 4 ? fill : new Color(0.055f, 0.035f, 0.10f);
                color.a = alpha;
                lettering[i].color = color;
            }
        }
    }
}
