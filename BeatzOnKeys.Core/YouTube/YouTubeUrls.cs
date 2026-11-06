using System.Text.RegularExpressions;

namespace BeatzOnKeys.Core.YouTube;

/// <summary>Why an input cannot be downloaded: the app takes one video at a time.</summary>
public enum YouTubeUrlProblem
{
    None = 0,
    Empty = 1,
    NotYouTube = 2,
    Playlist = 3,
    Channel = 4
}

/// <summary>
/// Recognises the YouTube URL shapes a user may paste. Only a single video is accepted; playlists,
/// channels and short links that point at a list are rejected before any process is started.
/// </summary>
public static class YouTubeUrls
{
    private static readonly Regex VideoIdInUrl = new(
        @"(?:youtu\.be/|youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|embed/|live/))(?<id>[A-Za-z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareId = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    public static bool TryParse(string? input, out string videoId, out YouTubeUrlProblem problem)
    {
        videoId = string.Empty;
        problem = YouTubeUrlProblem.Empty;
        if (string.IsNullOrWhiteSpace(input)) { return false; }

        var text = input.Trim();
        if (BareId.IsMatch(text))
        {
            videoId = text;
            problem = YouTubeUrlProblem.None;
            return true;
        }

        var isYouTube = text.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);
        if (!isYouTube)
        {
            problem = YouTubeUrlProblem.NotYouTube;
            return false;
        }

        // "watch?v=…&list=…" opens a playlist; we only take single videos.
        var hasList = Regex.IsMatch(text, @"[?&]list=", RegexOptions.IgnoreCase);
        var isChannel = Regex.IsMatch(text, @"youtube\.com/(?:@|channel/|c/|user/)", RegexOptions.IgnoreCase);

        var match = VideoIdInUrl.Match(text);
        if (!match.Success)
        {
            problem = hasList ? YouTubeUrlProblem.Playlist :
                isChannel ? YouTubeUrlProblem.Channel : YouTubeUrlProblem.NotYouTube;
            return false;
        }

        if (hasList && !text.Contains("watch?v=", StringComparison.OrdinalIgnoreCase))
        {
            problem = YouTubeUrlProblem.Playlist;
            return false;
        }

        videoId = match.Groups["id"].Value;
        problem = YouTubeUrlProblem.None;
        return true;
    }

    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    /// <summary>
    /// An 11-character id is the only shape we accept: it becomes a file name in the cache, so
    /// anything else would be a path injection. Search output is filtered through this too.
    /// </summary>
    public static bool IsValidVideoId(string? videoId) => !string.IsNullOrWhiteSpace(videoId) && BareId.IsMatch(videoId);

    /// <summary>True when the text looks like a link or an id rather than a search phrase.</summary>
    public static bool LooksLikeLink(string? input) =>
        !string.IsNullOrWhiteSpace(input) &&
        (input.Contains("youtu", StringComparison.OrdinalIgnoreCase) || BareId.IsMatch(input.Trim()));
}