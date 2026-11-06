using System.Diagnostics;
using System.Text;

using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.YouTube;

/// <summary>
/// Runs yt-dlp as a child process. Both streams are drained line by line while the process runs:
/// reading only one of them deadlocks as soon as the other pipe fills up, which yt-dlp does on
/// every download because it reports progress continuously.
/// </summary>
public sealed class YouTubeProcessRunner : IYouTubeProcessRunner
{
    public async Task<YouTubeProcessResult> RunAsync(
        YouTubeProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in request.Arguments)
        {
            if (!string.IsNullOrEmpty(argument)) { startInfo.ArgumentList.Add(argument); }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {request.Executable}.");
        }

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var outputPump = PumpAsync(process.StandardOutput, standardOutput, request.OnStandardOutput);
        var errorPump = PumpAsync(process.StandardError, standardError, request.OnStandardError);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            KillTree(process);
        }

        // Always let the pumps finish so the caller sees everything the process wrote.
        await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);

        if (timedOut)
        {
            AppLogger.Warning("youtube", $"yt-dlp exceeded its {request.Timeout.TotalMinutes:F0} minute limit and was stopped.");
            return new YouTubeProcessResult(-1, standardOutput.ToString(), standardError.ToString(), TimedOut: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new YouTubeProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString(), TimedOut: false);
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, Action<string>? onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                sink.AppendLine(line);
                try { onLine?.Invoke(line); }
                catch (Exception exception) { AppLogger.Warning("youtube", $"A yt-dlp output handler failed: {exception.Message}"); }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The process was killed; whatever was already read stays in the sink.
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            AppLogger.Warning("youtube", $"Could not stop yt-dlp: {exception.Message}");
        }
    }
}