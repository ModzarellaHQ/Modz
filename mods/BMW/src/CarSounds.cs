using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CheeseMods
{
    public class CarSounds : MonoBehaviour
    {
        private const int Rate = 44100;
        private static Dictionary<string, AudioClip> clips;
        private static AudioClip[] crunch, glass;
        private static AudioClip squeal, scrape, turbo, blowoff, pop;

        private AudioSource low, high, turboSrc, squealSrc, scrapeSrc, oneShot;
        private float lastThrottle, nextPop;
        public float Volume = 0.6f;

        public static void EnsureClips()
        {
            if (clips != null) return;
            clips = new Dictionary<string, AudioClip>();
            string dir = Path.Combine(Path.GetDirectoryName(typeof(CarSounds).Assembly.Location), "sounds");
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.wav"))
                {
                    try { clips[Path.GetFileNameWithoutExtension(f)] = LoadWav(f); }
                    catch (Exception e) { CarPlugin.Log.LogWarning($"sound {f}: {e.Message}"); }
                }
            crunch = new[] { Crunch(11, 0.9f), Crunch(23, 1.2f), Crunch(37, 0.7f) };
            glass = new[] { Glass(5), Glass(9) };
            squeal = Squeal();
            scrape = Scrape();
            turbo = Tone(2600f, 0.25f);
            blowoff = Blowoff();
            pop = Pop();
            CarPlugin.Log.LogInfo($"Car sounds: {clips.Count} samples loaded from {dir}");
        }

        public static AudioClip Get(string name, Func<AudioClip> fallback) =>
            clips != null && clips.TryGetValue(name, out var c) ? c : fallback();

        private AudioSource Src(AudioClip clip, bool loop, float minD = 25f)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.clip = clip; s.loop = loop; s.playOnAwake = false;
            s.spatialBlend = 0.75f; s.minDistance = minD; s.maxDistance = 600f;
            s.rolloffMode = AudioRolloffMode.Linear; s.dopplerLevel = 0.4f;
            s.volume = 0f;
            if (loop) { s.time = UnityEngine.Random.value * clip.length * 0.9f; s.Play(); }
            return s;
        }

        private void Awake()
        {
            EnsureClips();
            low = Src(Get("engine_low", CarAudio.Engine), true);
            high = Src(Get("engine_high", CarAudio.Engine), true);
            turboSrc = Src(turbo, true);
            squealSrc = Src(squeal, true);
            scrapeSrc = Src(scrape, true);
            oneShot = Src(null, false);
            oneShot.volume = 1f;
        }

        public void Drive(bool running, float rpm, float throttle, float slip, float scrapeAmt, bool shifting)
        {
            float v = Volume * ModCommon.GameSfxVolume * 2f;
            if (!running)
            {
                low.volume = high.volume = turboSrc.volume = 0f;
            }
            else
            {
                float lowW = Mathf.Clamp01(1f - (rpm - 0.15f) / 0.35f);
                float highW = Mathf.Clamp01((rpm - 0.2f) / 0.35f);
                float load = Mathf.Lerp(0.55f, 1f, throttle);
                low.pitch = Mathf.Lerp(0.7f, 1.25f, rpm);
                high.pitch = Mathf.Lerp(0.38f, 0.8f, rpm);
                low.volume = lowW * load * v;
                high.volume = highW * load * v * 0.9f;
                turboSrc.pitch = Mathf.Lerp(0.6f, 1.5f, rpm);
                turboSrc.volume = throttle * rpm * 0.05f * v;

                if (lastThrottle > 0.6f && throttle < 0.2f && rpm > 0.45f) oneShot.PlayOneShot(blowoff, 0.5f * v);
                if (throttle < 0.1f && rpm > 0.5f && Time.time > nextPop)
                {
                    nextPop = Time.time + UnityEngine.Random.Range(0.08f, 0.5f);
                    if (UnityEngine.Random.value < 0.45f) { oneShot.pitch = UnityEngine.Random.Range(0.8f, 1.3f); oneShot.PlayOneShot(pop, 0.6f * v); }
                }
                if (shifting) oneShot.PlayOneShot(pop, 0.35f * v);
            }
            lastThrottle = throttle;
            squealSrc.volume = Mathf.Lerp(squealSrc.volume, Mathf.Clamp01(slip) * 0.55f * v, Time.deltaTime * 12f);
            squealSrc.pitch = 0.8f + slip * 0.15f;
            scrapeSrc.volume = Mathf.Lerp(scrapeSrc.volume, Mathf.Clamp01(scrapeAmt) * 0.6f * v, Time.deltaTime * 15f);
        }

        public void Crash(float strength01, bool breakGlass)
        {
            oneShot.pitch = UnityEngine.Random.Range(0.85f, 1.15f);
            float v = Volume * ModCommon.GameSfxVolume * 2f;
            oneShot.pitch = UnityEngine.Random.Range(0.85f, 1.1f);
            oneShot.PlayOneShot(crunch[UnityEngine.Random.Range(0, crunch.Length)], Mathf.Lerp(0.35f, 1f, strength01) * v);
            if (breakGlass) oneShot.PlayOneShot(glass[UnityEngine.Random.Range(0, glass.Length)], 0.7f * v);
        }

        public void PlayOnce(string name, float vol)
        {
            if (clips != null && clips.TryGetValue(name, out var c)) { oneShot.pitch = 1f; oneShot.PlayOneShot(c, vol * Volume * ModCommon.GameSfxVolume * 2f); }
        }

        private static AudioClip Make(string name, float[] d)
        {
            var c = AudioClip.Create(name, d.Length, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        private static AudioClip Crunch(int seed, float len)
        {
            var rng = new System.Random(seed);
            int n = (int)(Rate * len);
            var d = new float[n];
            var res = new (float f, float decay, float amp)[6];
            for (int i = 0; i < res.Length; i++) res[i] = (300f + (float)rng.NextDouble() * 2500f, 6f + (float)rng.NextDouble() * 18f, 0.1f + (float)rng.NextDouble() * 0.25f);
            float lp = 0f, crackle = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float noise = (float)rng.NextDouble() * 2f - 1f;
                lp += 0.08f * (noise - lp);
                float thud = Mathf.Sin(2f * Mathf.PI * (55f - 20f * t) * t) * Mathf.Exp(-t * 9f) * 0.9f;
                if (rng.NextDouble() < 0.004 * Math.Exp(-t * 4)) crackle = 1f;
                crackle *= 0.995f;
                float v = thud + lp * Mathf.Exp(-t * 7f) * 1.4f + noise * crackle * 0.5f * Mathf.Exp(-t * 3f);
                foreach (var r in res) v += Mathf.Sin(2f * Mathf.PI * r.f * t) * Mathf.Exp(-t * r.decay) * r.amp;
                d[i] = (float)Math.Tanh(v * 1.3f) * 0.9f;
            }
            return Make("crunch" + seed, d);
        }

        private static AudioClip Glass(int seed)
        {
            var rng = new System.Random(seed);
            int n = (int)(Rate * 1.4f);
            var d = new float[n];
            var tinkles = new List<(int at, float f, float a)>();
            for (int i = 0; i < 40; i++) tinkles.Add(((int)(Rate * Math.Pow(rng.NextDouble(), 1.8) * 1.2), 2500f + (float)rng.NextDouble() * 6000f, 0.05f + (float)rng.NextDouble() * 0.2f));
            float hp = 0f, prev = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float noise = (float)rng.NextDouble() * 2f - 1f;
                hp = 0.9f * (hp + noise - prev); prev = noise;
                float v = hp * Mathf.Exp(-t * 14f) * 0.8f;
                foreach (var tk in tinkles)
                    if (i >= tk.at) { float tt = (i - tk.at) / (float)Rate; v += Mathf.Sin(2f * Mathf.PI * tk.f * tt) * Mathf.Exp(-tt * 30f) * tk.a; }
                d[i] = Mathf.Clamp(v, -1f, 1f);
            }
            return Make("glass" + seed, d);
        }

        private static AudioClip Squeal()
        {
            var rng = new System.Random(3);
            var d = new float[Rate];
            float y1 = 0, y2 = 0;
            for (int i = 0; i < Rate; i++)
            {
                float t = (float)i / Rate;
                float f = 1100f + 120f * Mathf.Sin(2f * Mathf.PI * 3f * t) + 60f * Mathf.Sin(2f * Mathf.PI * 7f * t);
                float w = 2f * Mathf.PI * f / Rate, r = 0.995f;
                float x = (float)rng.NextDouble() * 2f - 1f;
                float y = x * 0.08f + 2f * r * Mathf.Cos(w) * y1 - r * r * y2;
                y2 = y1; y1 = y;
                d[i] = Mathf.Clamp(y * 1.5f, -1f, 1f);
            }
            return Make("squeal", d);
        }

        private static AudioClip Scrape()
        {
            var rng = new System.Random(4);
            var d = new float[Rate];
            float lp = 0f;
            for (int i = 0; i < Rate; i++)
            {
                float x = (float)rng.NextDouble() * 2f - 1f;
                lp += 0.15f * (x - lp);
                float grit = rng.NextDouble() < 0.02 ? x : 0f;
                d[i] = Mathf.Clamp(lp * 1.6f + grit * 0.6f, -1f, 1f);
            }
            return Make("scrape", d);
        }

        private static AudioClip Tone(float f, float amp)
        {
            var d = new float[Rate];
            for (int i = 0; i < Rate; i++) d[i] = Mathf.Sin(2f * Mathf.PI * f * i / Rate) * amp;
            return Make("tone", d);
        }

        private static AudioClip Blowoff()
        {
            var rng = new System.Random(6);
            int n = (int)(Rate * 0.45f);
            var d = new float[n];
            float hp = 0, prev = 0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float x = (float)rng.NextDouble() * 2f - 1f;
                hp = 0.97f * (hp + x - prev); prev = x;
                d[i] = hp * Mathf.Min(1f, t * 60f) * Mathf.Exp(-t * 7f) * 0.7f;
            }
            return Make("blowoff", d);
        }

        private static AudioClip Pop()
        {
            var rng = new System.Random(8);
            int n = (int)(Rate * 0.12f);
            var d = new float[n];
            float lp = 0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float x = (float)rng.NextDouble() * 2f - 1f;
                lp += 0.25f * (x - lp);
                d[i] = (float)Math.Tanh((lp * 3f + Mathf.Sin(2f * Mathf.PI * 90f * t)) * Mathf.Exp(-t * 45f) * 2f) * 0.9f;
            }
            return Make("pop", d);
        }

        public static AudioClip LoadWav(string path)
        {
            var b = File.ReadAllBytes(path);
            int pos = 12, channels = 1, rate = Rate, bits = 16;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(b, pos + 10);
                    rate = BitConverter.ToInt32(b, pos + 12);
                    bits = BitConverter.ToInt16(b, pos + 22);
                }
                else if (id == "data")
                {
                    if (bits != 16) throw new Exception("only 16-bit PCM");
                    int samples = size / 2;
                    var d = new float[samples];
                    for (int i = 0; i < samples; i++) d[i] = BitConverter.ToInt16(b, pos + 8 + i * 2) / 32768f;
                    var c = AudioClip.Create(Path.GetFileNameWithoutExtension(path), samples / channels, channels, rate, false);
                    c.SetData(d, 0);
                    return c;
                }
                pos += 8 + size + (size & 1);
            }
            throw new Exception("no data chunk");
        }
    }
}
