using NAudio.Wave;

namespace BeatzOnKeys.Core.Audio;

/// <summary>Reads the original recording sequentially and applies per-sample gain.</summary>
internal sealed class ContinuousSampleProvider(AudioFileReader reader, VolumeEnvelope envelope) : ISampleProvider
{
    private readonly object _gate = new();
    public WaveFormat WaveFormat => reader.WaveFormat;
    public TimeSpan Position { get { lock (_gate) { return reader.CurrentTime; } } }
    public int Read(Span<float> buffer)
    {
        lock (_gate)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = reader.Read(buffer[read..]);
                if (count == 0) { break; }
                read += count;
            }
            envelope.Apply(buffer[..read], WaveFormat.Channels, WaveFormat.SampleRate);
            return read;
        }
    }
}
