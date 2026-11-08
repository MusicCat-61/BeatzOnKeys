namespace BeatzOnKeys.Core.Models;

/// <summary>User preferences and playlist, stored in the local app-data folder.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 5;
    public const double DefaultMinVolume = 0.2;
    public const double DefaultMaxVolume = 1.0;
    public const double DefaultIdleTimeout = 3.0;
    public const double DefaultBassReactiveGain = 0.15;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public double MinVolume { get; set; } = DefaultMinVolume;
    public double MaxVolume { get; set; } = DefaultMaxVolume;
    public double IdleTimeout { get; set; } = DefaultIdleTimeout;
    public bool PauseOnIdle { get; set; } = true;
    public bool AutoAdvance { get; set; }
    public bool Loop { get; set; } = true;

    /// <summary>Shortens the attack to 15 ms when the track itself just hit a transient.</summary>
    public bool PunchResponseEnabled { get; set; } = true;

    /// <summary>Extra gain added while the 40–120 Hz band is loud; zero turns the effect off.</summary>
    public double BassReactiveGain { get; set; } = DefaultBassReactiveGain;

    /// <summary>Shifts the left/right balance for 50 ms after each key press.</summary>
    public bool StereoPulseEnabled { get; set; }

    /// <summary>Lets the quiet level follow the measured loudness of the selected track.</summary>
    public bool AdaptiveVolumeEnabled { get; set; } = true;
    public Guid? SelectedTrackId { get; set; }
    public string? Language { get; set; }
    public string Theme { get; set; } = "dark";
    public List<TrackState> Tracks { get; set; } = [];

    /// <summary>
    /// Downloaded YouTube tracks. Deliberately a separate list: the local playlist stays exactly
    /// what the user added from disk, and clearing the cache never touches it.
    /// </summary>
    public List<YouTubeTrackState> YouTubeTracks { get; set; } = [];

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
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Tracks = Tracks.Where(track =>
        {
            if (track is null || string.IsNullOrWhiteSpace(track.SourcePath) || !paths.Add(track.SourcePath)) { return false; }
            if (track.Id == Guid.Empty) { track.Id = Guid.NewGuid(); }
            return ids.Add(track.Id);
        }).ToList();
        if (SelectedTrackId is Guid selected && !Tracks.Any(track => track.Id == selected)) { SelectedTrackId = null; }
        NormalizeYouTube();
    }

    /// <summary>
    /// Keeps the cached-track list consistent: one row per video, no empty identifiers, and a
    /// "Downloading" row that survived a crash becomes "Failed" instead of spinning forever.
    /// </summary>
    private void NormalizeYouTube()
    {
        YouTubeTracks ??= [];
        CookiesFolderPath = (CookiesFolderPath ?? string.Empty).Trim();
        ProxyUrl = (ProxyUrl ?? string.Empty).Trim();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<YouTubeTrackState>(YouTubeTracks.Count);
        foreach (var track in YouTubeTracks)
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

        YouTubeTracks = kept;
    }
}
