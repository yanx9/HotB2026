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

        // krok: stłumiony szum i niskie stuknięcie; weight przesuwa go w stronę ciężkiego buta
        public static AudioClip Step(float weight)
        {
            var random = new System.Random(3);
            float low = 0f;
            return Make("Krok", 0.22f, 0.8f, t =>
            {
                low += ((float)random.NextDouble() * 2f - 1f - low) * Mathf.Lerp(0.3f, 0.08f, weight);
                return low * Mathf.Exp(-t * 35f) + weight * Sine(70f, t) * Mathf.Exp(-t * 22f);
            });
        }

        // skan: krótki ton, który opada jak echo
        public static AudioClip Ping() =>
            Make("Skan", 1.4f, 0.6f, t => (Sine(880f - 300f * t, t) + 0.4f * Sine(1760f - 600f * t, t)) * Mathf.Exp(-t * 3.2f));

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
