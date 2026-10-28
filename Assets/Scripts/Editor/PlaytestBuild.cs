using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ParkShift.EditorTools
{
    public static class PlaytestBuild
    {
        [MenuItem("ParkShift/Build Windows LAN Playtest")]
        public static void BuildWindows()
        {
            SceneBuilder.BuildAll();
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) != 0)
                    throw new System.InvalidOperationException("Missing scene component on " + child.name);
            string folder = Path.GetFullPath("Builds/Windows");
            Directory.CreateDirectory(folder);
            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/MainMenu.unity", "Assets/Scenes/PrototypeAttraction.unity" },
                locationPathName = Path.Combine(folder, "ParkChaos.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            });
            if (result.summary.result != BuildResult.Succeeded)
                throw new System.InvalidOperationException("Park Chaos Windows build failed: " + result.summary.result);
            Debug.Log("Park Chaos LAN playtest build: " + folder);
        }
    }
}
