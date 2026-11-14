using UnityEngine;

namespace Precel.Game
{
    // Dźwięk syntezowany z kodu: polka w tle (tuba, akordeon, klarnet) i efekty.
    public static class Sfx
    {
        const int Rate = 44100;

        static AudioClip ToClip(string name, float[] samples, float peak)
        {
            float max = 1e-6f;
            foreach (float s in samples) max = Mathf.Max(max, Mathf.Abs(s));
            for (int i = 0; i < samples.Length; i++) samples[i] *= peak / max;
            var clip = AudioClip.Create(name, samples.Length, 1, Rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        static float Hz(int midi) => 440f * Mathf.Pow(2f, (midi - 69) / 12f);

        // nuta dopisywana z zawinięciem, żeby pętla nie miała szwu
        static void Note(float[] buffer, float start, float length, float frequency, float gain, int voice)
        {
            int from = (int)(start * Rate), count = (int)((length + 0.15f) * Rate);
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)Rate;
                float envelope = Mathf.Min(1f, t / 0.012f) * (t < length ? 1f : Mathf.Exp(-(t - length) * 30f));
                float phase = 2f * Mathf.PI * frequency * t;
                float sample;
                if (voice == 0)        // tuba
                    sample = (Mathf.Sin(phase) + 0.45f * Mathf.Sin(2f * phase) + 0.2f * Mathf.Sin(3f * phase)) * Mathf.Exp(-t * 3f);
                else if (voice == 1)   // akordeon
                {
                    sample = 0f;
                    for (int h = 1; h <= 6; h++) sample += Mathf.Sin(h * phase * (1f + 0.0015f * h)) / h;
                    sample *= 0.6f;
                }
                else                   // klarnet
                {
                    float vibrato = 1f + 0.004f * Mathf.Sin(2f * Mathf.PI * 5.5f * t);
                    sample = Mathf.Sin(phase * vibrato) + 0.33f * Mathf.Sin(3f * phase * vibrato) + 0.12f * Mathf.Sin(5f * phase * vibrato);
                }
                buffer[(from + i) % buffer.Length] += sample * envelope * gain;
            }
        }

        public static AudioClip Polka()
        {
            const float beat = 60f / 116f;
            // akord na takt (2/4): prymy F, C, B
            int[] roots = { 5, 5, 0, 0, 0, 0, 5, 5, 5, 5, 10, 10, 0, 0, 5, 5 };
            bool[] seventh = { false, false, true, true, true, true, false, false, false, false, false, false, true, true, false, false };
            int[] tune = { 0, 1, 2, 1, 2, 3, 2, 1 };   // który składnik akordu gra klarnet w kolejnych ósemkach

            var buffer = new float[(int)(roots.Length * 2 * beat * Rate)];
            for (int bar = 0; bar < roots.Length; bar++)
            {
                float start = bar * 2 * beat;
                int root = 36 + roots[bar];
                int[] chord = { root + 24, root + 28, root + 31, root + (seventh[bar] ? 34 : 36) };

                Note(buffer, start, beat * 0.8f, Hz(root), 0.9f, 0);
                Note(buffer, start + beat, beat * 0.8f, Hz(root + 7 - (roots[bar] >= 5 ? 12 : 0)), 0.8f, 0);
                for (int half = 0; half < 2; half++)
                    for (int n = 0; n < 3; n++)
                        Note(buffer, start + (half + 0.5f) * beat, beat * 0.22f, Hz(chord[n] - 12), 0.16f, 1);
                for (int eighth = 0; eighth < 4; eighth++)
                {
                    if (bar % 4 == 3 && eighth > 1) continue;   // oddech na końcu frazy
                    int step = tune[(bar * 4 + eighth) % tune.Length];
                    Note(buffer, start + eighth * beat / 2f, beat * 0.42f, Hz(chord[step]), 0.2f, 2);
                }
            }
            return ToClip("Polka", buffer, 0.8f);
        }

        public static AudioClip Chop()
        {
            var samples = new float[(int)(0.3f * Rate)];
            var random = new System.Random(7);
            float low = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)Rate;
                low += ((float)random.NextDouble() * 2f - 1f - low) * 0.25f;
                samples[i] = low * Mathf.Exp(-t * 45f) + 0.9f * Mathf.Sin(2f * Mathf.PI * 95f * t) * Mathf.Exp(-t * 20f);
            }
            return ToClip("Ciach", samples, 0.9f);
        }

        public static AudioClip Bell()
        {
            var samples = new float[(int)(1.2f * Rate)];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)Rate;
                samples[i] = (Mathf.Sin(2f * Mathf.PI * 1568f * t) + 0.5f * Mathf.Sin(2f * Mathf.PI * 2349f * t)
                              + 0.25f * Mathf.Sin(2f * Mathf.PI * 3136f * t)) * Mathf.Exp(-t * 4.5f);
            }
            return ToClip("Dzwonek", samples, 0.6f);
        }

        public static AudioClip Buzz()
        {
            var samples = new float[(int)(0.45f * Rate)];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)Rate;
                float phase = 2f * Mathf.PI * (130f - 60f * t) * t;
                samples[i] = (Mathf.Sin(phase) + 0.5f * Mathf.Sin(3f * phase)) * Mathf.Min(1f, (0.45f - t) * 12f);
            }
            return ToClip("Pudło", samples, 0.5f);
        }
    }
}
