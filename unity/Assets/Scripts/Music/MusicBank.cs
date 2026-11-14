using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Ogrod.Music
{
    // Format materiału muzycznego: to samo, co ma generować LLM. Nuty zapisane jako stopnie skali
    // (0 = tonika, 7 = oktawa wyżej), więc ten sam zapis gra w dur i w moll.
    // Fraza: "stopień@początek:długość ..." (w uderzeniach, opcjonalnie "*głośność"),
    // motyw: "stopień stopień ...".

    [Serializable]
    public class PhraseData
    {
        public int level;      // poziom rośliny, na którym fraza trafia do puli
        public string notes;
    }

    [Serializable]
    public class VoiceData
    {
        public string id;
        public string label;
        public string instrument;
        public string role;
        public int octave;
        public string motif;
        public PhraseData[] phrases;   // dodatkowe frazy (pisane ręcznie albo przez LLM)
    }

    [Serializable]
    public class MusicBankData
    {
        public int bpm;
        public int beatsPerBar;
        public int bars;
        public int[] progression;   // stopień prymy akordu w każdym takcie
        public int maxNotesPerBar;
        public VoiceData[] voices;

        public float BeatSeconds => 60f / bpm;
        public float TotalBeats => bars * beatsPerBar;
    }

    public struct Note
    {
        public int degree;
        public float beat;
        public float length;
        public float velocity;   // 0 = domyślna (1)
    }

    public static class MusicBank
    {
        public const float MotifNoteBeats = 0.5f;

        public static MusicBankData Load()
        {
            var asset = Resources.Load<TextAsset>("music_bank");
            if (asset == null) { Debug.LogError("[MusicBank] Brak Resources/music_bank.json"); return null; }
            var bank = JsonUtility.FromJson<MusicBankData>(asset.text);
            if (bank == null || bank.voices == null || bank.voices.Length == 0 || bank.bpm <= 0)
            {
                Debug.LogError("[MusicBank] music_bank.json jest pusty lub uszkodzony");
                return null;
            }
            return bank;
        }

        public static int[] ParseMotif(string motif)
        {
            var parts = motif.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var degrees = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) degrees[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
            return degrees;
        }

        public static List<Note> MotifNotes(int[] degrees)
        {
            var notes = new List<Note>(degrees.Length);
            for (int i = 0; i < degrees.Length; i++)
                notes.Add(new Note { degree = degrees[i], beat = i * MotifNoteBeats, length = MotifNoteBeats, velocity = 1f });
            return notes;
        }

        public static bool TryParsePhrase(string text, out List<Note> notes, out string error)
        {
            notes = new List<Note>();
            error = null;
            foreach (var token in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string body = token;
                float velocity = 1f;
                int star = token.IndexOf('*');
                if (star > 0)
                {
                    body = token.Substring(0, star);
                    if (!float.TryParse(token.Substring(star + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out velocity))
                    { error = $"nieczytelna głośność w '{token}'"; return false; }
                }
                int at = body.IndexOf('@'), colon = body.IndexOf(':');
                if (at <= 0 || colon <= at + 1
                    || !int.TryParse(body.Substring(0, at), NumberStyles.Integer, CultureInfo.InvariantCulture, out int degree)
                    || !float.TryParse(body.Substring(at + 1, colon - at - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float beat)
                    || !float.TryParse(body.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float length))
                {
                    error = $"nieczytelna nuta '{token}'";
                    return false;
                }
                notes.Add(new Note { degree = degree, beat = beat, length = length, velocity = velocity });
            }
            notes.Sort((a, b) => a.beat.CompareTo(b.beat));
            return true;
        }
    }

    public static class MusicValidator
    {
        // Wspólne ograniczenia z założeń: stała długość frazy, ograniczona gęstość,
        // zgodność z planem harmonicznym (na "raz" każdego taktu gra składnik akordu).
        public static bool Validate(List<Note> notes, MusicBankData bank, out string error)
        {
            error = null;
            if (notes.Count == 0) { error = "pusta fraza"; return false; }

            var perBar = new int[bank.bars];
            foreach (var n in notes)
            {
                if (n.length <= 0f || n.beat < 0f || n.beat + n.length > bank.TotalBeats + 0.001f)
                { error = $"nuta poza frazą (początek {n.beat}, długość {n.length})"; return false; }
                if (n.degree < -7 || n.degree > 14)
                { error = $"stopień {n.degree} poza zakresem"; return false; }
                if (n.velocity < 0f || n.velocity > 1.2f)
                { error = $"głośność {n.velocity} poza zakresem"; return false; }

                int bar = Mathf.Min((int)(n.beat / bank.beatsPerBar), bank.bars - 1);
                if (++perBar[bar] > bank.maxNotesPerBar)
                { error = $"za gęsto w takcie {bar + 1}"; return false; }

                bool downbeat = Mathf.Abs(n.beat - bar * bank.beatsPerBar) < 0.001f;
                if (downbeat && bank.progression != null && bar < bank.progression.Length && !IsChordTone(n.degree, bank.progression[bar]))
                { error = $"takt {bar + 1}: stopień {n.degree} nie należy do akordu"; return false; }
            }
            return true;
        }

        public static bool IsChordTone(int degree, int root)
        {
            int interval = (((degree - root) % 7) + 7) % 7;
            return interval == 0 || interval == 2 || interval == 4;
        }
    }
}
