namespace BeatzOnKeys.Core.YouTube;

/// <summary>
/// Supplies the external executables. Nothing is downloaded at startup: <see cref="Ensure*"/> runs
/// only when the user first searches or downloads, so a fresh install stays offline until asked.
/// </summary>
public interface IYtDlpBinaryProvider
{
    /// <summary>Path of an already installed yt-dlp, or null when it still has to be fetched.</summary>
    string? FindYtDlp();

    /// <summary>Path of an already installed ffmpeg, or null when it is not needed yet.</summary>
    string? FindFfmpeg();

    Task<string> EnsureYtDlpAsync(IProgress<double>? progress, CancellationToken cancellationToken);

    Task<string> EnsureFfmpegAsync(IProgress<double>? progress, CancellationToken cancellationToken);
}