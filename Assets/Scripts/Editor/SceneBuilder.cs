using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ParkShift.Attraction;
using ParkShift.Guests;
using ParkShift.Vehicles;
using PSEnv = ParkShift.Environment;

namespace ParkShift.EditorTools
{
    /// <summary>
    /// One-click scene construction from primitives. Run: menu ParkShift → Build All Scenes.
    /// Rebuildable at any time; scenes are throwaway artifacts, this script is the source of truth.
    /// Visual direction: stylized flat-color low-poly park at cosmic dusk — gradient space
    /// skybox (stars + planet), warm low sun, cool violet ambient/fog, neon accents.
    /// </summary>
    public static class SceneBuilder
    {
        static Material Mat(string name, Color c)
        {
            return Mat(name, c, null, 0.25f, 0f, 1f);
        }

        /// <summary>Creates/updates a material. If texSet is given, looks for
        /// Assets/Textures/{texSet}_albedo.png and {texSet}_normal.png — replace those
        /// files with downloaded CC0 packs (ambientCG etc.) under the same names and
        /// rebuild scenes to swap the whole look.</summary>
        static Material Mat(string name, Color c, string texSet, float smoothness, float metallic, float tiling)
        {
            string path = $"Assets/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            m.color = c;
            m.SetFloat("_Glossiness", smoothness);  // Standard
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness); // URP
            m.SetFloat("_Metallic", metallic);
            // Emission hygiene: rebuilds must be able to demote a formerly-glowing mat.
            m.DisableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);

            if (!string.IsNullOrEmpty(texSet))
            {
                var albedo = LoadTexture($"Assets/Textures/{texSet}_albedo.png", false);
                var normal = LoadTexture($"Assets/Textures/{texSet}_normal.png", true);
                m.mainTexture = albedo;
                m.mainTextureScale = Vector2.one * tiling;
                if (normal != null)
                {
                    m.SetTexture("_BumpMap", normal);
                    m.SetTextureScale("_BumpMap", Vector2.one * tiling);
                    m.EnableKeyword("_NORMALMAP");
                }
            }
            else
            {
                m.mainTexture = null;
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Flat material with an HDR emissive tint (neon trim, lamp bulbs, signage,
        /// crystals). Realtime-rendered emission only — flagged so it never contributes to GI
        /// and nothing gets baked.</summary>
        static Material GlowMat(string name, Color baseCol, Color emission, float intensity)
        {
            var m = Mat(name, baseCol);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission * intensity);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            EditorUtility.SetDirty(m);
            return m;
        }

        const string DeweyPath = "Assets/Models/Dewey.fbx";

        static GameObject Prop(string file) =>
            AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Models/{file}");

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>Instantiates a prop FBX under a wrapper GameObject. The wrapper takes all
        /// game-driven position/rotation; the FBX instance keeps its imported axis-compensation
        /// rotation as local state (single-root Blender FBXs carry ~-90X on their root node —
        /// overwriting it makes props lie down).</summary>
        static GameObject PlaceProp(GameObject prefab, string name, Vector3 pos, float yRot = 0f)
        {
            var wrapper = new GameObject(name);
            wrapper.transform.position = pos;
            wrapper.transform.rotation = Quaternion.Euler(0, yRot, 0);
            var inst = (GameObject)Object.Instantiate(prefab);
            inst.name = name + "_Model";
            inst.transform.SetParent(wrapper.transform, false); // preserves the prefab's import pose
            return wrapper;
        }

        static void SwapMaterial(GameObject go, string matNameContains, Material replacement)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials;
                bool dirty = false;
                for (int k = 0; k < mats.Length; k++)
                    if (mats[k] != null && mats[k].name.Contains(matNameContains))
                    { mats[k] = replacement; dirty = true; }
                if (dirty) r.sharedMaterials = mats;
            }
        }

        /// <summary>Adds a raycast-interaction volume. The collider is a TRIGGER on purpose:
        /// the player interacts by looking at it (camera raycast), and a trigger is still hit by
        /// raycasts but never physically pushes the CharacterController — so standing/walking in
        /// front of an interactable can never shove the player. Pass <paramref name="center"/> to
        /// place the volume on the visible part when the mesh origin sits elsewhere (e.g. levers
        /// whose pivot is at the floor).</summary>
        static void AddInteractor(Transform part, PanelInteractable.Kind kind, Vector3 colliderSize, Vector3 center = default)
        {
            if (part == null) return;
            var bc = part.gameObject.AddComponent<BoxCollider>();
            bc.size = colliderSize;
            bc.center = center;
            bc.isTrigger = true;
            part.gameObject.AddComponent<PanelInteractable>().PanelKind = kind;
        }

        /// <summary>Configures the Dewey FBX (looping Walk/Idle/Sit clips) and returns the prefab.</summary>
        static GameObject SetupDeweyModel()
        {
            var importer = AssetImporter.GetAtPath(DeweyPath) as ModelImporter;
            if (importer == null) return null; // model not exported yet — capsule fallback
            string[] wanted = { "Walk", "Idle", "Sit" };
            var clips = importer.defaultClipAnimations;
            bool ok = importer.clipAnimations.Length == wanted.Length
                   && importer.clipAnimations.All(c => wanted.Contains(c.name) && c.loopTime);
            if (!ok)
            {
                foreach (var c in clips)
                {
                    foreach (var n in wanted)
                        if (c.name.Contains(n)) { c.name = n; break; }
                    c.loopTime = true;
                }
                importer.clipAnimations = clips;
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.importAnimation = true;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(DeweyPath);
        }

        /// <summary>Animator controller with Idle (default) / Walk / Sit states; code crossfades by name.</summary>
        static RuntimeAnimatorController BuildGuestAnimator()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Animations"))
                AssetDatabase.CreateFolder("Assets", "Animations");
            string path = "Assets/Animations/DeweyGuest.controller";
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (ctrl == null) ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);

            var sm = ctrl.layers[0].stateMachine;
            foreach (var child in sm.states.ToArray()) sm.RemoveState(child.state);

            var clips = AssetDatabase.LoadAllAssetsAtPath(DeweyPath)
                .OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview")).ToList();
            foreach (var name in new[] { "Idle", "Walk", "Sit" })
            {
                var clip = clips.FirstOrDefault(c => c.name == name)
                        ?? clips.FirstOrDefault(c => c.name.Contains(name));
                var st = sm.AddState(name);
                st.motion = clip;
                if (name == "Idle") sm.defaultState = st;
            }
            EditorUtility.SetDirty(ctrl);
            return ctrl;
        }

        static Texture2D LoadTexture(string path, bool asNormal)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return null; // texture not present — flat color fallback
            if (asNormal && imp.textureType != TextureImporterType.NormalMap)
            {
                imp.textureType = TextureImporterType.NormalMap;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static GameObject Box(string name, Vector3 pos, Vector3 scale, Material mat, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = scale;
            if (parent) go.transform.SetParent(parent, true);
            if (mat) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        static GameObject Cyl(string name, Vector3 pos, Vector3 scale, Material mat, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = scale;
            if (parent) go.transform.SetParent(parent, true);
            if (mat) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        static GameObject Ball(string name, Vector3 pos, float diameter, Material mat, Transform parent = null, bool keepCollider = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * diameter;
            if (parent) go.transform.SetParent(parent, true);
            if (mat) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Flat-shaded low-poly cone mesh (trees, hills, crystals). Verts are not
        /// shared between faces so RecalculateNormals yields hard facets. The mesh embeds in
        /// the scene file on save — same pattern TrackMeshBuilder already relies on.</summary>
        static Mesh ConeMesh(int segments, float radius, float height)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var apex = new Vector3(0, height, 0);
            for (int s = 0; s < segments; s++)
            {
                float a0 = (s / (float)segments) * Mathf.PI * 2f;
                float a1 = ((s + 1) / (float)segments) * Mathf.PI * 2f;
                var p0 = new Vector3(Mathf.Cos(a0) * radius, 0, Mathf.Sin(a0) * radius);
                var p1 = new Vector3(Mathf.Cos(a1) * radius, 0, Mathf.Sin(a1) * radius);
                int b = verts.Count;
                verts.Add(apex); verts.Add(p1); verts.Add(p0);          // side, outward
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                b = verts.Count;
                verts.Add(Vector3.zero); verts.Add(p0); verts.Add(p1);  // base cap, downward
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            }
            var mesh = new Mesh();
            mesh.name = $"Cone_{segments}_{radius:0.0}x{height:0.0}";
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static GameObject Cone(string name, Vector3 pos, float radius, float height, int segments, Material mat, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            if (parent) go.transform.SetParent(parent, true);
            go.AddComponent<MeshFilter>().sharedMesh = ConeMesh(segments, radius, height);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Shared dusk/space mood for every scene: gradient skybox material (custom
        /// ParkShift/GradientSkybox with procedural stars + a planet; Skybox/Procedural tinted
        /// to dusk as fallback), cool trilight ambient and violet distance fog. Realtime-only;
        /// nothing here bakes. Reuses the existing Assets/Materials/Skybox.mat asset.</summary>
        static Material BuildDuskEnvironment(Light sun)
        {
            const string skyPath = "Assets/Materials/Skybox.mat";
            var grad = Shader.Find("ParkShift/GradientSkybox");
            var shader = grad != null ? grad : Shader.Find("Skybox/Procedural");
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (sky == null)
            {
                sky = new Material(shader);
                AssetDatabase.CreateAsset(sky, skyPath);
            }
            sky.shader = shader;
            if (grad != null)
            {
                sky.SetColor("_TopColor", new Color(0.045f, 0.04f, 0.15f));
                sky.SetColor("_HorizonColor", new Color(0.96f, 0.47f, 0.32f));
                sky.SetColor("_BottomColor", new Color(0.19f, 0.13f, 0.26f));
                sky.SetFloat("_HorizonHeight", 0.01f);
                sky.SetFloat("_HorizonFalloff", 1.5f);
                sky.SetFloat("_StarDensity", 0.5f);
                sky.SetFloat("_StarBrightness", 1.8f);
                sky.SetVector("_PlanetDir", new Vector4(-0.45f, 0.24f, 0.86f, 0f));
                sky.SetColor("_PlanetColor", new Color(0.99f, 0.66f, 0.7f));
                sky.SetFloat("_PlanetCos", 0.9975f);
                sky.SetColor("_HaloColor", new Color(0.75f, 0.35f, 0.65f));
            }
            else
            {
                sky.SetFloat("_SunSize", 0.05f);
                sky.SetFloat("_AtmosphereThickness", 0.62f);
                sky.SetFloat("_Exposure", 1.05f);
                sky.SetColor("_SkyTint", new Color(0.55f, 0.35f, 0.68f));
                sky.SetColor("_GroundColor", new Color(0.22f, 0.16f, 0.28f));
            }
            EditorUtility.SetDirty(sky);

            RenderSettings.skybox = sky;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.4f, 0.36f, 0.62f);
            RenderSettings.ambientEquatorColor = new Color(0.46f, 0.3f, 0.44f);
            RenderSettings.ambientGroundColor = new Color(0.17f, 0.14f, 0.24f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 42f;
            RenderSettings.fogEndDistance = 150f;
            RenderSettings.fogColor = new Color(0.36f, 0.23f, 0.42f);
            return sky;
        }

        /// <summary>Warm low dusk sun shared by both scenes.</summary>
        static Light MakeDuskSun()
        {
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(26f, -38f, 0);
            sun.intensity = 1.05f;
            sun.color = new Color(1f, 0.72f, 0.52f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            return sun;
        }

        static void Label(GameObject target, string text, float yOffset = 0.8f)
        {
            var go = new GameObject(target.name + "_Label");
            go.transform.SetParent(target.transform, false);
            go.transform.localPosition = Vector3.up * yOffset;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text; tm.characterSize = 0.12f; tm.fontSize = 48;
            tm.anchor = TextAnchor.MiddleCenter; tm.color = Color.white;
        }

        [MenuItem("ParkShift/Build All Scenes")]
        public static void BuildAll()
        {
            BuildMainMenu();
            BuildPrototypeAttraction();
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene("Assets/Scenes/MainMenu.unity", true),
                new EditorBuildSettingsScene("Assets/Scenes/PrototypeAttraction.unity", true),
            };
            EditorUtility.DisplayDialog("ParkShift", "Scenes built.\nOpen Assets/Scenes/PrototypeAttraction.unity and press Play,\nor play MainMenu for the menu flow.", "OK");
        }

        static void BuildMainMenu()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("MainMenuUI").AddComponent<UI.MainMenuUI>();
            // Same dusk/space mood as the park so the menu backdrop isn't a flat color card.
            BuildDuskEnvironment(MakeDuskSun());
            var camGO = new GameObject("Camera");
            var cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.backgroundColor = new Color(0.06f, 0.05f, 0.15f); // fallback if skybox is missing
            // Aim at the horizon band so stars, gradient and the planet all land in frame.
            camGO.transform.rotation = Quaternion.Euler(-10f, -28f, 0);
            camGO.AddComponent<AudioListener>();
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/MainMenu.unity");
        }

        static void BuildPrototypeAttraction()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- Palette: flat solid-color Standard materials (low smoothness) for the
            // stylized Overcooked/Astroneer-with-a-cosmic-twist look. Existing material
            // assets keep their names/paths (other scripts may reference them) — they are
            // simply re-tinted flat. Pass a texSet string again to go back to textured PBR.
            var mFloor    = Mat("Floor",    new Color(0.24f, 0.22f, 0.35f), null, 0.12f, 0f, 1f); // indigo plaza
            var mPlatform = Mat("Platform", new Color(0.89f, 0.84f, 0.74f), null, 0.15f, 0f, 1f); // warm cream
            var mTrack    = Mat("Track",    new Color(0.58f, 0.30f, 0.85f), null, 0.30f, 0.15f, 1f); // cosmic purple
            var mTrainA   = Mat("TrainA",   new Color(0.98f, 0.52f, 0.12f), null, 0.45f, 0.10f, 1f); // hero orange
            var mTrainB   = Mat("TrainB",   new Color(0.12f, 0.62f, 0.95f), null, 0.45f, 0.10f, 1f); // hero blue
            var mPanel    = Mat("Panel",    new Color(0.13f, 0.62f, 0.45f), null, 0.25f, 0f, 1f);
            var mDanger   = Mat("Danger",   new Color(0.90f, 0.20f, 0.25f), null, 0.25f, 0f, 1f);
            var mGate     = Mat("Gate",     new Color(0.98f, 0.78f, 0.18f), null, 0.30f, 0f, 1f);  // sunny yellow
            var mGuest    = Mat("Guest",    new Color(0.82f, 0.80f, 0.88f));
            var mBooth    = Mat("Booth",    new Color(0.36f, 0.38f, 0.58f), null, 0.18f, 0f, 1f);  // dusk slate
            var mRail     = Mat("Rail",     new Color(0.75f, 0.78f, 0.92f), null, 0.55f, 0.60f, 1f); // pale steel
            var mTie      = Mat("Tie",      new Color(0.20f, 0.17f, 0.30f), null, 0.12f, 0f, 1f);  // dark violet
            var mSeat     = Mat("Seat",     new Color(0.16f, 0.15f, 0.22f), null, 0.30f, 0f, 1f);
            var mRoof     = Mat("Roof",     new Color(0.42f, 0.22f, 0.62f), null, 0.20f, 0f, 1f);  // grape canopy

            // Environment dressing palette (new assets, all Assets/Materials/Env_*)
            var mWhite    = Mat("Env_White",    new Color(0.93f, 0.92f, 0.96f), null, 0.20f, 0f, 1f);
            var mPathA    = Mat("Env_PathA",    new Color(0.95f, 0.44f, 0.32f), null, 0.15f, 0f, 1f); // coral
            var mPathB    = Mat("Env_PathB",    new Color(0.15f, 0.68f, 0.66f), null, 0.15f, 0f, 1f); // teal
            var mPlanter  = Mat("Env_Planter",  new Color(0.55f, 0.36f, 0.52f), null, 0.15f, 0f, 1f);
            var mSoil     = Mat("Env_Soil",     new Color(0.23f, 0.16f, 0.20f), null, 0.05f, 0f, 1f);
            var mTrunk    = Mat("Env_Trunk",    new Color(0.42f, 0.28f, 0.24f), null, 0.10f, 0f, 1f);
            var mFoliageA = Mat("Env_FoliageA", new Color(0.16f, 0.62f, 0.46f), null, 0.10f, 0f, 1f);
            var mFoliageB = Mat("Env_FoliageB", new Color(0.36f, 0.75f, 0.48f), null, 0.10f, 0f, 1f);
            var mRock     = Mat("Env_Rock",     new Color(0.48f, 0.45f, 0.60f), null, 0.10f, 0f, 1f);
            var mHill     = Mat("Env_Hill",     new Color(0.25f, 0.17f, 0.38f), null, 0.05f, 0f, 1f);
            var mHillFar  = Mat("Env_HillFar",  new Color(0.31f, 0.21f, 0.45f), null, 0.05f, 0f, 1f);
            var mLampPole = Mat("Env_LampPole", new Color(0.20f, 0.20f, 0.28f), null, 0.30f, 0.20f, 1f);
            var mNeonCyan    = GlowMat("Env_NeonCyan",    new Color(0.10f, 0.35f, 0.38f), new Color(0.25f, 0.95f, 1f),   2.4f);
            var mNeonMagenta = GlowMat("Env_NeonMagenta", new Color(0.38f, 0.10f, 0.30f), new Color(1f, 0.30f, 0.85f),   2.4f);
            var mLampGlow    = GlowMat("Env_LampGlow",    new Color(0.45f, 0.35f, 0.20f), new Color(1f, 0.75f, 0.45f),   2.0f);
            var mStringBulb  = GlowMat("Env_StringBulb",  new Color(0.45f, 0.38f, 0.18f), new Color(1f, 0.85f, 0.50f),   1.8f);
            var mCrystal     = GlowMat("Env_Crystal",     new Color(0.30f, 0.20f, 0.55f), new Color(0.65f, 0.40f, 1f),   1.2f);

            // Dewey guest model + animator (null-safe: capsule fallback if FBX is absent)
            var deweyPrefab = SetupDeweyModel();
            var deweyCtrl = deweyPrefab != null ? BuildGuestAnimator() : null;

            // Lighting — cosmic dusk: low warm sun with long soft shadows, cool violet
            // trilight ambient, gradient space skybox (stars + planet) and matching fog.
            var sun = MakeDuskSun();
            BuildDuskEnvironment(sun);
            QualitySettings.shadowDistance = 70f;

            // Ground + station platform
            Box("Floor", new Vector3(0, -0.5f, 8), new Vector3(70, 1, 60), mFloor);
            Box("StationPlatform", new Vector3(0, 0.25f, 0), new Vector3(14, 0.5f, 3.5f), mPlatform);

            // ---- Access ramps: ground (0) <-> platform top (0.5), gentle ~13 degree plates ----
            // (lambdas instead of local functions throughout: the headless mcs 7.2 parser
            // used by CI chokes on local functions)
            System.Func<string, Vector3, Vector3, Vector3, GameObject> Ramp = (name, pos, scale, euler) =>
            {
                var r = Box(name, pos, scale, mPlatform);
                r.transform.rotation = Quaternion.Euler(euler);
                return r;
            };
            // Queue entry: proper station steps through the fence gap (visual), with an
            // invisible ramp collider so the CharacterController glides up smoothly.
            var stepsPrefab = Prop("StationSteps.fbx");
            if (stepsPrefab != null)
            {
                var steps = PlaceProp(stepsPrefab, "StationSteps", new Vector3(0, 0f, -2.245f));
                steps.transform.localScale = new Vector3(1f, 1f, 0.55f); // compress depth to clear rail row 0
                // standalone (not parented) so the wrapper's depth compression doesn't shrink it
                var rampCol = new GameObject("StepsRampCollider");
                rampCol.transform.position = new Vector3(0, 0.235f, -2.24f);
                rampCol.transform.rotation = Quaternion.Euler(-26f, 0, 0);
                var rcb = rampCol.AddComponent<BoxCollider>();
                rcb.size = new Vector3(1.6f, 0.1f, 1.18f);
            }
            else
            {
                Ramp("RampQueue", new Vector3(0, 0.24f, -2.78f), new Vector3(3.0f, 0.1f, 2.25f), new Vector3(-13.7f, 0, 0));
            }
            // Booth-side ramp (east end, rising west onto the platform)
            Ramp("RampBooth", new Vector3(8.15f, 0.24f, 0), new Vector3(2.25f, 0.1f, 3.0f), new Vector3(0, 0, -13.7f));
            // Maintenance-side ramp (west end, rising east onto the platform)
            Ramp("RampMaint", new Vector3(-8.15f, 0.24f, 0), new Vector3(2.25f, 0.1f, 3.0f), new Vector3(0, 0, 13.7f));

            // ---- Station architecture: grape canopy with sloped skirts on white columns,
            // exposed beams, pulsing neon edge trim and a double-sided marquee sign ----
            var stationRoot = new GameObject("StationDressing");
            Box("RoofSlab", new Vector3(0, 4.0f, 0.6f), new Vector3(15f, 0.25f, 6f), mRoof, stationRoot.transform);
            var skirtS = Box("RoofSkirtS", new Vector3(0, 3.9f, -2.95f), new Vector3(15.2f, 0.15f, 1.3f), mRoof, stationRoot.transform);
            skirtS.transform.rotation = Quaternion.Euler(-22f, 0, 0);
            var skirtN = Box("RoofSkirtN", new Vector3(0, 3.9f, 4.15f), new Vector3(15.2f, 0.15f, 1.3f), mRoof, stationRoot.transform);
            skirtN.transform.rotation = Quaternion.Euler(22f, 0, 0);
            // Exposed beams under the slab
            for (int bIdx = 0; bIdx < 5; bIdx++)
                Box($"RoofBeam{bIdx}", new Vector3(-6f + 3f * bIdx, 3.78f, 0.6f), new Vector3(0.18f, 0.18f, 5.6f), mWhite, stationRoot.transform);
            Box("RoofBeamSpine", new Vector3(0, 3.78f, 0.6f), new Vector3(14.6f, 0.16f, 0.16f), mWhite, stationRoot.transform);
            // Neon edge trim (one NeonPulse runs all four strips in sync)
            var roofNeon = new GameObject("RoofNeon");
            roofNeon.transform.SetParent(stationRoot.transform, false);
            Box("NeonEdgeS", new Vector3(0, 4.16f, -2.42f), new Vector3(15.2f, 0.09f, 0.09f), mNeonCyan, roofNeon.transform);
            Box("NeonEdgeN", new Vector3(0, 4.16f, 3.62f), new Vector3(15.2f, 0.09f, 0.09f), mNeonCyan, roofNeon.transform);
            Box("NeonEdgeW", new Vector3(-7.55f, 4.16f, 0.6f), new Vector3(0.09f, 0.09f, 6.1f), mNeonCyan, roofNeon.transform);
            Box("NeonEdgeE", new Vector3(7.55f, 4.16f, 0.6f), new Vector3(0.09f, 0.09f, 6.1f), mNeonCyan, roofNeon.transform);
            var roofPulse = roofNeon.AddComponent<PSEnv.NeonPulse>();
            roofPulse.EmissionColor = new Color(0.25f, 0.95f, 1f);
            roofPulse.Intensity = 2.4f; roofPulse.PulseAmount = 0.12f; roofPulse.Speed = 1.2f;
            // Columns: white shafts with grape base + capital
            foreach (var cx in new[] { -6.8f, 6.8f })
                foreach (var cz in new[] { -1.9f, 3.1f })
                {
                    Cyl("RoofColumn", new Vector3(cx, 2f, cz), new Vector3(0.28f, 1.9f, 0.28f), mWhite, stationRoot.transform);
                    Box("ColumnBase", new Vector3(cx, 0.4f, cz), new Vector3(0.5f, 0.8f, 0.5f), mRoof, stationRoot.transform);
                    Box("ColumnCap", new Vector3(cx, 3.72f, cz), new Vector3(0.44f, 0.2f, 0.44f), mRoof, stationRoot.transform);
                }
            // ---- "COSMIC CONVEYOR" marquee above the south (queue-side) roof edge ----
            var sign = new GameObject("StationSign");
            sign.transform.SetParent(stationRoot.transform, false);
            Box("SignBoard", new Vector3(0, 4.85f, -2.3f), new Vector3(8.6f, 1.15f, 0.22f), mBooth, sign.transform);
            Box("SignTrimTop", new Vector3(0, 5.5f, -2.3f), new Vector3(8.8f, 0.08f, 0.26f), mNeonMagenta, sign.transform);
            Box("SignTrimBot", new Vector3(0, 4.2f, -2.3f), new Vector3(8.8f, 0.08f, 0.26f), mNeonMagenta, sign.transform);
            Box("SignStrutL", new Vector3(-3.2f, 4.15f, -2.3f), new Vector3(0.12f, 0.55f, 0.12f), mWhite, sign.transform);
            Box("SignStrutR", new Vector3(3.2f, 4.15f, -2.3f), new Vector3(0.12f, 0.55f, 0.12f), mWhite, sign.transform);
            Ball("SignOrbL", new Vector3(-4.6f, 4.85f, -2.3f), 0.42f, mNeonMagenta, sign.transform);
            Ball("SignOrbR", new Vector3(4.6f, 4.85f, -2.3f), 0.42f, mNeonMagenta, sign.transform);
            System.Action<string, Vector3, float> SignText = (sName, sPos, sYRot) =>
            {
                var go = new GameObject(sName);
                go.transform.SetParent(sign.transform, true);
                go.transform.position = sPos;
                go.transform.rotation = Quaternion.Euler(0, sYRot, 0);
                var tm = go.AddComponent<TextMesh>();
                tm.text = "COSMIC CONVEYOR";
                tm.characterSize = 0.16f; tm.fontSize = 50; tm.fontStyle = FontStyle.Bold;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.color = new Color(1f, 0.5f, 0.92f);
            };
            SignText("SignTextS", new Vector3(0, 4.85f, -2.45f), 0f);    // readable from the queue
            SignText("SignTextN", new Vector3(0, 4.85f, -2.15f), 180f);  // readable from the course
            var signPulse = sign.AddComponent<PSEnv.NeonPulse>();
            signPulse.EmissionColor = new Color(1f, 0.3f, 0.85f);
            signPulse.Intensity = 2.4f; signPulse.PulseAmount = 0.2f; signPulse.Speed = 0.9f; signPulse.Phase = 1.7f;
            // Platform edge neon (helps night readability of the drop-off)
            Box("PlatformEdgeNeonS", new Vector3(0, 0.54f, -1.72f), new Vector3(14f, 0.06f, 0.06f), mNeonMagenta, stationRoot.transform);
            Box("PlatformEdgeNeonW", new Vector3(-6.97f, 0.54f, 0f), new Vector3(0.06f, 0.06f, 3.4f), mNeonMagenta, stationRoot.transform);
            Box("PlatformEdgeNeonE", new Vector3(6.97f, 0.54f, 0f), new Vector3(0.06f, 0.06f, 3.4f), mNeonMagenta, stationRoot.transform);
            // Platform back-edge fence (guests' side) with a center opening for the queue entry
            for (int f = 0; f < 8; f++)
            {
                float fx = -6.6f + f * 1.9f;
                if (Mathf.Abs(fx) < 1.6f) continue; // entry gap
                Box($"FencePost{f}", new Vector3(fx, 0.9f, -1.7f), new Vector3(0.08f, 0.8f, 0.08f), mWhite, stationRoot.transform);
            }
            foreach (float sideX in new[] { -4.3f, 4.3f })
            {
                Box($"FenceTopRail{(sideX < 0 ? "L" : "R")}", new Vector3(sideX, 1.28f, -1.7f), new Vector3(5.4f, 0.07f, 0.07f), mWhite, stationRoot.transform);
                Box($"FenceMidRail{(sideX < 0 ? "L" : "R")}", new Vector3(sideX, 0.95f, -1.7f), new Vector3(5.4f, 0.06f, 0.06f), mWhite, stationRoot.transform);
            }

            // ---- Track: rounded-rectangle loop, 20 waypoints, 5 blocks of 4.
            // The station straight (z=2.2) is split: block 0 = UNLOAD (entry / left, RV parks at x=-3)
            // and block 1 = LOAD (exit / right, RV parks at x=+3), so two RVs park at once a full
            // block apart. Blocks 2-4 = Launch, Course, Brake run. ----
            var trackGO = new GameObject("Track");
            var path = trackGO.AddComponent<TrackPath>();
            path.BlockCount = 5;
            Vector2[] layout =
            {
                // Station straight (9 pts, x -6..6). Equal slices -> Unload park x=-3, Load park x=+3.
                new Vector2(-6, 2.2f), new Vector2(-4.5f, 2.2f), new Vector2(-3, 2.2f), new Vector2(-1.5f, 2.2f),
                new Vector2(0, 2.2f), new Vector2(1.5f, 2.2f), new Vector2(3, 2.2f), new Vector2(4.5f, 2.2f), new Vector2(6, 2.2f),
                // Block 2: LAUNCH — east side heading out
                new Vector2(12, 5f), new Vector2(16, 10f), new Vector2(16, 15f),
                // Block 3: COURSE — far straight
                new Vector2(12, 20f), new Vector2(2, 22f), new Vector2(-8, 21f), new Vector2(-15, 17f),
                // Block 4: BRAKE RUN — west side returning, straightens out before the station
                new Vector2(-17, 11f), new Vector2(-15, 6f), new Vector2(-11.5f, 2.2f), new Vector2(-8.5f, 2.2f),
            };
            string[] blockNames = { "UNLOAD", "LOAD", "LAUNCH", "COURSE", "BRAKE RUN" };
            // Gentle elevation: flat station, launch climbs, course crests, brake run descends.
            float[] heights =
            {
                0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f,   // STATION (Unload + Load, flat)
                1.0f, 2.0f, 2.8f,                                        // LAUNCH climb
                3.2f, 3.4f, 3.0f, 2.2f,                                  // COURSE crest
                1.4f, 1.0f, 0.7f, 0.6f,                                  // BRAKE RUN descent
            };
            for (int i = 0; i < layout.Length; i++)
            {
                var wp = new GameObject($"WP_{i:00}");
                wp.transform.SetParent(trackGO.transform);
                wp.transform.position = new Vector3(layout[i].x, heights[i], layout[i].y);
                path.Waypoints.Add(wp.transform);
                if (i % 4 == 0)
                {
                    var post = Box($"BlockMarker_{i / 4}", wp.transform.position + new Vector3(0, 0.6f, 1.4f),
                                   new Vector3(0.12f, 1.2f, 0.12f), mTrack, trackGO.transform);
                    Label(post, "BLOCK " + (i / 4) + ": " + blockNames[i / 4], 0.9f);
                }
            }
            // Full styled track mesh along the spline: twin octagonal rails, ties, center spine,
            // plumb support columns with footers (TrackMeshBuilder's own Supports child — no
            // primitive support boxes needed here anymore), neon brake fins and station drive tires.
            var trackStyle = new TrackMeshBuilder.TrackStyle();
            trackStyle.AccentMaterial = mNeonCyan; // brake fins + drive tires pick up the neon accent
            TrackMeshBuilder.Build(path, mRail, mTie, trackStyle);

            // ---- Trains (Blender CoasterTrain.fbx; procedural factory as fallback) ----
            var trainPrefab = Prop("CoasterTrain.fbx");
            System.Func<int, Material, TrainVisual> MakeTrain = (id, m) =>
            {
                GameObject body;
                Transform[] seats;
                if (trainPrefab != null)
                {
                    body = PlaceProp(trainPrefab, $"Train{id}", Vector3.zero);
                    SwapMaterial(body, "PS_TrainBody", m);
                    seats = new Transform[8];
                    for (int s = 0; s < 8; s++) seats[s] = FindDeep(body.transform, "Seat" + s);
                    var bars = new List<Transform>();
                    for (int b = 0; b < 4; b++)
                    {
                        var bar = FindDeep(body.transform, "RestraintBar" + b);
                        if (bar != null) bars.Add(bar);
                    }
                    var rd = body.AddComponent<TrainRestraintDriver>();
                    rd.TrainId = id; rd.Bars = bars.ToArray();
                }
                else
                {
                    body = TrainCarFactory.Create($"Train{id}", m, mSeat, mSeat, mGate, out seats);
                }
                var tv = body.AddComponent<TrainVisual>();
                tv.TrainId = id; tv.Path = path;
                tv.SeatAnchors = seats;
                // Per-train juice: speed-driven rumble loop, launch squash/whoosh, brake sparks,
                // restraint clunk + click flash. Runtime-added RVs get theirs from JuiceDirector.
                var tj = body.AddComponent<ParkShift.Juice.TrainJuice>();
                tj.TrainId = id;
                Label(body, "TRAIN " + (id == 0 ? "A" : "B"), 1.6f);
                return tv;
            };
            var trainA = MakeTrain(0, mTrainA);
            var trainB = MakeTrain(1, mTrainB);

            // Flagged-seat prop on train A (restraint fault marker)
            // Reusable low-poly red flag for fault markers (replaces the old red mDanger boxes).
            // Uses the Blender FaultFlag.fbx if present, else falls back to a red box. Any interactor
            // is added by the caller via AddInteractor (trigger-only, so it never shoves the player).
            System.Func<string, Vector3, float, float, GameObject> MakeFaultFlag = (flagName, worldPos, yRot, scale) =>
            {
                var flagPrefab = Prop("FaultFlag.fbx");
                GameObject go = flagPrefab != null
                    ? PlaceProp(flagPrefab, flagName, worldPos, yRot)
                    : Box(flagName, worldPos, new Vector3(0.4f, 0.6f, 0.4f), mDanger);
                if (scale != 1f) go.transform.localScale = Vector3.one * scale;
                return go;
            };

            // Flagged-seat marker on train A (red flag; restraint fault). Interactor = SeatInspect.
            var flagged = MakeFaultFlag("FlaggedSeatMarker", Vector3.zero, 0f, 0.5f);
            flagged.transform.SetParent(trainA.transform, false);
            flagged.transform.localPosition = new Vector3(0.2f, 0.9f, 0f);
            AddInteractor(flagged.transform, PanelInteractable.Kind.SeatInspect,
                new Vector3(0.6f, 1.2f, 0.6f), new Vector3(0f, 0.6f, 0f));
            Label(flagged, "ROW 3", 1.3f);

            // ---- Station gates (Blender swing-door units; sliding cubes as fallback) ----
            var gateRoot = new GameObject("Gates");
            gateRoot.transform.position = new Vector3(0, 0.5f, 1.2f);
            var gatePrefab = Prop("StationGate.fbx");
            if (gatePrefab != null)
            {
                for (int g = 0; g < 4; g++)
                {
                    var unit = PlaceProp(gatePrefab, $"GateUnit{g}",
                        gateRoot.transform.position + new Vector3(-4.5f + 3f * g, 0f, 0f));
                    unit.transform.SetParent(gateRoot.transform, true);
                    var gd = unit.AddComponent<GateUnitDriver>();
                    gd.DoorL = FindDeep(unit.transform, "GateDoorL");
                    gd.DoorR = FindDeep(unit.transform, "GateDoorR");
                }
                gateRoot.AddComponent<GateVisual>().GatePanels = new Transform[0]; // sim mirror handled by drivers
            }
            else
            {
                var gv = gateRoot.AddComponent<GateVisual>();
                var panels = new List<Transform>();
                for (int g = 0; g < 4; g++)
                {
                    var gp = Box($"GatePanel{g}", gateRoot.transform.position + new Vector3(-4.5f + 3f * g, 0.4f, 0),
                                 new Vector3(2.4f, 1.0f, 0.12f), mGate, gateRoot.transform);
                    panels.Add(gp.transform);
                }
                gv.GatePanels = panels.ToArray();
            }

            // ---- Control booth: open-front kiosk — half walls, white corner posts,
            // grape roof, cyan trim and a warm interior point light ----
            Box("BoothFloor", new Vector3(10, 0.05f, -3), new Vector3(6, 0.1f, 5), mBooth);
            Box("BoothWall", new Vector3(10, 1.2f, -5.4f), new Vector3(6, 2.4f, 0.2f), mBooth);
            Box("BoothWallL", new Vector3(7.15f, 0.75f, -3f), new Vector3(0.18f, 1.5f, 5f), mBooth);
            Box("BoothWallR", new Vector3(12.85f, 0.75f, -3f), new Vector3(0.18f, 1.5f, 5f), mBooth);
            Box("BoothRoof", new Vector3(10, 2.75f, -3f), new Vector3(6.6f, 0.16f, 5.6f), mRoof);
            foreach (var bp in new[] { new Vector3(7.15f, 1.7f, -0.62f), new Vector3(12.85f, 1.7f, -0.62f),
                                       new Vector3(7.15f, 1.7f, -5.35f), new Vector3(12.85f, 1.7f, -5.35f) })
                Box("BoothPost", bp, new Vector3(0.22f, 2.2f, 0.22f), mWhite);
            Box("BoothNeon", new Vector3(10, 2.86f, -0.45f), new Vector3(6.7f, 0.08f, 0.08f), mNeonCyan);
            var boothLightGO = new GameObject("BoothLight");
            boothLightGO.transform.position = new Vector3(10, 2.35f, -3.4f);
            var boothLight = boothLightGO.AddComponent<Light>();
            boothLight.type = LightType.Point;
            boothLight.range = 7f;
            boothLight.intensity = 1.3f;
            boothLight.color = new Color(1f, 0.78f, 0.55f);
            boothLight.shadows = LightShadows.None;

            System.Action<string, PanelInteractable.Kind, Vector3, Material, string> Panel = (name, kind, pos, m, label) =>
            {
                var p = Box(name, pos, new Vector3(0.9f, 1.1f, 0.5f), m);
                p.AddComponent<PanelInteractable>().PanelKind = kind;
                Label(p, label, 1.0f);
            };
            // Open-shift lever (green Blender prop; box fallback). Throws to "on" when the shift opens.
            var shiftLeverPrefab = Prop("OpenShiftLever.fbx");
            if (shiftLeverPrefab != null)
            {
                // The lever's mount PLATE normal runs along the model's X. The booth back wall (z=-5.4)
                // faces along Z, so the lever must yaw 270° to lay the flush plate INTO the wall while the
                // handle faces +Z toward the player. (At 0 the plate faced sideways = not flush.)
                var sl = PlaceProp(shiftLeverPrefab, "OpenShiftLever", new Vector3(8.5f, 0.5f, -5.05f), 270f);
                var pivot = FindDeep(sl.transform, "OpenShiftLever_Pivot");
                // Interaction volume on the lever wrapper, raised to cover the handle (~1.1-2.2m up).
                // Trigger-only: lets the player look-to-interact without the collider ever pushing them.
                AddInteractor(sl.transform, PanelInteractable.Kind.ShiftStart,
                    new Vector3(0.7f, 2.2f, 0.7f), new Vector3(0f, 1.1f, 0f));
                sl.AddComponent<ShiftLeverDriver>().Pivot = pivot;
                Label(sl, "SHIFT START", 2.0f);
            }
            else
            {
                Panel("ShiftStartPanel", PanelInteractable.Kind.ShiftStart, new Vector3(8.5f, 0.7f, -5.15f), mPanel, "SHIFT START");
            }
            var consolePrefab = Prop("Console.fbx");
            if (consolePrefab != null)
            {
                var console = PlaceProp(consolePrefab, "DispatchConsole", new Vector3(10.1f, 0.1f, -3.4f));
                var lever = FindDeep(console.transform, "GateLever");
                var button = FindDeep(console.transform, "DispatchButton");
                AddInteractor(lever, PanelInteractable.Kind.GateLever, new Vector3(0.3f, 0.3f, 0.55f));
                AddInteractor(button, PanelInteractable.Kind.DispatchButton, new Vector3(0.28f, 0.28f, 0.28f));
                var cd = console.AddComponent<ConsoleDriver>();
                cd.Lever = lever; cd.Button = button;
                // HMI screen: look at the little screen + press E to open the clickable ride menu.
                // The Screen mesh origin sits at the console root, so the interactor is offset onto the screen.
                var screen = FindDeep(console.transform, "Screen");
                AddInteractor(screen, PanelInteractable.Kind.HmiScreen, new Vector3(0.6f, 0.5f, 0.6f), new Vector3(-0.18f, -0.205f, 0.93f));
                Label(console, "GATES  |  DISPATCH  |  [E] HMI SCREEN", 1.5f);
            }
            else
            {
                Panel("GateLever", PanelInteractable.Kind.GateLever, new Vector3(9.4f, 0.7f, -3.4f), mGate, "GATES");
                Panel("DispatchPanel", PanelInteractable.Kind.DispatchButton, new Vector3(10.8f, 0.7f, -3.4f), mDanger, "DISPATCH");
            }
            Panel("AssistantPhone", PanelInteractable.Kind.AssistantPhone, new Vector3(12.2f, 0.7f, -3.4f), mPanel, "TECH PHONE (H)");

            // Zone-sensor reset button beside the dispatch console (BlockSensorGhost step 2:
            // refused until the operator has walked the course and verified the block on foot).
            var sensorReset = Box("SensorResetButton", new Vector3(11.35f, 1.0f, -3.9f), new Vector3(0.24f, 0.24f, 0.24f), mDanger);
            Object.DestroyImmediate(sensorReset.GetComponent<Collider>()); // trigger interactor replaces the primitive collider
            AddInteractor(sensorReset.transform, PanelInteractable.Kind.SensorReset, new Vector3(0.5f, 0.5f, 0.5f));
            Label(sensorReset, "SENSOR RESET", 0.5f);

            // Platform-side stations: attendant podiums (boxes as fallback)
            var podiumPrefab = Prop("Podium.fbx");
            if (podiumPrefab != null)
            {
                System.Action<string, PanelInteractable.Kind, Vector3, Material, string> Podium = (name, kind, pos, accent, label) =>
                {
                    var p = PlaceProp(podiumPrefab, name, pos, 180f); // face the platform interior
                    SwapMaterial(p, "PS_Blue", accent);
                    // Interaction volume raised to the podium head/screen (~0.95m up on the model, ~1.45m
                    // world on the platform) so the prompt sits at the working height, not at the base.
                    AddInteractor(p.transform, kind, new Vector3(0.7f, 0.8f, 0.7f), new Vector3(0f, 0.95f, 0f));
                    var btn = FindDeep(p.transform, "PodiumButton");
                    if (btn != null)
                    {
                        var pd = p.AddComponent<PodiumDriver>();
                        pd.Button = btn;
                        pd.PodiumMode = kind == PanelInteractable.Kind.LoadHelper
                            ? PodiumDriver.Mode.Load : PodiumDriver.Mode.Restraint;
                    }
                    Label(p, label, 1.35f);
                };
                Podium("LoadHelper", PanelInteractable.Kind.LoadHelper, new Vector3(-3, 0.5f, 0.2f), mPanel, "WAVE ABOARD");
                Podium("RestraintStation", PanelInteractable.Kind.RestraintCheck, new Vector3(3, 0.5f, 0.2f), mGate, "RESTRAINT CHECK");
            }
            else
            {
                Panel("LoadHelper", PanelInteractable.Kind.LoadHelper, new Vector3(-3, 0.7f, 0.2f), mPanel, "WAVE ABOARD");
                Panel("RestraintStation", PanelInteractable.Kind.RestraintCheck, new Vector3(3, 0.7f, 0.2f), mPanel, "RESTRAINT CHECK");
            }

            // ---- Maintenance bay ----
            Box("MaintBayFloor", new Vector3(-14, 0.05f, -4), new Vector3(8, 0.1f, 7), mBooth);
            GameObject lotoTag;
            var cabinetPrefab = Prop("DriveCabinet.fbx");
            if (cabinetPrefab != null)
            {
                var cab = PlaceProp(cabinetPrefab, "DriveCabinet", new Vector3(-15f, 0.1f, -6.2f));
                var breaker = FindDeep(cab.transform, "BreakerLever");
                var resetBtn = FindDeep(cab.transform, "ResetBtn");
                AddInteractor(breaker, PanelInteractable.Kind.LotoLever, new Vector3(0.3f, 0.3f, 0.55f));
                AddInteractor(resetBtn, PanelInteractable.Kind.MaintenancePanel, new Vector3(0.3f, 0.3f, 0.3f));
                var cabd = cab.AddComponent<CabinetDriver>();
                cabd.Door = FindDeep(cab.transform, "CabDoor");
                cabd.Breaker = breaker;
                cabd.ResetButton = resetBtn;
                Label(cab, "DRIVE CABINET — LOCKOUT + RESET", 2.1f);
                lotoTag = Box("LotoTag", new Vector3(-14.3f, 1.1f, -6.0f), new Vector3(0.25f, 0.35f, 0.05f), mGate);
            }
            else
            {
                Panel("MaintenancePanel", PanelInteractable.Kind.MaintenancePanel, new Vector3(-16, 0.7f, -6.2f), mPanel, "DRIVE RESET");
                var loto = Box("LotoLever", new Vector3(-12, 0.8f, -6.2f), new Vector3(0.5f, 1.6f, 0.5f), mDanger);
                loto.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.LotoLever;
                Label(loto, "LOCKOUT", 1.2f);
                lotoTag = Box("LotoTag", loto.transform.position + new Vector3(0.5f, 0.2f, 0), new Vector3(0.25f, 0.35f, 0.05f), mGate);
            }
            Label(lotoTag, "TAGGED OUT", 0.4f);

            // Bus-stage button at the drive cabinet (PowerBrownout: technician energizes the
            // three drive bus stages in order, letting each stabilize before the next).
            var busStage = Box("BusStageButton", new Vector3(-13.6f, 1.0f, -6.2f), new Vector3(0.24f, 0.24f, 0.24f), mGate);
            Object.DestroyImmediate(busStage.GetComponent<Collider>()); // trigger interactor replaces the primitive collider
            AddInteractor(busStage.transform, PanelInteractable.Kind.BrownoutStage, new Vector3(0.5f, 0.5f, 0.5f));
            Label(busStage, "BUS STAGE", 0.5f);
            // Undervolt warning light above the cabinet — lit only during the brownout
            // (toggled by FaultPropController below).
            var brownoutWarn = Box("BrownoutWarnLight", new Vector3(-13.6f, 1.7f, -6.2f), new Vector3(0.2f, 0.2f, 0.2f), mNeonMagenta);
            Object.DestroyImmediate(brownoutWarn.GetComponent<Collider>()); // pure indicator — no physics, no interaction
            Label(brownoutWarn, "BUS UNDERVOLT", 0.4f);

            // ---- Dropped item prop (near station edge) ----
            var item = Box("DroppedItem", new Vector3(5.5f, 0.65f, 1.6f), new Vector3(0.35f, 0.35f, 0.35f), mGate);
            item.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.DroppedItem;
            Label(item, "GUEST ITEM", 0.5f);

            // ---- Technician incident props ----
            // Guest who climbed onto the track (e-stop scenario)
            GameObject intruder;
            if (deweyPrefab != null)
            {
                intruder = (GameObject)Object.Instantiate(deweyPrefab);
                intruder.name = "TrackIntruder";
                intruder.transform.position = new Vector3(9f, 0.65f, 3.1f);
                intruder.transform.localScale = Vector3.one * 0.5f;
                var ianim = intruder.GetComponent<Animator>();
                if (ianim == null) ianim = intruder.AddComponent<Animator>();
                ianim.runtimeAnimatorController = deweyCtrl; // plays Idle by default
                var icol = intruder.AddComponent<CapsuleCollider>();
                icol.height = 2.6f; icol.radius = 0.7f; icol.center = Vector3.up * 1.3f;
            }
            else
            {
                intruder = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                intruder.name = "TrackIntruder";
                intruder.transform.position = new Vector3(9f, 1.1f, 3.1f);
                intruder.transform.localScale = new Vector3(0.5f, 0.8f, 0.5f);
                intruder.GetComponent<Renderer>().sharedMaterial = mDanger;
            }
            intruder.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.IntruderEscort;
            Label(intruder, "GUEST ON TRACK!", deweyPrefab != null ? 2.9f : 1.3f);

            // Collision contact-point marker on the brake run approach (red flag). Interactor = CollisionInspect.
            var crash = MakeFaultFlag("CollisionMarker", new Vector3(-9.5f, 0.6f, 2.2f), 0f, 1f);
            AddInteractor(crash.transform, PanelInteractable.Kind.CollisionInspect,
                new Vector3(0.7f, 1.4f, 0.7f), new Vector3(0f, 0.7f, 0f));
            Label(crash, "CONTACT POINT", 1.5f);

            // Evac winch out on the course (valleyed-train recovery — a real hike for the tech).
            // Red Blender lever (box fallback); throws + cranks while the tech works the recovery.
            // The lever is permanent equipment; the beacon above it only shows during the fault.
            var evacPrefab = Prop("EvacLever.fbx");
            if (evacPrefab != null)
            {
                var evac = PlaceProp(evacPrefab, "EvacLever", new Vector3(0, 0.5f, 19.5f), 180f);
                var pivot = FindDeep(evac.transform, "EvacLever_Pivot");
                // Interaction volume on the lever wrapper, raised to cover the handle. Trigger-only.
                AddInteractor(evac.transform, PanelInteractable.Kind.WinchPanel,
                    new Vector3(0.8f, 2.2f, 0.8f), new Vector3(0f, 1.1f, 0f));
                evac.AddComponent<EvacLeverDriver>().Pivot = pivot;
                Label(evac, "EVAC WINCH", 2.0f);
            }
            else
            {
                var winch = Box("EvacWinch", new Vector3(0, 0.7f, 19.5f), new Vector3(0.9f, 1.1f, 0.6f), mPanel);
                winch.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.WinchPanel;
                Label(winch, "EVAC WINCH", 1.0f);
            }
            // Stalled-train indicator out on the course (red flag; non-interactable, toggled by FaultPropController).
            var valleyBeacon = MakeFaultFlag("ValleyedBeacon", new Vector3(0, 3.4f, 21f), 0f, 1.4f);
            Label(valleyBeacon, "TRAIN STALLED", 2.2f);

            // ---- Zone sensor post out by block 2 (BlockSensorGhost step 1: the operator hikes
            // to the launch zone and visually confirms the block is clear before the reset). ----
            var zoneSensor = new GameObject("ZoneSensorPost");
            zoneSensor.transform.position = new Vector3(14.2f, 0f, 10f);
            Cyl("SensorPole", new Vector3(14.2f, 0.75f, 10f), new Vector3(0.09f, 0.75f, 0.09f), mLampPole, zoneSensor.transform);
            Box("SensorHead", new Vector3(14.2f, 1.6f, 10f), new Vector3(0.3f, 0.3f, 0.3f), mBooth, zoneSensor.transform);
            AddInteractor(zoneSensor.transform, PanelInteractable.Kind.BlockVerify,
                new Vector3(0.9f, 1.9f, 0.9f), new Vector3(0f, 0.95f, 0f));
            Label(zoneSensor, "ZONE SENSOR", 2.1f);
            // Phantom-occupancy beacon on the post — lit only while the ghost fault is active.
            var ghostBeacon = Ball("GhostSensorBeacon", new Vector3(14.2f, 2.05f, 10f), 0.32f, mNeonMagenta, zoneSensor.transform);

            // ---- Lap-bar check points along the Load berth edge (RestraintJammedCar: the HMI
            // names the flagged car; checking the wrong car is refused by the sim). ----
            var lapBarRoot = new GameObject("LapBarCheckPoints");
            var lapBarAnchors = new Transform[4];
            for (int carIdx = 0; carIdx < 4; carIdx++)
            {
                float lx = 1.65f + 0.9f * carIdx; // spans the Load berth (train parks at x=+3)
                var pt = Box($"LapBarCheck{carIdx}", new Vector3(lx, 0.56f, 1.5f), new Vector3(0.3f, 0.12f, 0.3f), mGate, lapBarRoot.transform);
                Object.DestroyImmediate(pt.GetComponent<Collider>()); // trigger interactor replaces the primitive collider
                AddInteractor(pt.transform, PanelInteractable.Kind.LapBarUnjam,
                    new Vector3(0.55f, 1.0f, 0.55f), new Vector3(0f, 0.45f, 0f));
                pt.GetComponent<PanelInteractable>().CarIndex = carIdx;
                Label(pt, "CAR " + (carIdx + 1), 0.55f);
                lapBarAnchors[carIdx] = pt.transform;
            }
            // Jam marker flag — FaultPropController snaps it to the flagged car's check point.
            var jamFlag = MakeFaultFlag("JammedCarFlag", new Vector3(3f, 0.56f, 1.5f), 0f, 0.5f);
            Label(jamFlag, "LAP BAR JAM", 1.3f);

            // Fault prop controller wiring
            var props = new GameObject("FaultProps").AddComponent<FaultPropController>();
            props.DroppedItemProp = item;
            props.FlaggedSeatProp = flagged;
            props.LotoTagProp = lotoTag;
            props.IntruderProp = intruder;
            props.CollisionProp = crash;
            props.ValleyedProp = valleyBeacon;
            props.GhostSensorProp = ghostBeacon;
            props.BrownoutCabinetProp = brownoutWarn;
            props.JammedCarProp = jamFlag;
            props.JammedCarAnchors = lapBarAnchors;

            // ---- Queue ----
            var queueRoot = new GameObject("Queue");
            var gvm = queueRoot.AddComponent<GuestVisualManager>();
            gvm.GuestMaterial = mGuest;
            gvm.GuestPrefab = deweyPrefab;
            gvm.GuestAnimController = deweyCtrl;
            gvm.Trains = new[] { trainA, trainB };
            var spawn = new GameObject("GuestSpawn"); spawn.transform.position = new Vector3(-2, 0f, -16);
            var exit = new GameObject("GuestExit"); exit.transform.position = new Vector3(14, 0f, 4);
            gvm.SpawnPoint = spawn.transform; gvm.ExitPoint = exit.transform;
            // zig-zag queue spots from gates backwards
            for (int q = 0; q < 40; q++)
            {
                int row = q / 8; int col = q % 8;
                float x = (row % 2 == 0) ? -4.2f + 1.2f * col : 4.2f - 1.2f * col;
                var spot = new GameObject($"QSpot{q:00}");
                spot.transform.SetParent(queueRoot.transform);
                spot.transform.position = new Vector3(x, 0f, -2.8f - 1.3f * row); // ground level — guest origins are at their feet
                gvm.QueueSpots.Add(spot.transform);
            }
            // Queue rails in serpentine pattern: each row leaves a gap at the turn end
            // (east on even rows, west on odd), matching the zig-zag of the queue spots.
            // Solid colliders so the player (and visually the guests) must snake through.
            var railPrefab = Prop("QueueRail.fbx");
            var railsRoot = new GameObject("QueueRails");
            for (int row = 0; row <= 4; row++)
            {
                bool gapEast = row % 2 == 0;
                for (int mIdx = 0; mIdx < 7; mIdx++)
                {
                    if (gapEast && mIdx == 6) continue;   // turn opening, east end
                    if (!gapEast && mIdx == 0) continue;  // turn opening, west end
                    Vector3 pos = new Vector3(-4.5f + 1.5f * mIdx, 0f, -3.45f - 1.3f * row);
                    GameObject seg;
                    if (railPrefab != null)
                    {
                        seg = PlaceProp(railPrefab, $"QueueRail_{row}_{mIdx}", pos);
                    }
                    else
                    {
                        seg = Box($"QueueRail_{row}_{mIdx}", pos + Vector3.up * 0.45f,
                                  new Vector3(1.5f, 0.08f, 0.08f), mPlatform);
                        seg.transform.position = pos + Vector3.up * 0.45f;
                    }
                    var rc = seg.AddComponent<BoxCollider>();
                    rc.center = new Vector3(0, 0.55f, 0);
                    rc.size = new Vector3(1.5f, 1.1f, 0.14f);
                    seg.transform.SetParent(railsRoot.transform, true);
                }
            }
            // queue entry waypoint: guests come in through the back-row gap
            var qEntry = new GameObject("QueueEntry");
            qEntry.transform.position = new Vector3(4.4f, 0.0f, -9.2f);
            qEntry.transform.SetParent(queueRoot.transform, true);
            gvm.QueueEntryPoint = qEntry.transform;

            // ---- Load / Unload station markers + controller ----
            // The station straight runs z=2.2, x=-6..6. Left half = Unload (RVs drop riders),
            // right half = Load (RVs board). These markers + controller are harmless in single-station
            // mode; they become live when a SimConfig with SeparateLoadUnload is assigned and the
            // TrackPath blocks 0 (Unload) / 1 (Load) are positioned on the station straight.
            System.Func<string, Vector3, GameObject> Marker = (mName, mPos) =>
            {
                var g = new GameObject(mName);
                g.transform.position = mPos;
                return g;
            };

            var stationCtl = new GameObject("StationController").AddComponent<StationController>();
            stationCtl.UnloadStopPoint = Marker("Station_Unload_StopPoint", new Vector3(-3f, 0.6f, 2.2f)).transform;
            stationCtl.LoadStopPoint   = Marker("Station_Load_StopPoint",   new Vector3( 3f, 0.6f, 2.2f)).transform;

            var unloadExits = new List<Transform>();
            for (int e = 0; e < 3; e++)
                unloadExits.Add(Marker($"Guest_Unload_ExitPoint_{e + 1:00}", new Vector3(-5f + e * 1.6f, 0f, 0.7f)).transform);
            var loadEntries = new List<Transform>();
            for (int e = 0; e < 3; e++)
                loadEntries.Add(Marker($"Guest_Load_EntryPoint_{e + 1:00}", new Vector3(1f + e * 1.6f, 0f, 0.7f)).transform);

            stationCtl.UnloadGuestExitPoints = unloadExits.ToArray();
            stationCtl.LoadGuestEntryPoints  = loadEntries.ToArray();
            // Unloaded riders step off at the Unload side and spread across the exit markers, so they
            // never walk through the boarding queue on the Load side.
            gvm.UnloadExitPoints = unloadExits;

            // ---- Unload door (operator-controlled gate guests leave through) ----
            var unloadDoorRoot = new GameObject("UnloadDoor");
            unloadDoorRoot.transform.position = new Vector3(-5.5f, 0f, 0.5f);
            Box("UnloadDoorFrameL", new Vector3(-6.45f, 1.1f, 0.5f), new Vector3(0.2f, 2.3f, 0.3f), mBooth, unloadDoorRoot.transform);
            Box("UnloadDoorFrameR", new Vector3(-4.55f, 1.1f, 0.5f), new Vector3(0.2f, 2.3f, 0.3f), mBooth, unloadDoorRoot.transform);
            Box("UnloadDoorHeader", new Vector3(-5.5f, 2.35f, 0.5f), new Vector3(2.1f, 0.25f, 0.3f), mBooth, unloadDoorRoot.transform);
            var unloadDoorPanel = Box("UnloadDoorPanel", new Vector3(-5.5f, 1.0f, 0.5f), new Vector3(1.7f, 1.9f, 0.12f), mGate, unloadDoorRoot.transform);
            unloadDoorRoot.AddComponent<UnloadDoorDriver>().DoorPanel = unloadDoorPanel.transform;
            Label(unloadDoorRoot, "UNLOAD DOOR", 2.7f);

            // ---- Exit door (simple primitive; guests walk here then despawn) ----
            var exitDoorRoot = new GameObject("UnloadExitDoor");
            exitDoorRoot.transform.position = new Vector3(-10.5f, 0f, -3f);
            Box("ExitDoorFrameL", new Vector3(-11.4f, 1.1f, -3f), new Vector3(0.2f, 2.3f, 0.3f), mBooth, exitDoorRoot.transform);
            Box("ExitDoorFrameR", new Vector3(-9.6f, 1.1f, -3f), new Vector3(0.2f, 2.3f, 0.3f), mBooth, exitDoorRoot.transform);
            Box("ExitDoorHeader", new Vector3(-10.5f, 2.35f, -3f), new Vector3(2f, 0.25f, 0.3f), mBooth, exitDoorRoot.transform);
            Box("ExitDoorPanel", new Vector3(-10.5f, 1.05f, -3f), new Vector3(1.5f, 2f, 0.1f), mPanel, exitDoorRoot.transform);
            Label(exitDoorRoot, "EXIT", 2.8f);

            // Exit path: Unload side -> off the platform -> exit door (last node = despawn point).
            var exitPath = new List<Transform>
            {
                Marker("GuestExitPath_01", new Vector3(-5f,   0f,  0.0f)).transform,
                Marker("GuestExitPath_02", new Vector3(-7.5f, 0f, -1.2f)).transform,
                Marker("GuestExitPath_03", new Vector3(-9.5f, 0f, -2.6f)).transform,
                Marker("UnloadExitDoorPoint", new Vector3(-10.5f, 0f, -3f)).transform,
            };
            gvm.UnloadExitPath = exitPath;

            // ---- Unload control podium (left of Unload): Open / Close door + Release restraints ----
            var unloadPodiumPrefab = Prop("Podium.fbx");
            var upPos = new Vector3(-6.8f, 0.5f, 1.2f);
            GameObject upRoot = unloadPodiumPrefab != null
                ? PlaceProp(unloadPodiumPrefab, "UnloadControlPodium", upPos, 90f)
                : Box("UnloadControlPodium", upPos + Vector3.up * 0.6f, new Vector3(0.6f, 1.2f, 0.6f), mDanger);
            if (unloadPodiumPrefab != null) SwapMaterial(upRoot, "PS_Blue", mDanger);

            System.Func<string, PanelInteractable.Kind, Vector3, string, GameObject> UnloadButton = (n, kind, worldPos, lbl) =>
            {
                var b = Box(n, worldPos, new Vector3(0.2f, 0.12f, 0.24f), mPanel, upRoot.transform);
                var col = b.GetComponent<Collider>();
                if (col) Object.DestroyImmediate(col);            // replace the primitive collider with a trigger interactor
                AddInteractor(b.transform, kind, new Vector3(0.4f, 0.4f, 0.4f));
                Label(b, lbl, 0.4f);
                return b;
            };
            var openBtn  = UnloadButton("UnloadPodium_OpenDoorButton",        PanelInteractable.Kind.UnloadDoorOpen,    new Vector3(-7.05f, 1.4f, 1.15f), "OPEN DOOR");
            var closeBtn = UnloadButton("UnloadPodium_CloseDoorButton",       PanelInteractable.Kind.UnloadDoorClose,   new Vector3(-6.8f,  1.4f, 1.15f), "CLOSE DOOR");
            var relBtn   = UnloadButton("UnloadPodium_ReleaseRestraintsButton", PanelInteractable.Kind.ReleaseRestraints, new Vector3(-6.55f, 1.4f, 1.15f), "RELEASE RESTRAINTS");
            var upd = upRoot.AddComponent<UnloadPodiumDriver>();
            upd.OpenButton = openBtn.transform; upd.CloseButton = closeBtn.transform; upd.ReleaseButton = relBtn.transform;
            Label(upRoot, "UNLOAD CONTROL", 1.6f);

            // ================= PARK DRESSING (pure decor — no gameplay wiring) =================
            var parkRoot = new GameObject("ParkDressing");
            var rng = new System.Random(20260703); // fixed seed: rebuilds are deterministic
            System.Func<float, float, float> Rnd = (lo, hi) => lo + (float)rng.NextDouble() * (hi - lo);

            // ---- Plaza path: colored panels from the spawn plaza to the queue entrance ----
            var pathRoot = new GameObject("PlazaPath");
            pathRoot.transform.SetParent(parkRoot.transform, false);
            Vector3[] pathPts = { new Vector3(-2, 0, -18.5f), new Vector3(-2, 0, -11.5f), new Vector3(4.4f, 0, -9.8f) };
            int panelIdx = 0;
            for (int seg = 0; seg + 1 < pathPts.Length; seg++)
            {
                Vector3 a = pathPts[seg], b = pathPts[seg + 1];
                int n = Mathf.Max(1, Mathf.RoundToInt(Vector3.Distance(a, b) / 1.8f));
                for (int k = 0; k < n; k++)
                {
                    Vector3 pos = Vector3.Lerp(a, b, (k + 0.5f) / n) + new Vector3(0, 0.02f, 0);
                    Material pm = (panelIdx % 3 == 0) ? mPathA : (panelIdx % 3 == 1) ? mPathB : mWhite;
                    var panel = Box($"PathPanel{panelIdx:00}", pos, new Vector3(1.7f, 0.04f, 1.7f), pm, pathRoot.transform);
                    panel.transform.rotation = Quaternion.Euler(0, Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg, 0);
                    Object.DestroyImmediate(panel.GetComponent<Collider>()); // flat decor — no ledges, lean physics
                    panelIdx++;
                }
            }
            // Ride-entrance arch over the queue's back-row opening
            Box("ArchPostL", new Vector3(3.1f, 1.25f, -9.3f), new Vector3(0.16f, 2.5f, 0.16f), mWhite, parkRoot.transform);
            Box("ArchPostR", new Vector3(6.0f, 1.25f, -9.3f), new Vector3(0.16f, 2.5f, 0.16f), mWhite, parkRoot.transform);
            var archHeader = Box("ArchHeader", new Vector3(4.55f, 2.62f, -9.3f), new Vector3(3.3f, 0.35f, 0.2f), mRoof, parkRoot.transform);
            Box("ArchNeon", new Vector3(4.55f, 2.42f, -9.3f), new Vector3(3.34f, 0.07f, 0.24f), mNeonCyan, parkRoot.transform);
            Label(archHeader, "RIDE ENTRANCE", 0.55f);

            // ---- Trees: cylinder trunk + two stacked flat-shaded cones ----
            System.Func<string, Vector3, float, Material, Transform, GameObject> Tree = (tName, tPos, s, foliage, tParent) =>
            {
                var t = new GameObject(tName);
                t.transform.position = tPos;
                t.transform.SetParent(tParent, true);
                var trunk = Cyl("Trunk", tPos + new Vector3(0, 0.5f * s, 0), new Vector3(0.16f * s, 0.5f * s, 0.16f * s), mTrunk, t.transform);
                Object.DestroyImmediate(trunk.GetComponent<Collider>());
                Cone("Canopy0", tPos + new Vector3(0, 0.75f * s, 0), 0.85f * s, 1.5f * s, 7, foliage, t.transform);
                Cone("Canopy1", tPos + new Vector3(0, 1.65f * s, 0), 0.55f * s, 1.1f * s, 7, foliage, t.transform);
                return t;
            };
            System.Action<string, Vector3, float, Material> Planter = (pName, pPos, s, foliage) =>
            {
                var shell = Box(pName, pPos + new Vector3(0, 0.25f, 0), new Vector3(1.5f, 0.5f, 1.5f), mPlanter, parkRoot.transform);
                Box(pName + "_Soil", pPos + new Vector3(0, 0.47f, 0), new Vector3(1.3f, 0.12f, 1.3f), mSoil, shell.transform);
                Tree(pName + "_Tree", pPos + new Vector3(0, 0.5f, 0), s, foliage, shell.transform);
            };
            Planter("Planter00", new Vector3(-7f, 0, -9.5f), 1.0f, mFoliageA);
            Planter("Planter01", new Vector3(7.2f, 0, -7f), 0.9f, mFoliageB);
            Planter("Planter02", new Vector3(-7f, 0, -4.5f), 1.1f, mFoliageB);
            Planter("Planter03", new Vector3(-6.5f, 0, -13.5f), 1.0f, mFoliageA);
            Planter("Planter04", new Vector3(7f, 0, -13f), 1.1f, mFoliageA);
            Planter("Planter05", new Vector3(2.5f, 0, -19.8f), 0.9f, mFoliageB);
            Planter("Planter06", new Vector3(14f, 0, -2.5f), 1.0f, mFoliageA);
            Planter("Planter07", new Vector3(-12.3f, 0, -5.5f), 0.9f, mFoliageB);
            // Free-standing trees around the plaza and inside/around the track loop
            var groveRoot = new GameObject("Trees");
            groveRoot.transform.SetParent(parkRoot.transform, false);
            Vector3[] treeSpots =
            {
                new Vector3(-12f, 0, -14f), new Vector3(12.5f, 0, -13f), new Vector3(-16f, 0, -7f),
                new Vector3(16.5f, 0, -10f), new Vector3(10f, 0, -18.5f), new Vector3(-9f, 0, -19.5f),
                new Vector3(20f, 0, 16f), new Vector3(-21f, 0, 3f), new Vector3(-6f, 0, 10f),
                new Vector3(5f, 0, 13f), new Vector3(0f, 0, 26f), new Vector3(22f, 0, 26f),
                new Vector3(-24f, 0, 22f), new Vector3(-12f, 0, 26f), new Vector3(25f, 0, 8f),
            };
            for (int tIdx = 0; tIdx < treeSpots.Length; tIdx++)
                Tree($"Tree{tIdx:00}", treeSpots[tIdx], Rnd(0.9f, 1.5f),
                     tIdx % 2 == 0 ? mFoliageA : mFoliageB, groveRoot.transform);

            // ---- Rocks: rotated, squashed cubes in small clusters ----
            var rockRoot = new GameObject("Rocks");
            rockRoot.transform.SetParent(parkRoot.transform, false);
            Vector3[] rockSpots =
            {
                new Vector3(17f, 0, 6f), new Vector3(19f, 0, 12f), new Vector3(-19f, 0, 9f),
                new Vector3(-14f, 0, 20f), new Vector3(9f, 0, 24.5f), new Vector3(-2f, 0, 25f),
                new Vector3(14f, 0, -18f), new Vector3(-13f, 0, -18f), new Vector3(-21f, 0, -8f),
            };
            for (int rIdx = 0; rIdx < rockSpots.Length; rIdx++)
            {
                var big = Box($"Rock{rIdx:00}", rockSpots[rIdx] + new Vector3(0, 0.25f, 0),
                              new Vector3(Rnd(1.1f, 1.9f), Rnd(0.6f, 1f), Rnd(0.9f, 1.5f)), mRock, rockRoot.transform);
                big.transform.rotation = Quaternion.Euler(Rnd(-8f, 8f), Rnd(0f, 360f), Rnd(-8f, 8f));
                var small = Box($"Rock{rIdx:00}b", rockSpots[rIdx] + new Vector3(Rnd(0.7f, 1.1f), 0.15f, Rnd(-0.5f, 0.5f)),
                                new Vector3(Rnd(0.4f, 0.7f), Rnd(0.3f, 0.5f), Rnd(0.4f, 0.7f)), mRock, rockRoot.transform);
                small.transform.rotation = Quaternion.Euler(Rnd(-12f, 12f), Rnd(0f, 360f), Rnd(-12f, 12f));
            }

            // ---- Cosmic crystal clusters (emissive, slow pulse) ----
            System.Action<string, Vector3> CrystalCluster = (cName, cPos) =>
            {
                var c = new GameObject(cName);
                c.transform.position = cPos;
                c.transform.SetParent(parkRoot.transform, true);
                Cone("Spire0", cPos, 0.45f, 2.6f, 5, mCrystal, c.transform);
                var s1 = Cone("Spire1", cPos + new Vector3(0.5f, 0, 0.2f), 0.3f, 1.6f, 5, mCrystal, c.transform);
                s1.transform.rotation = Quaternion.Euler(0, 0, -12f);
                var s2 = Cone("Spire2", cPos + new Vector3(-0.4f, 0, -0.3f), 0.24f, 1.2f, 5, mCrystal, c.transform);
                s2.transform.rotation = Quaternion.Euler(8f, 0, 10f);
                var cp = c.AddComponent<PSEnv.NeonPulse>();
                cp.EmissionColor = new Color(0.65f, 0.4f, 1f);
                cp.Intensity = 1.3f; cp.PulseAmount = 0.3f; cp.Speed = 0.8f; cp.Phase = Rnd(0f, 6.28f);
            };
            CrystalCluster("Crystals00", new Vector3(18.5f, 0, 0.5f));
            CrystalCluster("Crystals01", new Vector3(-20f, 0, 14f));
            CrystalCluster("Crystals02", new Vector3(10f, 0, 25f));
            CrystalCluster("Crystals03", new Vector3(-6.5f, 0, -20f));

            // ---- Lamp posts (warm bulbs; a couple carry real point lights) ----
            System.Func<string, Vector3, bool, GameObject> Lamp = (lName, lPos, realLight) =>
            {
                var lamp = new GameObject(lName);
                lamp.transform.position = lPos;
                lamp.transform.SetParent(parkRoot.transform, true);
                Cyl("Pole", lPos + new Vector3(0, 1.5f, 0), new Vector3(0.09f, 1.5f, 0.09f), mLampPole, lamp.transform);
                Box("Head", lPos + new Vector3(0, 3.12f, 0), new Vector3(0.34f, 0.12f, 0.34f), mLampPole, lamp.transform);
                Ball("Bulb", lPos + new Vector3(0, 2.92f, 0), 0.3f, mLampGlow, lamp.transform);
                var pulse = lamp.AddComponent<PSEnv.NeonPulse>();
                pulse.EmissionColor = new Color(1f, 0.75f, 0.45f);
                pulse.Intensity = 2f; pulse.PulseAmount = 0.08f; pulse.Speed = 2.2f; pulse.Phase = Rnd(0f, 6.28f);
                if (realLight)
                {
                    var lgo = new GameObject("PointLight");
                    lgo.transform.SetParent(lamp.transform, false);
                    lgo.transform.position = lPos + new Vector3(0, 2.7f, 0);
                    var l = lgo.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.range = 8f; l.intensity = 1.2f;
                    l.color = new Color(1f, 0.78f, 0.5f);
                    l.shadows = LightShadows.None;
                    pulse.LinkedLight = l; pulse.LightIntensity = 1.2f;
                }
                return lamp;
            };
            Lamp("Lamp00", new Vector3(-4.8f, 0, -11.2f), true);   // plaza, queue side
            Lamp("Lamp01", new Vector3(5.6f, 0, -10.6f), true);    // by the entrance arch
            Lamp("Lamp02", new Vector3(-2f, 0, -18.6f), false);
            Lamp("Lamp03", new Vector3(8f, 0, -14f), false);
            Lamp("Lamp04", new Vector3(-8f, 0, -15f), false);
            Lamp("Lamp05", new Vector3(13.5f, 0, -5f), false);     // outside the booth
            Lamp("Lamp06", new Vector3(-12.5f, 0, -1.5f), false);  // by the exit door path

            // ---- String lights zig-zagging over the queue ----
            var stringRoot = new GameObject("StringLights");
            stringRoot.transform.SetParent(parkRoot.transform, false);
            Vector3[] sPoles =
            {
                new Vector3(-5.9f, 0, -3.6f), new Vector3(5.9f, 0, -5.2f),
                new Vector3(-5.9f, 0, -6.8f), new Vector3(5.9f, 0, -8.4f),
            };
            const float poleTop = 2.8f;
            foreach (var sp in sPoles)
                Cyl("StringPole", sp + new Vector3(0, poleTop * 0.5f, 0), new Vector3(0.07f, poleTop * 0.5f, 0.07f), mLampPole, stringRoot.transform);
            for (int span = 0; span + 1 < sPoles.Length; span++)
            {
                Vector3 a = sPoles[span] + Vector3.up * poleTop;
                Vector3 b = sPoles[span + 1] + Vector3.up * poleTop;
                var wire = Box($"StringWire{span}", (a + b) * 0.5f, new Vector3(0.03f, 0.03f, Vector3.Distance(a, b)), mLampPole, stringRoot.transform);
                wire.transform.rotation = Quaternion.LookRotation(b - a);
                Object.DestroyImmediate(wire.GetComponent<Collider>());
                const int bulbs = 15;
                for (int k = 1; k < bulbs; k++)
                {
                    float t = k / (float)bulbs;
                    Vector3 bp2 = Vector3.Lerp(a, b, t) + Vector3.down * (0.45f * 4f * t * (1f - t) + 0.08f);
                    Ball($"StringBulb{span}_{k:00}", bp2, 0.16f, mStringBulb, stringRoot.transform);
                }
            }

            // ---- Maintenance bay dressing: back wall, hazard trim, work lamp ----
            Box("MaintWall", new Vector3(-14, 1.3f, -7.4f), new Vector3(8, 2.6f, 0.2f), mBooth);
            Box("MaintTrim", new Vector3(-14, 2.66f, -7.4f), new Vector3(8.2f, 0.1f, 0.26f), mNeonMagenta);
            Lamp("MaintLamp", new Vector3(-10.8f, 0, -6.6f), true);

            // ---- Horizon: low-poly hill silhouettes so the skyline isn't empty ----
            var hillsRoot = new GameObject("Hills");
            hillsRoot.transform.SetParent(parkRoot.transform, false);
            for (int hIdx = 0; hIdx < 14; hIdx++)
            {
                float ang = hIdx * (Mathf.PI * 2f / 14f) + Rnd(0f, 0.3f);
                float dist = 58f + Rnd(0f, 26f);
                var hp = new Vector3(Mathf.Cos(ang) * dist, -1.5f, Mathf.Sin(ang) * dist + 8f);
                Cone($"Hill{hIdx:00}", hp, Rnd(14f, 26f), Rnd(7f, 16f), 8,
                     hIdx % 3 == 0 ? mHillFar : mHill, hillsRoot.transform);
            }

            // ---- Managers, spawn, bootstrap ----
            var attractionMgr = new GameObject("AttractionManager").AddComponent<AttractionManager>();
            attractionMgr.SeparateLoadUnload = true;  // station split: block 0 = Unload, block 1 = Load
            attractionMgr.RvCount = 2;                 // one RV at Unload, one at Load (matches the 5-block track)
            attractionMgr.EscalatedShift = true;       // Wave-1 content: extended fault catalog, fault/arrival
                                                       // ramps + surge waves (inspector preset defaults).
                                                       // Applied in the Unity layer only — Core defaults untouched.
            attractionMgr.RvPrefab = trainPrefab;      // for runtime "Add RV" from the HMI (null-safe)
            attractionMgr.Track = path;
            attractionMgr.GuestVisuals = gvm;
            attractionMgr.AddedRvMaterial = mTrainB;
            // ---- Juice: procedural audio + particle FX + game-feel director (no asset files;
            // everything is synthesized/configured in code at runtime). All components degrade
            // gracefully if a reference is missing, so rebuilds stay safe. ----
            var juiceGO = new GameObject("Juice");
            var audioDir = juiceGO.AddComponent<ParkShift.Juice.AudioDirector>();
            juiceGO.AddComponent<ParkShift.Juice.JuiceFx>();
            var juiceDir = juiceGO.AddComponent<ParkShift.Juice.JuiceDirector>();
            var queueCenter = new GameObject("QueueCenter");
            queueCenter.transform.SetParent(juiceGO.transform, false);
            queueCenter.transform.position = new Vector3(0f, 1.2f, -5.6f);   // middle of the serpentine
            audioDir.QueueCenter = queueCenter.transform;                    // crowd walla + grumble loops
            audioDir.StationCenter = stationCtl.LoadStopPoint;               // fault klaxon
            juiceDir.QueueCenter = queueCenter.transform;                    // cheer origin
            juiceDir.StationCenter = stationCtl.LoadStopPoint;               // confetti + dispatch FX
            juiceDir.GateCenter = gateRoot.transform;                        // gate servo audio

            // Clickable ride HMI (opened from the console screen).
            new GameObject("HmiPanel").AddComponent<ParkShift.UI.HmiPanel>();
            new GameObject("NetworkBootstrap").AddComponent<Networking.NetworkBootstrap>();
            new GameObject("GameManager").AddComponent<GameManager>();
            var ps = new GameObject("PlayerSpawn"); ps.transform.position = new Vector3(10, 1.1f, -1.5f);

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/PrototypeAttraction.unity");
        }
    }
}
// (wave-1 integration pass)
