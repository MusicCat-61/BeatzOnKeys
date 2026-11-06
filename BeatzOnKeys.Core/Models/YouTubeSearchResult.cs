namespace BeatzOnKeys.Core.Models;

/// <summary>One row of a YouTube search; it is not a playlist entry yet.</summary>
public sealed class YouTubeSearchResult
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }

    /// <summary>True when yt-dlp reported a live stream, which cannot be cached as a file.</summary>
    public bool IsLive { get; set; }

    public string DurationText => Duration <= TimeSpan.Zero ? "—" :
        Duration.TotalHours >= 1 ? $"{(int)Duration.TotalHours}:{Duration:mm\\:ss}" : Duration.ToString(@"mm\:ss");
}