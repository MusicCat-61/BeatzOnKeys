using System.Diagnostics;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.YouTube;

namespace BeatzOnKeys.Tests;

/// <summary>
/// The YouTube layer is tested through its two seams: a fake binary provider says what is installed,
/// a fake process runner says what yt-dlp would have answered. No network, no binaries.
/// </summary>
internal static class YouTubeTests
{
    public static async Task SearchParsesDumpJson()
    {
        var runner = new ScriptedRunner();
        runner.Results.Enqueue(new YouTubeProcessResult(0,
            """
            {"id":"dQw4w9WgXcQ","title":"Never Gonna Give You Up","uploader":"Rick Astley","duration":213.5}
            {"id":"kJQP7kiw5Fk","title":"Despacito","channel":"Luis Fonsi","duration":282}
            {"id":"abc","title":""}
            """, string.Empty, false));
        using var service = new ServiceContext(runner);
        var results = await service.Service.SearchAsync("rick astley", 10);

        AssertEx.Equal(2, results.Count);
        AssertEx.Equal("dQw4w9WgXcQ", results[0].VideoId);
        AssertEx.Equal("Never Gonna Give You Up", results[0].Title);
        AssertEx.Equal("Rick Astley", results[0].Author);
        AssertEx.Equal(TimeSpan.FromSeconds(213.5), results[0].Duration);
        AssertEx.Equal("Luis Fonsi", results[1].Author);
        // The search term reaches yt-dlp in the documented pipe-less shape.
        AssertEx.True(runner.Requests[0].Arguments.Contains("ytsearch10:rick astley"),
            $"Arguments were: {string.Join(' ', runner.Requests[0].Arguments)}");
    }

    public static async Task MissingBinaryIsReportedBeforeAnyProcess()
    {
        var runner = new ScriptedRunner();
        using var service = new ServiceContext(runner, ytDlp: null);
        var failure = await Failure(async () => await service.Service.SearchAsync("anything"));
        AssertEx.Equal(YouTubeFailure.YtDlpMissing, failure, "A missing yt-dlp must be named as such.");
        AssertEx.Equal(0, runner.Requests.Count, "No process may be started without the binary.");
    }

    public static async Task RestrictedResponseIsClassified()
    {
        var runner = new ScriptedRunner();
        runner.Results.Enqueue(new YouTubeProcessResult(1, string.Empty,
            "ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm you're not a bot. Use --cookies-from-browser.", false));
        using var service = new ServiceContext(runner);
        var failure = await Failure(async () => await service.Service.SearchAsync("anything"));
        AssertEx.Equal(YouTubeFailure.Restricted, failure, "A bot check must be reported as restricted access.");
    }

    public static async Task DownloadExposesOnlyTwoSlots()
    {
        using var directory = new TemporaryDirectory();
        var runner = new BlockingRunner();
        var options = new YouTubeOptions
        {
            MusicDirectory = directory.Path,
            DownloadDirectory = directory.Path,
            MaxConcurrentDownloads = 2
        };
        using var binaries = new StubBinaries(ytDlp: "yt-dlp.exe", ffmpeg: null);
        var service = new YouTubeTrackService(binaries, runner, options);
        using var cancellation = new CancellationTokenSource();
        var attempts = new[]
        {
            service.DownloadAsync("video000001", null, cancellation.Token),
            service.DownloadAsync("video000002", null, cancellation.Token),
            service.DownloadAsync("video000003", null, cancellation.Token)
        };

        await WaitFor(() => runner.Entered >= 2, TimeSpan.FromSeconds(5), "two yt-dlp runs at once");
        await Task.Delay(250);
        AssertEx.Equal(2, runner.Entered, "A third download must wait for a free slot.");
        AssertEx.Equal(0, service.FreeSlots);

        await cancellation.CancelAsync();
        foreach (var attempt in attempts)
        {
            try { await attempt; }
            catch (Exception exception) when (exception is OperationCanceledException or YouTubeException) { }
        }
    }

    public static async Task CancellingStopsTheRealProcess()
    {
        var runner = new YouTubeProcessRunner();
        using var cancellation = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var task = runner.RunAsync(new YouTubeProcessRequest
        {
            Executable = "cmd.exe",
            Arguments = ["/c", "ping -n 30 127.0.0.1"],
            Timeout = TimeSpan.FromMinutes(5)
        }, cancellation.Token);

        await Task.Delay(400);
        await cancellation.CancelAsync();
        var cancelled = false;
        try { await task; }
        catch (OperationCanceledException) { cancelled = true; }

        watch.Stop();
        AssertEx.True(cancelled, "Cancellation must surface as OperationCanceledException.");
        // A surviving child process would keep this call alive for about thirty seconds.
        AssertEx.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"The process tree must be killed on cancel; it took {watch.Elapsed.TotalSeconds:F1} s.");
    }

    public static async Task TimeoutIsEnforcedByTheRunner()
    {
        var runner = new YouTubeProcessRunner();
        var watch = Stopwatch.StartNew();
        var result = await runner.RunAsync(new YouTubeProcessRequest
        {
            Executable = "cmd.exe",
            Arguments = ["/c", "ping -n 30 127.0.0.1"],
            Timeout = TimeSpan.FromMilliseconds(400)
        });
        watch.Stop();

        AssertEx.True(result.TimedOut, "An over-long run must be reported as a timeout.");
        AssertEx.False(result.Succeeded);
        AssertEx.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"The timeout must kill the process; it took {watch.Elapsed.TotalSeconds:F1} s.");
    }

    public static async Task CacheSizeSumsTheFolderAndFormats()
    {
        using var directory = new TemporaryDirectory();
        var music = Path.Combine(directory.Path, "Music");
        Directory.CreateDirectory(music);
        await File.WriteAllBytesAsync(Path.Combine(music, "one.m4a"), new byte[1500 * 1024]);
        await File.WriteAllBytesAsync(Path.Combine(music, "two.m4a"), new byte[500 * 1024]);
        var nested = Path.Combine(music, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "three.wav"), new byte[1024]);

        var service = new YouTubeTrackService(
            new StubBinaries(ytDlp: "yt-dlp.exe", ffmpeg: null),
            new ScriptedRunner(),
            new YouTubeOptions { MusicDirectory = music, DownloadDirectory = directory.Path });
        var bytes = await service.GetCacheSizeAsync();
        AssertEx.Equal(1500L * 1024 + 500L * 1024 + 1024, bytes);
        AssertEx.Equal("2.0 МБ", ByteSizeFormatter.Format(bytes, "КБ", "МБ", "ГБ"));

        AssertEx.Equal("0 КБ", ByteSizeFormatter.Format(0, "КБ", "МБ", "ГБ"));
        AssertEx.Equal("512.0 КБ", ByteSizeFormatter.Format(512 * 1024, "КБ", "МБ", "ГБ"));
        AssertEx.Equal("14.3 МБ", ByteSizeFormatter.Format((long)(14.3 * 1024 * 1024), "КБ", "МБ", "ГБ"));
        AssertEx.Equal("1.2 ГБ", ByteSizeFormatter.Format((long)(1.2 * 1024 * 1024 * 1024), "КБ", "МБ", "ГБ"));
        AssertEx.Equal("2.0 MB", ByteSizeFormatter.Format(bytes, "KB", "MB", "GB"));

        await service.ClearCacheAsync();
        AssertEx.Equal(0L, await service.GetCacheSizeAsync(), "Clearing removes every cached file.");
        AssertEx.True(Directory.Exists(music), "The folder itself stays; only the files go.");
    }

    public static Task LinksAreClassifiedBeforeAnythingStarts()
    {
        AssertEx.True(YouTubeUrls.TryParse("https://www.youtube.com/watch?v=dQw4w9WgXcQ", out var id, out var problem));
        AssertEx.Equal("dQw4w9WgXcQ", id);
        AssertEx.Equal(YouTubeUrlProblem.None, problem);

        AssertEx.True(YouTubeUrls.TryParse("https://youtu.be/dQw4w9WgXcQ?t=42", out id, out _));
        AssertEx.Equal("dQw4w9WgXcQ", id);

        AssertEx.True(YouTubeUrls.TryParse("dQw4w9WgXcQ", out id, out _));
        AssertEx.Equal("dQw4w9WgXcQ", id);

        AssertEx.False(YouTubeUrls.TryParse("https://www.youtube.com/playlist?list=PL123", out _, out problem));
        AssertEx.Equal(YouTubeUrlProblem.Playlist, problem);

        AssertEx.False(YouTubeUrls.TryParse("https://www.youtube.com/@someone", out _, out problem));
        AssertEx.Equal(YouTubeUrlProblem.Channel, problem);

        AssertEx.False(YouTubeUrls.TryParse("not a link at all", out _, out problem));
        AssertEx.Equal(YouTubeUrlProblem.NotYouTube, problem);

        AssertEx.True(YouTubeUrls.LooksLikeLink("https://youtube.com/watch?v=dQw4w9WgXcQ"));
        AssertEx.False(YouTubeUrls.LooksLikeLink("linkin park numb"));
        return Task.CompletedTask;
    }

    public static Task YouTubeTracksNeverEnterTheLocalPlaylist()
    {
        using var directory = new TemporaryDirectory();
        // A "Ready" row is only kept when its file is really there; that is what makes a stale
        // cache entry surface as failed instead of pointing at nothing.
        var cached = Path.Combine(directory.Path, "dQw4w9WgXcQ.m4a");
        File.WriteAllBytes(cached, new byte[16]);
        var shared = Guid.NewGuid();
        var settings = new AppSettings
        {
            Tracks = [new TrackState { Id = shared, SourcePath = @"C:\music\local.mp3" }],
            YouTubeTracks =
            [
                new YouTubeTrackState { VideoId = "dQw4w9WgXcQ", Title = "Cached", CachedPath = cached, Status = YouTubeTrackStatus.Ready },
                new YouTubeTrackState { VideoId = "dQw4w9WgXcQ", Title = "Duplicate video" },
                new YouTubeTrackState { VideoId = "  " },
                new YouTubeTrackState { VideoId = "kJQP7kiw5Fk", Status = YouTubeTrackStatus.Downloading }
            ]
        };

        settings.Normalize();

        AssertEx.Equal(1, settings.Tracks.Count, "Normalizing YouTube data must not touch the local playlist.");
        AssertEx.Equal(@"C:\music\local.mp3", settings.Tracks[0].SourcePath);
        AssertEx.Equal(2, settings.YouTubeTracks.Count, "One row per video id, blank ids dropped.");
        AssertEx.Equal("dQw4w9WgXcQ", settings.YouTubeTracks[0].VideoId);
        AssertEx.Equal(YouTubeTrackStatus.Ready, settings.YouTubeTracks[0].Status);
        // A download cannot survive a restart, so it resurfaces as failed instead of spinning.
        AssertEx.Equal(YouTubeTrackStatus.Failed, settings.YouTubeTracks[1].Status);
        AssertEx.Equal(5, AppSettings.CurrentSchemaVersion);
        AssertEx.True(settings.CookiesFolderPath is not null && settings.ProxyUrl is not null,
            "The new YouTube settings must be non-null after normalization.");
        return Task.CompletedTask;
    }

    public static Task CookiesAreOnlyTakenFromANetscapeJar()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "not-cookies.txt"), "hello");
        AssertEx.True(YouTubeTrackService.FindCookiesFile(directory.Path) is null,
            "An unrelated text file must not be handed to yt-dlp.");

        var jar = Path.Combine(directory.Path, "cookies.txt");
        File.WriteAllText(jar, "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tNAME\tvalue\n");
        AssertEx.Equal(jar, YouTubeTrackService.FindCookiesFile(directory.Path));
        AssertEx.True(YouTubeTrackService.FindCookiesFile(null) is null);
        AssertEx.True(YouTubeTrackService.FindCookiesFile(Path.Combine(directory.Path, "missing")) is null);
        return Task.CompletedTask;
    }

    public static Task ProgressTemplateIsParsed()
    {
        var reported = new List<double>();
        var progress = new Progress<double>(reported.Add);
        YouTubeTrackService.ReportProgress("""[bok-progress]{"downloaded_bytes":524288,"total_bytes":1048576}""", progress);
        YouTubeTrackService.ReportProgress("""[bok-progress]{"downloaded_bytes":5,"total_bytes_estimate":10}""", progress);
        YouTubeTrackService.ReportProgress("""[bok-progress]{"downloaded_bytes":5}""", progress);
        YouTubeTrackService.ReportProgress("[bok-progress]not json", progress);
        YouTubeTrackService.ReportProgress("ordinary log line", progress);
        return Task.CompletedTask;
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static async Task<YouTubeFailure> Failure(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (YouTubeException exception)
        {
            return exception.Failure;
        }

        throw new TestFailureException("The action was expected to fail with a YouTubeException.");
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition()) { return; }
            await Task.Delay(25);
        }

        throw new TestFailureException($"Timed out waiting for {what}.");
    }

    private sealed class ServiceContext : IDisposable
    {
        public ServiceContext(ScriptedRunner runner, string? ytDlp = "yt-dlp.exe")
        {
            Binaries = new StubBinaries(ytDlp, ffmpeg: null);
            Service = new YouTubeTrackService(Binaries, runner, new YouTubeOptions());
        }

        public StubBinaries Binaries { get; }
        public YouTubeTrackService Service { get; }
        public void Dispose() => Binaries.Dispose();
    }
}

internal sealed class StubBinaries(string? ytDlp, string? ffmpeg) : IYtDlpBinaryProvider, IDisposable
{
    public string? FindYtDlp() => ytDlp;
    public string? FindFfmpeg() => ffmpeg;

    public Task<string> EnsureYtDlpAsync(IProgress<double>? progress, CancellationToken cancellationToken) =>
        ytDlp is null
            ? throw new InvalidOperationException("no network in tests")
            : Task.FromResult(ytDlp);

    public Task<string> EnsureFfmpegAsync(IProgress<double>? progress, CancellationToken cancellationToken) =>
        ffmpeg is null
            ? throw new InvalidOperationException("no network in tests")
            : Task.FromResult(ffmpeg);

    public void Dispose() { }
}

internal sealed class ScriptedRunner : IYouTubeProcessRunner
{
    public Queue<YouTubeProcessResult> Results { get; } = new();
    public List<YouTubeProcessRequest> Requests { get; } = [];

    public Task<YouTubeProcessResult> RunAsync(YouTubeProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Results.Count == 0)
        {
            return Task.FromResult(new YouTubeProcessResult(0, string.Empty, string.Empty, false));
        }

        return Task.FromResult(Results.Dequeue());
    }
}

/// <summary>Blocks inside the runner until cancelled, which is how the slot cap is observed.</summary>
internal sealed class BlockingRunner : IYouTubeProcessRunner
{
    private int _entered;
    public int Entered => Volatile.Read(ref _entered);

    public async Task<YouTubeProcessResult> RunAsync(YouTubeProcessRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _entered);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return new YouTubeProcessResult(0, string.Empty, string.Empty, false);
    }
}