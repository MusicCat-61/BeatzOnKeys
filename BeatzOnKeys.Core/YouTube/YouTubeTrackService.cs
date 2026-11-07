using System.Globalization;
using System.Text.Json;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.YouTube;

/// <summary>Everything the service needs that the user can change while the app runs.</summary>
public sealed class YouTubeOptions
{
    public string MusicDirectory { get; init; } = AppPaths.MusicDirectory;
    public string DownloadDirectory { get; init; } = AppPaths.DownloadDirectory;
    public string? CookiesFolder { get; init; }
    public string? ProxyUrl { get; init; }
    public bool CacheEnabled { get; init; } = true;
    public int MaxConcurrentDownloads { get; init; } = YouTubeTrackService.MaxConcurrentDownloads;
    public TimeSpan DownloadTimeout { get; init; } = YouTubeTrackService.DownloadTimeout;
    public TimeSpan SearchTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public IReadOnlyList<string> Mirrors { get; init; } = InvidiousMirrors.Default;
}

/// <summary>
/// Mirrors the Invidious fallback of yt-dlp-simple: when YouTube answers with a bot check or a rate
/// limit, the same request is repeated against a public mirror before giving up.
/// </summary>
public static class InvidiousMirrors
{
    public static readonly IReadOnlyList<string> Default = ["yewtu.be", "invidious.nerdvpn.de", "inv.nadeko.net"];

    public static string WatchUrl(string instance, string videoId) => $"https://{instance}/watch?v={videoId}";
}

/// <summary>
/// Searches and downloads YouTube audio through yt-dlp.exe. Downloads are capped at two at a time so
/// a burst of clicks cannot look like abuse, and every invocation has a hard time limit.
/// </summary>
public sealed partial class YouTubeTrackService
{
    public const int MaxConcurrentDownloads = 2;
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    internal const string ProgressPrefix = "[bok-progress]";
    internal const string ProgressTemplate = "[bok-progress]%(progress)j";

    // yt-dlp words its bot check differently between releases, so the check is on fragments.
    private static readonly string[] RestrictedMarkers =
    [
        "Sign in to confirm", "confirm you", "HTTP Error 429", "HTTP Error 403",
        "Too Many Requests", "This content isn't available", "not a bot"
    ];

    private readonly IYouTubeProcessRunner _runner;
    private readonly IYtDlpBinaryProvider _binaries;
    private readonly SemaphoreSlim _slots;
    private readonly object _optionsGate = new();
    private YouTubeOptions _options;

    public YouTubeTrackService(
        IYtDlpBinaryProvider binaries,
        IYouTubeProcessRunner runner,
        YouTubeOptions? options = null)
    {
        _binaries = binaries;
        _runner = runner;
        _options = options ?? new YouTubeOptions();
        _slots = new SemaphoreSlim(_options.MaxConcurrentDownloads, _options.MaxConcurrentDownloads);
    }

    public YouTubeOptions Options
    {
        get { lock (_optionsGate) { return _options; } }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_optionsGate) { _options = value; }
        }
    }

    /// <summary>Where yt-dlp was found, or null when it still has to be downloaded.</summary>
    public string? InstalledYtDlp => _binaries.FindYtDlp();

    /// <summary>
    /// The version the installed binary reports. Used to tell "installed and current" from
    /// "installed but old", so the card does not offer an update that does not exist.
    /// </summary>
    public async Task<string?> ReadYtDlpVersionAsync(CancellationToken cancellationToken = default)
    {
        var executable = _binaries.FindYtDlp();
        if (executable is null) { return null; }
        try
        {
            var result = await _runner.RunAsync(new YouTubeProcessRequest
            {
                Executable = executable,
                Arguments = ["--version"],
                Timeout = TimeSpan.FromSeconds(20)
            }, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded) { return null; }
            var version = result.StandardOutput.Trim();
            return version.Length is > 0 and < 40 ? version : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            AppLogger.Warning("youtube", $"Could not read the yt-dlp version: {exception.Message}");
            return null;
        }
    }

    public string? InstalledFfmpeg => _binaries.FindFfmpeg();

    /// <summary>True when the binary exists but cannot run: the card offers a repair then.</summary>
    public bool YtDlpLooksBroken => _binaries.YtDlpLooksBroken;
    /// <summary>Slots still free; the UI shows this and the tests assert the cap.</summary>
    public int FreeSlots => _slots.CurrentCount;

    public async Task<IReadOnlyList<YouTubeSearchResult>> SearchAsync(
        string query,
        int count = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) { return []; }
        count = Math.Clamp(count, 1, 50);
        var executable = RequireYtDlp();
        var options = Options;

        var arguments = BaseArguments(options);
        arguments.AddRange([
            "--flat-playlist", "--dump-json", "--no-progress",
            "--playlist-end", count.ToString(CultureInfo.InvariantCulture),
            $"ytsearch{count}:{query.Trim()}"
        ]);

        var result = await _runner.RunAsync(new YouTubeProcessRequest
        {
            Executable = executable,
            Arguments = arguments,
            Timeout = options.SearchTimeout
        }, cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new YouTubeException(YouTubeFailure.Timeout, "The search took too long and was stopped.");
        }

        var found = new List<YouTubeSearchResult>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries))
        {
            var parsed = ParseSearchLine(line);
            if (parsed is not null && found.All(item => item.VideoId != parsed.VideoId)) { found.Add(parsed); }
        }

        if (found.Count == 0 && IsRestricted(result))
        {
            throw new YouTubeException(YouTubeFailure.Restricted, Tail(result.StandardError, result.StandardOutput));
        }

        return found;
    }

    /// <summary>Installs yt-dlp on demand; the first search or download is what triggers it.</summary>
    public async Task InstallYtDlpAsync(IProgress<double>? progress, CancellationToken cancellationToken = default, bool force = false)
    {
        try
        {
            await _binaries.EnsureYtDlpAsync(progress, cancellationToken, force).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not YouTubeException)
        {
            throw new YouTubeException(YouTubeFailure.YtDlpMissing, exception.Message, exception);
        }
    }

    /// <summary>Search that fetches the binary first, because the very first click has none.</summary>
    public async Task<IReadOnlyList<YouTubeSearchResult>> SearchOrInstallAsync(
        string query,
        int count = 10,
        IProgress<double>? installProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (_binaries.FindYtDlp() is null)
        {
            await InstallYtDlpAsync(installProgress, cancellationToken).ConfigureAwait(false);
        }

        return await SearchAsync(query, count, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every file in the cache folder, so the UI can tell kept tracks from previews.</summary>
    public IReadOnlyList<string> ListCachedFiles()
    {
        var directory = Options.MusicDirectory;
        if (!Directory.Exists(directory)) { return []; }
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning("youtube", $"Could not list the cache: {exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// Deletes exactly the given files. Used to clear the preview cache without touching the
    /// tracks the user chose to keep, which is why a blanket folder wipe would be wrong here.
    /// </summary>
    public Task DeleteFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var targets = paths.ToList();
        return Task.Run(() =>
        {
            foreach (var path in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { if (File.Exists(path)) { File.Delete(path); } }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    AppLogger.Warning("youtube", $"Could not delete {path}: {exception.Message}");
                }
            }
        }, cancellationToken);
    }
    /// <summary>Bytes currently used by the cache folder; the UI formats this into KB, MB or GB.</summary>
    public Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Options.MusicDirectory;
        return Task.Run(() => MeasureDirectory(directory, cancellationToken), cancellationToken);
    }

    internal static long MeasureDirectory(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) { return 0L; }
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { total += new FileInfo(file).Length; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return total;
    }

    /// <summary>Deletes every cached audio file. The local playlist lives elsewhere and is untouched.</summary>
    public Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        var directory = Options.MusicDirectory;
        return Task.Run(() =>
        {
            if (!Directory.Exists(directory)) { return; }
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Delete(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    AppLogger.Warning("youtube", $"Could not delete {file}: {exception.Message}");
                }
            }
        }, cancellationToken);
    }

    // ---- shared helpers --------------------------------------------------------------------

    internal string RequireYtDlp() => _binaries.FindYtDlp() ??
        throw new YouTubeException(YouTubeFailure.YtDlpMissing,
            "yt-dlp.exe is not installed yet; download it from the YouTube settings.");

    private List<string> BaseArguments(YouTubeOptions options)
    {
        var arguments = new List<string> { "--no-playlist", "--no-check-certificates" };
        if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            arguments.Add("--proxy");
            arguments.Add(options.ProxyUrl);
        }

        var cookies = FindCookiesFile(options.CookiesFolder);
        if (cookies is not null)
        {
            arguments.Add("--cookies");
            arguments.Add(cookies);
        }

        return arguments;
    }

    /// <summary>Only a real Netscape cookie jar is passed on; anything else would make yt-dlp fail.</summary>
    internal static string? FindCookiesFile(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { return null; }
        foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
        {
            try
            {
                using var reader = new StreamReader(file);
                var head = new char[256];
                var read = reader.Read(head, 0, head.Length);
                if (new string(head, 0, read).Contains("Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return null;
    }

    internal static YouTubeSearchResult? ParseSearchLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith('{')) { return null; }
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (!YouTubeUrls.IsValidVideoId(id)) { return null; }
            var videoId = id!;
            var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? videoId : videoId;
            var author = root.TryGetProperty("uploader", out var uploader) ? uploader.GetString() :
                root.TryGetProperty("channel", out var channel) ? channel.GetString() : null;
            var seconds = root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
                ? duration.GetDouble()
                : 0d;
            var live = (root.TryGetProperty("is_live", out var liveElement) && liveElement.ValueKind == JsonValueKind.True) ||
                       (root.TryGetProperty("live_status", out var statusElement) &&
                        string.Equals(statusElement.GetString(), "is_live", StringComparison.OrdinalIgnoreCase));
            return new YouTubeSearchResult
            {
                VideoId = videoId,
                Title = title,
                Author = author ?? string.Empty,
                Duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero,
                IsLive = live
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool IsRestricted(YouTubeProcessResult result)
    {
        var text = result.StandardError + "\n" + result.StandardOutput;
        foreach (var marker in RestrictedMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) { return true; }
        }

        return false;
    }

    internal static string Tail(string stderr, string stdout)
    {
        var text = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        text = text.Trim();
        return text.Length <= 500 ? text : text[^500..];
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning("youtube", $"Could not clean up {path}: {exception.Message}");
        }
    }
}