using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Player
{
    /// <summary>Roles are hats, not classes: Tab cycles. Some panels require Technician/Manager.</summary>
    public class RoleController : MonoBehaviour
    {
        /// <summary>The local player's role controller (solo / host body).</summary>
        public static RoleController Local { get; private set; }

        public PlayerRole Role { get; private set; } = PlayerRole.Operator;
        public System.Action<PlayerRole> OnRoleChanged;

        void Awake()
        {
            if (Networking.NetworkBootstrap.Mode == Networking.NetMode.Solo) Local = this;
        }
        public void SetLocalControl(bool local)
        {
            enabled = local;
            if (local) Local = this;
            else if (Local == this) Local = null;
        }
        void OnDestroy() { if (Local == this) Local = null; }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Tab))
                SetRole((PlayerRole)(((int)Role + 1) % 3));
        }

        public bool SetRole(PlayerRole role)
        {
            if (!System.Enum.IsDefined(typeof(PlayerRole), role)) return false;
            Role = role;
#if FISHNET
            Networking.AttractionNetSync.Instance?.SelectRole(Role);
#endif
            OnRoleChanged?.Invoke(Role);
            return true;
        }

        public bool CanUseMaintenance => Role == PlayerRole.Technician || Role == PlayerRole.Manager;
    }
}


