using UnityEngine;
using ParkShift.Player;
using ParkShift.Interaction;
using ParkShift.UI;

namespace ParkShift
{
    /// <summary>Scene conductor: spawns the local player rig and wires the HUD.
    /// In FishNet sessions the NetworkManager spawns player prefabs instead;
    /// this local rig is used for solo and as the host's body fallback.</summary>
    public class GameManager : MonoBehaviour
    {
        void Start()
        {
            var spawn = GameObject.Find("PlayerSpawn");
            Vector3 pos = spawn ? spawn.transform.position : new Vector3(0, 1.1f, -6);

            var rig = new GameObject("PlayerRig");
            rig.transform.position = pos;
            var cc = rig.AddComponent<CharacterController>();
            cc.height = 1.8f; cc.radius = 0.35f; cc.center = Vector3.up * 0.9f;
            cc.stepOffset = 0.4f;   // platform lips shouldn't snag
            cc.slopeLimit = 50f;    // ramps walk cleanly

            var camGO = new GameObject("PlayerCamera");
            camGO.transform.SetParent(rig.transform, false);
            camGO.transform.localPosition = new Vector3(0, 1.6f, 0);
            var cam = camGO.AddComponent<Camera>();
            cam.allowHDR = true;
            camGO.AddComponent<AudioListener>();
            SetupPostFx(camGO);

            rig.AddComponent<FirstPersonController>();
            var role = rig.AddComponent<RoleController>();
            var interactor = rig.AddComponent<PlayerInteractor>();

            var hud = new GameObject("HUD").AddComponent<HUDController>();
            hud.Bind(interactor, role);
        }

        /// <summary>Bloom + ACES tonemapping + vignette for the "console HD" look.
        /// Compiles to a no-op until the Post Processing package resolves (POSTFX define).</summary>
        static void SetupPostFx(GameObject camGO)
        {
#if POSTFX
            var layer = camGO.AddComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
            // runtime-added layers need the package's resources asset bound manually
            var allRes = Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessResources>();
            if (allRes.Length > 0) layer.Init(allRes[0]);
            layer.volumeLayer = ~0; // blend volumes on all layers
            layer.antialiasingMode = UnityEngine.Rendering.PostProcessing.PostProcessLayer.Antialiasing.FastApproximateAntialiasing;

            var volGO = new GameObject("GlobalPostFx");
            var vol = volGO.AddComponent<UnityEngine.Rendering.PostProcessing.PostProcessVolume>();
            vol.isGlobal = true;

            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.PostProcessing.PostProcessProfile>();

            var bloom = profile.AddSettings<UnityEngine.Rendering.PostProcessing.Bloom>();
            bloom.intensity.Override(2.4f);
            bloom.threshold.Override(1.05f);
            bloom.softKnee.Override(0.6f);

            var grading = profile.AddSettings<UnityEngine.Rendering.PostProcessing.ColorGrading>();
            grading.tonemapper.Override(UnityEngine.Rendering.PostProcessing.Tonemapper.ACES);
            grading.saturation.Override(8f);
            grading.contrast.Override(6f);
            grading.postExposure.Override(0.25f);

            var vignette = profile.AddSettings<UnityEngine.Rendering.PostProcessing.Vignette>();
            vignette.intensity.Override(0.22f);
            vignette.smoothness.Override(0.45f);

            var ao = profile.AddSettings<UnityEngine.Rendering.PostProcessing.AmbientOcclusion>();
            ao.mode.Override(UnityEngine.Rendering.PostProcessing.AmbientOcclusionMode.ScalableAmbientObscurance);
            ao.intensity.Override(0.55f);

            vol.profile = profile;
#endif
        }
    }
}
