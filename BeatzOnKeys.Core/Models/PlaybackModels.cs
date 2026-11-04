namespace BeatzOnKeys.Core.Models;

public sealed record PlaybackTrack(Guid Id, string Name, string SourcePath, TimeSpan Duration);
public sealed record PlaybackTrackChangedEventArgs(Guid TrackId, string TrackName, bool AutoAdvanced);
public sealed record PlaybackEndedEventArgs(Guid? TrackId, Guid PlaybackId);
public sealed record PlaybackFailureEventArgs(Guid? TrackId, Exception Exception)
{
    public Guid PlaybackId { get; init; }
    public long SessionId { get; init; }
}
