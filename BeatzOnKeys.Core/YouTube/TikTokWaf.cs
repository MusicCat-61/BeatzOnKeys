using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.YouTube;

/// <summary>
/// TikTok answers a request it does not like with a "Please wait" page instead of the video. The
/// page is not a dead end: it carries a small proof of work. The server picks a random salt and a
/// target digest, and any number whose hash matches unlocks the request for a couple of minutes.
///
/// <para>
/// The whole job is one SHA-256 loop over a fixed buffer, so the full million candidates fit in
/// well under a second and no browser, JavaScript engine or VPN is involved. The budget is capped
/// on purpose: a puzzle harder than that is a refusal, and spinning the CPU will not change it.
/// </para>
///
/// <para>
/// The signature inside the payload is never checked here. There is no key to check it with, and
/// there is no need: the server verifies it when the cookie comes back. The payload is copied
/// through byte for byte, with the answer appended, so the signature stays exactly as issued.
/// </para>
/// </summary>
public static class TikTokWaf
{
    /// <summary>The cookie TikTok expects once the nonce is found.</summary>
    public const string CookieName = "_wafchallengeid";

    /// <summary>The element the challenge payload hides in, as base64 without padding.</summary>
    private const string PayloadId = "id=\"cs\"";

    /// <summary>Room for the longest decimal a long can hold, so the guard below can never trip.</summary>
    private const int MaximumDigits = 20;

    /// <summary>
    /// A salt larger than this is not something TikTok sends; treat it as a hostile payload rather
    /// than risk a stack overflow from <c>stackalloc</c> in <see cref="Solve"/>.
    /// </summary>
    private const int MaximumSaltLength = 1024;

    /// <summary>A page carrying the cookie name and the wait text is the interception page.</summary>
    public static bool LooksLikeChallenge(string? html) =>
        !string.IsNullOrWhiteSpace(html) &&
        html.Contains(CookieName, StringComparison.Ordinal) &&
        html.Contains("Please wait", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the payload out of a challenge page. Returns null when the page is not a challenge or
    /// the payload cannot be found, which the caller treats the same way: nothing to unlock.
    ///
    /// <para>
    /// This is deliberately a scan rather than an HTML parser: the page is a fixed stub of a dozen
    /// tags, and pulling a dependency in for it would cost more than it explains. Single and double
    /// quotes and spaces around the equals sign are all accepted, because the stub has changed its
    /// spelling before and will again. The scan is also bounded to the tag that carries the marker:
    /// a <c>class</c> attribute on a neighbouring tag is not our payload.
    /// </para>
    /// </summary>
    public static string? ReadPayload(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) { return null; }
        var marker = html.IndexOf(PayloadId, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) { return null; }

        // The class attribute lives inside the same tag as the marker; anything past the next '>'
        // belongs to a different element and cannot carry our payload.
        var tagEnd = html.IndexOf('>', marker);
        if (tagEnd < 0) { return null; }

        var classAt = FindAttribute(html, "class", marker, tagEnd);
        if (classAt < 0) { return null; }

        var equals = classAt + "class".Length;
        while (equals < tagEnd && char.IsWhiteSpace(html[equals])) { equals++; }
        if (equals >= tagEnd || html[equals] != '=') { return null; }
        equals++;
        while (equals < tagEnd && char.IsWhiteSpace(html[equals])) { equals++; }
        if (equals >= tagEnd) { return null; }

        var quote = html[equals];
        if (quote is not ('"' or '\'')) { return null; }
        var start = equals + 1;
        var end = html.IndexOf(quote, start);
        if (end <= start || end > tagEnd) { return null; }
        return html[start..end];
    }

    /// <summary>
    /// Finds a bare attribute name inside a tag, so <c>className</c> or <c>data-class</c> are not
    /// mistaken for <c>class</c>. Returns the index of the name, or -1.
    /// </summary>
    private static int FindAttribute(string html, string name, int from, int to)
    {
        var search = from;
        while (search < to)
        {
            var hit = html.IndexOf(name, search, to - search, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) { return -1; }

            var before = hit == 0 ? ' ' : html[hit - 1];
            var after = hit + name.Length >= html.Length ? ' ' : html[hit + name.Length];
            var leftFree = char.IsWhiteSpace(before) || before == '"' || before == '\'';
            var rightFree = char.IsWhiteSpace(after) || after == '=' || after == '/' || after == '"' || after == '\'';
            if (leftFree && rightFree) { return hit; }

            search = hit + name.Length;
        }

        return -1;
    }

    /// <summary>
    /// Turns a payload into the cookie value, or null when the payload is not the shape we know.
    /// </summary>
    public static string? Unlock(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) { return null; }
        string json;
        try
        {
            json = Encoding.UTF8.GetString(FromBase64(payload));
        }
        catch (FormatException) { return null; }

        var root = TryReadObject(json);
        if (root is not { } element) { return null; }

        if (!element.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Object ||
            !version.TryGetProperty("a", out var salt) || salt.ValueKind != JsonValueKind.String ||
            !version.TryGetProperty("c", out var target) || target.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        byte[] saltBytes;
        byte[] targetBytes;
        try
        {
            saltBytes = FromBase64(salt.GetString()!);
            targetBytes = FromBase64(target.GetString()!);
        }
        catch (FormatException) { return null; }

        var nonce = Solve(saltBytes, targetBytes);
        if (nonce < 0)
        {
            // Worth a line in the log: "TikTok made the puzzle harder" is a different bug report
            // from "TikTok is broken", and this is the only place that can tell them apart.
            AppLogger.Warning("tiktok", "The interception puzzle exceeded the solving budget.");
            return null;
        }

        // The answer goes in as a last property; the rest of the document keeps its exact text so
        // the signature the server computed still matches.
        var trimmed = json.TrimEnd();
        if (!trimmed.EndsWith('}')) { return null; }

        Span<byte> answerBytes = stackalloc byte[MaximumDigits];
        var answerLength = WriteDigits(nonce, answerBytes);
        if (answerLength < 0) { return null; }
        var answer = Convert.ToBase64String(answerBytes[..answerLength]);

        var completed = string.Concat(trimmed.AsSpan(0, trimmed.Length - 1), ",\"d\":\"", answer, "\"}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(completed));
    }

    /// <summary>
    /// Finds the number whose hash matches. -1 means "not in the range", which is the honest answer
    /// for a puzzle the server made harder than we are willing to grind.
    ///
    /// <para>
    /// Everything that changes per step is written into one stack buffer and hashed in place: a
    /// million candidates cost a million hash operations and no allocations. The salt is bounded
    /// before the buffer is sized, because it arrives from a server and must not be allowed to
    /// exhaust the stack.
    /// </para>
    /// </summary>
    internal static long Solve(byte[] salt, byte[] target, long limit = 1_000_000)
    {
        if (salt.Length == 0 || salt.Length > MaximumSaltLength ||
            target.Length != 32 || limit < 0)
        {
            return -1;
        }

        // Allocated once, outside the loop, on purpose: a stackalloc inside it would grow the frame
        // with every candidate. Only the digit bytes are rewritten in place.
        Span<byte> buffer = stackalloc byte[salt.Length + MaximumDigits];
        salt.AsSpan().CopyTo(buffer);
        Span<byte> hash = stackalloc byte[32];
        using var sha = SHA256.Create();
        for (var candidate = 0L; candidate <= limit; candidate++)
        {
            var digits = WriteDigits(candidate, buffer[salt.Length..]);
            if (digits < 0) { return -1; }
            if (sha.TryComputeHash(buffer[..(salt.Length + digits)], hash, out _) &&
                hash.SequenceEqual(target))
            {
                return candidate;
            }
        }

        return -1;
    }

    /// <summary>
    /// Writes a non-negative number as ASCII digits; returns how many were written, or -1 when the
    /// destination is too short. The length is counted before anything is written, so a short
    /// destination cannot leave a half-written number behind.
    /// </summary>
    private static int WriteDigits(long value, Span<byte> destination)
    {
        var length = value == 0 ? 1 : 0;
        for (var remaining = value; remaining > 0; remaining /= 10) { length++; }
        if (length > destination.Length) { return -1; }

        if (value == 0)
        {
            destination[0] = (byte)'0';
            return 1;
        }

        for (var index = length - 1; index >= 0; index--)
        {
            destination[index] = (byte)('0' + (int)(value % 10));
            value /= 10;
        }

        return length;
    }

    private static byte[] FromBase64(string value)
    {
        // TikTok writes base64 without padding, which Convert rejects.
        var padded = value.Trim();
        switch (padded.Length % 4)
        {
            case 1:
                // No amount of padding makes this valid, so say so instead of handing back garbage.
                throw new FormatException("The payload is not base64.");
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Parses the payload only when it really is an object. A document whose root is an array or a
    /// number would make TryGetProperty throw, which is not a failure the caller could act on.
    /// <c>Clone</c> detaches the element from the document so <see cref="Unlock"/> can read it
    /// after the document is disposed.
    /// </summary>
    private static JsonElement? TryReadObject(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
    }
}
