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

        void Awake() { Local = this; }
        void OnDestroy() { if (Local == this) Local = null; }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                Role = (PlayerRole)(((int)Role + 1) % 3);
                OnRoleChanged?.Invoke(Role);
            }
        }

        public bool CanUseMaintenance => Role == PlayerRole.Technician || Role == PlayerRole.Manager;
    }
}
