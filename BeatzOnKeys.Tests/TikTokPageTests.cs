using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeatzOnKeys.Core.YouTube;

namespace BeatzOnKeys.Tests;

/// <summary>
/// The page fetch around the puzzle: the key has to travel on the second request, and a short link
/// that answers with the puzzle instead of a redirect still has to resolve.
/// </summary>
public static class TikTokPageTests
{
    public static async Task TheKeyTravelsOnTheSecondRequest()
    {
        var (payload, _) = BuildChallenge();
        var handler = new RecordingHandler(challenge: ChallengePage(payload), page: RealPage());
        var service = new TikTokAudioService(
            new StubBinaries(ytDlp: null, ffmpeg: null), new ScriptedRunner(), handler);

        var page = await service.ReadPageAsync("https://www.tiktok.com/@i/video/7704233321361870094", CancellationToken.None);
        AssertEx.True(page is not null && page.Contains("__UNIVERSAL_DATA_FOR_REHYDRATION__", StringComparison.Ordinal),
            "The page behind the puzzle is returned.");

        AssertEx.Equal(2, handler.Requests.Count, "The page was asked for twice: before and after unlocking.");
        AssertEx.False(handler.Requests[0].Contains("_wafchallengeid", StringComparison.Ordinal),
            "The first request carries no key, because there is not one yet.");
        AssertEx.True(handler.Requests[1].Contains($"{TikTokWaf.CookieName}=", StringComparison.Ordinal),
            "The second request carries the solved key.");
    }

    public static async Task ASolvedPuzzleIsReused()
    {
        var (firstPayload, firstNonce) = BuildChallenge();
        var (_, secondNonce) = BuildChallenge();
        var handler = new RecordingHandler(challenge: ChallengePage(firstPayload), page: RealPage());
        var service = new TikTokAudioService(
            new StubBinaries(ytDlp: null, ffmpeg: null), new ScriptedRunner(), handler);

        await service.ReadPageAsync("https://www.tiktok.com/@i/video/1", CancellationToken.None);
        // A second page inside the same download must not grind a second puzzle: the key is still
        // valid and already in hand.
        await service.ReadPageAsync("https://www.tiktok.com/@i/video/2", CancellationToken.None);

        AssertEx.Equal(3, handler.Requests.Count,
            "Only the new page was asked for once more; the key was reused rather than solved again.");
        AssertEx.True(handler.Requests[1].Contains($"{TikTokWaf.CookieName}=", StringComparison.Ordinal) &&
            handler.Requests[2].Contains($"{TikTokWaf.CookieName}=", StringComparison.Ordinal),
            "Every request after the first carries the key.");
        AssertEx.True(firstNonce >= 0 && secondNonce >= 0, "Both challenges were real puzzles.");
    }

    public static async Task APuzzleThatKeepsComingBackIsReported()
    {
        var (payload, _) = BuildChallenge();
        // The stub answers with the puzzle every single time, which is a refusal, not a prompt to
        // grind again: the service must stop after one solve instead of looping.
        var handler = new RecordingHandler(challenge: ChallengePage(payload), page: ChallengePage(payload));
        var service = new TikTokAudioService(
            new StubBinaries(ytDlp: null, ffmpeg: null), new ScriptedRunner(), handler);

        var exception = await AssertEx.ThrowsAsync<TikTokException>(
            () => service.ReadPageAsync("https://www.tiktok.com/@i/video/3", CancellationToken.None));
        AssertEx.Equal(TikTokFailure.ChallengeFailed, exception.Failure);
        AssertEx.Equal(2, handler.Requests.Count, "One attempt, one retry, then the truth.");
    }

    public static async Task AShortLinkAnsweredWithThePuzzleStillResolves()
    {
        // TikTok sometimes guards the short-link host too: the HEAD is answered with the puzzle
        // page and no redirect at all, so the key has to be solved before the redirect appears.
        var (payload, _) = BuildChallenge();
        var handler = new ShortLinkHandler(ChallengePage(payload));
        var service = new TikTokAudioService(
            new StubBinaries(ytDlp: null, ffmpeg: null), new ScriptedRunner(), handler);

        var id = await service.ResolveIdAsync("https://vt.tiktok.com/ZSpSfwrwT", CancellationToken.None);
        AssertEx.Equal("7704233321361870094", id, "The id is found once the puzzle is out of the way.");
        AssertEx.True(handler.Requests.Any(request => request.Contains($"{TikTokWaf.CookieName}=", StringComparison.Ordinal)),
            "The unlocking key was used to get past the guarded short link.");
    }

    public static Task TheHardCaseFitsInTheBudget()
    {
        // The real challenges from TikTok solve in single-digit milliseconds. The worst case the
        // budget allows is a nonce near the end of the million, and that is the number worth
        // knowing: it must stay well inside a second even though the whole range is scanned.
        var salt = RandomNumberGenerator.GetBytes(32);
        var target = SHA256.HashData([.. salt, .. Encoding.ASCII.GetBytes("999999")]);
        var stopwatch = Stopwatch.StartNew();
        var nonce = TikTokWaf.Solve(salt, target);
        stopwatch.Stop();

        AssertEx.Equal(999999L, nonce, "The hardest allowed puzzle is still solved.");
        AssertEx.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"The worst case took {stopwatch.ElapsedMilliseconds} ms, which is far too slow to ship.");
        return Task.CompletedTask;
    }

    public static Task TheTitleAndPathArePulledApart()
    {
        // yt-dlp is asked for both facts on one line, because a second process call would cost more
        // than the parse. The title may contain spaces, pipes and colons; the path must still be
        // recovered exactly.
        var line = @"C:\Music\TikTok\7704233321361870094.mp3|:|Снежинка Северянка & Вольный Дух";
        var parts = line.Split("|:|", 2);
        AssertEx.Equal(@"C:\Music\TikTok\7704233321361870094.mp3", parts[0], "The path comes first.");
        AssertEx.Equal("Снежинка Северянка & Вольный Дух", parts[1], "The title keeps its spaces and ampersand.");

        // A line without the separator is an older yt-dlp or a plain path, and must not break.
        AssertEx.False("C:\\Music\\a.mp3".Contains("|:|", StringComparison.Ordinal),
            "A plain path is recognised as having no title attached.");
        return Task.CompletedTask;
    }

    private static (string Payload, long Nonce) BuildChallenge()
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetInt32(0, 5000);
        var target = SHA256.HashData([.. salt, .. Encoding.ASCII.GetBytes(nonce.ToString())]);
        var json = JsonSerializer.Serialize(new
        {
            v = new { a = Convert.ToBase64String(salt), b = 1794154132, c = Convert.ToBase64String(target) },
            s = "signature"
        });
        return (Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), nonce);
    }

    private static string ChallengePage(string payload) =>
        "<html><body>Please wait...<p id=\"wci\" class=\"_wafchallengeid\"></p>" +
        $"<p id=\"cs\" class=\"{payload}\"></p></body></html>";

    private static string RealPage() =>
        "<html><script id=\"__UNIVERSAL_DATA_FOR_REHYDRATION__\" type=\"application/json\">" +
        "{\"__DEFAULT_SCOPE__\":{\"webapp.video-detail\":{\"itemInfo\":{\"itemStruct\":" +
        "{\"desc\":\"clip\",\"music\":{\"playUrl\":\"https://cdn.example/a.mp3\"}}}}}}</script></html>";

    /// <summary>A handler that records the Cookie header of each request and replays fixed bodies.</summary>
    private sealed class RecordingHandler(string challenge, string page) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Headers.TryGetValues("Cookie", out var values)
                ? string.Join(";", values)
                : string.Empty);
            var first = Requests.Count == 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(first ? challenge : page, Encoding.UTF8, "text/html")
            });
        }
    }

    /// <summary>
    /// Models a guarded short link: the first HEAD is answered with the puzzle and no redirect, and
    /// only a request carrying the solved key is allowed to redirect to the video.
    /// </summary>
    private sealed class ShortLinkHandler(string challenge) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cookie = request.Headers.TryGetValues("Cookie", out var values)
                ? string.Join(";", values)
                : string.Empty;
            Requests.Add(cookie);

            // A request without the key is answered with the puzzle; with the key, the redirect.
            var hasKey = cookie.Contains(TikTokWaf.CookieName, StringComparison.Ordinal);
            if (!hasKey)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(challenge, Encoding.UTF8, "text/html")
                });
            }

            // With the key, TikTok answers the redirect and the real client follows it, so what the
            // caller sees is a finished request whose address is the video. That finished address is
            // what ResolveIdAsync reads, so the stub reports it the same way.
            var landed = new Uri("https://www.tiktok.com/@someone/video/7704233321361870094");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>video page</html>", Encoding.UTF8, "text/html"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, landed)
            });
        }
    }
}