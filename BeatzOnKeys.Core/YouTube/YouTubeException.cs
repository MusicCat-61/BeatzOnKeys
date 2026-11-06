namespace BeatzOnKeys.Core.YouTube;

/// <summary>Classified reasons a YouTube action failed; the UI turns these into localized text.</summary>
public enum YouTubeFailure
{
    Unknown = 0,
    YtDlpMissing = 1,
    Timeout = 2,
    Cancelled = 3,
    Restricted = 4,
    NoResult = 5,
    Playlist = 6,
    LiveStream = 7,
    DownloadFailed = 8,
    UnsupportedAudio = 9,
    FfmpegMissing = 10
}

/// <summary>
/// Carries the classification, not a finished sentence: Core has no access to the UI dictionary,
/// so the view model decides the wording for each failure.
/// </summary>
public sealed class YouTubeException(YouTubeFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public YouTubeFailure Failure { get; } = failure;
}