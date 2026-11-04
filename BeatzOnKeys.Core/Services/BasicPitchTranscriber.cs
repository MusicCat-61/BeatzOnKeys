using System.Diagnostics;
using System.Text;
using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Services;

/// <summary>Starts Basic Pitch from the Python runtime shipped beside BeatzOnKeys.exe.</summary>
public sealed class BasicPitchTranscriber
{
    private readonly string _applicationDirectory;

    public BasicPitchTranscriber(string? applicationDirectory = null)
    {
        _applicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
    }

    public string PythonPath => Path.Combine(_applicationDirectory, "python", "python.exe");

    public string ScriptPath => Path.Combine(_applicationDirectory, "Tools", "transcribe.py");

    public async Task<string> TranscribeAsync(
        string sourcePath,
        string outputMidiPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputMidiPath);

        sourcePath = Path.GetFullPath(sourcePath);
        outputMidiPath = Path.GetFullPath(outputMidiPath);

        if (!File.Exists(sourcePath))
        {
            throw new TranscriptionException(
                TranscriptionFailureCode.SourceMissing,
                $"Input audio does not exist: {sourcePath}");
        }

        if (!File.Exists(PythonPath))
        {
            throw new TranscriptionException(
                TranscriptionFailureCode.PythonMissing,
                $"Bundled Python was not found: {PythonPath}");
        }

        if (!File.Exists(ScriptPath))
        {
            throw new TranscriptionException(
                TranscriptionFailureCode.PythonScriptMissing,
                $"Basic Pitch launcher was not found: {ScriptPath}");
        }

        var outputDirectory = Path.GetDirectoryName(outputMidiPath)
            ?? throw new TranscriptionException(TranscriptionFailureCode.MidiOutputMissing, "Invalid MIDI output path.");
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(outputMidiPath))
        {
            File.Delete(outputMidiPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = PythonPath,
            WorkingDirectory = _applicationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(ScriptPath);
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add(outputMidiPath);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";
        var pythonDirectory = Path.GetDirectoryName(PythonPath)!;
        startInfo.Environment["PATH"] = $"{pythonDirectory};{Environment.GetEnvironmentVariable("PATH") ?? string.Empty}";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new TranscriptionException(
                    TranscriptionFailureCode.ProcessStartFailed,
                    "The bundled Python process did not start.");
            }
        }
        catch (TranscriptionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TranscriptionException(
                TranscriptionFailureCode.ProcessStartFailed,
                exception.Message,
                exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        var processDetails = BuildProcessDetails(standardOutput, standardError);

        if (process.ExitCode != 0)
        {
            AppLogger.Error("transcription", $"Basic Pitch exited with {process.ExitCode}. {processDetails}");
            throw new TranscriptionException(
                TranscriptionFailureCode.BasicPitchFailed,
                string.IsNullOrWhiteSpace(processDetails)
                    ? $"Basic Pitch exited with code {process.ExitCode}."
                    : processDetails);
        }

        if (!File.Exists(outputMidiPath) || new FileInfo(outputMidiPath).Length == 0)
        {
            throw new TranscriptionException(
                TranscriptionFailureCode.MidiOutputMissing,
                string.IsNullOrWhiteSpace(processDetails)
                    ? "Basic Pitch did not create a MIDI file."
                    : processDetails);
        }

        AppLogger.Info("transcription", $"Created MIDI for '{sourcePath}' at '{outputMidiPath}'.");
        return outputMidiPath;
    }

    private static string BuildProcessDetails(string standardOutput, string standardError)
    {
        var text = string.Join(
            Environment.NewLine,
            new[] { standardError.Trim(), standardOutput.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
        const int maximumLength = 4000;
        return text.Length <= maximumLength ? text : text[^maximumLength..];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Cancellation still wins even if Windows has already ended the child process.
        }
    }
}
