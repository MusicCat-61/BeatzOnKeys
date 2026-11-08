using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeatzOnKeys.Core.YouTube;

namespace BeatzOnKeys.Tests;

/// <summary>
/// The interception page TikTok serves instead of a video. Every check here is offline: the puzzle
/// is generated in the test, so the solver is proven without asking TikTok for permission.
/// </summary>
public static class TikTokWafTests
{
    public static Task ThePuzzleIsSolved()
    {
        // The server picks a salt and any number; the digest is sha256(salt + digits).
        var salt = RandomNumberGenerator.GetBytes(32);
        const int expected = 4321;
        var target = SHA256.HashData([.. salt, .. Encoding.ASCII.GetBytes(expected.ToString())]);

        var nonce = TikTokWaf.Solve(salt, target);
        AssertEx.Equal((long)expected, nonce, "The nonce the puzzle was built around is found.");

        // A target nothing can match must end as "not found", not as a crash or an endless loop.
        var impossible = RandomNumberGenerator.GetBytes(32);
        AssertEx.Equal(-1L, TikTokWaf.Solve(salt, impossible, limit: 500),
            "A search that runs out of budget reports failure.");
        AssertEx.Equal(-1L, TikTokWaf.Solve([], impossible), "An empty salt is refused.");
        AssertEx.Equal(-1L, TikTokWaf.Solve(salt, [1, 2, 3]), "A target that is not a digest is refused.");
        return Task.CompletedTask;
    }

    public static Task TheCookieCarriesTheAnswer()
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        const int expected = 77;
        var target = SHA256.HashData([.. salt, .. Encoding.ASCII.GetBytes(expected.ToString())]);
        var payload = JsonSerializer.Serialize(new
        {
            v = new { a = Convert.ToBase64String(salt), b = 1794154132, c = Convert.ToBase64String(target) },
            s = "signature-goes-through-untouched"
        });

        var cookie = TikTokWaf.Unlock(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)));
        AssertEx.NotNull(cookie, "A well-formed payload is unlocked.");

        // What comes back is base64 of the same document plus the answer, so the signature survives.
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cookie!));
        using var document = JsonDocument.Parse(decoded);
        AssertEx.Equal(77L + 0, document.RootElement.GetProperty("d").GetString() is { } answer
            ? long.Parse(Encoding.ASCII.GetString(Convert.FromBase64String(Pad(answer))))
            : -1L, "The answer is carried back as base64 digits.");
        AssertEx.Equal("signature-goes-through-untouched",
            document.RootElement.GetProperty("s").GetString(), "The signature is copied through.");
        AssertEx.Equal(Convert.ToBase64String(salt),
            document.RootElement.GetProperty("v").GetProperty("a").GetString(), "The salt is copied through.");
        return Task.CompletedTask;
    }

    public static Task MalformedPayloadsAreRefused()
    {
        AssertEx.Null(TikTokWaf.Unlock(null));
        AssertEx.Null(TikTokWaf.Unlock("   "));
        AssertEx.Null(TikTokWaf.Unlock("not base64 at all!"), "Text that is not base64 is refused.");
        AssertEx.Null(TikTokWaf.Unlock(Base64("""{"v":{"a":"x"}}""")), "A payload without a target is refused.");
        AssertEx.Null(TikTokWaf.Unlock(Base64("[]")), "An array root is refused instead of throwing.");
        AssertEx.Null(TikTokWaf.Unlock(Base64("42")), "A number root is refused instead of throwing.");
        AssertEx.Null(TikTokWaf.Unlock(Base64("""{"v":{"a":"@@@","c":"@@@"}}""")),
            "A payload whose fields are not base64 is refused.");
        return Task.CompletedTask;
    }

    public static Task TheChallengePageIsRecognised()
    {
        const string page = """
            <html><body>Please wait...
            <p id="wci" class="_wafchallengeid"></p>
            <p id="cs" class="eyJ2Ijp7ImEiOiIzM0F0eTZLTWVBSXJYNDdtRDlqeS9qYXV2SEYzWFdlaTE9In19"></p>
            </body></html>
            """;
        AssertEx.True(TikTokWaf.LooksLikeChallenge(page), "The wait page is recognised.");
        AssertEx.Equal("eyJ2Ijp7ImEiOiIzM0F0eTZLTWVBSXJYNDdtRDlqeS9qYXV2SEYzWFdlaTE9In19",
            TikTokWaf.ReadPayload(page), "The payload is read out of the stub.");

        // Spelling variants are the difference between working and silently not working later.
        AssertEx.Equal("abc", TikTokWaf.ReadPayload("""<p id="cs" class='abc'></p>"""),
            "Single quotes are accepted.");
        AssertEx.Equal("abc", TikTokWaf.ReadPayload("""<p id="cs" class = "abc"></p>"""),
            "Spaces around the equals sign are accepted.");

        AssertEx.False(TikTokWaf.LooksLikeChallenge("<html>a real video page</html>"),
            "A real page is not mistaken for the stub.");
        AssertEx.Null(TikTokWaf.ReadPayload("<html>nothing here</html>"), "A page without the payload yields null.");
        AssertEx.Null(TikTokWaf.ReadPayload(null));
        AssertEx.Null(TikTokWaf.ReadPayload("""<p id="cs" class=abc></p>"""), "An unquoted value is not guessed at.");
        return Task.CompletedTask;
    }

    public static Task TheProviderRefusalIsReadFromYtDlp()
    {
        // yt-dlp knows why it failed; the difference decides whether retrying is worth offering.
        // Its "IP address is blocked" wording is a translation of TikTok's own status 10204, which
        // is a regional decision by TikTok rather than a provider interfering with the connection.
        AssertEx.Equal(TikTokFailure.RegionBlocked,
            TikTokAudioService.Classify("ERROR: [TikTok] 123: Your IP address is blocked from accessing this post")?.Failure,
            "TikTok withholding a post is the region answer, not a broken network.");
        AssertEx.Equal(TikTokFailure.RegionBlocked,
            TikTokAudioService.Classify("This video is not available in your region")?.Failure,
            "A regional refusal is its own kind.");
        AssertEx.Equal(TikTokFailure.NetworkBlocked,
            TikTokAudioService.Classify("curl: (56) Recv failure: Connection was reset")?.Failure,
            "A reset connection is a network refusal too.");

        AssertEx.Equal(TikTokFailure.Restricted,
            TikTokAudioService.Classify("ERROR: [TikTok] 123: TikTok is requiring login for access to this content")?.Failure,
            "A login wall is its own answer.");

        AssertEx.Equal(TikTokFailure.VideoNotFound,
            TikTokAudioService.Classify("ERROR: Unsupported URL: https://www.tiktok.com/x")?.Failure,
            "An unrecognised address is not a network problem.");

        AssertEx.Null(TikTokAudioService.Classify(""), "Nothing said means nothing to report.");
        AssertEx.Null(TikTokAudioService.Classify("WARNING: something unrelated"), "An unrelated warning says nothing.");
        return Task.CompletedTask;
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Pad(string value) => (value.Length % 4) switch
    {
        2 => value + "==",
        3 => value + "=",
        _ => value
    };
}