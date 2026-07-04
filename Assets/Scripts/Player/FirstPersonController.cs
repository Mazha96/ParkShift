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

        [Header("Feel (juice)")]
        [Tooltip("Gentle view bob while walking. Turn off if it bothers you.")]
        public bool EnableHeadBob = true;
        [Tooltip("Head-bob height at full walk speed (m). Keep tiny.")]
        public float BobAmplitude = 0.038f;
        [Tooltip("Bob cycles per second at full walk speed.")]
        public float BobFrequency = 1.9f;
        [Tooltip("Minimum fall speed (m/s) that triggers the landing thump.")]
        public float LandThumpMinFall = 5f;

        CharacterController _cc;
        float _pitch;
        float _smoothEyeY;   // smoothed world-space eye height
        bool _eyeInit;
        float _vy;           // vertical velocity (m/s), driven by gravity + jump
        float _coyote;       // remaining coyote grace
        float _bobPhase;     // radians
        float _bobWeight;    // 0..1 blend so the bob eases in/out with movement
        float _landDip;      // camera dip right after a hard landing (m)
        float _fallSpeed;    // fastest downward speed while airborne (for the landing thump)
        bool _wasGrounded = true;
        public bool InputEnabled = true;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (Cam == null) Cam = GetComponentInChildren<Camera>();
            // Camera feel layer (shake/FOV kicks) — added here so every player rig gets one.
            if (Cam != null && Cam.GetComponent<Juice.CameraJuice>() == null)
                Cam.gameObject.AddComponent<Juice.CameraJuice>();
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

            // Landing thump: track the fastest fall while airborne; on touchdown, dip the
            // camera and add a small kick of shake + a soft thump. Grounded state is read
            // AFTER Move so touchdown registers on the frame it happens.
            bool groundedNow = _cc.isGrounded;
            if (!groundedNow) _fallSpeed = Mathf.Max(_fallSpeed, -_vy);
            else if (!_wasGrounded)
            {
                if (_fallSpeed > LandThumpMinFall)
                {
                    float k = Mathf.Clamp01((_fallSpeed - LandThumpMinFall) / 8f);
                    _landDip = 0.05f + 0.06f * k;
                    Juice.CameraJuice.AddTrauma(0.12f + 0.18f * k);
                    Juice.AudioDirector.Thump(0.5f + 0.5f * k);
                }
                _fallSpeed = 0f;
            }
            _wasGrounded = groundedNow;
            _landDip = Mathf.MoveTowards(_landDip, 0f, 0.4f * Time.deltaTime);

            // Head bob: phase advances with actual horizontal speed; weight eases in/out so
            // starting/stopping never pops. |sin| gives the two-footstep cadence per cycle.
            float bobX = 0f, bobY = 0f;
            float hSpeed = new Vector3(_cc.velocity.x, 0f, _cc.velocity.z).magnitude;
            bool bobbing = EnableHeadBob && groundedNow && hSpeed > 0.4f;
            _bobWeight = Mathf.MoveTowards(_bobWeight, bobbing ? Mathf.Clamp01(hSpeed / MoveSpeed) : 0f,
                                           4f * Time.deltaTime);
            if (_bobWeight > 0.001f)
            {
                _bobPhase += Time.deltaTime * BobFrequency * 2f * Mathf.PI
                             * Mathf.Clamp(hSpeed / MoveSpeed, 0.4f, 1.25f);
                bobY = Mathf.Abs(Mathf.Sin(_bobPhase)) * BobAmplitude * _bobWeight;
                bobX = Mathf.Cos(_bobPhase) * BobAmplitude * 0.55f * _bobWeight;
            }

            // Smooth the camera's vertical motion so steps/ramps don't thump the view.
            if (Cam != null)
            {
                float targetEyeY = transform.position.y + EyeHeight;
                if (!_eyeInit) { _smoothEyeY = targetEyeY; _eyeInit = true; }
                _smoothEyeY = Mathf.Lerp(_smoothEyeY, targetEyeY,
                                         1f - Mathf.Exp(-VerticalSmoothing * Time.deltaTime));
                // never let the lag exceed a comfortable bound (e.g. teleports)
                _smoothEyeY = Mathf.Clamp(_smoothEyeY, targetEyeY - 0.4f, targetEyeY + 0.4f);
                Cam.transform.localPosition = new Vector3(
                    bobX, EyeHeight + (_smoothEyeY - targetEyeY) + bobY - _landDip, 0f);
            }

            if (Input.GetKeyDown(KeyCode.Escape))
                Cursor.lockState = Cursor.lockState == CursorLockMode.Locked
                    ? CursorLockMode.None : CursorLockMode.Locked;
        }
    }
}
