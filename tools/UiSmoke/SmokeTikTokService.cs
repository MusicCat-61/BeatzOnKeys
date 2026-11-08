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

    /// <summary>
    /// Refuses the next call with this kind and then behaves, so one view model can be shown both a
    /// provider refusal and the successful retry that clears the plate it raised.
    /// </summary>
    public static TikTokFailure? RefuseNext { get; set; }

    public static void Reset()
    {
        Calls = 0;
        RefuseNext = null;
    }

    public async Task<TikTokAudioResult> DownloadAsync(
        string? mediaId, string originalUrl, TikTokAudioOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Calls++;
        if (RefuseNext is { } refusal)
        {
            RefuseNext = null;
            throw new TikTokException(refusal, "The smoke refuses on purpose.");
        }

        if (Delay > TimeSpan.Zero) { await Task.Delay(Delay, cancellationToken).ConfigureAwait(false); }
        var id = mediaId ?? "7234567890123456789";
        Directory.CreateDirectory(options.MusicDirectory);
        var target = Path.Combine(options.MusicDirectory, id + ".mp3");
        if (!File.Exists(target)) { File.Copy(fixturePath, target, overwrite: true); }
        progress?.Report(1);
        return new TikTokAudioResult(id, "Снежинка Северянка — Вольный Дух", target);
    }
}

/// <summary>
/// A TikTok service that always refuses, so the localized failure text can be checked. The kind is
/// a parameter because one of them, a provider refusal, has to raise a plate in the interface.
/// </summary>
internal sealed class FailingTikTokService(TikTokFailure failure = TikTokFailure.Restricted) : ITikTokAudioService
{
    public Task<TikTokAudioResult> DownloadAsync(
        string? mediaId, string originalUrl, TikTokAudioOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken) =>
        throw new TikTokException(failure, "The smoke pretends TikTok said no.");
}
