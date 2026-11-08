using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.YouTube;

namespace BeatzOnKeys.Tests;

/// <summary>Covers the link recognition, the TikTok service and the library file.</summary>
public static class MediaTests
{
    public static Task MediaUrlRecognisesBothPlatforms()
    {
        // YouTube keeps working exactly as before: the wrapper exists so old callers need no edit.
        AssertEx.True(MediaUrl.TryParse("https://www.youtube.com/watch?v=dQw4w9WgXcQ", out var id, out var platform, out var problem));
        AssertEx.Equal("dQw4w9WgXcQ", id);
        AssertEx.Equal(MediaPlatform.YouTube, platform);
        AssertEx.Equal(MediaUrlProblem.None, problem);

        // A TikTok video page carries its id in the path.
        AssertEx.True(MediaUrl.TryParse("https://www.tiktok.com/@someone/video/7234567890123456789", out id, out platform, out _));
        AssertEx.Equal("7234567890123456789", id);
        AssertEx.Equal(MediaPlatform.TikTok, platform);

        // A short link is a valid request with an unknown id: only the network can resolve it.
        AssertEx.True(MediaUrl.TryParse("https://vm.tiktok.com/ZMabcdefg/", out id, out platform, out _));
        AssertEx.Null(id, "A short link has no id until it is followed.");
        AssertEx.Equal(MediaPlatform.TikTok, platform);

        AssertEx.True(MediaUrl.TryParse("https://vt.tiktok.com/ZSabcdefg/", out id, out platform, out _));
        AssertEx.Null(id);
        AssertEx.Equal(MediaPlatform.TikTok, platform);

        // A bare id of either shape is accepted, because both are what a share sheet hands over.
        AssertEx.True(MediaUrl.TryParse("7234567890123456789", out id, out platform, out _));
        AssertEx.Equal(MediaPlatform.TikTok, platform);
        AssertEx.True(MediaUrl.TryParse("dQw4w9WgXcQ", out id, out platform, out _));
        AssertEx.Equal(MediaPlatform.YouTube, platform);
        return Task.CompletedTask;
    }

    public static Task MediaUrlIgnoresTelegramQueryParameters()
    {
        // Telegram appends tracking to a forwarded link; it must not change what the link means.
        const string forwarded = "https://vm.tiktok.com/ZMabcdefg/?is_from_webapp=1&sender_device=pc";
        AssertEx.True(MediaUrl.TryParse(forwarded, out var id, out var platform, out _));
        AssertEx.Equal(MediaPlatform.TikTok, platform);
        AssertEx.Null(id, "The tracking must not be mistaken for an id.");

        AssertEx.True(MediaUrl.TryParse(
            "https://www.tiktok.com/@someone/video/7234567890123456789?is_from_webapp=1&sender_device=pc",
            out id, out platform, out _));
        AssertEx.Equal("7234567890123456789", id);

        // A YouTube watch link keeps its id inside the query, so stripping must not be too eager.
        AssertEx.True(MediaUrl.TryParse("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLx&t=42", out id, out _, out _));
        AssertEx.Equal("dQw4w9WgXcQ", id);
        return Task.CompletedTask;
    }

    public static Task MediaUrlClassifiesRefusals()
    {
        AssertEx.False(MediaUrl.TryParse("   ", out _, out _, out var problem));
        AssertEx.Equal(MediaUrlProblem.Empty, problem);

        AssertEx.False(MediaUrl.TryParse("просто текст", out _, out _, out problem));
        AssertEx.Equal(MediaUrlProblem.NotSupported, problem);

        AssertEx.False(MediaUrl.TryParse("https://www.youtube.com/playlist?list=PLabc", out _, out _, out problem));
        AssertEx.Equal(MediaUrlProblem.Playlist, problem);

        AssertEx.False(MediaUrl.TryParse("https://www.youtube.com/@someone", out _, out _, out problem));
        AssertEx.Equal(MediaUrlProblem.Channel, problem);

        // A profile is not a video, and the user should be told which mistake they made.
        AssertEx.False(MediaUrl.TryParse("https://www.tiktok.com/@someone", out _, out _, out problem));
        AssertEx.Equal(MediaUrlProblem.Channel, problem);

        AssertEx.False(MediaUrl.TryParse("https://example.com/video/7234567890123456789", out _, out _, out problem));
        AssertEx.Equal(MediaUrlProblem.NotSupported, problem);
        return Task.CompletedTask;
    }

    public static Task MediaUrlBuildsAndValidatesIds()
    {
        AssertEx.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            MediaUrl.WatchUrl("dQw4w9WgXcQ", MediaPlatform.YouTube));
        AssertEx.Equal("https://www.tiktok.com/video/7234567890123456789",
            MediaUrl.WatchUrl("7234567890123456789", MediaPlatform.TikTok));

        // The id becomes a file name, so anything else would be a path injection.
        AssertEx.False(MediaUrl.IsValidId("../escape", MediaPlatform.TikTok));
        AssertEx.False(MediaUrl.IsValidId("dQw4w9WgXcQ", MediaPlatform.TikTok));
        AssertEx.True(MediaUrl.IsValidId("7234567890123456789", MediaPlatform.TikTok));

        AssertEx.True(MediaUrl.LooksLikeLink("https://vm.tiktok.com/ZMabcdefg/"));
        AssertEx.True(MediaUrl.LooksLikeLink("https://youtu.be/dQw4w9WgXcQ"));
        AssertEx.False(MediaUrl.LooksLikeLink("never gonna give you up"));
        return Task.CompletedTask;
    }

    public static Task TikTokPageIsParsedWithoutTheApi()
    {
        // The public page embeds the same item the signed API would return; this is the path that
        // keeps working when the API demands a signature.
        const string html = """
            <html><body><script id="__UNIVERSAL_DATA_FOR_REHYDRATION__" type="application/json">
            {"__DEFAULT_SCOPE__":{"webapp.video-detail":{"itemInfo":{"itemStruct":{
              "desc":"Cat plays piano",
              "music":{"playUrl":"https://cdn.example/audio.m4a"},
              "video":{"playAddr":"https://cdn.example/video.mp4"}}}}}}
            </script></body></html>
            """;
        var media = TikTokAudioService.ParsePage(html);
        AssertEx.NotNull(media, "The page carries an item.");
        AssertEx.Equal("https://cdn.example/audio.m4a", media!.AudioUrl);
        AssertEx.Equal("https://cdn.example/video.mp4", media.VideoUrl);
        AssertEx.Equal("Cat plays piano", media.Title);

        // The older spelling still shows up on cached pages.
        const string legacy = """
            <script id="SIGI_STATE" type="application/json">
            {"ItemModule":{"7234567890123456789":{"desc":"Old page","video":{"playAddr":"https://cdn.example/v.mp4"}}}}
            </script>
            """;
        var old = TikTokAudioService.ParsePage(legacy);
        AssertEx.NotNull(old);
        AssertEx.Equal("https://cdn.example/v.mp4", old!.VideoUrl);
        AssertEx.Null(old.AudioUrl);

        // A challenge page is HTML with no item, which must read as "nothing", not as an exception.
        AssertEx.Null(TikTokAudioService.ParsePage("<html>verify you are human</html>"));
        AssertEx.Null(TikTokAudioService.ParsePage(null));
        AssertEx.Null(TikTokAudioService.ParsePage("<script id=\"__UNIVERSAL_DATA_FOR_REHYDRATION__\" type=\"application/json\">{oops</script>"));
        return Task.CompletedTask;
    }

    public static Task TikTokApiShapeIsParsed()
    {
        using var document = JsonDocument.Parse("""
            {"itemInfo":{"itemStruct":{"desc":"From the API","music":{"playUrl":{"UrlList":["https://cdn.example/a.m4a"]}},"video":{}}}}
            """);
        var media = TikTokAudioService.ParseMedia(document);
        AssertEx.NotNull(media);
        AssertEx.Equal("https://cdn.example/a.m4a", media!.AudioUrl);
        AssertEx.Equal("From the API", media.Title);

        // Neither address means there is nothing to download, which is a clear failure, not a crash.
        using var empty = JsonDocument.Parse("""{"itemInfo":{"itemStruct":{"desc":"nothing"}}}""");
        AssertEx.Null(TikTokAudioService.ParseMedia(empty));
        return Task.CompletedTask;
    }

    public static async Task TikTokServiceRefusesWithoutAudio()
    {
        var handler = new StubHandler(request =>
        {
            // The API answers with an item that has no playable address, and the page has no item.
            if (request.RequestUri!.AbsolutePath.Contains("item/detail", StringComparison.Ordinal))
            {
                return Json("""{"itemInfo":{"itemStruct":{"desc":"no media","video":{}}}}""");
            }

            return Html("<html>nothing here</html>");
        });
        var service = new TikTokAudioService(new StubBinaries(ytDlp: null, ffmpeg: null), new ScriptedRunner(), handler);
        using var directory = new TemporaryDirectory();
        var options = new TikTokAudioOptions { MusicDirectory = directory.Path };

        // With no address at all the service falls through to yt-dlp, which is not installed here.
        var exception = await AssertEx.ThrowsAsync<TikTokException>(
            () => service.DownloadAsync("7234567890123456789", "https://www.tiktok.com/video/7234567890123456789",
                options, null, CancellationToken.None));
        AssertEx.Equal(TikTokFailure.Restricted, exception.Failure);
    }

    public static async Task TikTokServiceReportsAnEmptyBody()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("item/detail", StringComparison.Ordinal))
            {
                return Json("""{"itemInfo":{"itemStruct":{"desc":"clip","music":{"playUrl":"https://cdn.example/a.m4a"}}}}""");
            }

            return EmptyAudio();
        });
        var service = new TikTokAudioService(new StubBinaries(ytDlp: null, ffmpeg: null), new ScriptedRunner(), handler);
        using var directory = new TemporaryDirectory();
        var options = new TikTokAudioOptions { MusicDirectory = directory.Path };
        var exception = await AssertEx.ThrowsAsync<TikTokException>(
            () => service.DownloadAsync("7234567890123456789", "https://www.tiktok.com/video/7234567890123456789",
                options, null, CancellationToken.None));
        AssertEx.Equal(TikTokFailure.NoAudio, exception.Failure,
            "An empty file is reported as no audio rather than a broken download.");
    }

    public static async Task TikTokServiceRefusesOversizedFiles()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("item/detail", StringComparison.Ordinal))
            {
                return Json("""{"itemInfo":{"itemStruct":{"desc":"clip","music":{"playUrl":"https://cdn.example/a.m4a"}}}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
                {
                    Headers = { ContentLength = 64L * 1024 * 1024, ContentType = new("audio/mp4") }
                }
            };
        });
        var service = new TikTokAudioService(new StubBinaries(ytDlp: null, ffmpeg: null), new ScriptedRunner(), handler);
        using var directory = new TemporaryDirectory();
        var options = new TikTokAudioOptions { MusicDirectory = directory.Path };
        var exception = await AssertEx.ThrowsAsync<TikTokException>(
            () => service.DownloadAsync("7234567890123456789", "https://www.tiktok.com/video/7234567890123456789",
                options, null, CancellationToken.None));
        AssertEx.Equal(TikTokFailure.TooLarge, exception.Failure);
        AssertEx.Equal(0, Directory.GetFiles(directory.Path, "*.part").Length,
            "A refused download leaves no partial file behind.");
    }

    public static async Task TikTokServiceMapsForbiddenToRestricted()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("item/detail", StringComparison.Ordinal))
            {
                return Json("""{"itemInfo":{"itemStruct":{"desc":"clip","music":{"playUrl":"https://cdn.example/a.m4a"}}}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });
        var service = new TikTokAudioService(new StubBinaries(ytDlp: null, ffmpeg: null), new ScriptedRunner(), handler);
        using var directory = new TemporaryDirectory();
        var options = new TikTokAudioOptions { MusicDirectory = directory.Path };
        var exception = await AssertEx.ThrowsAsync<TikTokException>(
            () => service.DownloadAsync("7234567890123456789", "https://www.tiktok.com/video/7234567890123456789",
                options, null, CancellationToken.None));
        AssertEx.Equal(TikTokFailure.Restricted, exception.Failure);
    }

    public static async Task TikTokServiceFollowsAShortLink()
    {
        string? requested = null;
        var handler = new StubHandler(request =>
        {
            requested = request.RequestUri!.AbsoluteUri;
            if (request.RequestUri.AbsoluteUri.Contains("vm.tiktok.com", StringComparison.Ordinal))
            {
                // The redirect is what turns a Telegram share link into a real video id.
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location =
                    new Uri("https://www.tiktok.com/@someone/video/7234567890123456789?is_from_webapp=1");
                return response;
            }

            if (request.RequestUri.AbsolutePath.Contains("item/detail", StringComparison.Ordinal))
            {
                return Json("""{"itemInfo":{"itemStruct":{"desc":"clip","music":{"playUrl":"https://cdn.example/a.m4a"}}}}""");
            }

            return EmptyAudio();
        });
        var service = new TikTokAudioService(new StubBinaries(ytDlp: null, ffmpeg: null), new ScriptedRunner(), handler);
        using var directory = new TemporaryDirectory();
        var options = new TikTokAudioOptions { MusicDirectory = directory.Path };
        await AssertEx.ThrowsAsync<TikTokException>(
            () => service.DownloadAsync(null, "https://vm.tiktok.com/ZMabcdefg/", options, null, CancellationToken.None));
        AssertEx.True(requested?.Contains("vm.tiktok.com", StringComparison.Ordinal) == true,
            "The short link is followed rather than guessed at.");
    }

    public static async Task TikTokServiceConvertsAVideoToWav()
    {
        // Media Foundation is the decoder the app already trusts for the YouTube cache, so a video
        // container is unwrapped with it instead of shelling out to ffmpeg.
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "clip.mp4");
        await File.WriteAllBytesAsync(source, []);

        // An unreadable container must answer null instead of throwing into the caller.
        AssertEx.Null(TikTokAudioService.ConvertToWav(source));
        AssertEx.False(File.Exists(source), "A container that cannot be decoded is not kept.");
    }

    public static async Task LibraryRoundTripsAndSurvivesCorruption()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "library.json");
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var music = Path.Combine(directory.Path, "Music");
        var service = new LibraryService(path, settingsPath, music, Path.Combine(music, "TikTok"));

        var snapshot = new LibrarySnapshot(
            [new TrackState { SourcePath = Path.Combine(directory.Path, "a.mp3") }],
            [new YouTubeTrackState { VideoId = "dQw4w9WgXcQ", Title = "One", Status = YouTubeTrackStatus.Failed }],
            [new YouTubeTrackState { VideoId = "7234567890123456789", Title = "Two", Status = YouTubeTrackStatus.Failed }],
            null);
        await service.SaveAsync(snapshot);
        AssertEx.True(File.Exists(path), "The file is written where the caller asked.");
        var loaded = await service.LoadAsync();
        AssertEx.Equal(1, loaded.Tracks.Count);
        AssertEx.Equal(1, loaded.YouTubeTracks.Count);
        AssertEx.Equal(1, loaded.TikTokTracks.Count);
        AssertEx.Equal("7234567890123456789", loaded.TikTokTracks[0].VideoId);

        // A corrupt file is not worth crashing over: the user gets an empty library and the file
        // stays on disk for a bug report.
        await File.WriteAllTextAsync(path, "{ this is not json");
        var broken = await service.LoadAsync();
        AssertEx.Equal(0, broken.Tracks.Count);
        AssertEx.Equal(0, broken.YouTubeTracks.Count);
        AssertEx.Equal(0, broken.TikTokTracks.Count);
    }

    public static async Task LibraryMigratesTracksOutOfSettings()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var libraryPath = Path.Combine(directory.Path, "library.json");
        var music = Path.Combine(directory.Path, "Music");
        Directory.CreateDirectory(music);

        // An installation from before 1.4.0 keeps its tracks inside settings.json.
        var legacy = new AppSettings
        {
            MaxVolume = 0.8,
            Tracks = [new TrackState { SourcePath = Path.Combine(directory.Path, "old.mp3") }],
            YouTubeTracks = [new YouTubeTrackState { VideoId = "dQw4w9WgXcQ", Status = YouTubeTrackStatus.Failed }]
        };
        await new SettingsService(settingsPath).SaveAsync(legacy);
        AssertEx.True(File.Exists(settingsPath));

        var service = new LibraryService(libraryPath, settingsPath, music, Path.Combine(music, "TikTok"));
        var snapshot = await service.LoadAsync();
        AssertEx.Equal(1, snapshot.Tracks.Count, "The old tracks move into the library.");
        AssertEx.Equal(1, snapshot.YouTubeTracks.Count);
        AssertEx.True(File.Exists(libraryPath), "The library file is created by the migration.");

        // The flag stops a second migration from resurrecting tracks the user later removed.
        var settingsAfter = await new SettingsService(settingsPath).LoadAsync();
        AssertEx.True(settingsAfter.TracksMigratedToLibrary);
        AssertEx.Equal(0.8, settingsAfter.MaxVolume, "A migration must not touch a single preference.");

        var second = await service.LoadAsync();
        AssertEx.Equal(1, second.Tracks.Count, "The second load reads the library, not the old copy.");
    }

    public static async Task LibraryRelocatesCachedPathsOnce()
    {
        using var directory = new TemporaryDirectory();
        var music = Path.Combine(directory.Path, "Music");
        var tikTok = Path.Combine(music, "TikTok");
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(tikTok);
        var libraryPath = Path.Combine(directory.Path, "library.json");
        var settingsPath = Path.Combine(directory.Path, "settings.json");

        // One file really moved with the root; the other one is gone for good. The old copies exist
        // while the library is written, which is what a real installation looks like before a move.
        var oldRoot = Path.Combine(directory.Path, "old");
        var oldMusic = Path.Combine(oldRoot, "Music");
        var oldTikTok = Path.Combine(oldRoot, "TikTok");
        Directory.CreateDirectory(oldMusic);
        Directory.CreateDirectory(oldTikTok);
        var movedFile = Path.Combine(music, "dQw4w9WgXcQ.m4a");
        await File.WriteAllBytesAsync(movedFile, [1, 2, 3]);
        var tikTokFile = Path.Combine(tikTok, "7234567890123456789.m4a");
        await File.WriteAllBytesAsync(tikTokFile, [1, 2, 3]);

        var movedOld = Path.Combine(oldMusic, "dQw4w9WgXcQ.m4a");
        await File.WriteAllBytesAsync(movedOld, [1, 2, 3]);
        var tikTokOld = Path.Combine(oldTikTok, "7234567890123456789.m4a");
        await File.WriteAllBytesAsync(tikTokOld, [1, 2, 3]);

        var snapshot = new LibrarySnapshot(
            [],
            [new YouTubeTrackState
            {
                VideoId = "dQw4w9WgXcQ", CachedPath = movedOld,
                Status = YouTubeTrackStatus.Ready
            },
            new YouTubeTrackState
            {
                VideoId = "aaaaaaaaaaa", CachedPath = Path.Combine(oldMusic, "aaaaaaaaaaa.m4a"),
                Status = YouTubeTrackStatus.Ready
            }],
            [new YouTubeTrackState
            {
                VideoId = "7234567890123456789", CachedPath = tikTokOld,
                Status = YouTubeTrackStatus.Ready
            }],
            null);
        await new LibraryService(libraryPath, settingsPath, music, tikTok).SaveAsync(snapshot);

        // The move itself: the old folder is gone, the files sit in the new root.
        Directory.Delete(oldRoot, recursive: true);

        var loaded = await new LibraryService(libraryPath, settingsPath, music, tikTok).LoadAsync();
        AssertEx.Equal(movedFile, loaded.YouTubeTracks[0].CachedPath, "A file found in the current root is repointed.");
        AssertEx.Equal(YouTubeTrackStatus.Ready, loaded.YouTubeTracks[0].Status);
        AssertEx.Equal(string.Empty, loaded.YouTubeTracks[1].CachedPath, "A file that is gone everywhere is cleared.");
        AssertEx.Equal(YouTubeTrackStatus.Failed, loaded.YouTubeTracks[1].Status);
        AssertEx.Equal(tikTokFile, loaded.TikTokTracks[0].CachedPath, "TikTok paths move into the TikTok folder.");
        AssertEx.Equal(LibraryMigrator.CurrentVersion, loaded.PathsMigratedToLibraryVersion,
            "The version is recorded so the scan runs once.");
    }

    /// <summary>A handler that answers from a function, so no test ever touches the network.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };

    private static HttpResponseMessage EmptyAudio() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([]) { Headers = { ContentType = new("audio/mp4") } }
    };
}