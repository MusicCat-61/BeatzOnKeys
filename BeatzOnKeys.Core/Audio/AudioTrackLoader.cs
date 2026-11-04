using BeatzOnKeys.Core.Models;
using NAudio.Wave;

namespace BeatzOnKeys.Core.Audio;

/// <summary>Opens audio immediately and reads only enough data to validate the source.</summary>
public static class AudioTrackLoader
{
    public static PlaybackTrack Load(Guid id, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source audio file no longer exists.", sourcePath);
        }
        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only MP3 and WAV files are supported.");
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
}
