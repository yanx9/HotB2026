using System.Collections.Generic;
using UnityEngine;

namespace Ogrod.Music
{
    // Prosty syntezator: zamienia zapis nutowy na AudioClip. Zastępuje "gotowe instrumenty
    // w silniku audio", żeby prototyp nie potrzebował żadnych plików dźwiękowych.
    public static class Synth
    {
        public const int SampleRate = 44100;
        const float Tau = 2f * Mathf.PI;
        static readonly int[] Major = { 0, 2, 4, 5, 7, 9, 11 };
        static readonly int[] Minor = { 0, 2, 3, 5, 7, 8, 10 };

        public static float Frequency(int degree, int octave, bool minor)
        {
            int oct = Mathf.FloorToInt(degree / 7f);
            int semitones = (minor ? Minor : Major)[degree - oct * 7] + 12 * (oct + octave - 4);
            return 261.63f * Mathf.Pow(2f, semitones / 12f);   // tonacja C
        }

        // loop = true: wybrzmienia zawijają się na początek, więc klip zapętla się bez szwu.
        public static AudioClip Render(string name, IList<Note> notes, string instrument, int octave,
            bool minor, float beatSeconds, float totalBeats, bool loop, float gain = 0.22f)
        {
            float tail = TailSeconds(instrument);
            int loopSamples = Mathf.RoundToInt(totalBeats * beatSeconds * SampleRate);
            int length = loop ? loopSamples : loopSamples + Mathf.RoundToInt(tail * SampleRate);
            var buffer = new float[length];

            foreach (var note in notes)
            {
                float freq = Frequency(note.degree, octave, minor);
                float duration = note.length * beatSeconds;
                int start = Mathf.RoundToInt(note.beat * beatSeconds * SampleRate);
                int count = Mathf.RoundToInt((duration + tail) * SampleRate);
                for (int i = 0; i < count; i++)
                {
                    int index = start + i;
                    if (index >= length)
                    {
                        if (!loop) break;
                        index %= length;
                    }
                    buffer[index] += gain * Voice(instrument, freq, i / (float)SampleRate, duration);
                }
            }

            var clip = AudioClip.Create(name, length, 1, SampleRate, false);
            clip.SetData(buffer, 0);
            return clip;
        }

        static float TailSeconds(string instrument)
        {
            switch (instrument)
            {
                case "bell": return 1.4f;
                case "pluck": return 0.9f;
                case "pad": return 1.5f;
                default: return 0.15f;
            }
        }

        static float Voice(string instrument, float f, float t, float duration)
        {
            switch (instrument)
            {
                case "bell":
                {
                    float env = Mathf.Exp(-t / 0.45f) * Mathf.Min(1f, t / 0.004f);
                    return env * (Mathf.Sin(Tau * f * t) + 0.35f * Mathf.Exp(-t / 0.12f) * Mathf.Sin(Tau * f * 3f * t));
                }
                case "flute":
                {
                    float env = Mathf.Min(1f, t / 0.07f) * Release(t, duration, 0.12f);
                    float vibrato = f * 0.001f * Mathf.Sin(Tau * 5f * t);
                    return env * 0.8f * (Mathf.Sin(Tau * f * t + vibrato) + 0.15f * Mathf.Sin(Tau * f * 2f * t));
                }
                case "pluck":
                {
                    float sum = 0f;
                    for (int n = 1; n <= 5; n++)
                        sum += Mathf.Exp(-t * (2.5f + 2f * n)) * Mathf.Sin(Tau * f * n * t) / n;
                    return sum * Mathf.Min(1f, t / 0.003f);
                }
                case "bass":
                {
                    float env = Mathf.Min(1f, t / 0.02f) * Release(t, duration, 0.15f);
                    return env * (Mathf.Sin(Tau * f * t) + 0.3f * Mathf.Sin(Tau * f * 2f * t) + 0.12f * Mathf.Sin(Tau * f * 3f * t));
                }
                default:   // pad
                {
                    float env = Mathf.Min(1f, t / 1.5f) * Release(t, duration, 1.5f);
                    return env * 0.5f * (Mathf.Sin(Tau * f * t) + Mathf.Sin(Tau * f * 1.003f * t));
                }
            }
        }

        static float Release(float t, float duration, float release)
        {
            return t <= duration ? 1f : Mathf.Max(0f, 1f - (t - duration) / release);
        }
    }
}
