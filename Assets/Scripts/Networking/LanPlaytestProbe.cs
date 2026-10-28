#if DEVELOPMENT_BUILD && FISHNET
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using ParkShift.Attraction;
using ParkShift.Core;

namespace ParkShift.Networking
{
    /// <summary>Opt-in executable smoke probe; absent from release builds. Writes JSON lines.</summary>
    public sealed class LanPlaytestProbe : MonoBehaviour
    {
        string _proof;
        float _started, _nextReport;
        int _screenshot;
        AutoOperator _operator;
        PlayerRole _requestedRole = PlayerRole.Operator;
        bool _roleSet, _paused, _resumed, _care;
        bool _physicsProof, _physicsChecked;
        int _lapChecks;
        float _minimumLapHeight;
        readonly List<string> _errors = new List<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            string proof = null, address = null;
            bool host = false, solo = false;
            bool physicsProof = false;
            PlayerRole role = PlayerRole.Operator;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-parkchaos-host") host = true;
                if (args[i] == "-parkchaos-solo") solo = true;
                if (args[i] == "-parkchaos-physics-proof") physicsProof = true;
                if (args[i] == "-parkchaos-join" && i + 1 < args.Length) address = args[++i];
                if (args[i] == "-parkchaos-proof" && i + 1 < args.Length) proof = args[++i];
                if (args[i] == "-parkchaos-role" && i + 1 < args.Length)
                    Enum.TryParse(args[++i], true, out role);
            }
            if (proof == null || (!solo && !host && address == null)) return;
            if (!Path.IsPathRooted(proof)) { Debug.LogError("LAN proof path must be absolute."); return; }
            NetworkBootstrap.Mode = solo ? NetMode.Solo : host ? NetMode.Host : NetMode.Client;
            if (!solo && !host) NetworkBootstrap.Address = address;
            var probe = new GameObject("Development LAN playtest probe").AddComponent<LanPlaytestProbe>();
            probe._proof = proof;
            probe._requestedRole = role;
            probe._physicsProof = physicsProof;
            DontDestroyOnLoad(probe.gameObject);
        }

        IEnumerator Start()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_proof));
            File.WriteAllText(_proof, "");
            Application.logMessageReceived += OnLog;
            Application.runInBackground = true;
            _started = Time.realtimeSinceStartup;
            Time.timeScale = 4f;
            yield return null;
            SceneManager.LoadScene("PrototypeAttraction");
        }

        void OnLog(string message, string stack, LogType type)
        {
            if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && _errors.Count < 30)
                _errors.Add(message + "\n" + stack);
        }

        void LateUpdate()
        {
            if (_proof == null) return;
            float elapsed = Time.realtimeSinceStartup - _started;
            var manager = AttractionManager.Instance;
            if (_physicsProof && !_physicsChecked && manager != null && elapsed >= 5f)
            {
                _physicsChecked = true;
                _minimumLapHeight = float.MaxValue;
                foreach (var panel in FindObjectsByType<PanelInteractable>(FindObjectsSortMode.None))
                {
                    if (panel.PanelKind != PanelInteractable.Kind.LapBarUnjam) continue;
                    _lapChecks++;
                    float height = 0f;
                    foreach (var collider in panel.GetComponentsInChildren<Collider>())
                        if (collider.isTrigger) height = Mathf.Max(height, collider.bounds.size.y);
                    _minimumLapHeight = Mathf.Min(_minimumLapHeight, height);
                    if (height < 0.9f) Debug.LogError("Lap bar interaction volume too short: " + panel.name + " height=" + height);
                }
                if (_lapChecks == 0) { _minimumLapHeight = 0f; Debug.LogError("No lap bar interaction volumes found."); }
            }
            var network = FishNet.InstanceFinder.NetworkManager;
            if (!_roleSet && Player.RoleController.Local != null)
                _roleSet = Player.RoleController.Local.SetRole(_requestedRole);
            if (_roleSet && _requestedRole == PlayerRole.Manager && manager != null && manager.Sim != null
                && manager.Sim.State != AttractionState.Closed && manager.Sim.State != AttractionState.EndOfShift)
            {
                var sim = manager.Sim;
                if (!_paused && sim.SimTime >= 25f && sim.SimTime < 35f)
                { manager.Submit(OpVerb.ToggleAdmissions); _paused = true; }
                if (_paused && !_resumed && sim.SimTime >= 35f && sim.Management.AdmissionsPaused)
                { manager.Submit(OpVerb.ToggleAdmissions); _resumed = true; }
                if (!_care && sim.SimTime >= 45f && sim.Guests.Satisfaction < 85f
                    && sim.Management.GuestCareCooldownRemaining <= 0f && sim.Management.Budget >= ManagementSim.GuestCareCost)
                { manager.Submit(OpVerb.GuestCare); _care = true; }
            }
            int peers = network != null && network.ServerManager.Started ? network.ServerManager.Clients.Count : 0;
            if (NetworkBootstrap.Mode == NetMode.Solo && manager != null && manager.IsAuthoritative && manager.Sim != null)
            {
                if (manager.Sim.State == AttractionState.Closed) manager.Submit(OpVerb.OpenShift);
                // Solo intentionally exercises AttractionManager's normal role-based operator.
                if (manager.Sim.State != AttractionState.EndOfShift && manager.Sim.Faults.Active.Count > 0 && manager.AssistantRemaining <= 0f)
                    manager.Submit(OpVerb.CallAssistant);
            }
            if (NetworkBootstrap.Mode == NetMode.Host && manager != null && manager.IsAuthoritative && manager.Sim != null && peers >= 2)
            {
                if (manager.Sim.State == AttractionState.Closed) manager.Submit(OpVerb.OpenShift);
                if (_operator == null) _operator = new AutoOperator(manager.Sim) { Enabled = true };
                _operator.Tick(Time.deltaTime);
                if (manager.Sim.Faults.Active.Count > 0 && manager.AssistantRemaining <= 0f)
                    manager.Submit(OpVerb.CallAssistant);
            }
            if (elapsed >= (_screenshot == 0 ? 10f : 30f) && _screenshot < 2)
            {
                CaptureCamera(_proof + "." + (++_screenshot) + ".png");
                if (_screenshot == 2) CaptureOverview(_proof + ".overview.png");
            }
            bool finished = elapsed >= 70f;
            if (elapsed >= _nextReport || finished)
            {
                _nextReport = elapsed + 1f;
                WriteReport(elapsed, peers, finished);
            }
            if (finished) Application.Quit(_errors.Count == 0 ? 0 : 1);
        }

        void CaptureOverview(string path)
        {
            var camera = Camera.main;
            if (camera == null) return;
            Vector3 position = camera.transform.position;
            Quaternion rotation = camera.transform.rotation;
            var canvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            var enabled = new bool[canvases.Length];
            try
            {
                for (int i = 0; i < canvases.Length; i++) { enabled[i] = canvases[i].enabled; canvases[i].enabled = false; }
                camera.transform.position = new Vector3(24f, 12f, -18f);
                camera.transform.LookAt(new Vector3(0f, 1f, 18f));
                CaptureCamera(path);
            }
            finally
            {
                camera.transform.SetPositionAndRotation(position, rotation);
                for (int i = 0; i < canvases.Length; i++) if (canvases[i] != null) canvases[i].enabled = enabled[i];
            }
        }

        void CaptureCamera(string path)
        {
            var camera = Camera.main;
            if (camera == null) { Debug.Log("Playtest capture skipped: no local camera is available."); return; }
            var canvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            var modes = new RenderMode[canvases.Length];
            var cameras = new Camera[canvases.Length];
            var distances = new float[canvases.Length];
            for (int i = 0; i < canvases.Length; i++)
            {
                modes[i] = canvases[i].renderMode;
                cameras[i] = canvases[i].worldCamera;
                distances[i] = canvases[i].planeDistance;
            }
            var oldTarget = camera.targetTexture;
            var oldActive = RenderTexture.active;
            float oldAspect = camera.aspect;
            RenderTexture target = null;
            Texture2D pixels = null;
            try
            {
                target = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
                pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                camera.targetTexture = target;
                camera.aspect = 1280f / 720f;
                for (int i = 0; i < canvases.Length; i++)
                {
                    if (modes[i] != RenderMode.ScreenSpaceOverlay) continue;
                    canvases[i].renderMode = RenderMode.ScreenSpaceCamera;
                    canvases[i].worldCamera = camera;
                    canvases[i].planeDistance = camera.nearClipPlane + 0.1f;
                }
                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                pixels.Apply();
                File.WriteAllBytes(path, pixels.EncodeToPNG());
            }
            catch (Exception ex) { Debug.LogException(ex); }
            finally
            {
                for (int i = 0; i < canvases.Length; i++)
                {
                    if (canvases[i] == null || modes[i] != RenderMode.ScreenSpaceOverlay) continue;
                    canvases[i].renderMode = modes[i];
                    canvases[i].worldCamera = cameras[i];
                    canvases[i].planeDistance = distances[i];
                }
                camera.targetTexture = oldTarget;
                camera.aspect = oldAspect;
                RenderTexture.active = oldActive;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (pixels != null) Destroy(pixels);
            }
        }

        [Serializable]
        class Report
        {
            public float elapsed, simTime;
            public string mode, role, status, state;
            public bool connected, authoritative, final;
            public int peers, players, cameras, listeners, score, riders, trains, dispatches;
            public Vector3 localRigPosition, cameraPosition, cameraEuler;
            public float cameraFarClip;
            public int cameraCullingMask;
            public int budget, guestCareCount;
            public bool admissionsPaused;
            public string commandStatus;
            public FaultReport[] faults;
            public bool doorPresent, doorFullyOpen, doorCanClose, physicsChecked;
            public int lapCheckCount;
            public float minimumLapHeight;
            public string[] errors;
        }

        [Serializable]
        class FaultReport
        {
            public string type;
            public float deadline;
            public bool missed;
        }

        void WriteReport(float elapsed, int peers, bool final)
        {
            var manager = AttractionManager.Instance;
            var sim = manager != null ? manager.Sim : null;
            var network = FishNet.InstanceFinder.NetworkManager;
            var camera = Camera.main;
            var door = FindFirstObjectByType<UnloadDoorDriver>();
            var faults = new List<FaultReport>();
            if (sim != null) foreach (var fault in sim.Faults.Active)
                faults.Add(new FaultReport { type = fault.Def.Type.ToString(), deadline = fault.DeadlineAt, missed = fault.DeadlineMissed });
            int listeners = 0;
            foreach (var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                if (listener.isActiveAndEnabled) listeners++;
            var report = new Report
            {
                elapsed = elapsed, final = final, mode = NetworkBootstrap.Mode.ToString(),
                role = Player.RoleController.Local != null ? Player.RoleController.Local.Role.ToString() : "none",
                status = NetworkBootstrap.ConnectionStatus,
                connected = network != null && network.ClientManager.Started,
                authoritative = manager != null && manager.IsAuthoritative,
                peers = peers, players = FindObjectsByType<NetworkedPlayer>(FindObjectsSortMode.None).Length,
                cameras = Camera.allCamerasCount, listeners = listeners,
                simTime = sim != null ? sim.SimTime : -1f,
                state = sim != null ? sim.State.ToString() : "none",
                score = sim != null ? sim.Score.Compute(sim.Guests.Satisfaction) : 0,
                riders = sim != null ? sim.Score.RidersServed : 0,
                dispatches = sim != null ? sim.Score.Dispatches : 0,
                trains = sim != null ? sim.Trains.Length : 0,
                localRigPosition = Player.RoleController.Local != null ? Player.RoleController.Local.transform.position : Vector3.zero,
                cameraPosition = camera != null ? camera.transform.position : Vector3.zero,
                cameraEuler = camera != null ? camera.transform.eulerAngles : Vector3.zero,
                cameraFarClip = camera != null ? camera.farClipPlane : 0f,
                cameraCullingMask = camera != null ? camera.cullingMask : 0,
                budget = sim != null ? sim.Management.Budget : 0,
                guestCareCount = sim != null ? sim.Management.GuestCareCount : 0,
                admissionsPaused = sim != null && sim.Management.AdmissionsPaused,
                commandStatus = manager != null ? manager.LastCommandStatus : "",
                faults = faults.ToArray(), doorPresent = door != null,
                doorFullyOpen = door != null && door.IsFullyOpen, doorCanClose = door != null && door.CanClose,
                physicsChecked = _physicsChecked, lapCheckCount = _lapChecks, minimumLapHeight = _minimumLapHeight,
                errors = _errors.ToArray()
            };
            File.AppendAllText(_proof, JsonUtility.ToJson(report) + "\n");
        }

        void OnDestroy() { Application.logMessageReceived -= OnLog; }
    }
}
#endif
