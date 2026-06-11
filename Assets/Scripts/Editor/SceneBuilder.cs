using System.Collections.Generic;
using UnityEditor;
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
            string path = $"Assets/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            m.color = c;
            return m;
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

            // Materials
            var mFloor = Mat("Floor", new Color(0.22f, 0.23f, 0.28f));
            var mPlatform = Mat("Platform", new Color(0.35f, 0.32f, 0.4f));
            var mTrack = Mat("Track", new Color(0.55f, 0.3f, 0.75f));
            var mTrainA = Mat("TrainA", new Color(0.95f, 0.45f, 0.1f));
            var mTrainB = Mat("TrainB", new Color(0.1f, 0.6f, 0.95f));
            var mPanel = Mat("Panel", new Color(0.15f, 0.5f, 0.35f));
            var mDanger = Mat("Danger", new Color(0.85f, 0.15f, 0.15f));
            var mGate = Mat("Gate", new Color(0.8f, 0.75f, 0.2f));
            var mGuest = Mat("Guest", new Color(0.8f, 0.8f, 0.85f));
            var mBooth = Mat("Booth", new Color(0.3f, 0.34f, 0.5f));

            // Lighting
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(55f, -30f, 0);
            sun.intensity = 1.1f;
            RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.55f);

            // Ground + station platform
            Box("Floor", new Vector3(0, -0.5f, 8), new Vector3(70, 1, 60), mFloor);
            Box("StationPlatform", new Vector3(0, 0.25f, 0), new Vector3(14, 0.5f, 3.5f), mPlatform);

            // ---- Track: rounded-rectangle waypoint loop, 16 waypoints, 4 blocks of 4 ----
            var trackGO = new GameObject("Track");
            var path = trackGO.AddComponent<TrackPath>();
            path.BlockCount = 4;
            Vector2[] layout =
            {
                // Block 0: STATION straightaway (z = 2.2, x from -6..6)
                new Vector2(-6, 2.2f), new Vector2(-2, 2.2f), new Vector2(2, 2.2f), new Vector2(6, 2.2f),
                // Block 1: LAUNCH — east side heading out
                new Vector2(12, 4f), new Vector2(16, 8f), new Vector2(17, 13f), new Vector2(15, 18f),
                // Block 2: COURSE — far straight
                new Vector2(8, 21f), new Vector2(0, 22f), new Vector2(-8, 21f), new Vector2(-15, 18f),
                // Block 3: BRAKE RUN — west side returning
                new Vector2(-17, 13f), new Vector2(-16, 8f), new Vector2(-12, 4f), new Vector2(-8, 2.6f),
            };
            string[] blockNames = { "STATION", "LAUNCH", "COURSE", "BRAKE RUN" };
            for (int i = 0; i < layout.Length; i++)
            {
                var wp = new GameObject($"WP_{i:00}");
                wp.transform.SetParent(trackGO.transform);
                wp.transform.position = new Vector3(layout[i].x, 0.6f, layout[i].y);
                path.Waypoints.Add(wp.transform);
                var sleeper = Box($"Sleeper_{i:00}", wp.transform.position + Vector3.down * 0.35f,
                                  new Vector3(1.6f, 0.15f, 0.6f), mTrack, trackGO.transform);
                if (i % 4 == 0) Label(sleeper, "BLOCK " + (i / 4) + ": " + blockNames[i / 4], 1.6f);
            }

            // ---- Trains (two, with 8 seat anchors each) ----
            TrainVisual MakeTrain(int id, Material m)
            {
                var body = Box($"Train{id}", Vector3.zero, new Vector3(3.2f, 0.7f, 1.2f), m);
                var tv = body.AddComponent<TrainVisual>();
                tv.TrainId = id; tv.Path = path;
                var seats = new List<Transform>();
                for (int s = 0; s < 8; s++)
                {
                    var anchor = new GameObject($"Seat{s}");
                    anchor.transform.SetParent(body.transform, false);
                    anchor.transform.localPosition = new Vector3(-1.2f + 0.34f * s, 0.6f, (s % 2 == 0 ? -0.25f : 0.25f));
                    seats.Add(anchor.transform);
                }
                tv.SeatAnchors = seats.ToArray();
                Label(body, "TRAIN " + (id == 0 ? "A" : "B"), 1.4f);
                return tv;
            }
            var trainA = MakeTrain(0, mTrainA);
            var trainB = MakeTrain(1, mTrainB);

            // Flagged-seat prop on train A (restraint fault marker)
            var flagged = Box("FlaggedSeatMarker", Vector3.zero, new Vector3(0.4f, 0.4f, 0.4f), mDanger, trainA.transform);
            flagged.transform.localPosition = new Vector3(0.2f, 1.0f, 0);
            var seatPanel = flagged.AddComponent<PanelInteractable>();
            seatPanel.PanelKind = PanelInteractable.Kind.SeatInspect;
            Label(flagged, "ROW 3", 0.5f);

            // ---- Station gates ----
            var gateRoot = new GameObject("Gates");
            gateRoot.transform.position = new Vector3(0, 0.5f, 1.2f);
            var gv = gateRoot.AddComponent<GateVisual>();
            var panels = new List<Transform>();
            for (int g = 0; g < 4; g++)
            {
                var gp = Box($"GatePanel{g}", gateRoot.transform.position + new Vector3(-4.5f + 3f * g, 0.4f, 0),
                             new Vector3(2.4f, 1.0f, 0.12f), mGate, gateRoot.transform);
                panels.Add(gp.transform);
            }
            gv.GatePanels = panels.ToArray();

            // ---- Control booth + panels ----
            Box("BoothFloor", new Vector3(10, 0.05f, -3), new Vector3(6, 0.1f, 5), mBooth);
            Box("BoothWall", new Vector3(10, 1.2f, -5.4f), new Vector3(6, 2.4f, 0.2f), mBooth);

            void Panel(string name, PanelInteractable.Kind kind, Vector3 pos, Material m, string label)
            {
                var p = Box(name, pos, new Vector3(0.9f, 1.1f, 0.5f), m);
                p.AddComponent<PanelInteractable>().PanelKind = kind;
                Label(p, label, 1.0f);
            }
            Panel("ShiftStartPanel", PanelInteractable.Kind.ShiftStart, new Vector3(8, 0.7f, -3.4f), mPanel, "SHIFT START");
            Panel("GateLever", PanelInteractable.Kind.GateLever, new Vector3(9.4f, 0.7f, -3.4f), mGate, "GATES");
            Panel("DispatchPanel", PanelInteractable.Kind.DispatchButton, new Vector3(10.8f, 0.7f, -3.4f), mDanger, "DISPATCH");
            Panel("AssistantPhone", PanelInteractable.Kind.AssistantPhone, new Vector3(12.2f, 0.7f, -3.4f), mPanel, "TECH PHONE (H)");

            // Platform-side stations
            Panel("LoadHelper", PanelInteractable.Kind.LoadHelper, new Vector3(-3, 0.7f, 0.2f), mPanel, "WAVE ABOARD");
            Panel("RestraintStation", PanelInteractable.Kind.RestraintCheck, new Vector3(3, 0.7f, 0.2f), mPanel, "RESTRAINT CHECK");

            // ---- Maintenance bay ----
            Box("MaintBayFloor", new Vector3(-14, 0.05f, -4), new Vector3(8, 0.1f, 7), mBooth);
            Panel("MaintenancePanel", PanelInteractable.Kind.MaintenancePanel, new Vector3(-16, 0.7f, -6.2f), mPanel, "DRIVE RESET");
            var loto = Box("LotoLever", new Vector3(-12, 0.8f, -6.2f), new Vector3(0.5f, 1.6f, 0.5f), mDanger);
            loto.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.LotoLever;
            Label(loto, "LOCKOUT", 1.2f);
            var lotoTag = Box("LotoTag", loto.transform.position + new Vector3(0.5f, 0.2f, 0), new Vector3(0.25f, 0.35f, 0.05f), mGate);
            Label(lotoTag, "TAGGED OUT", 0.4f);

            // ---- Dropped item prop (near station edge) ----
            var item = Box("DroppedItem", new Vector3(5.5f, 0.65f, 1.6f), new Vector3(0.35f, 0.35f, 0.35f), mGate);
            item.AddComponent<PanelInteractable>().PanelKind = PanelInteractable.Kind.DroppedItem;
            Label(item, "GUEST ITEM", 0.5f);

            // Fault prop controller wiring
            var props = new GameObject("FaultProps").AddComponent<FaultPropController>();
            props.DroppedItemProp = item;
            props.FlaggedSeatProp = flagged;
            props.LotoTagProp = lotoTag;

            // ---- Queue ----
            var queueRoot = new GameObject("Queue");
            var gvm = queueRoot.AddComponent<GuestVisualManager>();
            gvm.GuestMaterial = mGuest;
            gvm.Trains = new[] { trainA, trainB };
            var spawn = new GameObject("GuestSpawn"); spawn.transform.position = new Vector3(-2, 0.8f, -16);
            var exit = new GameObject("GuestExit"); exit.transform.position = new Vector3(14, 0.8f, 4);
            gvm.SpawnPoint = spawn.transform; gvm.ExitPoint = exit.transform;
            // zig-zag queue spots from gates backwards
            for (int q = 0; q < 40; q++)
            {
                int row = q / 8; int col = q % 8;
                float x = (row % 2 == 0) ? -4.2f + 1.2f * col : 4.2f - 1.2f * col;
                var spot = new GameObject($"QSpot{q:00}");
                spot.transform.SetParent(queueRoot.transform);
                spot.transform.position = new Vector3(x, 0.8f, -2.2f - 1.3f * row);
                gvm.QueueSpots.Add(spot.transform);
            }
            // queue rails
            for (int row = 0; row <= 5; row++)
                Box($"QueueRail{row}", new Vector3(0, 0.45f, -2.85f - 1.3f * row), new Vector3(10.5f, 0.08f, 0.08f), mPlatform);

            // ---- Managers, spawn, bootstrap ----
            new GameObject("AttractionManager").AddComponent<AttractionManager>();
            new GameObject("NetworkBootstrap").AddComponent<Networking.NetworkBootstrap>();
            new GameObject("GameManager").AddComponent<GameManager>();
            var ps = new GameObject("PlayerSpawn"); ps.transform.position = new Vector3(10, 1.1f, -1.5f);

            EditorSceneManager.SaveScene(scene, "Assets/Scenes/PrototypeAttraction.unity");
        }
    }
}
