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

namespace ParkShift.EditorTools
{
    /// <summary>
    /// One-click scene construction from primitives. Run: menu ParkShift → Build All Scenes.
    /// Rebuildable at any time; scenes are throwaway artifacts, this script is the source of truth.
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
            var camGO = new GameObject("Camera");
            var cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.05f, 0.15f);
            camGO.AddComponent<AudioListener>();
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/MainMenu.unity");
        }

        static void BuildPrototypeAttraction()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Materials — textured PBR sets from Assets/Textures (flat color fallback if missing)
            var mFloor = Mat("Floor", new Color(0.85f, 0.87f, 0.92f), "asphalt", 0.30f, 0f, 22f);
            var mPlatform = Mat("Platform", new Color(0.92f, 0.88f, 0.95f), "concrete", 0.22f, 0f, 5f);
            var mTrack = Mat("Track", new Color(0.55f, 0.3f, 0.75f), "paint", 0.45f, 0.3f, 1.5f);
            var mTrainA = Mat("TrainA", new Color(0.95f, 0.45f, 0.1f), "paint", 0.65f, 0.25f, 1f);
            var mTrainB = Mat("TrainB", new Color(0.1f, 0.6f, 0.95f), "paint", 0.65f, 0.25f, 1f);
            var mPanel = Mat("Panel", new Color(0.15f, 0.5f, 0.35f), "paint", 0.5f, 0.2f, 1f);
            var mDanger = Mat("Danger", new Color(0.85f, 0.15f, 0.15f), "paint", 0.5f, 0.2f, 1f);
            var mGate = Mat("Gate", new Color(0.8f, 0.75f, 0.2f), "paint", 0.55f, 0.3f, 1f);
            var mGuest = Mat("Guest", new Color(0.8f, 0.8f, 0.85f));
            var mBooth = Mat("Booth", new Color(0.55f, 0.6f, 0.78f), "concrete", 0.3f, 0f, 2.5f);
            var mRail = Mat("Rail", new Color(0.9f, 0.92f, 0.97f), "metal", 0.75f, 0.85f, 1f);
            var mTie = Mat("Tie", new Color(0.85f, 0.8f, 0.78f), "wood", 0.18f, 0f, 1f);
            var mSeat = Mat("Seat", new Color(0.2f, 0.2f, 0.25f), "seat", 0.35f, 0f, 1f);
            var mRoof = Mat("Roof", new Color(0.45f, 0.25f, 0.65f), "paint", 0.5f, 0.15f, 3f);

            // Dewey guest model + animator (null-safe: capsule fallback if FBX is absent)
            var deweyPrefab = SetupDeweyModel();
            var deweyCtrl = deweyPrefab != null ? BuildGuestAnimator() : null;

            // Lighting — warm sun, soft shadows, procedural sky, distance fog
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(48f, -32f, 0);
            sun.intensity = 1.25f;
            sun.color = new Color(1.0f, 0.956f, 0.87f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;

            string skyPath = "Assets/Materials/Skybox.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(sky, skyPath);
            }
            sky.shader = Shader.Find("Skybox/Procedural");
            sky.SetFloat("_SunSize", 0.045f);
            sky.SetFloat("_AtmosphereThickness", 0.95f);
            sky.SetFloat("_Exposure", 1.25f);
            sky.SetColor("_SkyTint", new Color(0.52f, 0.68f, 0.95f));
            sky.SetColor("_GroundColor", new Color(0.36f, 0.35f, 0.39f));
            RenderSettings.skybox = sky;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.68f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.48f, 0.49f, 0.55f);
            RenderSettings.ambientGroundColor = new Color(0.28f, 0.27f, 0.3f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 40f;
            RenderSettings.fogEndDistance = 120f;
            RenderSettings.fogColor = new Color(0.66f, 0.72f, 0.85f);
            QualitySettings.shadowDistance = 70f;

            // Ground + station platform
            Box("Floor", new Vector3(0, -0.5f, 8), new Vector3(70, 1, 60), mFloor);
            Box("StationPlatform", new Vector3(0, 0.25f, 0), new Vector3(14, 0.5f, 3.5f), mPlatform);

            // ---- Access ramps: ground (0) <-> platform top (0.5), gentle ~13 degree plates ----
            GameObject Ramp(string name, Vector3 pos, Vector3 scale, Vector3 euler)
            {
                var r = Box(name, pos, scale, mPlatform);
                r.transform.rotation = Quaternion.Euler(euler);
                return r;
            }
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

            // ---- Station canopy: roof slab on four columns + edge fencing ----
            var stationRoot = new GameObject("StationDressing");
            Box("RoofSlab", new Vector3(0, 4.0f, 0.6f), new Vector3(15f, 0.25f, 6f), mRoof, stationRoot.transform);
            Box("RoofTrim", new Vector3(0, 3.82f, 0.6f), new Vector3(15.4f, 0.12f, 6.4f), mGate, stationRoot.transform);
            foreach (var cx in new[] { -6.8f, 6.8f })
                foreach (var cz in new[] { -1.9f, 3.1f })
                {
                    var col = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    col.name = "RoofColumn";
                    col.transform.SetParent(stationRoot.transform, true);
                    col.transform.position = new Vector3(cx, 2.0f, cz);
                    col.transform.localScale = new Vector3(0.3f, 1.75f, 0.3f);
                    col.GetComponent<Renderer>().sharedMaterial = mBooth;
                }
            // Platform back-edge fence (guests' side) with a center opening for the queue entry
            for (int f = 0; f < 8; f++)
            {
                float fx = -6.6f + f * 1.9f;
                if (Mathf.Abs(fx) < 1.6f) continue; // entry gap
                Box($"FencePost{f}", new Vector3(fx, 0.9f, -1.7f), new Vector3(0.08f, 0.8f, 0.08f), mPlatform, stationRoot.transform);
            }
            foreach (float sideX in new[] { -4.3f, 4.3f })
            {
                Box($"FenceTopRail{(sideX < 0 ? "L" : "R")}", new Vector3(sideX, 1.28f, -1.7f), new Vector3(5.4f, 0.07f, 0.07f), mPlatform, stationRoot.transform);
                Box($"FenceMidRail{(sideX < 0 ? "L" : "R")}", new Vector3(sideX, 0.95f, -1.7f), new Vector3(5.4f, 0.06f, 0.06f), mPlatform, stationRoot.transform);
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
            // Banked rail + tie mesh along the spline (replaces sleeper boxes)
            TrackMeshBuilder.Build(path, mRail, mTie);
            // Support columns where the track leaves the ground
            for (int i = 4; i < layout.Length; i++)
            {
                if (heights[i] < 1.0f) continue;
                var top = new Vector3(layout[i].x, heights[i] - 0.45f, layout[i].y);
                Box($"Support_{i:00}", new Vector3(top.x, top.y * 0.5f, top.z),
                    new Vector3(0.25f, top.y, 0.25f), mTrack, trackGO.transform);
            }

            // ---- Trains (Blender CoasterTrain.fbx; procedural factory as fallback) ----
            var trainPrefab = Prop("CoasterTrain.fbx");
            TrainVisual MakeTrain(int id, Material m)
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
                Label(body, "TRAIN " + (id == 0 ? "A" : "B"), 1.6f);
                return tv;
            }
            var trainA = MakeTrain(0, mTrainA);
            var trainB = MakeTrain(1, mTrainB);

            // Flagged-seat prop on train A (restraint fault marker)
            // Reusable low-poly red flag for fault markers (replaces the old red mDanger boxes).
            // Uses the Blender FaultFlag.fbx if present, else falls back to a red box. Any interactor
            // is added by the caller via AddInteractor (trigger-only, so it never shoves the player).
            GameObject MakeFaultFlag(string flagName, Vector3 worldPos, float yRot, float scale)
            {
                var flagPrefab = Prop("FaultFlag.fbx");
                GameObject go = flagPrefab != null
                    ? PlaceProp(flagPrefab, flagName, worldPos, yRot)
                    : Box(flagName, worldPos, new Vector3(0.4f, 0.6f, 0.4f), mDanger);
                if (scale != 1f) go.transform.localScale = Vector3.one * scale;
                return go;
            }

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

            // ---- Control booth + panels ----
            Box("BoothFloor", new Vector3(10, 0.05f, -3), new Vector3(6, 0.1f, 5), mBooth);
            Box("BoothWall", new Vector3(10, 1.2f, -5.4f), new Vector3(6, 2.4f, 0.2f), mBooth);

            void Panel(string name, PanelInteractable.Kind kind, Vector3 pos, Material m, string label)
            {
                var p = Box(name, pos, new Vector3(0.9f, 1.1f, 0.5f), m);
                p.AddComponent<PanelInteractable>().PanelKind = kind;
                Label(p, label, 1.0f);
            }
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

            // Platform-side stations: attendant podiums (boxes as fallback)
            var podiumPrefab = Prop("Podium.fbx");
            if (podiumPrefab != null)
            {
                void Podium(string name, PanelInteractable.Kind kind, Vector3 pos, Material accent, string label)
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
                }
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

            // Fault prop controller wiring
            var props = new GameObject("FaultProps").AddComponent<FaultPropController>();
            props.DroppedItemProp = item;
            props.FlaggedSeatProp = flagged;
            props.LotoTagProp = lotoTag;
            props.IntruderProp = intruder;
            props.CollisionProp = crash;
            props.ValleyedProp = valleyBeacon;

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
            GameObject Marker(string mName, Vector3 mPos)
            {
                var g = new GameObject(mName);
                g.transform.position = mPos;
                return g;
            }

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

            GameObject UnloadButton(string n, PanelInteractable.Kind kind, Vector3 worldPos, string lbl)
            {
                var b = Box(n, worldPos, new Vector3(0.2f, 0.12f, 0.24f), mPanel, upRoot.transform);
                var col = b.GetComponent<Collider>();
                if (col) Object.DestroyImmediate(col);            // replace the primitive collider with a trigger interactor
                AddInteractor(b.transform, kind, new Vector3(0.4f, 0.4f, 0.4f));
                Label(b, lbl, 0.4f);
                return b;
            }
            var openBtn  = UnloadButton("UnloadPodium_OpenDoorButton",        PanelInteractable.Kind.UnloadDoorOpen,    new Vector3(-7.05f, 1.4f, 1.15f), "OPEN DOOR");
            var closeBtn = UnloadButton("UnloadPodium_CloseDoorButton",       PanelInteractable.Kind.UnloadDoorClose,   new Vector3(-6.8f,  1.4f, 1.15f), "CLOSE DOOR");
            var relBtn   = UnloadButton("UnloadPodium_ReleaseRestraintsButton", PanelInteractable.Kind.ReleaseRestraints, new Vector3(-6.55f, 1.4f, 1.15f), "RELEASE RESTRAINTS");
            var upd = upRoot.AddComponent<UnloadPodiumDriver>();
            upd.OpenButton = openBtn.transform; upd.CloseButton = closeBtn.transform; upd.ReleaseButton = relBtn.transform;
            Label(upRoot, "UNLOAD CONTROL", 1.6f);

            // ---- Managers, spawn, bootstrap ----
            var attractionMgr = new GameObject("AttractionManager").AddComponent<AttractionManager>();
            attractionMgr.SeparateLoadUnload = true;  // station split: block 0 = Unload, block 1 = Load
            attractionMgr.RvCount = 2;                 // one RV at Unload, one at Load (matches the 5-block track)
            attractionMgr.RvPrefab = trainPrefab;      // for runtime "Add RV" from the HMI (null-safe)
            attractionMgr.Track = path;
            attractionMgr.GuestVisuals = gvm;
            attractionMgr.AddedRvMaterial = mTrainB;
            // Clickable ride HMI (opened from the console screen).
            new GameObject("HmiPanel").AddComponent<ParkShift.UI.HmiPanel>();
            new GameObject("NetworkBootstrap").AddComponent<Networking.NetworkBootstrap>();
            new GameObject("GameManager").AddComponent<GameManager>();
            var ps = new GameObject("PlayerSpawn"); ps.transform.position = new Vector3(10, 1.1f, -1.5f);

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/PrototypeAttraction.unity");
        }
    }
}
