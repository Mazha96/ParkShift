using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParkShift.Environment
{
    /// <summary>Built-in pipeline paint and geometry ink. Applies to existing scenes as well as rebuilt ones.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class CartoonParkLook : MonoBehaviour
    {
        [Range(0.5f, 3f)] public float InkThickness = 1.25f;
        public Color InkColor = new Color(0.045f, 0.035f, 0.085f, 0.86f);
        Material ink;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Camera.onPreCull -= PrepareCamera;
            Camera.onPreCull += PrepareCamera;
        }

        static void PrepareCamera(Camera camera)
        {
            // The player camera is created after sceneLoaded by the game bootstrap.
            if (camera.cameraType == CameraType.Game && camera.gameObject.scene.IsValid()
                && camera.GetComponent<CartoonParkLook>() == null)
                camera.gameObject.AddComponent<CartoonParkLook>();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var palette = new GameObject("CartoonScenePalette");
            SceneManager.MoveGameObjectToScene(palette, scene);
            palette.AddComponent<CartoonScenePalette>();
            foreach (var root in scene.GetRootGameObjects())
            foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                if (camera.GetComponent<CartoonParkLook>() == null)
                    camera.gameObject.AddComponent<CartoonParkLook>();
        }

        void OnEnable()
        {
            GetComponent<Camera>().depthTextureMode |= DepthTextureMode.DepthNormals;
            var shader = Resources.Load<Shader>("ParkChaos/CartoonInk");
            if (shader != null && shader.isSupported)
                ink = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (ink == null) { Graphics.Blit(source, destination); return; }
            ink.SetFloat("_Thickness", InkThickness);
            ink.SetColor("_InkColor", InkColor);
            Graphics.Blit(source, destination, ink);
        }

        void OnDisable() { if (ink != null) Destroy(ink); }
    }
}

