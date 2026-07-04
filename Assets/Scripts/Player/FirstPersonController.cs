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
        [Tooltip("Eye height above the rig origin.")]
        public float EyeHeight = 1.6f;
        [Tooltip("How quickly the camera absorbs vertical steps/ramps (1/s). Lower = floatier.")]
        public float VerticalSmoothing = 11f;

        [Header("Jump / gravity")]
        [Tooltip("Peak jump height in metres (single jump). ~1.1 clears small steps and lips to get unstuck.")]
        public float JumpHeight = 1.1f;
        [Tooltip("Downward acceleration (m/s^2). More negative = snappier, less floaty.")]
        public float Gravity = -20f;
        [Tooltip("Grace window after stepping off a ledge where a jump still registers (s).")]
        public float CoyoteTime = 0.12f;
        public KeyCode JumpKey = KeyCode.Space;

        CharacterController _cc;
        float _pitch;
        float _smoothEyeY;   // smoothed world-space eye height
        bool _eyeInit;
        float _vy;           // vertical velocity (m/s), driven by gravity + jump
        float _coyote;       // remaining coyote grace
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

            // Horizontal intent (clamped so diagonals aren't faster).
            Vector3 input = transform.right * Input.GetAxis("Horizontal")
                          + transform.forward * Input.GetAxis("Vertical");
            Vector3 horizontal = Vector3.ClampMagnitude(input, 1f) * MoveSpeed;

            // Ground check + gravity + single jump. CharacterController.isGrounded can flicker for a
            // frame on lips/ramps, so a short coyote window keeps jumping reliable.
            bool grounded = _cc.isGrounded;
            if (grounded)
            {
                _coyote = CoyoteTime;
                if (_vy < 0f) _vy = -2f;   // small downward bias keeps the controller pinned to the floor
            }
            else _coyote -= Time.deltaTime;

            if (Input.GetKeyDown(JumpKey) && _coyote > 0f)
            {
                _vy = Mathf.Sqrt(2f * -Gravity * JumpHeight); // v = sqrt(2gh)
                _coyote = 0f;                                  // consumed → no double/infinite jump
            }

            _vy += Gravity * Time.deltaTime;
            _cc.Move((horizontal + Vector3.up * _vy) * Time.deltaTime);

            // Smooth the camera's vertical motion so steps/ramps don't thump the view.
            if (Cam != null)
            {
                float targetEyeY = transform.position.y + EyeHeight;
                if (!_eyeInit) { _smoothEyeY = targetEyeY; _eyeInit = true; }
                _smoothEyeY = Mathf.Lerp(_smoothEyeY, targetEyeY,
                                         1f - Mathf.Exp(-VerticalSmoothing * Time.deltaTime));
                // never let the lag exceed a comfortable bound (e.g. teleports)
                _smoothEyeY = Mathf.Clamp(_smoothEyeY, targetEyeY - 0.4f, targetEyeY + 0.4f);
                var lp = Cam.transform.localPosition;
                Cam.transform.localPosition = new Vector3(lp.x, EyeHeight + (_smoothEyeY - targetEyeY), lp.z);
            }

            if (Input.GetKeyDown(KeyCode.Escape))
                Cursor.lockState = Cursor.lockState == CursorLockMode.Locked
                    ? CursorLockMode.None : CursorLockMode.Locked;
        }
    }
}
