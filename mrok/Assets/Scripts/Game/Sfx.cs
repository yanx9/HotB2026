using UnityEngine;

namespace Mrok.Game
{
    // Wszystkie dźwięki powstają z kodu. W tej grze dźwięk zastępuje wzrok, kiedy oczy są zamknięte.
    public static class Sfx
    {
        const int Rate = 44100;

        static AudioClip Make(string name, float seconds, float peak, System.Func<float, float> wave)
        {
            var samples = new float[(int)(seconds * Rate)];
            float max = 1e-6f;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = wave(i / (float)Rate);
                max = Mathf.Max(max, Mathf.Abs(samples[i]));
            }
            for (int i = 0; i < samples.Length; i++) samples[i] *= peak / max;
            var clip = AudioClip.Create(name, samples.Length, 1, Rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        static float Sine(float frequency, float t) => Mathf.Sin(2f * Mathf.PI * frequency * t);

        // krok: miękki, stłumiony szmer podeszwy (pięta, potem palce) bez twardego stuknięcia;
        // weight dodaje cichy, niski ciężar buta
        public static AudioClip Step(float weight)
        {
            var random = new System.Random(3);
            float low = 0f, lower = 0f;
            return Make("Krok", 0.32f, 0.45f, t =>
            {
                // dwa stopnie wygładzania: z szumu zostaje samo głuche "fff"
                low += ((float)random.NextDouble() * 2f - 1f - low) * Mathf.Lerp(0.09f, 0.05f, weight);
                lower += (low - lower) * 0.25f;
                float toe = t - 0.075f;
                float envelope = Mathf.Min(1f, t / 0.012f) * Mathf.Exp(-t * 26f)
                               + (toe > 0f ? 0.55f * Mathf.Min(1f, toe / 0.015f) * Mathf.Exp(-toe * 30f) : 0f);
                return lower * envelope + weight * 0.05f * Sine(52f, t) * Mathf.Min(1f, t / 0.02f) * Mathf.Exp(-t * 24f);
            });
        }

        // skan: niski oddech fali, który narasta i długo wybrzmiewa, bez melodyjnego tonu
        public static AudioClip Ping()
        {
            var random = new System.Random(11);
            float low = 0f, lower = 0f;
            return Make("Skan", 1.8f, 0.5f, t =>
            {
                low += ((float)random.NextDouble() * 2f - 1f - low) * Mathf.Lerp(0.2f, 0.03f, Mathf.Clamp01(t / 0.9f));   // szum ciemnieje z czasem
                lower += (low - lower) * 0.3f;
                float swell = Mathf.Min(1f, t / 0.05f) * Mathf.Exp(-t * 2.4f);
                return lower * swell * 3f + 0.12f * Sine(82f - 18f * t, t) * Mathf.Min(1f, t / 0.03f) * Mathf.Exp(-t * 3.5f);
            });
        }

        // tyknięcie co sekundę zamkniętych oczu; wysokość ustawia gra
        public static AudioClip Tick() =>
            Make("Tyk", 0.12f, 0.35f, t => Sine(520f, t) * Mathf.Exp(-t * 40f));

        // łup: dzwonek, po którym następuje cisza, grany w pętli z miejsca, gdzie leży
        public static AudioClip Beacon() =>
            Make("Łup", 2.6f, 0.5f, t => t > 1.2f ? 0f : (Sine(1318f, t) + 0.6f * Sine(1976f, t) + 0.3f * Sine(2637f, t)) * Mathf.Exp(-t * 5f));

        public static AudioClip Pickup() =>
            Make("Mam", 0.7f, 0.6f, t => (Sine(t < 0.12f ? 988f : 1319f, t) + 0.4f * Sine(t < 0.12f ? 1976f : 2638f, t)) * Mathf.Exp(-t * 5f));

        // strażnik coś usłyszał
        public static AudioClip Alert() =>
            Make("Hm", 0.45f, 0.7f, t =>
            {
                float f = 150f - 60f * t;
                return (Sine(f, t) + 0.5f * Sine(2f * f, t) + 0.3f * Sine(3f * f, t)) * Mathf.Min(1f, t * 30f) * Mathf.Min(1f, (0.45f - t) * 10f);
            });

        // kamień spada na posadzkę: twarde stuknięcie i dwa cichsze odbicia
        public static AudioClip Clack()
        {
            var random = new System.Random(5);
            float low = 0f;
            return Make("Kamień", 0.5f, 0.9f, t =>
            {
                low += ((float)random.NextDouble() * 2f - 1f - low) * 0.5f;
                float sum = 0f;
                foreach (var (at, gain) in new[] { (0f, 1f), (0.16f, 0.45f), (0.27f, 0.2f) })
                {
                    float s = t - at;
                    if (s >= 0f) sum += gain * (low * Mathf.Exp(-s * 90f) + 0.6f * Sine(1250f, s) * Mathf.Exp(-s * 70f) + 0.5f * Sine(310f, s) * Mathf.Exp(-s * 45f));
                }
                return sum;
            });
        }

        // jedno uderzenie serca: dwa głuche stuknięcia
        public static AudioClip Heartbeat() =>
            Make("Serce", 0.42f, 0.9f, t =>
            {
                float second = t - 0.15f;
                return Sine(58f, t) * Mathf.Min(1f, t / 0.012f) * Mathf.Exp(-t * 30f)
                     + (second > 0f ? 0.7f * Sine(50f, second) * Mathf.Min(1f, second / 0.012f) * Mathf.Exp(-second * 26f) : 0f);
            });

        // drzwi nie puszczają: brakuje łupu
        public static AudioClip Deny() =>
            Make("Nie", 0.35f, 0.5f, t => (Sine(196f, t) + 0.5f * Sine(185f, t)) * Mathf.Min(1f, t * 60f) * Mathf.Exp(-t * 9f));

        public static AudioClip Caught() =>
            Make("Złapany", 1.2f, 0.9f, t => (Sine(55f, t) + 0.6f * Sine(82f, t) + 0.3f * Sine(110f * (1f - 0.2f * t), t)) * Mathf.Exp(-t * 2.5f));

        public static AudioClip Win() =>
            Make("Wyjście", 1.6f, 0.6f, t =>
            {
                int n = Mathf.Min(3, (int)(t / 0.16f));
                float f = new[] { 523f, 659f, 784f, 1047f }[n];
                return (Sine(f, t) + 0.3f * Sine(2f * f, t)) * Mathf.Exp(-(t - n * 0.16f) * (n == 3 ? 2.5f : 8f));
            });
    }
}
