namespace BeatzOnKeys.Core.Models;

/// <summary>A playlist entry; the original audio file is never copied or modified.</summary>
public sealed class TrackState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourcePath { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
