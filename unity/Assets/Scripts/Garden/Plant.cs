using UnityEngine;
using Ogrod.Music;

namespace Ogrod
{
    // Jedna roślina = jeden instrument: wygląd zależny od poziomu oraz jej warstwa kompozycji
    // w wersji durowej i molowej.
    public class Plant : MonoBehaviour
    {
        public const int MaxLevel = 3;
        const int PetalCount = 5;
        const float FadeSeconds = 1.5f;

        public VoiceData voice;
        public int index;
        public int level;
        public bool muted;
        public int[] motif;

        Color color;
        float growth, pulse, volume;
        AudioSource major, minor;
        Transform stem, head;
        Transform[] leaves, petals, outerPetals;
        SpriteRenderer stemRenderer, glow, core;
        SpriteRenderer[] leafRenderers, petalRenderers;

        public bool Audible => level > 0 && !muted;
        public Vector3 HeadPosition => head.position;

        public void Build(VoiceData voiceData, int plantIndex, Color plantColor, Material material)
        {
            voice = voiceData;
            index = plantIndex;
            color = plantColor;
            motif = MusicBank.ParseMotif(voice.motif);

            stemRenderer = SpriteFactory.Make("Łodyga", SpriteFactory.Square, transform, Vector3.zero, Vector2.one, Palette.Stem, 1, material);
            stem = stemRenderer.transform;

            leaves = new Transform[2];
            leafRenderers = new SpriteRenderer[2];
            for (int i = 0; i < 2; i++)
            {
                leafRenderers[i] = SpriteFactory.Make("Liść", SpriteFactory.Disc, transform, Vector3.zero, Vector2.one, Palette.Leaf, 2, material);
                leaves[i] = leafRenderers[i].transform;
                leaves[i].localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 30f : -30f);
            }

            head = new GameObject("Kwiat").transform;
            head.SetParent(transform, false);
            glow = SpriteFactory.Make("Poświata", SpriteFactory.Glow, head, Vector3.zero, Vector2.one * 3.2f, color, 0, material);

            petals = new Transform[PetalCount];
            outerPetals = new Transform[PetalCount];
            petalRenderers = new SpriteRenderer[PetalCount * 2];
            for (int i = 0; i < PetalCount; i++)
            {
                float angle = i * Mathf.PI * 2f / PetalCount + Mathf.PI / 2f;
                var inner = SpriteFactory.Make("Płatek", SpriteFactory.Disc, head,
                    new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * 0.55f, Vector2.one, color, 3, material);
                float outerAngle = angle + Mathf.PI / PetalCount;
                var outer = SpriteFactory.Make("Płatek zewnętrzny", SpriteFactory.Disc, head,
                    new Vector3(Mathf.Cos(outerAngle), Mathf.Sin(outerAngle)) * 0.9f, Vector2.one, color, 2, material);
                petals[i] = inner.transform;
                outerPetals[i] = outer.transform;
                petalRenderers[i] = inner;
                petalRenderers[PetalCount + i] = outer;
            }
            core = SpriteFactory.Make("Środek", SpriteFactory.Disc, head, Vector3.zero, Vector2.one * 0.6f, Color.white, 4, material);

            major = AddSource();
            minor = AddSource();
            UpdateVisuals(0f);
        }

        AudioSource AddSource()
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.volume = 0f;
            return source;
        }

        public void Pulse(float amount)
        {
            pulse = Mathf.Max(pulse, amount);
        }

        // Renderuje warstwę dla bieżącego poziomu i włącza ją zsynchronizowaną ze wspólną pętlą.
        public void ApplyLayer(MusicBankData bank, double loopStartDsp)
        {
            if (level <= 0) return;
            var notes = MusicBank.GetLayer(bank, voice, level - 1);
            if (notes == null) return;
            Swap(major, Synth.Render(voice.id + " dur", notes, voice.instrument, voice.octave, false, bank.BeatSeconds, bank.TotalBeats, true), loopStartDsp);
            Swap(minor, Synth.Render(voice.id + " moll", notes, voice.instrument, voice.octave, true, bank.BeatSeconds, bank.TotalBeats, true), loopStartDsp);
        }

        static void Swap(AudioSource source, AudioClip clip, double loopStartDsp)
        {
            source.Stop();
            source.clip = clip;
            double at = System.Math.Max(AudioSettings.dspTime + 0.1, loopStartDsp);
            long offset = (long)((at - loopStartDsp) * Synth.SampleRate) % clip.samples;
            source.timeSamples = (int)offset;
            source.PlayScheduled(at);
        }

        // minorMix: 0 = dur, 1 = moll; gain: wspólna głośność podkładu (przyciszana w trakcie zadania)
        public void Tick(float minorMix, float gain, float focus)
        {
            float dt = Time.deltaTime;
            volume = Mathf.MoveTowards(volume, Audible ? 1f : 0f, dt / FadeSeconds);
            major.volume = volume * gain * Mathf.Cos(minorMix * Mathf.PI / 2f);
            minor.volume = volume * gain * Mathf.Sin(minorMix * Mathf.PI / 2f);

            growth = Mathf.MoveTowards(growth, level, dt * 0.8f);
            pulse = Mathf.MoveTowards(pulse, 0f, dt * 1.8f);
            UpdateVisuals(focus);
        }

        void UpdateVisuals(float focus)
        {
            float g = growth / MaxLevel;
            float height = Mathf.Lerp(0.6f, 2.7f, g);
            float awake = Mathf.Clamp01(growth);
            float alpha = muted ? 0.4f : 1f;

            transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 0.8f + index * 1.7f) * 2.5f);
            stem.localPosition = new Vector3(0f, height / 2f, 0f);
            stem.localScale = new Vector3(0.07f, height, 1f);
            stemRenderer.color = WithAlpha(Palette.Stem, alpha);

            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? 1f : -1f;
                leaves[i].localPosition = new Vector3(side * 0.24f, height * (0.35f + 0.2f * i), 0f);
                leaves[i].localScale = new Vector3(0.55f, 0.2f, 1f) * Mathf.Lerp(0.5f, 1f, g);
                leafRenderers[i].color = WithAlpha(Palette.Leaf, alpha);
            }

            head.localPosition = new Vector3(0f, height, 0f);
            head.localScale = Vector3.one * Mathf.Lerp(0.3f, 0.8f, g) * (1f + 0.25f * pulse);

            Color tint = Color.Lerp(Palette.Dormant, color, awake);
            float petalScale = 0.55f * awake;
            float outerScale = 0.42f * Mathf.Clamp01(growth - 2f);
            for (int i = 0; i < PetalCount; i++)
            {
                petals[i].localScale = Vector3.one * petalScale;
                outerPetals[i].localScale = Vector3.one * outerScale;
            }
            foreach (var petal in petalRenderers) petal.color = WithAlpha(tint, alpha);
            core.color = WithAlpha(Color.Lerp(Palette.Dormant, Color.white, 0.3f + 0.7f * awake), alpha);
            glow.color = WithAlpha(tint, alpha * (0.08f + 0.22f * awake + 0.3f * pulse + 0.15f * focus * awake));
        }

        static Color WithAlpha(Color c, float a)
        {
            c.a = a;
            return c;
        }
    }
}
