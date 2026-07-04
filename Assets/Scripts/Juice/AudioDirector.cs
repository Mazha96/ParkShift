using UnityEngine;

namespace ParkShift.Juice
{
    /// <summary>
    /// Central mixer for all synthesized sound. Owns a small pool of reusable one-shot
    /// AudioSources (2D or 3D at a point) plus the persistent loops: crowd walla and grumble
    /// near the queue, and the fault klaxon at the station. Per-train rumble loops live on
    /// the trains themselves (TrainJuice) but share the Master constant here so the whole
    /// mix stays balanced. Every public entry point is a static null-guarded wrapper, so
    /// callers never need to care whether the director exists in the scene.
    /// </summary>
    public class AudioDirector : MonoBehaviour
    {
        public static AudioDirector Instance { get; private set; }

        /// <summary>Single master gain for the whole synthesized mix. Keep modest — nothing shrill.</summary>
        public const float Master = 0.5f;

        [Tooltip("Where the crowd walla/grumble loops sit (roughly the middle of the queue).")]
        public Transform QueueCenter;
        [Tooltip("Where the fault klaxon sounds from (the station).")]
        public Transform StationCenter;

        const int PoolSize = 10;
        AudioSource[] _pool;
        int _next;
        AudioSource _walla, _grumble, _klaxon;
        int _klaxonLevel;

        void Awake()
        {
            Instance = this;
            _pool = new AudioSource[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject("OneShot" + i);
                go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = false;
                s.dopplerLevel = 0f;
                s.rolloffMode = AudioRolloffMode.Linear;
                _pool[i] = s;
            }

            Vector3 queuePos = QueueCenter != null ? QueueCenter.position : new Vector3(0f, 1f, -5.5f);
            Vector3 stationPos = StationCenter != null ? StationCenter.position : new Vector3(0f, 1f, 2.2f);
            _walla = MakeLoop("CrowdWalla", AudioSynth.WallaLoop(), queuePos, 4f, 34f);
            _grumble = MakeLoop("CrowdGrumble", AudioSynth.GrumbleLoop(), queuePos, 4f, 26f);
            _klaxon = MakeLoop("Klaxon", null, stationPos, 6f, 60f);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        AudioSource MakeLoop(string name, AudioClip clip, Vector3 pos, float minDist, float maxDist)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, true);
            go.transform.position = pos;
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = true;
            s.playOnAwake = false;
            s.volume = 0f;
            s.spatialBlend = 1f;
            s.dopplerLevel = 0f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = minDist;
            s.maxDistance = maxDist;
            if (clip != null) s.Play();
            return s;
        }

        void Update()
        {
            // Continuous crowd mix, driven by observed sim state (works on clients too —
            // no authoritative callbacks, just polling whatever state is mirrored locally).
            float wallaTarget = 0f, grumbleTarget = 0f;
            var m = Attraction.AttractionManager.Instance;
            if (m != null && m.Sim != null)
            {
                var s = m.Sim;
                wallaTarget = 0.5f * Mathf.Clamp01(s.Guests.QueueCount / 24f);

                float mood = Mathf.Clamp01((55f - s.Guests.Satisfaction) / 45f);
                float stalled = 0f;
                var p = s.PlatformTrain();
                if (p != null && p.StationIdleSeconds > s.Config.StallGraceSeconds)
                    stalled = Mathf.Clamp01((p.StationIdleSeconds - s.Config.StallGraceSeconds) / 15f);
                grumbleTarget = 0.32f * Mathf.Max(mood, stalled) * Mathf.Clamp01(s.Guests.QueueCount / 8f);
            }
            if (_walla != null)
                _walla.volume = Mathf.MoveTowards(_walla.volume, wallaTarget * Master, 0.4f * Time.deltaTime);
            if (_grumble != null)
                _grumble.volume = Mathf.MoveTowards(_grumble.volume, grumbleTarget * Master, 0.4f * Time.deltaTime);
        }

        void PlayShot(AudioClip clip, Vector3 pos, float vol, float pitch, float spatial, float maxDist)
        {
            if (clip == null || _pool == null) return;
            var s = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            if (s == null) return;
            s.transform.position = pos;
            s.clip = clip;
            s.volume = Mathf.Clamp01(vol) * Master;
            s.pitch = pitch;
            s.spatialBlend = spatial;
            s.minDistance = 2.5f;
            s.maxDistance = maxDist;
            s.Play();
        }

        void SetKlaxonLevel(int level)
        {
            if (level == _klaxonLevel) return;
            _klaxonLevel = level;
            if (_klaxon == null) return;
            if (level <= 0) { _klaxon.Stop(); return; }
            _klaxon.clip = AudioSynth.KlaxonLoop(level >= 2);
            _klaxon.volume = Master * (level >= 2 ? 0.5f : 0.32f);
            _klaxon.pitch = 1f;
            _klaxon.Play();
            // Roughly phase-lock the beep to the strobes (both count from Time.time).
            if (_klaxon.clip != null && _klaxon.clip.length > 0.01f)
                _klaxon.time = Time.time % _klaxon.clip.length;
        }

        // ------------------------------------------------------------------
        // Static, null-guarded API
        // ------------------------------------------------------------------

        /// <summary>UI/panel blip: action accepted.</summary>
        public static void Confirm()
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.UiConfirm(), Vector3.zero, 0.5f, 1f, 0f, 20f);
        }

        /// <summary>UI/panel buzz: action refused or blocked.</summary>
        public static void Refuse()
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.UiRefuse(), Vector3.zero, 0.5f, 1f, 0f, 20f);
        }

        public static void Chime()
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Chime(), Vector3.zero, 0.55f, 1f, 0f, 20f);
        }

        /// <summary>Rising arpeggio scaled by the current dispatch streak.</summary>
        public static void ComboArp(int streak)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Arpeggio(streak), Vector3.zero, 0.5f, 1f, 0f, 20f);
        }

        public static void ResolveDing()
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.ResolveDing(), Vector3.zero, 0.5f, 1f, 0f, 20f);
        }

        public static void ShiftOpen()
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.PowerUp(), Vector3.zero, 0.55f, 1f, 0f, 20f);
        }

        public static void GateServo(Vector3 pos)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Servo(), pos, 0.6f, Random.Range(0.96f, 1.04f), 1f, 26f);
        }

        public static void GateClick(Vector3 pos)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Click(), pos, 0.55f, Random.Range(0.92f, 1.08f), 1f, 22f);
        }

        /// <summary>Restraint clunk-latch at a world position.</summary>
        public static void Clunk(Vector3 pos)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Clunk(), pos, 0.8f, Random.Range(0.95f, 1.05f), 1f, 28f);
        }

        public static void Clack(Vector3 pos)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Clack(), pos, 0.55f, Random.Range(0.95f, 1.05f), 1f, 24f);
        }

        /// <summary>Dispatch launch whoosh at the train.</summary>
        public static void Whoosh(Vector3 pos)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Whoosh(), pos, 0.85f, Random.Range(0.97f, 1.03f), 1f, 45f);
        }

        /// <summary>Pitch-shifted crowd pop near the queue; power scales volume + pitch.</summary>
        public static void CrowdCheer(Vector3 pos, float power)
        {
            if (Instance == null) return;
            power = Mathf.Clamp01(power);
            Instance.PlayShot(AudioSynth.CheerPop(), pos, 0.45f + 0.35f * power,
                              0.95f + 0.25f * power * Random.value, 1f, 38f);
        }

        public static void Fanfare(char grade)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Fanfare(grade), Vector3.zero, 0.75f, 1f, 0f, 20f);
        }

        /// <summary>Landing thump (local player — 2D).</summary>
        public static void Thump(float power)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Thump(), Vector3.zero, 0.45f + 0.35f * Mathf.Clamp01(power), 1f, 0f, 20f);
        }

        /// <summary>0 = silent, 1 = minor klaxon, 2 = major klaxon. Idempotent per level.</summary>
        public static void SetKlaxon(int level)
        {
            if (Instance != null) Instance.SetKlaxonLevel(level);
        }
    }
}
