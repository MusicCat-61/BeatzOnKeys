using System.Text.RegularExpressions;

namespace BeatzOnKeys.Core.YouTube;

/// <summary>The services whose single-item links the app can fetch.</summary>
public enum MediaPlatform
{
    None = 0,
    YouTube = 1,
    TikTok = 2
}

/// <summary>Why an input cannot be downloaded: the app takes one item at a time.</summary>
public enum MediaUrlProblem
{
    None = 0,
    Empty = 1,
    NotSupported = 2,
    Playlist = 3,
    Channel = 4,
    Live = 5
}

/// <summary>
/// Recognises the link shapes a user may paste from either service. Only a single item is
/// accepted; playlists and channels are rejected before any request is made.
/// </summary>
public static class MediaUrl
{
    /// <summary>YouTube ids are eleven characters; the same shape the cache file name relies on.</summary>
    private static readonly Regex YouTubeId = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    private static readonly Regex YouTubeIdInUrl = new(
        @"(?:youtu\.be/|youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|embed/|live/))(?<id>[A-Za-z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>TikTok ids are snowflake numbers; the range is generous so a change does not break us.</summary>
    private static readonly Regex TikTokId = new(@"^\d{15,25}$", RegexOptions.Compiled);

    private static readonly Regex TikTokIdInUrl = new(
        @"tiktok\.com/(?:@[^/\s]+/video|v|embed(?:/v2)?)/(?<id>\d{15,25})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>vm./vt. links and the newer /t/ form carry no id: the service resolves them.</summary>
    private static readonly Regex TikTokShort = new(
        @"(?:^|//)(?:vm|vt)\.tiktok\.com/|tiktok\.com/t/[A-Za-z0-9]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Splits an input into platform and id. TikTok short links return a null id on purpose: only
    /// the network can turn them into one, and the caller is told by the null rather than a code.
    /// </summary>
    public static bool TryParse(
        string? input, out string? mediaId, out MediaPlatform platform, out MediaUrlProblem problem)
    {
        mediaId = null;
        platform = MediaPlatform.None;
        problem = MediaUrlProblem.Empty;
        if (string.IsNullOrWhiteSpace(input)) { return false; }

        var text = input.Trim();
        if (text.Length == 0) { return false; }

        // A bare id may arrive with tracking glued on; the query is stripped only for these direct
        // checks, because a watch link keeps its id inside the query and must not lose it.
        var bare = StripQuery(text);

        if (YouTubeId.IsMatch(bare))
        {
            mediaId = bare;
            platform = MediaPlatform.YouTube;
            problem = MediaUrlProblem.None;
            return true;
        }

        if (TikTokId.IsMatch(bare))
        {
            mediaId = bare;
            platform = MediaPlatform.TikTok;
            problem = MediaUrlProblem.None;
            return true;
        }

        if (IsYouTubeHost(text)) { return TryParseYouTube(text, out mediaId, out platform, out problem); }
        if (IsTikTokHost(text)) { return TryParseTikTok(text, out mediaId, out platform, out problem); }

        problem = MediaUrlProblem.NotSupported;
        return false;
    }

    private static bool TryParseYouTube(
        string text, out string? mediaId, out MediaPlatform platform, out MediaUrlProblem problem)
    {
        mediaId = null;
        platform = MediaPlatform.YouTube;
        problem = MediaUrlProblem.None;
        var hasList = Regex.IsMatch(text, @"[?&]list=", RegexOptions.IgnoreCase);
        var isChannel = Regex.IsMatch(text, @"youtube\.com/(?:@|channel/|c/|user/)", RegexOptions.IgnoreCase);
        var match = YouTubeIdInUrl.Match(text);
        if (!match.Success)
        {
            problem = hasList ? MediaUrlProblem.Playlist :
                isChannel ? MediaUrlProblem.Channel : MediaUrlProblem.NotSupported;
            return false;
        }

        if (hasList && !text.Contains("watch?v=", StringComparison.OrdinalIgnoreCase))
        {
            problem = MediaUrlProblem.Playlist;
            return false;
        }

        mediaId = match.Groups["id"].Value;
        return true;
    }

    private static bool TryParseTikTok(
        string text, out string? mediaId, out MediaPlatform platform, out MediaUrlProblem problem)
    {
        mediaId = null;
        platform = MediaPlatform.TikTok;
        problem = MediaUrlProblem.None;
        var match = TikTokIdInUrl.Match(text);
        if (match.Success)
        {
            mediaId = match.Groups["id"].Value;
            return true;
        }

        // A short link is a valid request with an unknown id: the service follows the redirect.
        if (TikTokShort.IsMatch(text)) { return true; }

        // "/@someone" with no video is a profile, which we do not download.
        problem = Regex.IsMatch(text, @"tiktok\.com/@[^/\s]+/?$", RegexOptions.IgnoreCase)
            ? MediaUrlProblem.Channel
            : MediaUrlProblem.NotSupported;
        return false;
    }

    private static string StripQuery(string text)
    {
        var cut = text.IndexOfAny(['?', '#']);
        return cut < 0 ? text : text[..cut];
    }

    private static bool IsYouTubeHost(string text) =>
        text.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsTikTokHost(string text) =>
        text.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The canonical page for an item, used when only an id is known.</summary>
    public static string WatchUrl(string mediaId, MediaPlatform platform) => platform switch
    {
        MediaPlatform.YouTube => $"https://www.youtube.com/watch?v={mediaId}",
        MediaPlatform.TikTok => $"https://www.tiktok.com/video/{mediaId}",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "No watch URL for this platform.")
    };

    /// <summary>
    /// A valid id becomes a file name in the cache, so anything else would be a path injection.
    /// Service output is filtered through this too.
    /// </summary>
    public static bool IsValidId(string? mediaId, MediaPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(mediaId)) { return false; }
        return platform switch
        {
            MediaPlatform.YouTube => YouTubeId.IsMatch(mediaId),
            MediaPlatform.TikTok => TikTokId.IsMatch(mediaId),
            _ => false
        };
    }

    /// <summary>True when the text looks like a link or an id rather than a search phrase.</summary>
    public static bool LooksLikeLink(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) { return false; }
        var text = input.Trim();
        return text.Contains("youtu", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("tiktok", StringComparison.OrdinalIgnoreCase) ||
               YouTubeId.IsMatch(text) ||
               TikTokId.IsMatch(text);
    }
}