using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ogrod.Music
{
    // Układa czterotaktowe frazy z motywu rośliny. Każdy wariant to inne opracowanie tego samego
    // motywu (wprost, w odwróceniu, od końca, z nutami przejściowymi), przeniesione na kolejne
    // akordy i zagrane w rytmie właściwym dla instrumentu. Dzięki temu ogród nie powtarza
    // jednej pętli, a gracz wciąż słyszy motyw, którego się nauczył.
    public static class PhraseComposer
    {
        public const int VariantCount = 4;

        // Komórki rytmiczne: pary (początek, długość) w uderzeniach w obrębie taktu.
        // [gęstość zależna od poziomu rośliny][wersja komórki]
        static readonly float[][][] Bass =   // na "raz" i na "i" trzeciej miary
        {
            new[] { new[] { 0f, 2.5f, 2.5f, 1.5f }, new[] { 0f, 3f, 3.5f, .5f } },
            new[] { new[] { 0f, 1.5f, 1.5f, 1f, 2.5f, 1.5f }, new[] { 0f, 1.5f, 2.5f, 1f, 3.5f, .5f } },
            new[] { new[] { 0f, 1f, 1.5f, .5f, 2f, .5f, 2.5f, 1f, 3.5f, .5f }, new[] { 0f, .75f, .75f, .75f, 1.5f, 1f, 2.5f, .5f, 3f, .5f, 3.5f, .5f } },
        };
        static readonly float[][][] Pluck =  // ósemki z pauzami
        {
            new[] { new[] { 0f, .5f, 1.5f, .5f, 2f, .5f, 3.5f, .5f }, new[] { 0f, .5f, 1f, .5f, 2.5f, .5f, 3f, .5f } },
            new[] { new[] { 0f, .5f, .5f, .5f, 1.5f, .5f, 2f, .5f, 2.5f, .5f, 3.5f, .5f }, new[] { 0f, .5f, 1f, .5f, 1.5f, .5f, 2.5f, .5f, 3f, .5f, 3.5f, .5f } },
            new[] { new[] { 0f, .5f, .5f, .5f, 1f, .5f, 1.5f, .5f, 2.5f, .5f, 3f, .5f, 3.5f, .5f }, new[] { 0f, .5f, .5f, .5f, 1.5f, .5f, 2f, .5f, 2.5f, .5f, 3f, .5f, 3.5f, .5f } },
        };
        static readonly float[][][] Bell =   // rzadko, z synkopami
        {
            new[] { new[] { 0f, 1.5f, 1.5f, 1f, 2.5f, 1.5f }, new[] { .5f, 1f, 1.5f, 1.5f, 3f, 1f }, new[] { 0f, 1.5f, 1.5f, 2.5f } },
            new[] { new[] { 0f, 1f, 1.5f, .5f, 2f, .5f, 3.5f, .5f }, new[] { .5f, .5f, 1f, .5f, 2.5f, 1f, 3.5f, .5f } },
            new[] { new[] { 0f, .5f, .5f, .5f, 1.5f, .5f, 2.5f, .5f, 3f, .5f, 3.5f, .5f }, new[] { 0f, .5f, 1f, .5f, 1.5f, .5f, 2f, .5f, 3f, .5f, 3.5f, .5f } },
        };
        static readonly float[][][] Flute =  // długie nuty z ozdobnikami
        {
            new[] { new[] { 0f, 3.5f }, new[] { 0f, 2.5f, 3f, 1f } },
            new[] { new[] { 0f, 2f, 2.5f, .5f, 3f, 1f }, new[] { 0f, 1.5f, 2f, 1.5f, 3.5f, .5f } },
            new[] { new[] { 0f, 1.5f, 1.5f, .25f, 1.75f, .25f, 2f, 1.5f, 3.5f, .5f }, new[] { 0f, 1f, 1f, .5f, 1.5f, 1.5f, 3f, .25f, 3.25f, .25f, 3.5f, .5f } },
        };

        // Pula fraz rośliny na danym poziomie: warianty z motywu plus poprawne frazy z banku.
        // Fragment, który nie przejdzie walidacji, nigdy nie trafia do odtwarzania.
        public static List<List<Note>> BuildPool(MusicBankData bank, VoiceData voice, int voiceIndex, int level)
        {
            var pool = new List<List<Note>>();
            for (int variant = 0; variant < VariantCount; variant++)
            {
                var notes = Compose(bank, voice, voiceIndex, level, variant);
                if (MusicValidator.Validate(notes, bank, out string error)) pool.Add(notes);
                else Debug.LogWarning($"[Music] Odrzucono wariant {variant} głosu '{voice.id}' (poziom {level}): {error}");
            }

            if (voice.phrases != null)
            {
                foreach (var phrase in voice.phrases)
                {
                    if (phrase.level != level) continue;
                    if (MusicBank.TryParsePhrase(phrase.notes, out var notes, out string error)
                        && MusicValidator.Validate(notes, bank, out error))
                        pool.Add(notes);
                    else Debug.LogWarning($"[Music] Odrzucono frazę z banku dla głosu '{voice.id}': {error}");
                }
            }

            if (pool.Count == 0)   // ostateczność: same prymy akordów
            {
                var notes = new List<Note>();
                for (int bar = 0; bar < bank.bars; bar++)
                    notes.Add(new Note { degree = Anchor(bank.progression[bar % bank.progression.Length], 0, true), beat = bar * bank.beatsPerBar, length = 2f, velocity = 1f });
                pool.Add(notes);
            }
            return pool;
        }

        static List<Note> Compose(MusicBankData bank, VoiceData voice, int voiceIndex, int level, int variant)
        {
            int[] motif = MusicBank.ParseMotif(voice.motif);
            int[] line = Line(motif, variant);
            bool bass = voice.role == "podstawa";
            float[][] cells = Cells(voice.role)[Mathf.Clamp(level - 1, 0, 2)];
            var rng = new System.Random(voiceIndex * 101 + level * 31 + variant * 17 + 7);

            var notes = new List<Note>();
            int cursor = 0;
            for (int bar = 0; bar < bank.bars; bar++)
            {
                if (Rests(voice.role, level, variant, bar)) continue;
                int root = bank.progression[bar % bank.progression.Length];
                int anchor = Anchor(root, motif[0], bass);
                // warianty 0 i 1 zaczynają motyw od nowa w każdym takcie (sekwencja po akordach),
                // 2 i 3 ciągną linię przez kreskę taktową, więc motyw przesuwa się względem rytmu
                if (variant < 2) cursor = 0;
                float[] cell = cells[(variant + (bar == bank.bars - 1 ? 1 : 0)) % cells.Length];
                for (int i = 0; i < cell.Length; i += 2)
                {
                    int degree = Fold(anchor + line[cursor++ % line.Length]);
                    if (cell[i] == 0f) degree = SnapToChord(degree, root);
                    notes.Add(new Note
                    {
                        degree = degree,
                        beat = bar * bank.beatsPerBar + cell[i],
                        length = cell[i + 1],
                        velocity = Accent(cell[i], rng)
                    });
                }
            }
            return notes;
        }

        static float[][][] Cells(string role)
        {
            switch (role)
            {
                case "podstawa": return Bass;
                case "arpeggio": return Pluck;
                case "kontrmelodia": return Flute;
                default: return Bell;
            }
        }

        // Melodia zostawia miejsce na odpowiedź: w części wariantów milknie na jeden takt.
        static bool Rests(string role, int level, int variant, int bar)
        {
            if (level >= 3) return false;
            if (role == "melodia") return bar == 3 && variant % 2 == 1;
            if (role == "kontrmelodia") return bar == 2 && variant == 1;
            return false;
        }

        // Linia melodyczna: interwały motywu względem jego pierwszej nuty.
        static int[] Line(int[] motif, int variant)
        {
            var rel = new int[motif.Length];
            for (int i = 0; i < motif.Length; i++) rel[i] = motif[i] - motif[0];

            switch (variant % VariantCount)
            {
                case 1:   // odwrócenie interwałów
                    for (int i = 0; i < rel.Length; i++) rel[i] = -rel[i];
                    return rel;
                case 2:   // od końca
                    Array.Reverse(rel);
                    int first = rel[0];
                    for (int i = 0; i < rel.Length; i++) rel[i] -= first;
                    return rel;
                case 3:   // z nutami przejściowymi
                    var passing = new List<int> { rel[0] };
                    for (int i = 1; i < rel.Length; i++)
                    {
                        int step = rel[i] - rel[i - 1];
                        if (Math.Abs(step) >= 2) passing.Add(rel[i - 1] + step / 2);
                        passing.Add(rel[i]);
                    }
                    return passing.ToArray();
                default:
                    return rel;
            }
        }

        // Składnik akordu najbliższy rejestrowi motywu; bas trzyma się prymy.
        static int Anchor(int root, int center, bool rootOnly)
        {
            int best = root;
            int bestDistance = int.MaxValue;
            for (int tone = 0; tone <= (rootOnly ? 0 : 4); tone += 2)
                for (int octave = -1; octave <= 1; octave++)
                {
                    int candidate = root + tone + 7 * octave;
                    int distance = Math.Abs(candidate - center);
                    if (distance < bestDistance) { best = candidate; bestDistance = distance; }
                }
            return best;
        }

        static int Fold(int degree)
        {
            while (degree < -7) degree += 7;
            while (degree > 14) degree -= 7;
            return degree;
        }

        static int SnapToChord(int degree, int root)
        {
            foreach (int delta in new[] { 0, -1, 1, -2, 2 })
                if (MusicValidator.IsChordTone(degree + delta, root)) return Fold(degree + delta);
            return degree;
        }

        // Akcenty metryczne: "raz" najmocniej, potem "trzy", słabe części taktu najciszej.
        static float Accent(float beatInBar, System.Random rng)
        {
            float accent = beatInBar == 0f ? 1f : beatInBar == 2f ? 0.85f : beatInBar == Mathf.Floor(beatInBar) ? 0.75f : 0.6f;
            return Mathf.Clamp(accent + ((float)rng.NextDouble() - 0.5f) * 0.14f, 0.4f, 1f);
        }
    }
}
