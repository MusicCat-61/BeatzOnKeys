using System.Net.Http;
using System.Security.Cryptography;

using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.YouTube;

/// <summary>
/// Downloads and validates the two third-party executables. They live in the per-user bin folder
/// because the install directory may be read-only, and they are always written through a temporary
/// file so a broken download can never replace a working one.
/// </summary>
public sealed class YtDlpBinaryManager : IYtDlpBinaryProvider, IDisposable
{
    public const string YtDlpFileName = "yt-dlp.exe";
    public const string FfmpegFileName = "ffmpeg.exe";
    public const string YtDlpDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";


    /// <summary>Anything smaller than this cannot be a real build; it is an error page or a stub.</summary>
    private const long MinimumExecutableBytes = 1L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _binDirectory;
    private readonly string _appDirectory;
    private readonly string? _pathVariable;
    private bool _disposed;

    public YtDlpBinaryManager(
        HttpClient? httpClient = null,
        string? binDirectory = null,
        string? appDirectory = null,
        string? pathVariable = null)
    {
        _binDirectory = binDirectory ?? AppPaths.BinDirectory;
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
        _pathVariable = pathVariable ?? Environment.GetEnvironmentVariable("PATH");
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public string? FindYtDlp() => Find(YtDlpFileName);
    public string? FindFfmpeg() => Find(FfmpegFileName);

    /// <summary>The expected location, so the UI can offer a repair when the file went bad.</summary>
    public string? ExpectedYtDlpPath => YtDlpPath;

    /// <summary>A file exists but is not a healthy executable: truncated, empty or not a PE image.</summary>
    public bool YtDlpLooksBroken =>
        File.Exists(YtDlpPath) && !LooksLikeExecutable(YtDlpPath, out _);

    /// <summary>The well-known target path, whether or not the file exists yet.</summary>
    public string YtDlpPath => Path.Combine(_binDirectory, YtDlpFileName);

    public string FfmpegPath => Path.Combine(_binDirectory, FfmpegFileName);

    public string? Find(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) { return null; }
        foreach (var candidate in new[]
                 {
                     Path.Combine(_binDirectory, fileName),
                     Path.Combine(_appDirectory, fileName)
                 })
        {
            if (File.Exists(candidate)) { return candidate; }
        }

        if (string.IsNullOrWhiteSpace(_pathVariable)) { return null; }
        foreach (var folder in _pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate)) { return candidate; }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry must not stop the search.
            }
        }

        return null;
    }

    /// <summary>
    /// The newest published yt-dlp release. Returns null when the answer is unknown, so the card
    /// hides the update button rather than offering an update it cannot confirm.
    /// </summary>
    public static async Task<string?> FetchLatestReleaseAsync(
        HttpClient client, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return null; }
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseLatestTag(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            AppLogger.Warning("youtube", $"Could not read the latest yt-dlp release: {exception.Message}");
            return null;
        }
    }

    /// <summary>Reads tag_name out of the GitHub release payload; anything else means "unknown".</summary>
    public static string? ParseLatestTag(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tag_name", out var tag)) { return null; }
            var value = tag.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// yt-dlp versions are dates ("2025.01.15"), so they compare numerically part by part.
    /// A nightly suffix is ignored: the date alone decides.
    /// </summary>
    public static bool IsNewerRelease(string? latest, string? current)
    {
        if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current)) { return false; }
        var latestParts = NumericParts(latest);
        var currentParts = NumericParts(current);
        if (latestParts.Count == 0 || currentParts.Count == 0) { return false; }
        for (var index = 0; index < Math.Max(latestParts.Count, currentParts.Count); index++)
        {
            var left = index < latestParts.Count ? latestParts[index] : 0;
            var right = index < currentParts.Count ? currentParts[index] : 0;
            if (left != right) { return left > right; }
        }

        return false;
    }

    private static List<int> NumericParts(string value)
    {
        var parts = new List<int>();
        foreach (var chunk in value.Split(['.', '-', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = new string(chunk.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) { continue; }
            parts.Add(int.TryParse(digits, out var number) ? number : 0);
        }

        return parts;
    }

    public static string LatestReleaseApi => "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    public Task<string> EnsureYtDlpAsync(
        IProgress<double>? progress, CancellationToken cancellationToken, bool force = false) =>
        EnsureAsync(YtDlpFileName, YtDlpDownloadUrl, progress, cancellationToken, force);

    /// <summary>
    /// ffmpeg is optional and never downloaded. The only tiny build that performs the WAV
    /// conversion is compiled with --enable-nonfree, which makes the binary undistributable, and
    /// the properly licensed monolithic builds are 86–167 MB. The player works without it, so the
    /// user drops ffmpeg.exe next to yt-dlp.exe when their Windows lacks the M4A decoder.
    /// </summary>
    public Task<string> EnsureFfmpegAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var existing = FindFfmpeg();
        return existing is not null
            ? Task.FromResult(existing)
            : Task.FromException<string>(new YouTubeException(YouTubeFailure.FfmpegMissing,
                $"ffmpeg.exe is required for the WAV fallback. Place it in {_binDirectory} or on PATH."));
    }

    private async Task<string> EnsureAsync(
        string fileName,
        string url,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        bool force = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // A healthy file is reused; a repair or an update replaces it on purpose.
        if (!force)
        {
            var existing = Find(fileName);
            if (existing is not null) { return existing; }
        }

        AppPaths.EnsureYouTubeCreated();
        var target = Path.Combine(_binDirectory, fileName);
        var partial = target + ".partial";
        try
        {
            await DownloadAsync(url, partial, progress, cancellationToken).ConfigureAwait(false);
            if (!LooksLikeExecutable(partial, out var reason))
            {
                throw new YouTubeException(YouTubeFailure.DownloadFailed,
                    $"The downloaded {fileName} is not a Windows executable ({reason}).");
            }

            File.Move(partial, target, overwrite: true);
            var hash = ComputeSha256(target);
            // The path and the hash go to the log: that is what an antivirus report will ask for.
            AppLogger.Info("youtube", $"Installed {fileName} at {target} (sha256 {hash}, {new FileInfo(target).Length} bytes).");
            progress?.Report(1d);
            return target;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        AppLogger.Info("youtube", $"Downloading {url}.");
        using var response = await _httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        long received = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (total > 0) { progress?.Report((double)received / total); }
        }
    }

    /// <summary>A download is accepted only when the magic bytes say "Windows executable".</summary>
    internal static bool LooksLikeExecutable(string path, out string reason)
    {
        reason = string.Empty;
        if (!File.Exists(path))
        {
            reason = "the file is missing";
            return false;
        }

        var length = new FileInfo(path).Length;
        if (length < MinimumExecutableBytes)
        {
            reason = $"only {length} bytes, which is too small to be a build";
            return false;
        }

        using var stream = File.OpenRead(path);
        var header = new byte[512];
        var read = stream.Read(header, 0, header.Length);
        if (read < 64 || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            reason = "the file does not start with MZ";
            return false;
        }

        var peOffset = BitConverter.ToInt32(header, 0x3C);
        if (peOffset <= 0 || peOffset + 4 > read ||
            header[peOffset] != (byte)'P' || header[peOffset + 1] != (byte)'E' ||
            header[peOffset + 2] != 0 || header[peOffset + 3] != 0)
        {
            reason = "the PE header is missing";
            return false;
        }

        return true;
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning("youtube", $"Could not remove {path}: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _httpClient.Dispose();
    }
}