using System.Text.Json;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.YouTube;

/// <summary>The download half of the YouTube service: resolve, fetch, verify, cache.</summary>
public sealed partial class YouTubeTrackService
{
    public async Task<YouTubeTrackState> DownloadAsync(
        string videoId,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            throw new YouTubeException(YouTubeFailure.NoResult, "A video id is required.");
        }

        videoId = videoId.Trim();
        var executable = RequireYtDlp();
        var options = Options;

        // Two slots only: more parallel requests is what gets a client rate limited.
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var workDirectory = Path.Combine(options.DownloadDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workDirectory);
            var metadata = await ResolveAsync(executable, videoId, options, cancellationToken).ConfigureAwait(false);
            if (metadata.IsLive)
            {
                throw new YouTubeException(YouTubeFailure.LiveStream, "Live streams cannot be saved as a file.");
            }

            var downloaded = await DownloadAudioAsync(executable, videoId, workDirectory, options, progress, cancellationToken)
                .ConfigureAwait(false);
            var cached = await PrepareCachedFileAsync(downloaded, videoId, options, cancellationToken).ConfigureAwait(false);
            progress?.Report(1d);

            return new YouTubeTrackState
            {
                VideoId = videoId,
                Title = metadata.Title,
                CachedPath = cached,
                Status = YouTubeTrackStatus.Ready
            };
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
            _slots.Release();
        }
    }

    private async Task<(string Title, bool IsLive)> ResolveAsync(
        string executable,
        string videoId,
        YouTubeOptions options,
        CancellationToken cancellationToken)
    {
        var arguments = BaseArguments(options);
        arguments.AddRange(["--dump-single-json", "--no-download", YouTubeUrls.WatchUrl(videoId)]);

        var result = await _runner.RunAsync(new YouTubeProcessRequest
        {
            Executable = executable,
            Arguments = arguments,
            Timeout = options.SearchTimeout
        }, cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new YouTubeException(YouTubeFailure.Timeout, "Resolving the video took too long.");
        }

        if (!result.Succeeded)
        {
            throw IsRestricted(result)
                ? new YouTubeException(YouTubeFailure.Restricted, Tail(result.StandardError, result.StandardOutput))
                : new YouTubeException(YouTubeFailure.NoResult, Tail(result.StandardError, result.StandardOutput));
        }

        using var document = JsonDocument.Parse(result.StandardOutput.Trim());
        var root = document.RootElement;
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? videoId : videoId;
        var live = (root.TryGetProperty("is_live", out var liveElement) && liveElement.ValueKind == JsonValueKind.True) ||
                   (root.TryGetProperty("live_status", out var statusElement) &&
                    string.Equals(statusElement.GetString(), "is_live", StringComparison.OrdinalIgnoreCase));
        return (title, live);
    }

    private async Task<string> DownloadAudioAsync(
        string executable,
        string videoId,
        string workDirectory,
        YouTubeOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var result = await RunDownloadAsync(executable, YouTubeUrls.WatchUrl(videoId), videoId, workDirectory, options, progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new YouTubeException(YouTubeFailure.Timeout,
                $"The download exceeded {options.DownloadTimeout.TotalMinutes:F0} minutes and was stopped.");
        }

        if (IsRestricted(result))
        {
            // One mirror is tried per instance; the first success wins.
            foreach (var instance in options.Mirrors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = InvidiousMirrors.WatchUrl(instance, videoId);
                AppLogger.Info("youtube", $"YouTube refused the download; retrying through {instance}.");
                var retry = await RunDownloadAsync(executable, url, videoId, workDirectory, options, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!retry.Succeeded) { continue; }
                var fromMirror = FindProducedFile(videoId, workDirectory, retry.StandardOutput);
                if (fromMirror is not null) { return fromMirror; }
            }

            throw new YouTubeException(YouTubeFailure.Restricted, Tail(result.StandardError, result.StandardOutput));
        }

        if (!result.Succeeded)
        {
            throw new YouTubeException(YouTubeFailure.DownloadFailed, Tail(result.StandardError, result.StandardOutput));
        }

        return FindProducedFile(videoId, workDirectory, result.StandardOutput)
               ?? throw new YouTubeException(YouTubeFailure.DownloadFailed,
                   "yt-dlp finished but no audio file appeared in the download folder.");
    }

    private Task<YouTubeProcessResult> RunDownloadAsync(
        string executable,
        string url,
        string videoId,
        string workDirectory,
        YouTubeOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = BaseArguments(options);
        arguments.AddRange([
            "-f", "bestaudio[ext=m4a]/bestaudio/best",
            "--no-part",
            "--newline",
            "--progress-template", ProgressTemplate,
            "--print", "after_move:filepath",
            "-o", Path.Combine(workDirectory, $"{videoId}.%(ext)s"),
            "--ffmpeg-location", FfmpegFolder(),
            url
        ]);

        return _runner.RunAsync(new YouTubeProcessRequest
        {
            Executable = executable,
            Arguments = arguments,
            Timeout = options.DownloadTimeout,
            OnStandardOutput = line => ReportProgress(line, progress)
        }, cancellationToken);
    }

    /// <summary>yt-dlp wants a folder it can find ffmpeg in; without it the argument is harmless.</summary>
    private string FfmpegFolder()
    {
        var ffmpeg = _binaries.FindFfmpeg();
        if (ffmpeg is not null) { return Path.GetDirectoryName(ffmpeg)!; }
        var ytDlp = _binaries.FindYtDlp();
        return ytDlp is null ? "." : Path.GetDirectoryName(ytDlp)!;
    }

    internal static void ReportProgress(string line, IProgress<double>? progress)
    {
        if (progress is null || !line.StartsWith(ProgressPrefix, StringComparison.Ordinal)) { return; }
        try
        {
            using var document = JsonDocument.Parse(line[ProgressPrefix.Length..]);
            var root = document.RootElement;
            var total = ReadLong(root, "total_bytes") ?? ReadLong(root, "total_bytes_estimate");
            var done = ReadLong(root, "downloaded_bytes") ?? 0;
            if (total is > 0) { progress.Report(Math.Clamp((double)done / total.Value, 0d, 1d)); }
        }
        catch (JsonException)
        {
            // A malformed progress line is not worth failing a download over.
        }
    }

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt64(out var value)
            ? value
            : null;

    internal static string? FindProducedFile(string videoId, string workDirectory, string standardOutput)
    {
        foreach (var line in standardOutput.Split('\n', StringSplitOptions.TrimEntries).Reverse())
        {
            if (line.Length > 2 && File.Exists(line)) { return line; }
        }

        if (!Directory.Exists(workDirectory)) { return null; }
        foreach (var file in Directory.EnumerateFiles(workDirectory, $"{videoId}.*"))
        {
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    /// Moves the downloaded audio into the cache, converting to WAV when the player cannot decode it
    /// here. That is the Windows-without-Media-Foundation path: M4A is unreadable there, WAV is not.
    /// </summary>
    private async Task<string> PrepareCachedFileAsync(
        string source,
        string videoId,
        YouTubeOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.MusicDirectory);
        var target = Path.Combine(options.MusicDirectory, videoId + Path.GetExtension(source));
        if (AudioTrackLoader.TryLoad(source, out _, out var error))
        {
            File.Move(source, target, overwrite: true);
            return target;
        }

        AppLogger.Warning("youtube", $"The downloaded audio cannot be decoded here ({error}); converting to WAV.");
        var ffmpeg = _binaries.FindFfmpeg() ??
                     await _binaries.EnsureFfmpegAsync(null, cancellationToken).ConfigureAwait(false);
        var converted = Path.Combine(Path.GetDirectoryName(source)!, videoId + ".wav");
        var result = await _runner.RunAsync(new YouTubeProcessRequest
        {
            Executable = ffmpeg,
            Arguments = ["-hide_banner", "-loglevel", "warning", "-y", "-i", source, "-vn",
                "-ar", "44100", "-c:a", "pcm_s16le", converted],
            Timeout = options.DownloadTimeout
        }, cancellationToken).ConfigureAwait(false);

        var convertedPlayable = false;
        string? secondError = null;
        if (result.Succeeded)
        {
            convertedPlayable = AudioTrackLoader.TryLoad(converted, out _, out secondError);
        }

        if (!convertedPlayable)
        {
            var detail = result.Succeeded ? secondError : Tail(result.StandardError, result.StandardOutput);
            throw new YouTubeException(YouTubeFailure.UnsupportedAudio,
                $"Neither the download nor its WAV conversion is playable here. {detail ?? error}");
        }

        var wavTarget = Path.Combine(options.MusicDirectory, videoId + ".wav");
        File.Move(converted, wavTarget, overwrite: true);
        return wavTarget;
    }
}