namespace BeatzOnKeys.Core.Models;

/// <summary>Lifecycle of a cached YouTube track, persisted so a restart resumes where it stopped.</summary>
public enum YouTubeTrackStatus
{
    /// <summary>Chosen by the user but not downloaded yet.</summary>
    Pending = 0,
    Downloading = 1,
    Ready = 2,
    Failed = 3
}

/// <summary>
/// A track pulled from YouTube. It lives in its own list: it is never mixed into the local
/// playlist, so the user's own files and the cache stay separate.
/// </summary>
public sealed class YouTubeTrackState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>Absolute path of the cached audio file; empty until the download finishes.</summary>
    public string CachedPath { get; set; } = string.Empty;

    public YouTubeTrackStatus Status { get; set; } = YouTubeTrackStatus.Pending;

    /// <summary>Last failure reason, shown in the row and cleared on a successful retry.</summary>
    public string? ErrorMessage { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}