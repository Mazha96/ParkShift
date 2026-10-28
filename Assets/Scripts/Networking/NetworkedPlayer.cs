#if FISHNET
using FishNet.Object;
using ParkShift.Player;
using ParkShift.Interaction;
using UnityEngine;

namespace ParkShift.Networking
{
    /// <summary>Only the owning crew member receives input, a camera, and a HUD.</summary>
    public class NetworkedPlayer : NetworkBehaviour
    {
        GameObject _hud;
        void Awake() { SetLocal(false); }

        public override void OnStartClient()
        {
            base.OnStartClient();
            SetLocal(IsOwner);
            ParkShift.Environment.CartoonScenePalette.Paint(gameObject);
            if (!IsOwner) return;
            var role = GetComponent<RoleController>();
            role.SetLocalControl(true);
            _hud = new GameObject("LocalCrewHUD");
            _hud.AddComponent<UI.HUDController>().Bind(GetComponent<PlayerInteractor>(), role);
            AttractionNetSync.Instance?.SelectRole(role.Role);
        }

        void SetLocal(bool local)
        {
            var controller = GetComponent<FirstPersonController>();
            if (controller != null) { controller.InputEnabled = local; controller.enabled = local; }
            var role = GetComponent<RoleController>();
            if (role != null) role.SetLocalControl(local);
            var interactor = GetComponent<PlayerInteractor>();
            if (interactor != null) interactor.enabled = local;
            var labels = GetComponent<LabelLookSystem>();
            if (labels != null) labels.enabled = local;
            foreach (var cam in GetComponentsInChildren<Camera>(true))
            {
                cam.enabled = local;
                cam.tag = local ? "MainCamera" : "Untagged";
                if (local)
                {
                    cam.allowHDR = true;
                    GameManager.SetupPostFx(cam.gameObject);
                }
            }
            foreach (var listener in GetComponentsInChildren<AudioListener>(true)) listener.enabled = local;
            // The owner does not see the inside of their own capsule.
            foreach (var renderer in GetComponentsInChildren<Renderer>()) renderer.enabled = !local;
        }

        public override void OnStopClient()
        {
            if (_hud != null) Destroy(_hud);
            SetLocal(false);
            base.OnStopClient();
        }
    }
}
#endif

