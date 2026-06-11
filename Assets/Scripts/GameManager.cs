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

            var camGO = new GameObject("PlayerCamera");
            camGO.transform.SetParent(rig.transform, false);
            camGO.transform.localPosition = new Vector3(0, 1.6f, 0);
            camGO.AddComponent<Camera>();
            camGO.AddComponent<AudioListener>();

            rig.AddComponent<FirstPersonController>();
            var role = rig.AddComponent<RoleController>();
            var interactor = rig.AddComponent<PlayerInteractor>();

            var hud = new GameObject("HUD").AddComponent<HUDController>();
            hud.Bind(interactor, role);
        }
    }
}
