using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Ogrod.Music
{
    // Jedna fraza wyrenderowana w dur i w moll. Synteza liczy się w tle od razu po utworzeniu,
    // a AudioClip powstaje dopiero przy pierwszym użyciu (to musi się stać w głównym wątku).
    public class PhraseClips
    {
        readonly string name;
        Task<float[][]> task;
        AudioClip major, minor;

        public PhraseClips(string name, List<Note> notes, VoiceData voice, MusicBankData bank, int seed)
        {
            this.name = name;
            string instrument = voice.instrument;
            int octave = voice.octave;
            float beat = bank.BeatSeconds, total = bank.TotalBeats;
            task = Task.Run(() => new[]
            {
                Synth.RenderBuffer(notes, instrument, octave, false, beat, total, false, 0.22f, seed),
                Synth.RenderBuffer(notes, instrument, octave, true, beat, total, false, 0.22f, seed)
            });
        }

        public void Get(out AudioClip majorClip, out AudioClip minorClip)
        {
            if (major == null)
            {
                var buffers = task.Result;
                major = Synth.ToClip(name + " dur", buffers[0]);
                minor = Synth.ToClip(name + " moll", buffers[1]);
                task = null;
            }
            majorClip = major;
            minorClip = minor;
        }

        public void Dispose()
        {
            if (major != null) Object.Destroy(major);
            if (minor != null) Object.Destroy(minor);
            major = minor = null;
        }
    }
}
