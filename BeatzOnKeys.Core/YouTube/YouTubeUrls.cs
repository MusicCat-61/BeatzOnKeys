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
/// The YouTube-specific view of <see cref="MediaUrl"/>. Kept as a thin wrapper so every existing
/// caller and test keeps working while the recognition itself lives in one place for both services.
/// </summary>
public static class YouTubeUrls
{
    public static bool TryParse(string? input, out string videoId, out YouTubeUrlProblem problem)
    {
        videoId = string.Empty;
        var parsed = MediaUrl.TryParse(input, out var mediaId, out var platform, out var mediaProblem);
        if (!parsed)
        {
            problem = Map(mediaProblem);
            return false;
        }

        // A valid TikTok link is simply not a YouTube one as far as this API is concerned.
        if (platform != MediaPlatform.YouTube || mediaId is null)
        {
            problem = YouTubeUrlProblem.NotYouTube;
            return false;
        }

        videoId = mediaId;
        problem = YouTubeUrlProblem.None;
        return true;
    }

    private static YouTubeUrlProblem Map(MediaUrlProblem problem) => problem switch
    {
        MediaUrlProblem.Empty => YouTubeUrlProblem.Empty,
        MediaUrlProblem.Playlist => YouTubeUrlProblem.Playlist,
        MediaUrlProblem.Channel => YouTubeUrlProblem.Channel,
        _ => YouTubeUrlProblem.NotYouTube
    };

    public static string WatchUrl(string videoId) =>
        MediaUrl.WatchUrl(videoId, MediaPlatform.YouTube);

    /// <summary>
    /// An 11-character id is the only shape we accept: it becomes a file name in the cache, so
    /// anything else would be a path injection. Search output is filtered through this too.
    /// </summary>
    public static bool IsValidVideoId(string? videoId) =>
        MediaUrl.IsValidId(videoId, MediaPlatform.YouTube);

    /// <summary>True when the text looks like a link or an id rather than a search phrase.</summary>
    public static bool LooksLikeLink(string? input) => MediaUrl.LooksLikeLink(input);
}