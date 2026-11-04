namespace BeatzOnKeys.Core.Models;

/// <summary>Audio metadata for a playable entry in the playlist.</summary>
public sealed record PlaybackTrack(Guid Id, string Name, string SourcePath, TimeSpan Duration);

public sealed record PlaybackTrackChangedEventArgs(Guid TrackId, string TrackName, bool AutoAdvanced);

public sealed record PlaybackPositionEventArgs(
    Guid? TrackId,
    string TrackName,
    double PositionSeconds,
    double DurationSeconds,
    bool ReachedEnd = false);

/// <summary>One key press: seek to PositionSeconds and play at most FragmentSeconds.</summary>
public sealed record PlaybackStep(
    PlaybackTrack Track,
    double PositionSeconds,
    double FragmentSeconds,
    double Volume);

public sealed record PlaybackFailureEventArgs(Guid? TrackId, Exception Exception);
