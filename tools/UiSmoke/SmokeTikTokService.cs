using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Text;
using System.IO;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.YouTube;

namespace BeatzOnKeys.UiSmoke;

/// <summary>
/// Answers like TikTok would, without the network: the API path returns an item, the media request
/// returns a real audio file so the row really becomes playable.
/// </summary>
internal sealed class SmokeTikTokService(string fixturePath) : ITikTokAudioService
{
    public static TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public static int Calls { get; private set; }

    public static void Reset() => Calls = 0;

    public async Task<TikTokAudioResult> DownloadAsync(
        string? mediaId, string originalUrl, TikTokAudioOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Calls++;
        if (Delay > TimeSpan.Zero) { await Task.Delay(Delay, cancellationToken).ConfigureAwait(false); }
        var id = mediaId ?? "7234567890123456789";
        Directory.CreateDirectory(options.MusicDirectory);
        var target = Path.Combine(options.MusicDirectory, id + ".mp3");
        if (!File.Exists(target)) { File.Copy(fixturePath, target, overwrite: true); }
        progress?.Report(1);
        return new TikTokAudioResult(id, "Smoke TikTok clip", target);
    }
}

/// <summary>A TikTok service that always refuses, so the localized failure text can be checked.</summary>
internal sealed class FailingTikTokService : ITikTokAudioService
{
    public Task<TikTokAudioResult> DownloadAsync(
        string? mediaId, string originalUrl, TikTokAudioOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken) =>
        throw new TikTokException(TikTokFailure.Restricted, "The smoke pretends TikTok said no.");
}
