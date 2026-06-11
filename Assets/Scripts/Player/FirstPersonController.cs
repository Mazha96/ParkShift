using UnityEngine;

namespace ParkShift.Player
{
    /// <summary>Minimal FPS controller: WASD + mouse look + CharacterController.</summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        public float MoveSpeed = 4.5f;
        public float LookSensitivity = 2f;
        public Camera Cam;

        CharacterController _cc;
        float _pitch;
        public bool InputEnabled = true;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (Cam == null) Cam = GetComponentInChildren<Camera>();
            Cursor.lockState = CursorLockMode.Locked;
        }

        void Update()
        {
            if (!InputEnabled) return;

            float mx = Input.GetAxis("Mouse X") * LookSensitivity;
            float my = Input.GetAxis("Mouse Y") * LookSensitivity;
            transform.Rotate(0f, mx, 0f);
            _pitch = Mathf.Clamp(_pitch - my, -85f, 85f);
            if (Cam != null) Cam.transform.localEulerAngles = new Vector3(_pitch, 0f, 0f);

            Vector3 move = transform.right * Input.GetAxis("Horizontal")
                         + transform.forward * Input.GetAxis("Vertical");
            _cc.SimpleMove(move.normalized * MoveSpeed);

            if (Input.GetKeyDown(KeyCode.Escape))
                Cursor.lockState = Cursor.lockState == CursorLockMode.Locked
                    ? CursorLockMode.None : CursorLockMode.Locked;
        }
    }
}
