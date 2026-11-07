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

    /// <summary>
    /// Where the manager would put the binary, whether or not it is there. Lets the UI tell
    /// "never installed" from "installed and now broken" without guessing paths.
    /// </summary>
    string? ExpectedYtDlpPath => null;

    /// <summary>True when a file sits at the expected path but is not a usable executable.</summary>
    bool YtDlpLooksBroken => false;

    /// <summary>
    /// Downloads the binary. <paramref name="force"/> replaces a healthy file too, which is what
    /// "Починить" and "Обновить" need.
    /// </summary>
    Task<string> EnsureYtDlpAsync(IProgress<double>? progress, CancellationToken cancellationToken, bool force = false);

    Task<string> EnsureFfmpegAsync(IProgress<double>? progress, CancellationToken cancellationToken);
}