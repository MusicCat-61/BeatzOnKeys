using System.Net;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Services;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace BeatzOnKeys.Core.YouTube;

/// <summary>Why a TikTok link could not become a file; each maps to one human sentence in the UI.</summary>
public enum TikTokFailure
{
    NotTikTokUrl = 0,
    VideoNotFound = 1,
    Restricted = 2,
    NoAudio = 3,
    UnsupportedAudio = 4,
    DownloadFailed = 5,
    Timeout = 6,
    Cancelled = 7,
    TooLarge = 8
}

/// <summary>Carries the reason so the view model can translate it instead of guessing from text.</summary>
public sealed class TikTokException(TikTokFailure failure, string message) : Exception(message)
{
    public TikTokFailure Failure { get; } = failure;
}

/// <summary>What the caller needs to add a row: the id, a display title and the file on disk.</summary>
public sealed record TikTokAudioResult(string VideoId, string Title, string CachedPath);

/// <summary>Everything that can be decided before the first request.</summary>
public sealed class TikTokAudioOptions
{
    /// <summary>TikTok cache lives apart from the YouTube one so neither clear button touches it.</summary>
    public string MusicDirectory { get; init; } = Path.Combine(AppPaths.MusicDirectory, "TikTok");

    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A cap on a single file, so a mistake cannot fill the disk.</summary>
    public long MaximumBytes { get; init; } = 50L * 1024 * 1024;
}

public interface ITikTokAudioService
{
    Task<TikTokAudioResult> DownloadAsync(
        string? mediaId,
        string originalUrl,
        TikTokAudioOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Turns a TikTok link into a playable file over plain HTTP. The public page is tried before the
/// signed API because the page has to be readable by link-preview crawlers; when every HTTP path
/// fails, the yt-dlp build that YouTube already uses is asked as a last resort.
/// </summary>
public sealed class TikTokAudioService : ITikTokAudioService
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/119.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly IYtDlpBinaryProvider _binaries;
    private readonly IYouTubeProcessRunner _runner;

    public TikTokAudioService(
        IYtDlpBinaryProvider binaries,
        IYouTubeProcessRunner runner,
        HttpMessageHandler? handler = null)
    {
        _binaries = binaries;
        _runner = runner;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = true }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.tiktok.com/");
    }

    public async Task<TikTokAudioResult> DownloadAsync(
        string? mediaId,
        string originalUrl,
        TikTokAudioOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.DownloadTimeout);
        var token = timeout.Token;
        try
        {
            return await RunAsync(mediaId, originalUrl, options, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new TikTokException(TikTokFailure.Cancelled, "The download was cancelled.");
        }
        catch (OperationCanceledException)
        {
            throw new TikTokException(TikTokFailure.Timeout, $"TikTok did not answer within {options.DownloadTimeout.TotalMinutes:0} minutes.");
        }
    }

    private async Task<TikTokAudioResult> RunAsync(
        string? mediaId, string originalUrl, TikTokAudioOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var id = MediaUrl.IsValidId(mediaId, MediaPlatform.TikTok)
            ? mediaId!
            : await ResolveIdAsync(originalUrl, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(options.MusicDirectory);
        progress?.Report(0.05);

        var media = await ReadMediaAsync(id, originalUrl, cancellationToken).ConfigureAwait(false);
        if (media is null)
        {
            // Every HTTP path came back empty: the signed API and the public page both refused.
            return await DownloadWithYtDlpAsync(id, originalUrl, options, progress, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(0.15);
        var title = Trim(media.Title, 100) ?? id;
        var cached = media.AudioUrl is not null
            ? await SaveAudioAsync(id, media.AudioUrl, options, progress, cancellationToken).ConfigureAwait(false)
            : await SaveVideoAsWavAsync(id, media.VideoUrl!, options, progress, cancellationToken).ConfigureAwait(false);

        if (!AudioTrackLoader.TryLoad(cached, out _, out _))
        {
            // A container we cannot decode is not worth keeping on disk.
            TryDelete(cached);
            throw new TikTokException(TikTokFailure.UnsupportedAudio,
                "TikTok returned a format this machine cannot decode.");
        }

        progress?.Report(1d);
        return new TikTokAudioResult(id, title, cached);
    }

    /// <summary>
    /// Turns vm./vt. short links into an id. HEAD is tried first because it costs one packet; some
    /// TikTok edges refuse it, so a GET with headers-only reading is the fallback.
    /// </summary>
    private async Task<string> ResolveIdAsync(string originalUrl, CancellationToken cancellationToken)
    {
        var url = originalUrl.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { url = "https://" + url; }
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (TryReadId(response.RequestMessage?.RequestUri, out var id)) { return id; }
        }
        catch (HttpRequestException) { /* HEAD refused; the GET below is the real attempt. */ }

        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        using var full = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (TryReadId(full.RequestMessage?.RequestUri, out var resolved)) { return resolved; }
        throw new TikTokException(TikTokFailure.VideoNotFound, "The TikTok link did not lead to a video.");
    }

    private static bool TryReadId(Uri? uri, out string id)
    {
        id = string.Empty;
        if (uri is null) { return false; }
        var match = System.Text.RegularExpressions.Regex.Match(uri.AbsoluteUri, @"(?:video|v|embed(?:/v2)?)/(?<id>\d{15,25})");
        if (!match.Success) { return false; }
        id = match.Groups["id"].Value;
        return true;
    }

    internal sealed record TikTokMedia(string? AudioUrl, string? VideoUrl, string? Title);

    /// <summary>The three HTTP paths in order of reliability, each returning the same shape.</summary>
    private async Task<TikTokMedia?> ReadMediaAsync(string id, string originalUrl, CancellationToken cancellationToken)
    {
        var api = await TryReadJsonAsync(
            $"https://www.tiktok.com/api/item/detail/?aid=1988&itemId={id}", cancellationToken).ConfigureAwait(false);
        if (ParseMedia(api) is { } fromApi) { return fromApi; }

        var page = await TryReadTextAsync(
            $"https://www.tiktok.com/@i/video/{id}", cancellationToken).ConfigureAwait(false);
        if (ParsePage(page) is { } fromPage) { return fromPage; }

        // Only the title is recoverable here, so the caller still gets a sensible row name when the
        // media addresses are behind a wall.
        var oembed = await TryReadJsonAsync(
            $"https://www.tiktok.com/oembed?url={Uri.EscapeDataString(originalUrl)}", cancellationToken).ConfigureAwait(false);
        if (oembed is { } document &&
            document.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            return new TikTokMedia(null, null, title.GetString());
        }

        return null;
    }

    /// <summary>Reads the API shape: itemInfo.itemStruct at the root.</summary>
    internal static TikTokMedia? ParseMedia(JsonDocument? document) =>
        document is null ? null : ReadItem(FindItem(document.RootElement) ?? default);

    /// <summary>
    /// Reads the server-rendered page: the item hides inside __UNIVERSAL_DATA_FOR_REHYDRATION__,
    /// with SIGI_STATE as the older spelling. Both are tried, and a page without them is ignored.
    /// </summary>
    internal static TikTokMedia? ParsePage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) { return null; }
        foreach (var (open, close) in new[]
        {
            ("<script id=\"__UNIVERSAL_DATA_FOR_REHYDRATION__\" type=\"application/json\">", "</script>"),
            ("<script id=\"SIGI_STATE\" type=\"application/json\">", "</script>")
        })
        {
            var start = html.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) { continue; }
            start += open.Length;
            var end = html.IndexOf(close, start, StringComparison.Ordinal);
            if (end <= start) { continue; }
            try
            {
                using var document = JsonDocument.Parse(html[start..end]);
                if (ReadItem(FindItem(document.RootElement) ?? default) is { } media) { return media; }
            }
            catch (JsonException) { /* a broken blob is the same as no blob */ }
        }

        return null;
    }

    private static JsonElement? FindItem(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) { return null; }
        if (root.TryGetProperty("itemInfo", out var info) &&
            info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("itemStruct", out var item))
        {
            return item;
        }

        if (root.TryGetProperty("__DEFAULT_SCOPE__", out var scope) &&
            scope.ValueKind == JsonValueKind.Object &&
            scope.TryGetProperty("webapp.video-detail", out var detail) &&
            detail.ValueKind == JsonValueKind.Object &&
            detail.TryGetProperty("itemInfo", out var scoped) &&
            scoped.ValueKind == JsonValueKind.Object &&
            scoped.TryGetProperty("itemStruct", out var scopedItem))
        {
            return scopedItem;
        }

        if (root.TryGetProperty("ItemModule", out var module) && module.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in module.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object) { return property.Value; }
            }
        }

        return null;
    }

    private static TikTokMedia? ReadItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) { return null; }
        string? audio = null;
        string? video = null;
        string? title = null;
        if (item.TryGetProperty("music", out var music) && music.ValueKind == JsonValueKind.Object &&
            music.TryGetProperty("playUrl", out var play))
        {
            audio = ReadUrl(play);
        }

        if (item.TryGetProperty("video", out var videoNode) && videoNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "playAddr", "downloadAddr", "playAddrH264" })
            {
                if (videoNode.TryGetProperty(name, out var address) && ReadUrl(address) is { } candidate)
                {
                    video = candidate;
                    break;
                }
            }
        }

        if (item.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.String)
        {
            title = desc.GetString();
        }

        if (audio is null && video is null) { return null; }
        return new TikTokMedia(audio, video, title);
    }

    /// <summary>Field spellings differ between endpoints, so every known shape is accepted.</summary>
    private static string? ReadUrl(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                var raw = node.GetString();
                return string.IsNullOrWhiteSpace(raw) ? null : raw;
            case JsonValueKind.Object:
                foreach (var listName in new[] { "UrlList", "urlList", "url_list" })
                {
                    if (node.TryGetProperty(listName, out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in list.EnumerateArray())
                        {
                            if (entry.ValueKind == JsonValueKind.String &&
                                entry.GetString() is { Length: > 0 } value)
                            {
                                return value;
                            }
                        }
                    }
                }

                foreach (var name in new[] { "Url", "url", "playAddr", "PlayAddr" })
                {
                    if (node.TryGetProperty(name, out var single) && single.ValueKind == JsonValueKind.String &&
                        single.GetString() is { Length: > 0 } nested)
                    {
                        return nested;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private async Task<JsonDocument?> TryReadJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return null; }
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload) || payload[0] is not ('{' or '[')) { return null; }
            return JsonDocument.Parse(payload);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private async Task<string?> TryReadTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return null; }
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private async Task<string> SaveAudioAsync(
        string id, string url, TikTokAudioOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (path, contentType) = await SaveStreamAsync(url, id, options, progress, cancellationToken).ConfigureAwait(false);
        var extension = ExtensionFor(contentType, url);
        if (extension is ".m4a" or ".mp3") { return path; }

        // An audio URL that is really a video container still has to be unwrapped.
        return ConvertToWav(path) ?? throw new TikTokException(
            TikTokFailure.UnsupportedAudio, "TikTok returned a format this machine cannot decode.");
    }

    private async Task<string> SaveVideoAsWavAsync(
        string id, string url, TikTokAudioOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (path, _) = await SaveStreamAsync(url, id, options, progress, cancellationToken).ConfigureAwait(false);
        return ConvertToWav(path) ?? throw new TikTokException(
            TikTokFailure.UnsupportedAudio, "TikTok returned a format this machine cannot decode.");
    }

    /// <summary>
    /// Downloads to a .part file so an interrupted transfer never looks like a finished track, and
    /// refuses to grow past the cap instead of discovering it after the disk is full.
    /// </summary>
    private async Task<(string Path, string? ContentType)> SaveStreamAsync(
        string url, string id, TikTokAudioOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new TikTokException(
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? TikTokFailure.Restricted
                    : TikTokFailure.DownloadFailed,
                $"TikTok answered {(int)response.StatusCode}.");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null &&
            !contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new TikTokException(TikTokFailure.NoAudio, $"TikTok served {contentType} instead of media.");
        }

        var expected = response.Content.Headers.ContentLength ?? -1;
        if (expected == 0) { throw new TikTokException(TikTokFailure.NoAudio, "TikTok served an empty file."); }
        if (expected > options.MaximumBytes)
        {
            throw new TikTokException(TikTokFailure.TooLarge, $"The file is larger than {options.MaximumBytes / (1024 * 1024)} MB.");
        }

        var partial = Path.Combine(options.MusicDirectory, id + ".part");
        await using (var file = File.Create(partial))
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > options.MaximumBytes)
                {
                    throw new TikTokException(TikTokFailure.TooLarge,
                        $"The file is larger than {options.MaximumBytes / (1024 * 1024)} MB.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (expected > 0) { progress?.Report(0.2 + 0.7 * written / expected); }
            }
        }

        return (partial, contentType);
    }

    private static string ExtensionFor(string? contentType, string url) => contentType?.ToLowerInvariant() switch
    {
        "audio/mp4" or "audio/x-m4a" or "audio/aac" => ".m4a",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "video/mp4" => ".mp4",
        _ => url.Contains(".mp3", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".m4a"
    };

    /// <summary>
    /// Unwraps a container into WAV with Media Foundation, the decoder the app already uses for the
    /// YouTube cache. This is what makes ffmpeg unnecessary for TikTok.
    /// </summary>
    internal static string? ConvertToWav(string sourcePath)
    {
        var target = Path.ChangeExtension(sourcePath, ".wav");
        try
        {
            using (var reader = new MediaFoundationReader(sourcePath))
            using (var writer = new WaveFileWriter(target, reader.WaveFormat))
            {
                reader.CopyTo(writer);
            }

            TryDelete(sourcePath);
            return File.Exists(target) && new FileInfo(target).Length > 0 ? target : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryDelete(target);
            // A container that cannot be decoded is useless on disk, so it is not left behind
            // for the cache size to count forever.
            TryDelete(sourcePath);
            return null;
        }
    }

    /// <summary>
    /// The last resort: the yt-dlp build YouTube already uses. Kept for the day TikTok changes its
    /// page and API at once, which is a question of "when" rather than "if".
    /// </summary>
    private async Task<TikTokAudioResult> DownloadWithYtDlpAsync(
        string id, string originalUrl, TikTokAudioOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var executable = _binaries.FindYtDlp();
        if (executable is null)
        {
            throw new TikTokException(TikTokFailure.Restricted,
                "TikTok did not expose this video, and yt-dlp is not installed to try another way.");
        }

        var template = Path.Combine(options.MusicDirectory, id + ".%(ext)s");
        var result = await _runner.RunAsync(new YouTubeProcessRequest
        {
            Executable = executable,
            Arguments =
            [
                "--no-playlist", "--no-progress", "--no-warnings",
                "-f", "bestaudio/best",
                "-o", template,
                "--print", "after_move:filepath",
                originalUrl.Trim()
            ],
            Timeout = options.DownloadTimeout
        }, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new TikTokException(TikTokFailure.VideoNotFound, "yt-dlp could not fetch this TikTok either.");
        }

        var path = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (path is null || !File.Exists(path))
        {
            throw new TikTokException(TikTokFailure.VideoNotFound, "yt-dlp reported success but left no file.");
        }

        if (!AudioTrackLoader.TryLoad(path, out _, out _))
        {
            var converted = ConvertToWav(path);
            if (converted is null)
            {
                throw new TikTokException(TikTokFailure.UnsupportedAudio,
                    "TikTok returned a format this machine cannot decode.");
            }

            path = converted;
        }

        progress?.Report(1d);
        return new TikTokAudioResult(id, id, path);
    }

    private static string? Trim(string? text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= maximum ? flat : flat[..maximum].TrimEnd() + "…";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
