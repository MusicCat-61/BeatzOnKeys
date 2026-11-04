using BeatzOnKeys.Core.Models;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace BeatzOnKeys.Core.Services;

public sealed class MidiNoteParser
{
    public IReadOnlyList<MusicalNote> Parse(string midiPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(midiPath);
        if (!File.Exists(midiPath))
        {
            throw new FileNotFoundException("MIDI file was not found.", midiPath);
        }

        var midiFile = MidiFile.Read(midiPath);
        var tempoMap = midiFile.GetTempoMap();
        var result = new List<MusicalNote>();

        foreach (var note in midiFile.GetNotes().OrderBy(note => note.Time).ThenBy(note => (int)note.NoteNumber))
        {
            var start = note.TimeAs<MetricTimeSpan>(tempoMap);
            var length = note.LengthAs<MetricTimeSpan>(tempoMap);
            var durationMilliseconds = Math.Max(1d, length.TotalMilliseconds);

            result.Add(new MusicalNote(
                (int)note.NoteNumber,
                TimeSpan.FromMilliseconds(start.TotalMilliseconds),
                TimeSpan.FromMilliseconds(durationMilliseconds),
                (byte)note.Velocity));
        }

        return result;
    }
}
