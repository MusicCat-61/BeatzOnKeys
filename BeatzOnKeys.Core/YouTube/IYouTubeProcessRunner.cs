namespace BeatzOnKeys.Core.YouTube;

/// <summary>One yt-dlp invocation; the callbacks receive complete lines as they arrive.</summary>
public sealed record YouTubeProcessRequest
{
    public required string Executable { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public Action<string>? OnStandardOutput { get; init; }
    public Action<string>? OnStandardError { get; init; }
}

public sealed record YouTubeProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// The seam between the service and a real process. Tests substitute this, so the download logic,
/// the timeout and the concurrency limit are all exercised without network or binaries.
/// </summary>
public interface IYouTubeProcessRunner
{
    Task<YouTubeProcessResult> RunAsync(YouTubeProcessRequest request, CancellationToken cancellationToken = default);
}