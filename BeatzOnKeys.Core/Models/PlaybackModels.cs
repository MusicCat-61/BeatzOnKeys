namespace BeatzOnKeys.Core.Models;

/// <summary>An in-memory track ready for key-triggered playback.</summary>
public sealed record PlaybackTrack(Guid Id, string Name, IReadOnlyList<MusicalNote> Notes);

public sealed record NotePlayedEventArgs(
    Guid TrackId,
    string TrackName,
    MusicalNote Note,
    int PlayedNoteNumber,
    int TotalNotes);

public sealed record PlaybackTrackChangedEventArgs(Guid TrackId, string TrackName, bool AutoAdvanced);

public sealed record PlaybackStep(
    Guid TrackId,
    string TrackName,
    MusicalNote Note,
    int PlayedNoteNumber,
    int TotalNotes,
    double Volume,
    double Speed);
