namespace BeatzOnKeys.Core.Models;

/// <summary>
/// User preferences. The collected tracks used to live here too; they now live in library.json,
/// and the fields stay only so an old file can still be read during the migration.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 7;
    public const double DefaultMinVolume = 0.2;
    public const double DefaultMaxVolume = 1.0;
    public const double DefaultIdleTimeout = 3.0;
    public const double DefaultBassReactiveGain = 0.15;

    /// <summary>Matches MinWidth/MinHeight of the window, so a stale tiny value cannot shrink it.</summary>
    public const double MinWindowWidth = 920;
    public const double MinWindowHeight = 650;

    /// <summary>
    /// The window size the user last chose. Zero means "never set", so the size from the XAML file
    /// stands; the value is not exposed as a setting in the window on purpose.
    /// </summary>
    public double WindowWidth { get; set; }

    /// <inheritdoc cref="WindowWidth"/>
    public double WindowHeight { get; set; }

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public double MinVolume { get; set; } = DefaultMinVolume;
    public double MaxVolume { get; set; } = DefaultMaxVolume;
    public double IdleTimeout { get; set; } = DefaultIdleTimeout;
    public bool PauseOnIdle { get; set; } = true;
    public bool AutoAdvance { get; set; }
    public bool Loop { get; set; } = true;

    /// <summary>Volume punch on a hard key press.</summary>
    public bool PunchResponseEnabled { get; set; } = true;

    /// <summary>How strongly the bass drives the volume, 0 = off.</summary>
    public double BassReactiveGain { get; set; } = DefaultBassReactiveGain;

    /// <summary>Left and right channels react independently instead of together.</summary>
    public bool StereoPulseEnabled { get; set; }

    /// <summary>Volume follows the loudness of the music itself.</summary>
    public bool AdaptiveVolumeEnabled { get; set; } = true;
    public Guid? SelectedTrackId { get; set; }
    public string? Language { get; set; }
    public string Theme { get; set; } = "dark";

    /// <summary>
    /// Read-only leftovers from before library.json. Nothing writes them any more, and once
    /// <see cref="TracksMigratedToLibrary"/> is set they are ignored on load as well.
    /// </summary>
    public List<TrackState> Tracks { get; set; } = [];

    /// <inheritdoc cref="Tracks"/>
    public List<YouTubeTrackState> YouTubeTracks { get; set; } = [];

    /// <inheritdoc cref="Tracks"/>
    public List<YouTubeTrackState> TikTokTracks { get; set; } = [];

    /// <summary>
    /// Set once the tracks have been copied into library.json. Until then the old lists are read,
    /// so an existing installation keeps its library after the update.
    /// </summary>
    public bool TracksMigratedToLibrary { get; set; }

    /// <summary>Keeps downloaded audio on disk instead of deleting it after playback.</summary>
    public bool YouTubeCacheEnabled { get; set; } = true;

    /// <summary>Folder scanned for Netscape-format cookie files, passed to yt-dlp when non-empty.</summary>
    public string CookiesFolderPath { get; set; } = string.Empty;

    /// <summary>Optional proxy for yt-dlp, for example http://127.0.0.1:8080.</summary>
    public string ProxyUrl { get; set; } = string.Empty;

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        MinVolume = Math.Clamp(double.IsFinite(MinVolume) ? MinVolume : DefaultMinVolume, 0, 0.5);
        MaxVolume = Math.Clamp(double.IsFinite(MaxVolume) ? MaxVolume : DefaultMaxVolume, 0, 1);
        IdleTimeout = Math.Clamp(double.IsFinite(IdleTimeout) ? IdleTimeout : DefaultIdleTimeout, 1, 10);
        BassReactiveGain = Math.Clamp(double.IsFinite(BassReactiveGain) ? BassReactiveGain : DefaultBassReactiveGain, 0, 0.3);
        Theme = string.Equals(Theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        Language = string.Equals(Language, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" :
            string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : null;
        Tracks ??= [];
        YouTubeTracks ??= [];
        TikTokTracks ??= [];
        NormalizeTracks(Tracks);
        // Only checked when the list is here at all: once library.json owns the tracks, an empty
        // list in settings means "they live elsewhere", not "the selection is stale".
        if (Tracks.Count > 0 && SelectedTrackId is Guid selected && !Tracks.Any(track => track.Id == selected))
        {
            SelectedTrackId = null;
        }
        CookiesFolderPath = (CookiesFolderPath ?? string.Empty).Trim();
        ProxyUrl = (ProxyUrl ?? string.Empty).Trim();
        WindowWidth = NormalizeWindowSide(WindowWidth, MinWindowWidth);
        WindowHeight = NormalizeWindowSide(WindowHeight, MinWindowHeight);
        NormalizeCachedTracks(YouTubeTracks);
        NormalizeCachedTracks(TikTokTracks);
    }

    /// <summary>
    /// Zero and garbage both mean "not set", because an unreadable value must not become a window
    /// that cannot be resized back; a real value is clamped to the minimum the window allows.
    /// </summary>
    private static double NormalizeWindowSide(double value, double minimum) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(value, minimum, 16000) : 0;

    /// <summary>One row per file, no empty paths, and a stable id for every entry.</summary>
    public static void NormalizeTracks(List<TrackState> tracks)
    {
        if (tracks is null) { return; }
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        tracks.RemoveAll(track =>
        {
            if (track is null || string.IsNullOrWhiteSpace(track.SourcePath) || !paths.Add(track.SourcePath))
            {
                return true;
            }

            if (track.Id == Guid.Empty) { track.Id = Guid.NewGuid(); }
            return !ids.Add(track.Id);
        });
    }

    /// <summary>
    /// Keeps a cached-track list consistent: one row per video, no empty identifiers, and a
    /// "Downloading" row that survived a crash becomes "Failed" instead of spinning forever.
    /// Shared by the YouTube and TikTok lists, which have the same shape.
    /// </summary>
    public static void NormalizeCachedTracks(List<YouTubeTrackState> tracks)
    {
        if (tracks is null) { return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<YouTubeTrackState>(tracks.Count);
        foreach (var track in tracks)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.VideoId)) { continue; }
            track.VideoId = track.VideoId.Trim();
            if (!seen.Add(track.VideoId)) { continue; }
            if (track.Id == Guid.Empty) { track.Id = Guid.NewGuid(); }
            track.Title = string.IsNullOrWhiteSpace(track.Title) ? track.VideoId : track.Title.Trim();
            track.CachedPath ??= string.Empty;
            // A download cannot still be running across a restart.
            if (track.Status == YouTubeTrackStatus.Downloading) { track.Status = YouTubeTrackStatus.Failed; }
            if (track.Status == YouTubeTrackStatus.Ready && !File.Exists(track.CachedPath))
            {
                track.Status = YouTubeTrackStatus.Failed;
                track.ErrorMessage = null;
                track.CachedPath = string.Empty;
            }

            kept.Add(track);
        }

        tracks.Clear();
        tracks.AddRange(kept);
    }
}