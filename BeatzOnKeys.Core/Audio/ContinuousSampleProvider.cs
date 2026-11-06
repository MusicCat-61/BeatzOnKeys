using NAudio.Wave;

namespace BeatzOnKeys.Core.Audio;

/// <summary>
/// Reads the original recording sequentially, measures it for the reactive features, and applies
/// per-sample gain plus the optional stereo pulse.
/// </summary>
internal sealed class ContinuousSampleProvider(AudioFileReader reader, VolumeEnvelope envelope, TimeProvider? clock = null) : ISampleProvider
{
    private readonly object _gate = new();
    private double _bassReactiveGain;

    /// <summary>Peak and bass measurements of the decoded stream, used by punch response.</summary>
    internal AudioAnalyzer Analyzer { get; } = new(clock);

    /// <summary>Zero disables the bass-reactive boost entirely.</summary>
    public double BassReactiveGain
    {
        get => Volatile.Read(ref _bassReactiveGain);
        set => Volatile.Write(ref _bassReactiveGain, value);
    }

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

            var decoded = buffer[..read];
            Analyzer.Process(decoded, WaveFormat.Channels, WaveFormat.SampleRate);
            var boost = BassReactiveGain;
            envelope.SetBassBoost(boost > 0 && Analyzer.BassEnergy >= AudioAnalyzer.BassThreshold ? boost : 0);
            envelope.Apply(decoded, WaveFormat.Channels, WaveFormat.SampleRate);
            return read;
        }
    }
}