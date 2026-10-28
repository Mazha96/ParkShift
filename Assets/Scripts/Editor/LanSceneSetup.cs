#if UNITY_EDITOR && FISHNET
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Object;
using FishNet.Component.Spawning;
using FishNet.Component.Transforming;
using FishNet.Transporting.Tugboat;
using ParkShift.Networking;
using ParkShift.Player;
using ParkShift.Interaction;

namespace ParkShift.Editor
{
    /// <summary>Builds explicit, serialized FishNet LAN dependencies for the prototype.</summary>
    public static class LanSceneSetup
    {
        const string Folder = "Assets/GeneratedLAN";

        [MenuItem("ParkShift/Configure Four Player LAN")]
        public static void ConfigureCurrentScene()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "GeneratedLAN");
            // Overwrite in place so serialized component fixes apply while the asset GUID remains stable.
            var prefab = CreatePlayerPrefab();
            foreach (var child in prefab.GetComponentsInChildren<Transform>(true))
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) > 0)
                    throw new System.InvalidOperationException("CrewPlayer contains a missing script on " + child.name);
            var collection = AssetDatabase.LoadAssetAtPath<SinglePrefabObjects>(Folder + "/CrewPrefabs.asset");
            if (collection == null)
            {
                collection = ScriptableObject.CreateInstance<SinglePrefabObjects>();
                AssetDatabase.CreateAsset(collection, Folder + "/CrewPrefabs.asset");
            }
            collection.AddObject(prefab.GetComponent<NetworkObject>(), true);
            EditorUtility.SetDirty(collection);

            var network = Object.FindObjectOfType<NetworkManager>();
            if (network == null)
            {
                var go = new GameObject("LAN_NetworkManager");
                go.AddComponent<Tugboat>();
                network = go.AddComponent<NetworkManager>();
            }
            network.SpawnablePrefabs = collection;
            var serialized = new SerializedObject(network);
            var persist = serialized.FindProperty("_dontDestroyOnLoad");
            if (persist != null) persist.boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var spawner = network.GetComponent<PlayerSpawner>();
            if (spawner == null) spawner = network.gameObject.AddComponent<PlayerSpawner>();
            spawner.SetPlayerPrefab(prefab.GetComponent<NetworkObject>());
            var spawn = GameObject.Find("PlayerSpawn");
            var spawnPoints = new Transform[4];
            for (int i = 0; i < spawnPoints.Length; i++)
            {
                string name = "CrewSpawn" + i;
                var point = GameObject.Find(name);
                if (point == null) point = new GameObject(name);
                point.transform.position = (spawn != null ? spawn.transform.position : new Vector3(0, 1.1f, -6))
                    + new Vector3((i % 2) * 1.2f, 0, -(i / 2) * 1.2f);
                spawnPoints[i] = point.transform;
            }
            spawner.Spawns = spawnPoints;
            EditorUtility.SetDirty(spawner);
            EditorUtility.SetDirty(network);

            if (Object.FindObjectOfType<AttractionNetSync>() == null)
            {
                var sync = new GameObject("LAN_AttractionState");
                sync.AddComponent<NetworkObject>();
                sync.AddComponent<AttractionNetSync>();
            }
            // FishNet's OnValidate scene-ID pass is throttled; procedural creation can
            // skip the new object. Run the same pass as its Reserialize Scenes utility.
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
                EditorSceneManager.SaveScene(scene, "Assets/Scenes/PrototypeAttraction.unity");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var createIds = typeof(NetworkObject).GetMethod("CreateSceneId", flags, null,
                new[] { typeof(UnityEngine.SceneManagement.Scene), typeof(bool), typeof(int).MakeByRefType() }, null);
            var serialize = typeof(NetworkObject).GetMethod("ReserializeEditorSetValues",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (createIds == null || serialize == null)
                throw new System.InvalidOperationException("Pinned FishNet scene serialization API is unavailable.");
            var objects = (System.Collections.Generic.List<NetworkObject>)createIds.Invoke(null, new object[] { scene, false, 0 });
            foreach (var networkObject in objects)
            {
                serialize.Invoke(networkObject, new object[] { true, false });
                if (!networkObject.IsSceneObject)
                    throw new System.InvalidOperationException("Missing FishNet SceneId: " + networkObject.name);
                EditorUtility.SetDirty(networkObject);
            }
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static GameObject CreatePlayerPrefab()
        {
            var rig = new GameObject("CrewPlayer");
            var cc = rig.AddComponent<CharacterController>();
            cc.height = 1.8f; cc.radius = 0.35f; cc.center = Vector3.up * 0.9f;
            cc.stepOffset = 0.4f; cc.slopeLimit = 50f;
            var eye = new GameObject("CrewCamera");
            eye.transform.SetParent(rig.transform, false);
            eye.transform.localPosition = Vector3.up * 1.6f;
            var camera = eye.AddComponent<Camera>();
            camera.enabled = false;
            eye.AddComponent<AudioListener>().enabled = false;
            var controller = rig.AddComponent<FirstPersonController>();
            controller.Cam = camera; controller.InputEnabled = false; controller.enabled = false;
            rig.AddComponent<RoleController>().enabled = false;
            var interactor = rig.AddComponent<PlayerInteractor>();
            interactor.Cam = camera; interactor.enabled = false;
            rig.AddComponent<LabelLookSystem>().enabled = false;
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "CrewBody";
            body.transform.SetParent(rig.transform, false);
            body.transform.localPosition = Vector3.up * 0.9f;
            body.transform.localScale = new Vector3(0.65f, 0.9f, 0.65f);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            var material = AssetDatabase.LoadAssetAtPath<Material>(Folder + "/CrewUniform.mat");
            if (material == null)
            {
                material = new Material(Shader.Find("Standard"));
                material.color = new Color(0.12f, 0.72f, 0.78f);
                AssetDatabase.CreateAsset(material, Folder + "/CrewUniform.mat");
            }
            body.GetComponent<Renderer>().sharedMaterial = material;
            rig.AddComponent<NetworkObject>();
            // FishNet's default is owner-authoritative transform with interpolation.
            rig.AddComponent<NetworkTransform>();
            rig.AddComponent<NetworkedPlayer>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(rig, Folder + "/CrewPlayer.prefab");
            Object.DestroyImmediate(rig);
            return prefab;
        }
    }
}
#endif
