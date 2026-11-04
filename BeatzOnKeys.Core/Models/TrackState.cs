namespace BeatzOnKeys.Core.Models;

/// <summary>A track entry persisted in the user settings file.</summary>
public sealed class TrackState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string SourcePath { get; set; } = string.Empty;

    public string? MidiPath { get; set; }

    public TrackStatus Status { get; set; } = TrackStatus.Pending;

    public string? ErrorCode { get; set; }

    public string? ErrorDetails { get; set; }

    public int NoteCount { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
