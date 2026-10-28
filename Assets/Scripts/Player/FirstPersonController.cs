using UnityEngine;

namespace ParkShift.Player
{
    /// <summary>Minimal FPS controller: WASD + mouse look + CharacterController.</summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        public float MoveSpeed = 4.5f;
        public float LookSensitivity = 2f;
        [Header("Movement response")]
        public float SprintMultiplier = 1.55f;
        public float GroundAcceleration = 28f;
        public float GroundBraking = 38f;
        public float AirAcceleration = 9f;
        public KeyCode SprintKey = KeyCode.LeftShift;
        public float JumpBufferTime = 0.14f;
        public bool EnableFootsteps = true;
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
        Vector3 _horizontalVelocity;
        float _jumpBuffer;
        float _stepDistance;
        int _stepIndex;
        Juice.CameraJuice _cameraJuice;
        public bool InputEnabled = true;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (Cam == null) Cam = GetComponentInChildren<Camera>();
            // Camera feel layer (shake/FOV kicks) — added here so every player rig gets one.
            if (Cam != null && Cam.GetComponent<Juice.CameraJuice>() == null)
                Cam.gameObject.AddComponent<Juice.CameraJuice>();
            if (Cam != null) _cameraJuice = Cam.GetComponent<Juice.CameraJuice>();
            Cursor.lockState = CursorLockMode.Locked;
        }

        void Update()
        {
            if (!InputEnabled)
            {
                _horizontalVelocity = Vector3.zero;
                _jumpBuffer = 0f;
                _stepDistance = 0f;
                if (_cameraJuice != null) _cameraJuice.SpeedFov = 0f;
                return;
            }
            bool controlsActive = Cursor.lockState == CursorLockMode.Locked;

            float mx = controlsActive ? Input.GetAxis("Mouse X") * LookSensitivity : 0f;
            float my = controlsActive ? Input.GetAxis("Mouse Y") * LookSensitivity : 0f;
            transform.Rotate(0f, mx, 0f);
            _pitch = Mathf.Clamp(_pitch - my, -85f, 85f);
            if (Cam != null) Cam.transform.localEulerAngles = new Vector3(_pitch, 0f, 0f);

            // Horizontal intent (clamped so diagonals aren't faster).
            Vector3 input = controlsActive ? transform.right * Input.GetAxisRaw("Horizontal")
                          + transform.forward * Input.GetAxisRaw("Vertical") : Vector3.zero;
            bool sprinting = controlsActive && Input.GetKey(SprintKey) && Input.GetAxisRaw("Vertical") > 0f;
            float targetSpeed = MoveSpeed * (sprinting ? SprintMultiplier : 1f);
            Vector3 targetVelocity = Vector3.ClampMagnitude(input, 1f) * targetSpeed;
            float acceleration = _cc.isGrounded
                ? (input.sqrMagnitude > 0.01f ? GroundAcceleration : GroundBraking) : AirAcceleration;
            _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, targetVelocity,
                Mathf.Max(0f, acceleration) * Time.deltaTime);
            if (controlsActive && Input.GetKeyDown(JumpKey)) _jumpBuffer = JumpBufferTime;
            else _jumpBuffer = Mathf.Max(0f, _jumpBuffer - Time.deltaTime);

            // Ground check + gravity + single jump. CharacterController.isGrounded can flicker for a
            // frame on lips/ramps, so a short coyote window keeps jumping reliable.
            bool grounded = _cc.isGrounded;
            if (grounded && _vy <= 0f)
            {
                _coyote = CoyoteTime;
                if (_vy < 0f) _vy = -2f;   // small downward bias keeps the controller pinned to the floor
            }
            else _coyote -= Time.deltaTime;

            if (_jumpBuffer > 0f && _coyote > 0f)
            {
                _vy = Mathf.Sqrt(2f * -Gravity * JumpHeight); // v = sqrt(2gh)
                _coyote = 0f;                                  // consumed → no double/infinite jump
                _jumpBuffer = 0f;
            }

            _vy += Gravity * Time.deltaTime;
            Vector3 beforeMove = transform.position;
            CollisionFlags collisions = _cc.Move((_horizontalVelocity + Vector3.up * _vy) * Time.deltaTime);
            if ((collisions & CollisionFlags.Above) != 0 && _vy > 0f) _vy = 0f;

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
            if (_cameraJuice != null)
                _cameraJuice.SpeedFov = 4f * Mathf.InverseLerp(MoveSpeed, MoveSpeed * Mathf.Max(1.01f, SprintMultiplier), hSpeed);
            // Distance-driven cadence stays silent against walls and does not depend on head bob.
            if (EnableFootsteps && groundedNow && hSpeed > 0.4f)
            {
                Vector3 travelled = transform.position - beforeMove;
                travelled.y = 0f;
                _stepDistance += travelled.magnitude;
                if (_stepDistance >= (sprinting ? 1.9f : 1.5f))
                {
                    _stepDistance = 0f;
                    bool soft = false;
                    RaycastHit hit;
                    if (Physics.Raycast(transform.position + Vector3.up * 0.15f, Vector3.down,
                        out hit, 0.65f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        string surface = hit.collider.name.ToLowerInvariant();
                        soft = surface.Contains("grass") || surface.Contains("ground") || surface.Contains("lawn");
                    }
                    Juice.AudioDirector.Footstep(soft, sprinting, _stepIndex++);
                }
            }
            else _stepDistance = 0f;
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
