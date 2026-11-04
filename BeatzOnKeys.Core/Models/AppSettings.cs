namespace BeatzOnKeys.Core.Models;

/// <summary>All user settings stored under the current user's local app-data folder.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public double Volume { get; set; } = 0.7;

    public double PlaybackSpeed { get; set; } = 1.0;

    public bool AutoAdvance { get; set; }

    public Guid? SelectedTrackId { get; set; }

    public string? Language { get; set; }

    public string Theme { get; set; } = "dark";

    public List<TrackState> Tracks { get; set; } = [];

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : 0.7, 0, 1);
        PlaybackSpeed = Math.Clamp(double.IsFinite(PlaybackSpeed) ? PlaybackSpeed : 1, 0.5, 2);
        Theme = string.Equals(Theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        Language = string.Equals(Language, "ru", StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : null;
        Tracks ??= [];
        Tracks = Tracks
            .Where(track => track is not null && !string.IsNullOrWhiteSpace(track.SourcePath))
            .GroupBy(track => track.Id)
            .Select(group => group.First())
            .ToList();
    }
}
