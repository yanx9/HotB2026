using System.Collections.Generic;
using UnityEngine;
using Ogrod.Music;

namespace Ogrod
{
    // Jedna roślina = jeden instrument. Wygląd zależy od poziomu; muzyka to pula fraz
    // wyprowadzonych z motywu, grana fraza po frazie według stałej formy. Roślina podskakuje
    // na każdą własną nutę.
    public class Plant : MonoBehaviour
    {
        public const int MaxLevel = 3;
        const float FadeSeconds = 1.5f;

        // Forma: który wariant gra w kolejnych frazach (A A B A C A D E). Rośliny wchodzą
        // w formę z przesunięciem, żeby nie zmieniały wariantu wszystkie naraz.
        static readonly int[] Form = { 0, 0, 1, 0, 2, 0, 3, 4 };

        static readonly int SwayId = Shader.PropertyToID("_Sway");
        static readonly int PhaseId = Shader.PropertyToID("_Phase");
        static readonly int PulseId = Shader.PropertyToID("_Pulse");
        static readonly int SatId = Shader.PropertyToID("_Sat");
        static readonly int DitherId = Shader.PropertyToID("_Dither");

        public VoiceData voice;
        public int index;
        public int level;
        public bool muted;
        public int[] motif;

        Color color;
        PixelArt.PlantArt art;
        SpriteRenderer body, halo;
        MaterialPropertyBlock block;
        int shownLevel = -1;
        float pulse, pop, awake, volume;

        AudioSource[] major, minor;
        int slot;
        float beatSeconds;
        List<List<Note>> pool;
        PhraseClips[] clips;

        // nuty frazy, która gra teraz, i tej zaplanowanej na następną granicę
        List<Note> currentNotes, nextNotes;
        double currentStart, nextStart;
        int cursor;

        public bool Audible => level > 0 && !muted;
        public Vector3 HeadPosition => transform.position + (Vector3)art.glowCenters[Mathf.Clamp(level, 0, MaxLevel)];

        public void Build(VoiceData voiceData, int plantIndex, Color plantColor, Material material)
        {
            voice = voiceData;
            index = plantIndex;
            color = plantColor;
            motif = MusicBank.ParseMotif(voice.motif);
            art = PixelArt.Plant(plantIndex, plantColor);
            block = new MaterialPropertyBlock();

            halo = NewRenderer("Poświata", PixelArt.Halo(), material, 1);
            halo.GetPropertyBlock(block);
            block.SetFloat(DitherId, 1f);
            halo.SetPropertyBlock(block);
            body = NewRenderer("Roślina", art.levels[0], material, 2);

            major = new[] { AddSource(), AddSource() };
            minor = new[] { AddSource(), AddSource() };
            UpdateVisuals(0f);
        }

        SpriteRenderer NewRenderer(string name, Sprite sprite, Material material, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sharedMaterial = material;
            renderer.sortingOrder = order;
            return renderer;
        }

        AudioSource AddSource()
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.volume = 0f;
            return source;
        }

        public void Pulse(float amount)
        {
            pulse = Mathf.Max(pulse, amount);
        }

        // ---------- muzyka ----------

        // Po zmianie poziomu: nowa pula fraz, pierwsza (motyw wprost) wchodzi od razu,
        // w środku bieżącej frazy ogrodu.
        public void OnLevelChanged(MusicBankData bank, double phraseStart)
        {
            foreach (var source in major) source.Stop();
            foreach (var source in minor) source.Stop();
            if (clips != null)
                foreach (var c in clips) c.Dispose();
            clips = null;
            pool = null;
            currentNotes = nextNotes = null;
            if (level <= 0) return;

            beatSeconds = bank.BeatSeconds;
            pool = PhraseComposer.BuildPool(bank, voice, index, level);
            clips = new PhraseClips[pool.Count];
            for (int i = 0; i < pool.Count; i++)
                clips[i] = new PhraseClips($"{voice.id} {level}.{i}", pool[i], voice, bank, 1 + index * 100 + level * 10 + i);

            double at = System.Math.Max(AudioSettings.dspTime + 0.1, phraseStart);
            slot = 0;
            PlayOn(slot, 0, at, at - phraseStart);
            currentNotes = pool[0];
            currentStart = phraseStart;
            cursor = 0;
            while (cursor < currentNotes.Count && currentStart + currentNotes[cursor].beat * beatSeconds < at) cursor++;
        }

        // Wołane przez dyrygenta na chwilę przed granicą frazy.
        public void Schedule(int phraseIndex, double startDsp)
        {
            if (level <= 0 || pool == null) return;
            int variant = Form[(phraseIndex + index * 3) % Form.Length] % pool.Count;
            slot ^= 1;   // poprzednia fraza wybrzmiewa na drugiej parze źródeł
            PlayOn(slot, variant, startDsp, 0.0);
            nextNotes = pool[variant];
            nextStart = startDsp;
        }

        void PlayOn(int slotIndex, int variant, double at, double offsetSeconds)
        {
            clips[variant].Get(out var majorClip, out var minorClip);
            Begin(major[slotIndex], majorClip, at, offsetSeconds);
            Begin(minor[slotIndex], minorClip, at, offsetSeconds);
        }

        static void Begin(AudioSource source, AudioClip clip, double at, double offsetSeconds)
        {
            source.Stop();
            source.clip = clip;
            source.timeSamples = Mathf.Clamp((int)(offsetSeconds * Synth.SampleRate), 0, clip.samples - 1);
            source.PlayScheduled(at);
        }

        // minorMix: 0 = dur, 1 = moll; gain: wspólna głośność podkładu (przyciszana w trakcie zadania)
        public void Tick(float minorMix, float gain, float focus)
        {
            float dt = Time.deltaTime;
            volume = Mathf.MoveTowards(volume, Audible ? 1f : 0f, dt / FadeSeconds);
            float majorVolume = volume * gain * Mathf.Cos(minorMix * Mathf.PI / 2f);
            float minorVolume = volume * gain * Mathf.Sin(minorMix * Mathf.PI / 2f);
            for (int i = 0; i < 2; i++)
            {
                major[i].volume = majorVolume;
                minor[i].volume = minorVolume;
            }

            FollowNotes(gain);
            pulse = Mathf.MoveTowards(pulse, 0f, dt * 4f);
            pop = Mathf.MoveTowards(pop, 0f, dt * 1.6f);
            awake = Mathf.MoveTowards(awake, level > 0 ? 1f : 0f, dt);
            UpdateVisuals(focus);
        }

        // podskok na każdą własną nutę, w chwili, gdy ona brzmi
        void FollowNotes(float gain)
        {
            double now = AudioSettings.dspTime;
            if (nextNotes != null && now >= nextStart)
            {
                currentNotes = nextNotes;
                currentStart = nextStart;
                nextNotes = null;
                cursor = 0;
            }
            if (currentNotes == null) return;
            while (cursor < currentNotes.Count && now >= currentStart + currentNotes[cursor].beat * beatSeconds)
            {
                float velocity = currentNotes[cursor].velocity > 0f ? currentNotes[cursor].velocity : 1f;
                if (Audible) Pulse((0.4f + 0.6f * velocity) * Mathf.Lerp(0.35f, 1f, gain));
                cursor++;
            }
        }

        // ---------- wygląd ----------

        void UpdateVisuals(float focus)
        {
            if (shownLevel != level)
            {
                if (shownLevel >= 0) pop = 1f;
                shownLevel = level;
                body.sprite = art.levels[Mathf.Clamp(level, 0, MaxLevel)];
            }

            // wyrastanie: roślina wyskakuje z ziemi i sprężyście się uspokaja
            float spring = 1f - 0.5f * pop * pop * Mathf.Cos((1f - pop) * 7.85f);
            body.transform.localScale = new Vector3(1f - 0.04f * pulse, spring * (1f + 0.08f * pulse), 1f);
            body.color = new Color(1f, 1f, 1f, muted ? 0.45f : 1f);

            body.GetPropertyBlock(block);
            block.SetFloat(SwayId, art.sway * (level > 0 ? 1f : 0.5f));
            block.SetFloat(PhaseId, index * 1.3f);
            block.SetFloat(PulseId, pulse);
            block.SetFloat(SatId, Mathf.Lerp(0.25f, 1f, awake));
            block.SetFloat(DitherId, 0f);
            body.SetPropertyBlock(block);

            halo.transform.localPosition = art.glowCenters[Mathf.Clamp(level, 0, MaxLevel)];
            halo.transform.localScale = Vector3.one * (1.3f + 0.35f * level + 0.4f * pulse);
            Color glow = color;
            glow.a = awake * (muted ? 0.1f : 0.45f + 0.4f * pulse + 0.15f * focus);
            halo.color = glow;
        }
    }
}
