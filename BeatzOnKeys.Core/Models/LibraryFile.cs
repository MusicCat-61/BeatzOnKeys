using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Models;

/// <summary>
/// Everything the user collected, kept apart from their preferences so that a settings reset cannot
/// wipe the library and a library edit cannot corrupt the preferences.
/// </summary>
public sealed class LibraryFile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Local files the user added from disk.</summary>
    public List<TrackState> Tracks { get; set; } = [];

    public List<YouTubeTrackState> YouTubeTracks { get; set; } = [];

    /// <summary>TikTok downloads. A separate list, so neither cache button can touch the other.</summary>
    public List<YouTubeTrackState> TikTokTracks { get; set; } = [];

    /// <summary>
    /// The version whose path migration has already run. Empty means it has not, and the scan is
    /// repeated once; afterwards it stays put so startup never walks the file system again.
    /// </summary>
    public string? PathsMigratedToLibraryVersion { get; set; }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Tracks ??= [];
        YouTubeTracks ??= [];
        TikTokTracks ??= [];
        AppSettings.NormalizeTracks(Tracks);
        AppSettings.NormalizeCachedTracks(YouTubeTracks);
        AppSettings.NormalizeCachedTracks(TikTokTracks);
    }
}

/// <summary>The three lists as one value, so a caller can hand them around without the file shape.</summary>
public sealed record LibrarySnapshot(
    List<TrackState> Tracks,
    List<YouTubeTrackState> YouTubeTracks,
    List<YouTubeTrackState> TikTokTracks,
    string? PathsMigratedToLibraryVersion)
{
    public static LibrarySnapshot Empty => new([], [], [], null);
}