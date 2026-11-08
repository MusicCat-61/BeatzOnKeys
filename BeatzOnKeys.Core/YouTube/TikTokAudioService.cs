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
    TooLarge = 8,

    /// <summary>
    /// The network itself refused the request: a reset in the middle, or TikTok refusing this
    /// address outright. Nothing the program does on its own fixes that, so it is its own kind.
    /// </summary>
    NetworkBlocked = 9,

    /// <summary>The interception page appeared and its puzzle could not be solved in the budget.</summary>
    ChallengeFailed = 10
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

    /// <summary>
    /// The solved interception puzzle, kept for the whole download: TikTok honours it for a couple
    /// of minutes, which covers the page, the media request and a retry or two.
    /// </summary>
    private string? _unlockedCookie;

    /// <summary>How many times a reset connection is retried before the user is told.</summary>
    private const int TransferAttempts = 3;

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
        progress?.Report(0.15);
        var title = Trim(media?.Title, 100) ?? id;

        // A direct address is the fastest way and needs no external tool. It is only a fast path,
        // though: whenever it does not end in a playable file, yt-dlp is asked instead of the user
        // being told the video is gone.
        string? cached = null;
        TikTokException? directFailure = null;
        if (media?.AudioUrl is { } audioUrl)
        {
            try { cached = await SaveAudioAsync(id, audioUrl, options, progress, cancellationToken).ConfigureAwait(false); }
            catch (TikTokException exception) when (IsWorthRetryingAnotherWay(exception)) { directFailure = exception; }
        }

        if (cached is null && media?.VideoUrl is { } videoUrl)
        {
            try { cached = await SaveVideoAsWavAsync(id, videoUrl, options, progress, cancellationToken).ConfigureAwait(false); }
            catch (TikTokException exception) when (IsWorthRetryingAnotherWay(exception)) { directFailure = exception; }
        }

        if (cached is null || !AudioTrackLoader.TryLoad(cached, out _, out _))
        {
            var fallback = await DownloadWithYtDlpAsync(
                id, title, originalUrl, options, progress, cancellationToken, directFailure).ConfigureAwait(false);
            cached = fallback.CachedPath;
            // yt-dlp often knows the real title even when the page refused to tell us.
            if (string.Equals(title, id, StringComparison.Ordinal)) { title = fallback.Title; }
        }

        if (!AudioTrackLoader.TryLoad(cached, out _, out _))
        {
            // The file stays on disk on purpose. Deleting it threw away the only evidence of what
            // TikTok actually served, and a retry can make use of it.
            throw new TikTokException(TikTokFailure.UnsupportedAudio,
                $"The file TikTok served is not audio this machine can play: {cached}");
        }

        progress?.Report(1d);
        return new TikTokAudioResult(id, title, cached);
    }

    /// <summary>
    /// Turns vm./vt. short links into an id. HEAD is tried first because it costs one packet; some
    /// TikTok edges answer a short link with the interception page instead of a redirect, so the
    /// body is read through the unlocking path and the redirect is followed once more after that.
    /// </summary>
    internal async Task<string> ResolveIdAsync(string originalUrl, CancellationToken cancellationToken)
    {
        var url = originalUrl.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { url = "https://" + url; }

        if (await TryFollowAsync(url, HttpMethod.Head, cancellationToken).ConfigureAwait(false) is { } fromHead)
        {
            return fromHead;
        }

        // A challenge here is a real possibility: TikTok sometimes guards the short-link host too.
        // ReadPageAsync unlocks it, and the second HEAD then gets the redirect it was denied.
        var body = await ReadPageAsync(url, cancellationToken).ConfigureAwait(false);
        if (await TryFollowAsync(url, HttpMethod.Head, cancellationToken).ConfigureAwait(false) is { } afterUnlock)
        {
            return afterUnlock;
        }

        // Some edges answer the follow-up with the destination inside the body instead of a redirect.
        if (body is not null && TryReadIdFromText(body, out var embedded)) { return embedded; }

        throw new TikTokException(TikTokFailure.VideoNotFound, "The TikTok link did not lead to a video.");
    }

    /// <summary>
    /// Sends one redirect-following request and reports the id in wherever it ended up. The unlocked
    /// key travels with it, or the follow-up would be challenged exactly like the first attempt.
    /// </summary>
    private async Task<string?> TryFollowAsync(string url, HttpMethod method, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (_unlockedCookie is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", $"{TikTokWaf.CookieName}={_unlockedCookie}");
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return TryReadId(response.RequestMessage?.RequestUri, out var id) ? id : null;
        }
        catch (HttpRequestException) { return null; }
    }

    private static bool TryReadId(Uri? uri, out string id)
    {
        id = string.Empty;
        return uri is not null && TryReadIdFromText(uri.AbsoluteUri, out id);
    }

    private static bool TryReadIdFromText(string text, out string id)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"(?:video|v|embed(?:/v2)?)/(?<id>\d{15,25})");
        id = match.Success ? match.Groups["id"].Value : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// A direct download that failed in a way another route might survive. Cancelling and a file
    /// that is simply too large are decisions, not accidents, so they stop here.
    /// </summary>
    private static bool IsWorthRetryingAnotherWay(TikTokException exception) =>
        exception.Failure is not (TikTokFailure.Cancelled or TikTokFailure.TooLarge);

    internal sealed record TikTokMedia(string? AudioUrl, string? VideoUrl, string? Title);

    /// <summary>
    /// The three HTTP paths in order of reliability, each returning the same shape. A page that
    /// answers with the interception puzzle is unlocked and read again rather than written off.
    /// </summary>
    private async Task<TikTokMedia?> ReadMediaAsync(string id, string originalUrl, CancellationToken cancellationToken)
    {
        var api = await TryReadJsonAsync(
            $"https://www.tiktok.com/api/item/detail/?aid=1988&itemId={id}", cancellationToken).ConfigureAwait(false);
        if (ParseMedia(api) is { } fromApi) { return fromApi; }

        var page = await ReadPageAsync($"https://www.tiktok.com/@i/video/{id}", cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Fetches a page and, when TikTok answers with the "Please wait" puzzle instead, solves it and
    /// asks again with the key. This is what turns a wall into content without any external tool.
    /// </summary>
    internal async Task<string?> ReadPageAsync(string url, CancellationToken cancellationToken)
    {
        // A key already in hand goes on the very first attempt: without it every page of the same
        // download would be challenged once more, which is both slower and one more strike on the
        // rate limiter.
        var first = await TryReadTextAsync(url, cancellationToken, _unlockedCookie).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(first)) { return first; }
        if (!TikTokWaf.LooksLikeChallenge(first)) { return first; }

        // The same puzzle twice in a row is a refusal, not a prompt to grind again.
        if (_unlockedCookie is null)
        {
            var payload = TikTokWaf.ReadPayload(first);
            // The solve is pure CPU work with no await inside, so it goes to a worker: on a slow
            // machine the hard case is around a second, and that must not freeze the window.
            var unlocked = await Task.Run(() => TikTokWaf.Unlock(payload), cancellationToken)
                .ConfigureAwait(false);
            if (unlocked is null)
            {
                throw new TikTokException(TikTokFailure.ChallengeFailed,
                    "TikTok asked for a proof this program could not produce.");
            }

            _unlockedCookie = unlocked;
            AppLogger.Info("tiktok", "Solved the interception puzzle; asking again with the key.");
        }

        var second = await TryReadTextAsync(url, cancellationToken, _unlockedCookie).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(second) || TikTokWaf.LooksLikeChallenge(second))
        {
            throw new TikTokException(TikTokFailure.ChallengeFailed,
                "TikTok kept asking for the proof even after it was solved.");
        }

        return second;
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
            // H.264 first, on purpose. The plain playAddr is very often the bytevc1 (H.265) variant,
            // which Windows only decodes with a separate HEVC package installed; asking for the AVC
            // variant up front is what keeps ordinary machines able to play the result.
            foreach (var name in new[] { "playAddrH264", "play_addr_h264", "downloadAddr", "playAddr" })
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

    private async Task<string?> TryReadTextAsync(
        string url, CancellationToken cancellationToken, string? cookie = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cookie is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", $"{TikTokWaf.CookieName}={cookie}");
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
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
    ///
    /// <para>
    /// The finished file is renamed to its real extension before this returns. Leaving the ".part"
    /// name in place was the reason good audio was thrown away: the player only accepts .mp3, .wav
    /// and .m4a, so a perfectly decoded MP3 called "123.part" was deleted as unreadable.
    /// </para>
    /// </summary>
    private async Task<(string Path, string? ContentType)> SaveStreamAsync(
        string url, string id, TikTokAudioOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TransferAsync(url, id, options, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (attempt < TransferAttempts)
            {
                // A reset in the middle of a transfer is the network, not the video. Give it two
                // more chances with a pause before telling the user anything.
                AppLogger.Info("tiktok", $"Transfer attempt {attempt} failed ({exception.Message}); retrying.");
                await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<(string Path, string? ContentType)> TransferAsync(
        string url, string id, TikTokAudioOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_unlockedCookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{TikTokWaf.CookieName}={_unlockedCookie}");
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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
        var final = Path.Combine(options.MusicDirectory, id + ExtensionFor(contentType, url));
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
                    TryDelete(partial);
                    throw new TikTokException(TikTokFailure.TooLarge,
                        $"The file is larger than {options.MaximumBytes / (1024 * 1024)} MB.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (expected > 0) { progress?.Report(0.2 + 0.7 * written / expected); }
            }
        }

        // The rename is the point: from here on the file has the name the player expects.
        File.Move(partial, final, overwrite: true);
        return (final, contentType);
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
        string id, string title, string originalUrl, TikTokAudioOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken, TikTokException? earlier = null)
    {
        string? executable;
        try
        {
            // Fetched on demand, exactly as the YouTube section does it: 22 MB arrive once, with a
            // progress bar, and every later TikTok download has a working tool.
            executable = _binaries.FindYtDlp() ??
                await _binaries.EnsureYtDlpAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLogger.Error("tiktok", "yt-dlp is not available for TikTok.", exception);
            executable = null;
        }

        if (executable is null)
        {
            // Nothing else can read the media, so the earlier, more specific reason is the honest
            // thing to report rather than "yt-dlp is missing".
            throw earlier ?? new TikTokException(TikTokFailure.Restricted,
                "TikTok did not expose this video, and yt-dlp is not installed to try another way.");
        }

        var template = Path.Combine(options.MusicDirectory, id + ".%(ext)s");
        var result = await _runner.RunAsync(new YouTubeProcessRequest
        {
            Executable = executable,
            Arguments =
            [
                "--no-playlist", "--no-progress", "--no-warnings",
                // "audio" is the mp3 track TikTok serves separately; bestaudio picks it first when
                // the extractor offers it, and the plain best is the fallback when it does not.
                "-f", "audio/bestaudio/best",
                "-o", template,
                "--print", "after_move:filepath",
                originalUrl.Trim()
            ],
            Timeout = options.DownloadTimeout
        }, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw Classify(result.StandardError) ?? earlier ?? new TikTokException(
                TikTokFailure.VideoNotFound, "yt-dlp could not fetch this TikTok either.");
        }

        var path = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (path is null || !File.Exists(path))
        {
            throw earlier ?? new TikTokException(TikTokFailure.VideoNotFound, "yt-dlp reported success but left no file.");
        }

        if (!AudioTrackLoader.TryLoad(path, out _, out _))
        {
            var converted = ConvertToWav(path);
            if (converted is null)
            {
                throw new TikTokException(TikTokFailure.UnsupportedAudio,
                    $"yt-dlp saved a container this machine cannot play: {path}");
            }

            path = converted;
        }

        progress?.Report(1d);
        return new TikTokAudioResult(id, string.IsNullOrWhiteSpace(title) ? id : title, path);
    }

    /// <summary>
    /// Reads yt-dlp's own words. It knows the difference between a video that is gone, one that
    /// needs an account, and a network that refuses this address, and the user deserves that
    /// difference instead of one catch-all sentence.
    /// </summary>
    internal static TikTokException? Classify(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError)) { return null; }
        var text = standardError.ToLowerInvariant();
        if (text.Contains("ip address is blocked") || text.Contains("not available in your region") ||
            text.Contains("connection was reset") || text.Contains("recv failure") ||
            text.Contains("failed to perform") || text.Contains("timed out"))
        {
            return new TikTokException(TikTokFailure.NetworkBlocked,
                "The network refuses access to this TikTok: " + FirstLine(standardError));
        }

        if (text.Contains("login") || text.Contains("private") || text.Contains("requires authentication"))
        {
            return new TikTokException(TikTokFailure.Restricted,
                "TikTok wants an account for this video: " + FirstLine(standardError));
        }

        if (text.Contains("unsupported url") || text.Contains("no video formats"))
        {
            return new TikTokException(TikTokFailure.VideoNotFound,
                "TikTok did not serve a playable video: " + FirstLine(standardError));
        }

        return null;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? text;

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
