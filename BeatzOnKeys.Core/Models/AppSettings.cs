namespace BeatzOnKeys.Core.Models;

/// <summary>User preferences and the playlist stored in the local app-data folder.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;
    public const double DefaultStepSeconds = 0.15;
    public const double MinimumStepSeconds = 0.05;
    public const double MaximumStepSeconds = 0.5;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public double Volume { get; set; } = 0.7;
    public double StepSeconds { get; set; } = DefaultStepSeconds;
    public bool AutoAdvance { get; set; }
    public bool Loop { get; set; } = true;
    public Guid? SelectedTrackId { get; set; }
    public string? Language { get; set; }
    public string Theme { get; set; } = "dark";
    public List<TrackState> Tracks { get; set; } = [];

    public void Normalize()
    {
        // Unknown properties from an older settings file are deliberately ignored on load.
        SchemaVersion = CurrentSchemaVersion;
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : 0.7, 0, 1);
        StepSeconds = Math.Clamp(
            double.IsFinite(StepSeconds) ? StepSeconds : DefaultStepSeconds,
            MinimumStepSeconds,
            MaximumStepSeconds);
        Theme = string.Equals(Theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        Language = string.Equals(Language, "ru", StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : null;

        Tracks ??= [];
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Tracks = Tracks.Where(track =>
        {
            if (track is null || string.IsNullOrWhiteSpace(track.SourcePath) || !paths.Add(track.SourcePath))
            {
                return false;
            }

            if (track.Id == Guid.Empty)
            {
                track.Id = Guid.NewGuid();
            }

            return ids.Add(track.Id);
        }).ToList();

        if (SelectedTrackId is Guid selected && !Tracks.Any(track => track.Id == selected))
        {
            SelectedTrackId = null;
        }
    }
}
