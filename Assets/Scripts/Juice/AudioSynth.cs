using System.Collections.Generic;
using UnityEngine;

namespace ParkShift.Juice
{
    /// <summary>
    /// Procedural AudioClip factory — every sound in the game is synthesized here at runtime
    /// (sine/saw/square/triangle/noise layers with simple ADSR-style envelopes), no audio
    /// asset files. Clips are cached by key so each sound is baked exactly once and reused.
    /// Loop clips are constructed to be seamless (integer-cycle partials / crossfaded noise).
    /// Pure clip generation only — mixing/spatialization lives in AudioDirector.
    /// </summary>
    public static class AudioSynth
    {
        public const int SampleRate = 44100;

        enum Wave { Sine, Saw, Square, Triangle, Noise }

        static readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>();

        static bool Has(string key, out AudioClip clip)
        {
            return _cache.TryGetValue(key, out clip) && clip != null;
        }

        static AudioClip Bake(string key, float[] data)
        {
            var clip = AudioClip.Create(key, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            _cache[key] = clip;
            return clip;
        }

        static float[] NewBuf(float seconds)
        {
            return new float[Mathf.Max(64, (int)(seconds * SampleRate))];
        }

        /// <summary>Adds one oscillator layer with linear attack/release into the buffer.
        /// Frequency sweeps f0 → f1 over the layer's duration.</summary>
        static void AddTone(float[] buf, float start, float dur, float f0, float f1,
                            Wave w, float gain, float attack, float release)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)(dur * SampleRate);
            if (n <= 1) return;
            double phase = 0.0;
            var rng = new System.Random(1237 + (int)(f0 * 7f));
            for (int i = 0; i < n; i++)
            {
                int idx = s0 + i;
                if (idx < 0) continue;
                if (idx >= buf.Length) break;
                float t01 = i / (float)(n - 1);
                float f = f0 + (f1 - f0) * t01;
                phase += 2.0 * System.Math.PI * f / SampleRate;
                float v;
                switch (w)
                {
                    case Wave.Saw:
                        v = (float)(2.0 * ((phase / (2.0 * System.Math.PI)) % 1.0) - 1.0);
                        break;
                    case Wave.Square:
                        v = System.Math.Sin(phase) >= 0.0 ? 1f : -1f;
                        break;
                    case Wave.Triangle:
                        v = Mathf.PingPong((float)(phase / System.Math.PI), 1f) * 2f - 1f;
                        break;
                    case Wave.Noise:
                        v = (float)(rng.NextDouble() * 2.0 - 1.0);
                        break;
                    default:
                        v = (float)System.Math.Sin(phase);
                        break;
                }
                float tSec = i / (float)SampleRate;
                float remain = (n - i) / (float)SampleRate;
                float env = 1f;
                if (attack > 0.0001f && tSec < attack) env *= tSec / attack;
                if (release > 0.0001f && remain < release) env *= remain / release;
                buf[idx] += v * gain * env;
            }
        }

        /// <summary>One-pole low-passed noise burst; the filter coefficient sweeps lp0 → lp1
        /// (0..1, higher = brighter), which is what sells whooshes and air.</summary>
        static void AddNoise(float[] buf, float start, float dur, float gain,
                             float attack, float release, float lp0, float lp1, int seed)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)(dur * SampleRate);
            if (n <= 1) return;
            var rng = new System.Random(seed);
            float y = 0f;
            for (int i = 0; i < n; i++)
            {
                int idx = s0 + i;
                if (idx < 0) continue;
                if (idx >= buf.Length) break;
                float t01 = i / (float)(n - 1);
                float a = Mathf.Lerp(lp0, lp1, t01);
                float x = (float)(rng.NextDouble() * 2.0 - 1.0);
                y += a * (x - y);
                float tSec = i / (float)SampleRate;
                float remain = (n - i) / (float)SampleRate;
                float env = 1f;
                if (attack > 0.0001f && tSec < attack) env *= tSec / attack;
                if (release > 0.0001f && remain < release) env *= remain / release;
                buf[idx] += y * gain * env;
            }
        }

        /// <summary>Seamless low-passed noise across the whole buffer: the head is crossfaded
        /// with an overrun tail so the clip loops without a click.</summary>
        static void AddLoopNoise(float[] buf, float gain, float lp, int seed)
        {
            int n = buf.Length;
            int fade = Mathf.Min(n / 8, SampleRate / 8);
            var rng = new System.Random(seed);
            float y = 0f;
            var tmp = new float[n + fade];
            for (int i = 0; i < tmp.Length; i++)
            {
                float x = (float)(rng.NextDouble() * 2.0 - 1.0);
                y += lp * (x - y);
                tmp[i] = y;
            }
            for (int i = 0; i < n; i++)
            {
                float v = tmp[i];
                if (i < fade)
                {
                    float k = i / (float)fade;
                    v = tmp[i] * k + tmp[n + i] * (1f - k);
                }
                buf[i] += v * gain;
            }
        }

        /// <summary>Amplitude wobble. For loop clips, pick hz so hz * loopSeconds is an integer.</summary>
        static void Tremolo(float[] buf, float hz, float depth)
        {
            for (int i = 0; i < buf.Length; i++)
            {
                float m = 1f - depth * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * hz * i / SampleRate));
                buf[i] *= m;
            }
        }

        /// <summary>Bell-ish note: fundamental + two decaying harmonics.</summary>
        static void AddNote(float[] buf, float start, float dur, float freq, float gain)
        {
            AddTone(buf, start, dur, freq, freq, Wave.Sine, gain, 0.008f, dur * 0.65f);
            AddTone(buf, start, dur, freq * 2f, freq * 2f, Wave.Sine, gain * 0.35f, 0.008f, dur * 0.6f);
            AddTone(buf, start, dur * 0.8f, freq * 3f, freq * 3f, Wave.Sine, gain * 0.14f, 0.008f, dur * 0.5f);
        }

        /// <summary>Hard de-click guarantee for one-shots: linear fade-in over the first
        /// <paramref name="attack"/> seconds and fade-out over the last <paramref name="release"/>
        /// seconds of the whole buffer, so every clip starts and ends at exactly zero amplitude
        /// no matter how its layers line up. Sized per sound (percussive hits keep a ~1 ms
        /// attack for punch; chimes/fanfares get softer edges and longer tails). Never applied
        /// to loop clips — their seams are made click-free by construction instead.</summary>
        static void FadeEnds(float[] buf, float attack, float release)
        {
            int a = Mathf.Clamp((int)(attack * SampleRate), 1, buf.Length / 2);
            int r = Mathf.Clamp((int)(release * SampleRate), 1, buf.Length / 2);
            for (int i = 0; i < a; i++) buf[i] *= i / (float)a;
            for (int i = 0; i < r; i++) buf[buf.Length - 1 - i] *= i / (float)r;
        }

        static void Normalize(float[] buf, float peak)
        {
            float max = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > max) max = a;
            }
            if (max < 1e-5f) return;
            float k = peak / max;
            for (int i = 0; i < buf.Length; i++) buf[i] *= k;
        }

        // ------------------------------------------------------------------
        // One-shots
        // ------------------------------------------------------------------

        /// <summary>Bright two-blip acknowledgment (panel action accepted).</summary>
        public static AudioClip UiConfirm()
        {
            AudioClip c; if (Has("ui_confirm", out c)) return c;
            var d = NewBuf(0.16f);
            AddTone(d, 0.00f, 0.07f, 880f, 880f, Wave.Sine, 0.55f, 0.003f, 0.045f);
            AddTone(d, 0.06f, 0.09f, 1318.5f, 1318.5f, Wave.Sine, 0.5f, 0.003f, 0.06f);
            AddTone(d, 0.06f, 0.09f, 2637f, 2637f, Wave.Sine, 0.12f, 0.003f, 0.06f);
            Normalize(d, 0.7f);
            FadeEnds(d, 0.003f, 0.03f);
            return Bake("ui_confirm", d);
        }

        /// <summary>Flat detuned buzz (action refused/blocked).</summary>
        public static AudioClip UiRefuse()
        {
            AudioClip c; if (Has("ui_refuse", out c)) return c;
            var d = NewBuf(0.24f);
            AddTone(d, 0f, 0.22f, 150f, 118f, Wave.Square, 0.28f, 0.004f, 0.07f);
            AddTone(d, 0f, 0.22f, 154f, 122f, Wave.Square, 0.22f, 0.004f, 0.07f);
            Normalize(d, 0.55f);
            FadeEnds(d, 0.004f, 0.025f);
            return Bake("ui_refuse", d);
        }

        /// <summary>Heavy restraint clunk-latch (bar seats home).</summary>
        public static AudioClip Clunk()
        {
            AudioClip c; if (Has("clunk", out c)) return c;
            var d = NewBuf(0.2f);
            AddTone(d, 0f, 0.16f, 92f, 70f, Wave.Sine, 0.9f, 0.001f, 0.13f);
            AddTone(d, 0f, 0.06f, 210f, 160f, Wave.Triangle, 0.35f, 0.001f, 0.05f);
            AddNoise(d, 0f, 0.03f, 0.5f, 0.001f, 0.02f, 0.5f, 0.2f, 71);
            Normalize(d, 0.85f);
            FadeEnds(d, 0.001f, 0.02f);
            return Bake("clunk", d);
        }

        /// <summary>Lighter clack for restraints releasing.</summary>
        public static AudioClip Clack()
        {
            AudioClip c; if (Has("clack", out c)) return c;
            var d = NewBuf(0.1f);
            AddTone(d, 0f, 0.08f, 150f, 120f, Wave.Sine, 0.5f, 0.001f, 0.06f);
            AddNoise(d, 0f, 0.02f, 0.4f, 0.001f, 0.015f, 0.6f, 0.3f, 72);
            Normalize(d, 0.6f);
            FadeEnds(d, 0.001f, 0.02f);
            return Bake("clack", d);
        }

        /// <summary>Tiny mechanical click (gate latch, small buttons).</summary>
        public static AudioClip Click()
        {
            AudioClip c; if (Has("click", out c)) return c;
            var d = NewBuf(0.06f);
            AddNoise(d, 0f, 0.012f, 0.6f, 0.0005f, 0.01f, 0.7f, 0.4f, 73);
            AddTone(d, 0.004f, 0.04f, 1500f, 1100f, Wave.Sine, 0.2f, 0.001f, 0.03f);
            Normalize(d, 0.5f);
            FadeEnds(d, 0.001f, 0.012f);
            return Bake("click", d);
        }

        /// <summary>Gate servo whir (doors swinging).</summary>
        public static AudioClip Servo()
        {
            AudioClip c; if (Has("servo", out c)) return c;
            var d = NewBuf(0.45f);
            AddTone(d, 0f, 0.42f, 120f, 185f, Wave.Triangle, 0.3f, 0.05f, 0.12f);
            AddTone(d, 0f, 0.42f, 241f, 372f, Wave.Triangle, 0.12f, 0.05f, 0.12f);
            AddNoise(d, 0f, 0.42f, 0.2f, 0.05f, 0.12f, 0.12f, 0.18f, 74);
            Normalize(d, 0.5f);
            FadeEnds(d, 0.006f, 0.03f);
            return Bake("servo", d);
        }

        /// <summary>Launch whoosh — filtered noise swelling open.</summary>
        public static AudioClip Whoosh()
        {
            AudioClip c; if (Has("whoosh", out c)) return c;
            var d = NewBuf(0.95f);
            AddNoise(d, 0f, 0.9f, 1f, 0.28f, 0.4f, 0.02f, 0.5f, 75);
            AddTone(d, 0.05f, 0.7f, 60f, 190f, Wave.Sine, 0.25f, 0.2f, 0.3f);
            Normalize(d, 0.8f);
            FadeEnds(d, 0.01f, 0.05f);
            return Bake("whoosh", d);
        }

        /// <summary>Clean dispatch success chime.</summary>
        public static AudioClip Chime()
        {
            AudioClip c; if (Has("chime", out c)) return c;
            var d = NewBuf(0.75f);
            AddNote(d, 0f, 0.7f, 1046.5f, 0.5f);
            AddNote(d, 0f, 0.7f, 1568f, 0.18f);
            Normalize(d, 0.65f);
            FadeEnds(d, 0.006f, 0.06f);
            return Bake("chime", d);
        }

        /// <summary>Fault-resolved ding (two rising notes).</summary>
        public static AudioClip ResolveDing()
        {
            AudioClip c; if (Has("resolve_ding", out c)) return c;
            var d = NewBuf(0.6f);
            AddNote(d, 0f, 0.16f, 659.25f, 0.4f);
            AddNote(d, 0.13f, 0.42f, 880f, 0.5f);
            Normalize(d, 0.65f);
            FadeEnds(d, 0.005f, 0.05f);
            return Bake("resolve_ding", d);
        }

        /// <summary>Shift-open power-up sweep.</summary>
        public static AudioClip PowerUp()
        {
            AudioClip c; if (Has("power_up", out c)) return c;
            var d = NewBuf(0.6f);
            AddTone(d, 0f, 0.45f, 330f, 660f, Wave.Sine, 0.4f, 0.03f, 0.18f);
            AddTone(d, 0f, 0.45f, 165f, 330f, Wave.Triangle, 0.2f, 0.03f, 0.18f);
            AddNote(d, 0.4f, 0.2f, 880f, 0.35f);
            Normalize(d, 0.6f);
            FadeEnds(d, 0.006f, 0.05f);
            return Bake("power_up", d);
        }

        /// <summary>Landing thump for the first-person camera.</summary>
        public static AudioClip Thump()
        {
            AudioClip c; if (Has("thump", out c)) return c;
            var d = NewBuf(0.16f);
            AddTone(d, 0f, 0.13f, 58f, 42f, Wave.Sine, 0.9f, 0.001f, 0.1f);
            AddNoise(d, 0f, 0.05f, 0.25f, 0.001f, 0.04f, 0.08f, 0.05f, 76);
            Normalize(d, 0.8f);
            FadeEnds(d, 0.001f, 0.02f);
            return Bake("thump", d);
        }

        /// <summary>Crowd cheer pop (layered upward vocal-ish sweeps over band noise).</summary>
        public static AudioClip CheerPop()
        {
            AudioClip c; if (Has("cheer_pop", out c)) return c;
            var d = NewBuf(0.75f);
            AddNoise(d, 0f, 0.6f, 0.65f, 0.04f, 0.4f, 0.14f, 0.24f, 77);
            for (int k = 0; k < 6; k++)
                AddTone(d, 0.02f * k, 0.32f, 260f + 34f * k, 680f + 70f * k,
                        Wave.Sine, 0.11f, 0.05f, 0.2f);
            Normalize(d, 0.7f);
            FadeEnds(d, 0.008f, 0.06f);
            return Bake("cheer_pop", d);
        }

        /// <summary>Rising combo arpeggio; more streak = more (and higher) notes.</summary>
        public static AudioClip Arpeggio(int steps)
        {
            steps = Mathf.Clamp(steps, 2, 8);
            string key = "arp_" + steps;
            AudioClip c; if (Has(key, out c)) return c;
            float[] notes = { 523.25f, 587.33f, 659.25f, 783.99f, 880f, 1046.5f, 1174.66f, 1318.51f };
            var d = NewBuf(0.085f * steps + 0.3f);
            for (int i = 0; i < steps; i++)
                AddNote(d, 0.082f * i, 0.14f, notes[i], 0.32f + 0.025f * i);
            Normalize(d, 0.68f);
            FadeEnds(d, 0.005f, 0.05f);
            return Bake(key, d);
        }

        /// <summary>Results fanfare, themed by grade letter (S/A triumphant … D/F glum).</summary>
        public static AudioClip Fanfare(char grade)
        {
            string key = "fanfare_" + grade;
            AudioClip c; if (Has(key, out c)) return c;
            var d = NewBuf(2.0f);
            if (grade == 'S' || grade == 'A')
            {
                float[] seq = { 523.25f, 659.25f, 783.99f, 1046.5f };
                for (int i = 0; i < seq.Length; i++) AddNote(d, 0.14f * i, 0.18f, seq[i], 0.45f);
                AddNote(d, 0.58f, 1.25f, 1046.5f, 0.45f);
                AddNote(d, 0.58f, 1.25f, 1318.51f, 0.3f);
                AddNote(d, 0.58f, 1.25f, 783.99f, 0.26f);
            }
            else if (grade == 'B')
            {
                float[] seq = { 523.25f, 659.25f, 783.99f };
                for (int i = 0; i < seq.Length; i++) AddNote(d, 0.16f * i, 0.2f, seq[i], 0.42f);
                AddNote(d, 0.52f, 1.1f, 783.99f, 0.42f);
                AddNote(d, 0.52f, 1.1f, 523.25f, 0.28f);
            }
            else if (grade == 'C')
            {
                AddNote(d, 0f, 0.3f, 392f, 0.4f);
                AddNote(d, 0.3f, 1.0f, 523.25f, 0.4f);
            }
            else
            {
                AddNote(d, 0f, 0.4f, 466.16f, 0.4f);
                AddNote(d, 0.42f, 0.4f, 415.3f, 0.38f);
                AddNote(d, 0.86f, 1.0f, 349.23f, 0.42f);
                AddNote(d, 0.86f, 1.0f, 277.18f, 0.28f);
            }
            Normalize(d, 0.7f);
            FadeEnds(d, 0.008f, 0.08f);
            return Bake(key, d);
        }

        // ------------------------------------------------------------------
        // Loops (all seamless by construction)
        // ------------------------------------------------------------------

        /// <summary>Coaster rumble loop (2s): low sine stack + filtered noise + wheel-clatter AM.
        /// All partials are integer multiples of 0.5 Hz so the 2-second loop is click-free.</summary>
        public static AudioClip RumbleLoop()
        {
            AudioClip c; if (Has("rumble_loop", out c)) return c;
            var d = NewBuf(2f);
            float[] freqs = { 27f, 40.5f, 54f, 67.5f, 81f };
            float[] gains = { 0.5f, 0.34f, 0.24f, 0.15f, 0.1f };
            for (int k = 0; k < freqs.Length; k++)
                AddTone(d, 0f, 2f, freqs[k], freqs[k], Wave.Sine, gains[k], 0f, 0f);
            AddLoopNoise(d, 0.55f, 0.05f, 771);
            Tremolo(d, 1.5f, 0.25f);   // 3 cycles per loop
            Tremolo(d, 4f, 0.12f);     // 8 cycles per loop — reads as track clatter
            Normalize(d, 0.6f);
            return Bake("rumble_loop", d);
        }

        /// <summary>Crowd walla loop (3s): dull + bright noise beds with slow chatter wobble.</summary>
        public static AudioClip WallaLoop()
        {
            AudioClip c; if (Has("walla_loop", out c)) return c;
            var d = NewBuf(3f);
            AddLoopNoise(d, 0.8f, 0.09f, 311);
            AddLoopNoise(d, 0.28f, 0.3f, 313);
            Tremolo(d, 1f, 0.3f);        // 3 cycles per loop
            Tremolo(d, 7f / 3f, 0.2f);   // 7 cycles per loop
            Normalize(d, 0.5f);
            return Bake("walla_loop", d);
        }

        /// <summary>Unhappy-queue grumble drone loop (2.5s): low saw cluster + murmur noise.</summary>
        public static AudioClip GrumbleLoop()
        {
            AudioClip c; if (Has("grumble_loop", out c)) return c;
            var d = NewBuf(2.5f);
            AddTone(d, 0f, 2.5f, 49.2f, 49.2f, Wave.Saw, 0.22f, 0f, 0f);
            AddTone(d, 0f, 2.5f, 55.2f, 55.2f, Wave.Saw, 0.2f, 0f, 0f);
            AddTone(d, 0f, 2.5f, 62f, 62f, Wave.Saw, 0.18f, 0f, 0f);
            AddLoopNoise(d, 0.5f, 0.035f, 317);
            Tremolo(d, 1.2f, 0.35f);     // 3 cycles per loop
            Normalize(d, 0.5f);
            return Bake("grumble_loop", d);
        }

        /// <summary>Fault klaxon loop. Minor = single soft beep per bar; major = urgent two-tone.
        /// Loop period matches FaultStrobe.SyncHz so light and sound flash together.</summary>
        public static AudioClip KlaxonLoop(bool major)
        {
            string key = major ? "klaxon_major" : "klaxon_minor";
            AudioClip c; if (Has(key, out c)) return c;
            float period = 1f / FaultStrobe.SyncHz;
            var d = NewBuf(period);
            if (major)
            {
                AddTone(d, 0f, period * 0.42f, 424f, 424f, Wave.Saw, 0.5f, 0.008f, 0.05f);
                AddTone(d, period * 0.5f, period * 0.42f, 302f, 302f, Wave.Saw, 0.5f, 0.008f, 0.05f);
            }
            else
            {
                AddTone(d, 0f, period * 0.4f, 640f, 600f, Wave.Triangle, 0.55f, 0.01f, 0.09f);
            }
            Normalize(d, 0.55f);
            return Bake(key, d);
        }
    }
}
