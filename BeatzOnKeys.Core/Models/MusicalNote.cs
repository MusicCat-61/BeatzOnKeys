namespace BeatzOnKeys.Core.Models;

/// <summary>A MIDI note converted to wall-clock timing for key-triggered playback.</summary>
public sealed record MusicalNote(
    int MidiNumber,
    TimeSpan Start,
    TimeSpan Duration,
    byte Velocity)
{
    private static readonly string[] NoteNames =
        ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    public double Frequency => 440d * Math.Pow(2d, (MidiNumber - 69d) / 12d);

    public string DisplayName
    {
        get
        {
            var clamped = Math.Clamp(MidiNumber, 0, 127);
            return $"{NoteNames[clamped % 12]}{(clamped / 12) - 1}";
        }
    }
}
