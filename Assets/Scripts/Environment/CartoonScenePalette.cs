using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Environment
{
    /// <summary>Owns paint for the scene lifetime, including guests and network spawns.</summary>
    public sealed class CartoonScenePalette : MonoBehaviour
    {
        readonly List<Material> ownedMaterials = new List<Material>();
        readonly Dictionary<Material, Material> replacements = new Dictionary<Material, Material>();
        static readonly Dictionary<int, CartoonScenePalette> scenes = new Dictionary<int, CartoonScenePalette>();
        Shader paintShader;
        Material ownedSky;
        void Awake()
        {
            scenes[gameObject.scene.handle] = this;
            paintShader = Resources.Load<Shader>("ParkChaos/CartoonSurface");
        }

        public static void Paint(GameObject root)
        {
            if (root != null && scenes.TryGetValue(root.scene.handle, out var palette) && palette != null)
                palette.PaintRoot(root);
        }

        void Start()
        {
            foreach (var root in gameObject.scene.GetRootGameObjects()) PaintRoot(root);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.50f, 0.60f, 0.80f);
            RenderSettings.ambientEquatorColor = new Color(0.43f, 0.34f, 0.52f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.20f, 0.30f);
            RenderSettings.fogColor = new Color(0.34f, 0.36f, 0.61f);
            RenderSettings.fogStartDistance = 85f;
            RenderSettings.fogEndDistance = 270f;
            if (RenderSettings.sun != null)
            {
                RenderSettings.sun.color = new Color(1f, 0.88f, 0.72f);
                RenderSettings.sun.intensity = 1.15f;
                RenderSettings.sun.shadowStrength = 0.75f;
            }
            if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_TopColor"))
            {
                ownedSky = new Material(RenderSettings.skybox);
                ownedSky.SetColor("_TopColor", new Color(0.12f, 0.18f, 0.42f));
                ownedSky.SetColor("_HorizonColor", new Color(0.94f, 0.53f, 0.44f));
                ownedSky.SetColor("_BottomColor", new Color(0.32f, 0.30f, 0.48f));
                ownedSky.SetFloat("_StarBrightness", 1.7f);
                RenderSettings.skybox = ownedSky;
            }
        }

        void PaintRoot(GameObject root)
        {
            if (paintShader == null || !paintShader.isSupported) return;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null || source.shader == null || source.renderQueue >= 2500) continue;
                    string name = source.shader.name;
                    if (name != "Standard" && name != "Legacy Shaders/Diffuse") continue;
                    if (!replacements.TryGetValue(source, out var painted))
                    {
                        painted = new Material(source) { name = source.name + " (Cartoon)", shader = paintShader };
                        painted.SetColor("_ShadowTint", new Color(0.53f, 0.48f, 0.72f));
                        replacements.Add(source, painted);
                        ownedMaterials.Add(painted);
                    }
                    materials[i] = painted;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
        }

        void OnDestroy()
        {
            scenes.Remove(gameObject.scene.handle);
            foreach (var material in ownedMaterials) if (material != null) Destroy(material);
            if (ownedSky != null) Destroy(ownedSky);
        }
    }
}

