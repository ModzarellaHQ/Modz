using UnityEngine;

namespace CheeseMods
{
    public class PoopSynth : MonoBehaviour
    {
        public volatile float Intensity;
        public volatile float Volume = 0.7f;

        private AudioSource src;
        private System.Random rng = new System.Random();
        private int rate;
        private float amp, phase, f0 = 85f, f0Target = 85f, jitter;
        private float gate = 1f, gateTarget = 1f; private int gateCount;
        private Biquad f1, f2, lp;
        private float bubbleF, bubbleAmp, bubblePhase, bubbleDecay;
        private float wetDrift;

        private void Awake()
        {
            rate = UnityEngine.AudioSettings.outputSampleRate;
            src = gameObject.AddComponent<AudioSource>();
            var clip = AudioClip.Create("poop_carrier", rate, 1, rate, false);
            var ones = new float[rate]; for (int i = 0; i < ones.Length; i++) ones[i] = 1f;
            clip.SetData(ones, 0);
            src.clip = clip; src.loop = true; src.spatialBlend = 0.6f;
            src.minDistance = 25f; src.maxDistance = 500f; src.rolloffMode = AudioRolloffMode.Linear;
            src.Play();
            f1 = Biquad.BandPass(220f, 3.5f, rate);
            f2 = Biquad.BandPass(640f, 5f, rate);
            lp = Biquad.LowPass(2200f, 0.7f, rate);
        }

        private float R() => (float)rng.NextDouble();

        private void OnAudioFilterRead(float[] data, int channels)
        {
            float target = Mathf.Clamp01(Intensity) * Volume;
            for (int i = 0; i < data.Length; i += channels)
            {
                amp += (target - amp) * (target > amp ? 0.002f : 0.0006f);
                if (amp < 1e-4f) { for (int c = 0; c < channels; c++) data[i + c] = 0f; continue; }

                if (rng.Next(rate / 12) == 0) f0Target = 60f + R() * 55f + Intensity * 25f;
                f0 += (f0Target - f0) * 0.0005f;

                if (--gateCount <= 0)
                {
                    float r = R();
                    gateTarget = r < 0.12f ? 0.05f : r < 0.3f ? 0.5f : 1f;
                    gateCount = (int)(rate * (gateTarget < 0.1f ? 0.02f + R() * 0.05f : 0.04f + R() * 0.25f));
                }
                gate += (gateTarget - gate) * 0.004f;

                phase += (f0 * (1f + jitter)) / rate;
                if (phase >= 1f) { phase -= 1f; jitter = (R() - 0.5f) * 0.18f; }
                float reed = phase < 0.33f ? 0.5f - 0.5f * Mathf.Cos(phase / 0.33f * 2f * Mathf.PI) : -0.12f;

                wetDrift += (R() - 0.5f) * 0.0005f; wetDrift = Mathf.Clamp(wetDrift, 0.4f, 1f);
                if (bubbleAmp < 0.01f && rng.Next((int)(rate / (8f + 30f * wetDrift))) == 0)
                {
                    bubbleF = 180f + R() * 900f; bubbleAmp = 0.25f + R() * 0.45f;
                    bubbleDecay = Mathf.Exp(-1f / (rate * (0.008f + R() * 0.03f))); bubblePhase = 0f;
                }
                bubblePhase += bubbleF / rate * (1f + bubbleAmp * 0.6f); // pitch rises as the bubble collapses
                float bubble = Mathf.Sin(bubblePhase * 2f * Mathf.PI) * bubbleAmp;
                bubbleAmp *= bubbleDecay;

                float noise = R() * 2f - 1f;
                float body = f1.Process(reed + noise * 0.25f) * 1.6f + f2.Process(reed) * 0.9f;
                float spray = lp.Process(noise) * 0.18f * wetDrift;
                float x = (body * gate + bubble * 0.6f + spray) * amp * 2.2f;
                float y = (float)System.Math.Tanh(x);
                for (int c = 0; c < channels; c++) data[i + c] = y * data[i + c];
            }
        }

        private struct Biquad
        {
            private float b0, b1, b2, a1, a2, z1, z2;

            public static Biquad BandPass(float f, float q, int sr)
            {
                float w = 2f * Mathf.PI * f / sr, alpha = Mathf.Sin(w) / (2f * q), a0 = 1f + alpha;
                return new Biquad { b0 = alpha / a0, b1 = 0f, b2 = -alpha / a0, a1 = -2f * Mathf.Cos(w) / a0, a2 = (1f - alpha) / a0 };
            }

            public static Biquad LowPass(float f, float q, int sr)
            {
                float w = 2f * Mathf.PI * f / sr, alpha = Mathf.Sin(w) / (2f * q), cs = Mathf.Cos(w), a0 = 1f + alpha;
                return new Biquad { b0 = (1f - cs) / 2f / a0, b1 = (1f - cs) / a0, b2 = (1f - cs) / 2f / a0, a1 = -2f * cs / a0, a2 = (1f - alpha) / a0 };
            }

            public float Process(float x)
            {
                float y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
        }
    }
}
