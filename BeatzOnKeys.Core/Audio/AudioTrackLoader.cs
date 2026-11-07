using BeatzOnKeys.Core.Models;
using NAudio.Wave;

namespace BeatzOnKeys.Core.Audio;

/// <summary>Opens audio immediately and reads only enough data to validate the source.</summary>
public static class AudioTrackLoader
{
    /// <summary>
    /// Extensions the player can decode. MP3 and WAV are read by NAudio directly; M4A goes through
    /// Media Foundation, which is what the YouTube cache stores.
    /// </summary>
    private static readonly string[] SupportedExtensions = [".mp3", ".wav", ".m4a"];

    public static bool IsSupported(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension)) { return false; }
        var extension = Path.GetExtension(pathOrExtension);
        if (string.IsNullOrEmpty(extension)) { extension = pathOrExtension.Trim(); }
        foreach (var supported in SupportedExtensions)
        {
            if (extension.Equals(supported, StringComparison.OrdinalIgnoreCase)) { return true; }
        }

        return false;
    }

    /// <summary>Human-readable list for error messages, e.g. ".mp3, .wav, .m4a".</summary>
    public static string SupportedList => string.Join(", ", SupportedExtensions);

    public static PlaybackTrack Load(Guid id, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source audio file no longer exists.", sourcePath);
        }
        if (!IsSupported(sourcePath))
        {
            throw new NotSupportedException($"Only {SupportedList} files are supported.");
        }

        using var reader = new AudioFileReader(sourcePath);
        if (reader.TotalTime <= TimeSpan.Zero)
        {
            throw new InvalidDataException("The audio file is empty or has no readable duration.");
        }
        Span<float> sample = stackalloc float[128];
        if (reader.Read(sample) == 0)
        {
            throw new InvalidDataException("The audio file has no decodable samples.");
        }
        return new PlaybackTrack(id, Path.GetFileName(sourcePath), sourcePath, reader.TotalTime);
    }

    /// <summary>
    /// Non-throwing probe used by the YouTube cache: it answers "will the player be able to open
    /// this file here?" without caring why not. On Windows without Media Foundation an M4A fails
    /// here, which is exactly the signal to fall back to a WAV conversion.
    /// </summary>
    public static bool TryLoad(string sourcePath, out PlaybackTrack? track, out string? error)
    {
        track = null;
        error = null;
        try
        {
            track = Load(Guid.NewGuid(), sourcePath);
            return true;
        }
        catch (Exception exception)
        {
            // A corrupt or unsupported file can fail in ways NAudio does not wrap in IOException:
            // Media Foundation throws its own type for an M4A it cannot open. This method is a
            // probe by contract, so every failure must become "no" instead of escaping.
            error = exception.Message;
            return false;
        }
    }
}