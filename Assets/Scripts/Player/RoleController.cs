using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Player
{
    /// <summary>Roles are hats, not classes: Tab cycles. Some panels require Technician/Manager.</summary>
    public class RoleController : MonoBehaviour
    {
        public PlayerRole Role { get; private set; } = PlayerRole.Operator;
        public System.Action<PlayerRole> OnRoleChanged;

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
