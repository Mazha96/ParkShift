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
    /// Mix polish lives here too: stolen pool voices fade out briefly instead of hard-cutting,
    /// the klaxon fades in/out while staying phase-locked to the strobes, foreground event
    /// sounds duck the background loops, and a gentle tanh soft limiter keeps stacked sounds
    /// from overloading. All of it is plain per-frame volume math — no runtime allocations.
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
        /// <summary>How long a stolen one-shot voice takes to fade out before its slot restarts (seconds).</summary>
        const float StealFadeSeconds = 0.02f;
        /// <summary>Ducker attack: background loops dip this fast when a foreground event fires (seconds).</summary>
        const float DuckAttackSeconds = 0.03f;
        /// <summary>Ducker recovery: background loops climb back to full over this long (seconds).</summary>
        const float DuckReleaseSeconds = 0.6f;
        /// <summary>Attenuation of background loops at full duck (0.45 ≈ −5.2 dB).</summary>
        const float DuckDepth = 0.45f;
        /// <summary>Klaxon volume fade-in time (seconds). Only the volume fades — the loop keeps
        /// playing through, so the beep stays phase-locked to the strobes.</summary>
        const float KlaxonFadeInSeconds = 0.08f;
        /// <summary>Klaxon volume fade-out time (seconds) before the loop actually stops.</summary>
        const float KlaxonFadeOutSeconds = 0.25f;
        /// <summary>Summed-gain level above which the soft limiter starts squeezing.</summary>
        const float LimiterKnee = 0.85f;
        /// <summary>Exponential time constant for crowd loop volume tracking (seconds).
        /// Critically-damped-feeling glide instead of steppy linear ramps.</summary>
        const float CrowdTau = 0.35f;

        AudioSource[] _pool;
        /// <summary>Per-slot base volume (already includes Master, before the limiter).</summary>
        float[] _slotVol;
        /// <summary>True while a slot is fading out prior to being reused (voice steal).</summary>
        bool[] _slotFading;
        /// <summary>Clip queued to start on a slot once its steal-fade completes.</summary>
        AudioClip[] _pendingClip;
        Vector3[] _pendingPos;
        float[] _pendingVol;
        float[] _pendingPitch;
        float[] _pendingSpatial;
        float[] _pendingMaxDist;
        int _next;

        AudioSource _walla, _grumble, _klaxon;
        int _klaxonLevel;
        /// <summary>0..1 klaxon fade envelope (volume only; phase never resets mid-alarm).</summary>
        float _klaxonEnv;
        /// <summary>Klaxon volume at full envelope (already includes Master).</summary>
        float _klaxonPeak;
        /// <summary>Exponentially smoothed crowd volumes (pre-Master), crossfaded against each other
        /// each frame so mood changes blend instead of dropping out.</summary>
        float _wallaVol, _grumbleVol;
        /// <summary>0..1 current duck amount applied to background loops.</summary>
        float _duckEnv;
        /// <summary>1 while a duck attack is in flight, 0 once recovery starts.</summary>
        float _duckTarget;
        /// <summary>Smoothed soft-limiter gain (1 = no limiting).</summary>
        float _limit = 1f;

        /// <summary>
        /// Gain multiplier that externally-owned background loops (e.g. per-train coaster rumble
        /// in TrainJuice) should multiply into their volume each frame: combines the foreground
        /// event ducker and the master soft limiter. 1 when the mix is idle or no director exists.
        /// </summary>
        public static float LoopDuck
        {
            get { return Instance != null ? (1f - DuckDepth * Instance._duckEnv) * Instance._limit : 1f; }
        }

        void Awake()
        {
            Instance = this;
            _pool = new AudioSource[PoolSize];
            _slotVol = new float[PoolSize];
            _slotFading = new bool[PoolSize];
            _pendingClip = new AudioClip[PoolSize];
            _pendingPos = new Vector3[PoolSize];
            _pendingVol = new float[PoolSize];
            _pendingPitch = new float[PoolSize];
            _pendingSpatial = new float[PoolSize];
            _pendingMaxDist = new float[PoolSize];
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
            float dt = Time.deltaTime;

            // --- Ducker envelope: fast attack toward 1, slow linear recovery back to 0. ---
            if (_duckEnv < _duckTarget)
            {
                _duckEnv = Mathf.MoveTowards(_duckEnv, _duckTarget, dt / DuckAttackSeconds);
                if (_duckEnv >= _duckTarget) _duckTarget = 0f;
            }
            else
            {
                _duckTarget = 0f;
                _duckEnv = Mathf.MoveTowards(_duckEnv, 0f, dt / DuckReleaseSeconds);
            }
            float duck = 1f - DuckDepth * _duckEnv;

            // --- Continuous crowd mix, driven by observed sim state (works on clients too —
            // no authoritative callbacks, just polling whatever state is mirrored locally). ---
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
            // Both loops run continuously and glide exponentially toward their targets, so a
            // mood change is a genuine crossfade between walla and grumble, never a dropout.
            float crowdK = 1f - Mathf.Exp(-dt / CrowdTau);
            _wallaVol += (wallaTarget - _wallaVol) * crowdK;
            _grumbleVol += (grumbleTarget - _grumbleVol) * crowdK;

            // --- Klaxon fade envelope (volume only; the clip keeps looping so the beep phase
            // stays locked to the strobes through fades). ---
            bool klaxonOn = _klaxonLevel > 0;
            _klaxonEnv = Mathf.MoveTowards(_klaxonEnv, klaxonOn ? 1f : 0f,
                                           dt / (klaxonOn ? KlaxonFadeInSeconds : KlaxonFadeOutSeconds));

            // --- Soft limiter: estimate the summed base gain of everything audible and squeeze
            // gently (tanh knee) when it stacks up, so simultaneous sounds don't distort. ---
            float sum = _wallaVol * Master * duck + _grumbleVol * Master * duck + _klaxonEnv * _klaxonPeak;
            if (_pool != null)
            {
                for (int i = 0; i < PoolSize; i++)
                {
                    var src = _pool[i];
                    if (src == null || !src.isPlaying) continue;
                    sum += _slotFading[i] ? src.volume : _slotVol[i];
                }
            }
            float limitTarget = 1f;
            if (sum > LimiterKnee)
                limitTarget = (LimiterKnee + (float)System.Math.Tanh((sum - LimiterKnee) * 2f) * 0.5f) / sum;
            float limitTau = limitTarget < _limit ? 0.03f : 0.2f;
            _limit += (limitTarget - _limit) * (1f - Mathf.Exp(-dt / limitTau));

            // --- Apply volumes. ---
            if (_walla != null)
                _walla.volume = _wallaVol * Master * duck * _limit;
            if (_grumble != null)
                _grumble.volume = _grumbleVol * Master * duck * _limit;
            if (_klaxon != null)
            {
                _klaxon.volume = _klaxonEnv * _klaxonPeak * _limit;
                if (!klaxonOn && _klaxonEnv <= 0f && _klaxon.isPlaying)
                    _klaxon.Stop();
            }

            // --- One-shot pool: run steal-fades to zero (then start the queued clip) and keep
            // playing voices tracking the limiter. ---
            if (_pool != null)
            {
                float stealStep = dt * (Master / StealFadeSeconds);
                for (int i = 0; i < PoolSize; i++)
                {
                    var src = _pool[i];
                    if (src == null) continue;
                    if (_slotFading[i])
                    {
                        float v = src.volume - stealStep;
                        if (v <= 0f)
                        {
                            _slotFading[i] = false;
                            StartSlot(i);
                        }
                        else
                        {
                            src.volume = v;
                        }
                    }
                    else if (src.isPlaying)
                    {
                        src.volume = _slotVol[i] * _limit;
                    }
                }
            }
        }

        /// <summary>Picks the pool slot to play into: prefers an idle slot (round-robin), and only
        /// when every voice is busy steals the least-audible one.</summary>
        int PickSlot()
        {
            for (int i = 0; i < PoolSize; i++)
            {
                int idx = (_next + i) % PoolSize;
                var s = _pool[idx];
                if (s == null) continue;
                if (!s.isPlaying && !_slotFading[idx])
                {
                    _next = (idx + 1) % PoolSize;
                    return idx;
                }
            }
            int best = _next;
            float bestVol = float.MaxValue;
            for (int i = 0; i < PoolSize; i++)
            {
                if (_pool[i] == null || _slotFading[i]) continue;
                float v = _pool[i].volume;
                if (v < bestVol) { bestVol = v; best = i; }
            }
            _next = (best + 1) % PoolSize;
            return best;
        }

        /// <summary>Starts a slot from its queued parameters (called immediately for idle slots,
        /// or once a stolen voice has finished its quick fade-out).</summary>
        void StartSlot(int i)
        {
            var s = _pool[i];
            var clip = _pendingClip[i];
            if (s == null || clip == null) return;
            _pendingClip[i] = null;
            s.transform.position = _pendingPos[i];
            s.clip = clip;
            _slotVol[i] = _pendingVol[i];
            s.volume = _slotVol[i] * _limit;
            s.pitch = _pendingPitch[i];
            s.spatialBlend = _pendingSpatial[i];
            s.minDistance = 2.5f;
            s.maxDistance = _pendingMaxDist[i];
            s.Play();
        }

        void PlayShot(AudioClip clip, Vector3 pos, float vol, float pitch, float spatial, float maxDist)
        {
            if (clip == null || _pool == null) return;
            int slot = PickSlot();
            var s = _pool[slot];
            if (s == null) return;
            _pendingClip[slot] = clip;
            _pendingPos[slot] = pos;
            _pendingVol[slot] = Mathf.Clamp01(vol) * Master;
            _pendingPitch[slot] = pitch;
            _pendingSpatial[slot] = spatial;
            _pendingMaxDist[slot] = maxDist;
            if (s.isPlaying)
            {
                // Voice steal: quick fade to zero first so there's no pop, then Update()
                // launches the queued clip.
                _slotFading[slot] = true;
            }
            else
            {
                StartSlot(slot);
            }
        }

        /// <summary>Kicks the background ducker: loops dip fast and recover slowly so foreground
        /// event sounds read clearly over the beds.</summary>
        void Duck()
        {
            _duckTarget = 1f;
        }

        void SetKlaxonLevel(int level)
        {
            if (level == _klaxonLevel) return;
            bool wasOff = _klaxonLevel <= 0;
            _klaxonLevel = level;
            if (_klaxon == null) return;
            if (level <= 0) return; // Update() fades the volume out and stops the source.
            var clip = AudioSynth.KlaxonLoop(level >= 2);
            _klaxonPeak = Master * (level >= 2 ? 0.5f : 0.32f);
            if (_klaxon.clip != clip || !_klaxon.isPlaying)
            {
                _klaxon.clip = clip;
                _klaxon.pitch = 1f;
                _klaxon.Play();
                // Roughly phase-lock the beep to the strobes (both count from Time.time).
                // Fades only ever touch volume, so this lock survives them.
                if (clip != null && clip.length > 0.01f)
                    _klaxon.time = Time.time % clip.length;
            }
            if (wasOff) Duck(); // alarm onset punches through the beds
        }

        // ------------------------------------------------------------------
        // Static, null-guarded API
        // ------------------------------------------------------------------

        /// <summary>UI/panel blip: action accepted. Slight pitch/volume jitter so rapid repeats
        /// don't sound machine-gun identical.</summary>
        public static void Confirm()
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.UiConfirm(), Vector3.zero,
                0.5f * Random.Range(0.94f, 1.04f), Random.Range(0.98f, 1.02f), 0f, 20f);
        }

        /// <summary>UI/panel buzz: action refused or blocked.</summary>
        public static void Refuse()
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.UiRefuse(), Vector3.zero,
                0.5f * Random.Range(0.94f, 1.04f), Random.Range(0.98f, 1.02f), 0f, 20f);
        }

        public static void Chime()
        {
            if (Instance == null) return;
            Instance.Duck();
            Instance.PlayShot(AudioSynth.Chime(), Vector3.zero, 0.55f, 1f, 0f, 20f);
        }

        /// <summary>Rising arpeggio scaled by the current dispatch streak.</summary>
        public static void ComboArp(int streak)
        {
            if (Instance == null) return;
            Instance.Duck();
            Instance.PlayShot(AudioSynth.Arpeggio(streak), Vector3.zero, 0.5f, 1f, 0f, 20f);
        }

        public static void ResolveDing()
        {
            if (Instance == null) return;
            Instance.Duck();
            Instance.PlayShot(AudioSynth.ResolveDing(), Vector3.zero, 0.5f, 1f, 0f, 20f);
        }

        public static void ShiftOpen()
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.PowerUp(), Vector3.zero, 0.55f, 1f, 0f, 20f);
        }

        public static void GateServo(Vector3 pos)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Servo(), pos,
                0.6f * Random.Range(0.94f, 1.04f), Random.Range(0.96f, 1.04f), 1f, 26f);
        }

        public static void GateClick(Vector3 pos)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Click(), pos,
                0.55f * Random.Range(0.92f, 1.06f), Random.Range(0.92f, 1.08f), 1f, 22f);
        }

        /// <summary>Restraint clunk-latch at a world position.</summary>
        public static void Clunk(Vector3 pos)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Clunk(), pos,
                0.8f * Random.Range(0.93f, 1.04f), Random.Range(0.95f, 1.05f), 1f, 28f);
        }

        public static void Clack(Vector3 pos)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Clack(), pos,
                0.55f * Random.Range(0.93f, 1.05f), Random.Range(0.95f, 1.05f), 1f, 24f);
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
            if (Instance == null) return;
            Instance.Duck();
            Instance.PlayShot(AudioSynth.Fanfare(grade), Vector3.zero, 0.75f, 1f, 0f, 20f);
        }

        /// <summary>Quiet work cadence; deliberately not a success chime.</summary>
        public static void RepairTick(int beat)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Click(), Vector3.zero,
                0.16f, 0.85f + 0.08f * Mathf.Clamp(beat, 0, 3), 0f, 20f);
        }

        public static void Footstep(bool soft, bool sprinting, int variation)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Footstep(soft, variation), Vector3.zero,
                sprinting ? 0.3f : 0.2f, (variation & 1) == 0 ? 0.96f : 1.04f, 0f, 20f);
        }

        public static void BrakeHiss(Vector3 pos, float strength)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.BrakeHiss(), pos,
                Mathf.Lerp(0.25f, 0.55f, Mathf.Clamp01(strength)), 1f, 1f, 28f);
        }

        /// <summary>Landing thump (local player — 2D).</summary>
        public static void Thump(float power)
        {
            if (Instance != null) Instance.PlayShot(AudioSynth.Thump(), Vector3.zero,
                (0.45f + 0.35f * Mathf.Clamp01(power)) * Random.Range(0.95f, 1.03f),
                Random.Range(0.97f, 1.03f), 0f, 20f);
        }

        /// <summary>0 = silent, 1 = minor klaxon, 2 = major klaxon. Idempotent per level.
        /// Onsets fade in (~80 ms) and shut-offs fade out (~250 ms) instead of hard-cutting,
        /// with the beep staying phase-locked to the strobes throughout.</summary>
        public static void SetKlaxon(int level)
        {
            if (Instance != null) Instance.SetKlaxonLevel(level);
        }
    }
}
