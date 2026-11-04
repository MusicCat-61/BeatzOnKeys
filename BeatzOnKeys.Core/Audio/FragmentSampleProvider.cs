using NAudio.Wave;

namespace BeatzOnKeys.Core.Audio;

/// <summary>
/// Serializes decoder reads and seeks, and limits each trigger to an exact sample budget.
/// Silence keeps the output stream alive until the idle timer pauses it; it never advances the reader.
/// </summary>
internal sealed class FragmentSampleProvider(AudioFileReader reader) : ISampleProvider
{
    private readonly object _gate = new();
    private long _remainingFrames;
    private long _totalFrames;
    private long _readFrames;
    private readonly int _fadeFrames = Math.Max(1, reader.WaveFormat.SampleRate / 500);
    private readonly float[] _seekBuffer = new float[2048 * reader.WaveFormat.Channels];

    public WaveFormat WaveFormat => reader.WaveFormat;

    public double Volume
    {
        get { lock (_gate) { return reader.Volume; } }
        set { lock (_gate) { reader.Volume = (float)Math.Clamp(double.IsFinite(value) ? value : 0.7, 0, 1); } }
    }

    public void BeginFragment(double positionSeconds, double durationSeconds)
    {
        if (!double.IsFinite(positionSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }
        lock (_gate)
        {
            var targetPosition = Math.Clamp(positionSeconds, 0, reader.TotalTime.TotalSeconds);
            // Media Foundation can produce complete but silent priming blocks after an MP3 seek.
            // Decode a short pre-roll off the output path, then land at the requested sample.
            if (Math.Abs(reader.CurrentTime.TotalSeconds - targetPosition) > 1d / WaveFormat.SampleRate)
            {
                var seekPosition = Math.Max(0, targetPosition - 0.15);
                if (Math.Abs(reader.CurrentTime.TotalSeconds - seekPosition) > 1d / WaveFormat.SampleRate)
                {
                    reader.CurrentTime = TimeSpan.FromSeconds(seekPosition);
                }
                var framesToSkip = Math.Max(0L, (long)Math.Round(
                    (targetPosition - reader.CurrentTime.TotalSeconds) * WaveFormat.SampleRate));
                while (framesToSkip > 0)
                {
                    var frames = (int)Math.Min(framesToSkip, _seekBuffer.Length / WaveFormat.Channels);
                    var skipped = reader.Read(_seekBuffer.AsSpan(0, frames * WaveFormat.Channels));
                    if (skipped == 0) { break; }
                    framesToSkip -= skipped / WaveFormat.Channels;
                }
            }
            var availableSeconds = Math.Max(0, reader.TotalTime.TotalSeconds - reader.CurrentTime.TotalSeconds);
            _totalFrames = (long)Math.Ceiling(Math.Min(durationSeconds, availableSeconds) * WaveFormat.SampleRate);
            _remainingFrames = _totalFrames;
            _readFrames = 0;
        }
    }

    public void ClearFragment()
    {
        lock (_gate) { _remainingFrames = 0; }
    }

    public int Read(Span<float> buffer)
    {
        lock (_gate)
        {
            buffer.Clear();
            var channels = WaveFormat.Channels;
            var requestedFrames = buffer.Length / channels;
            var frames = (int)Math.Min(requestedFrames, _remainingFrames);
            if (frames == 0) { return buffer.Length; }

            var wantedSamples = frames * channels;
            var read = 0;
            // Compressed decoders may return a partial block even in the middle of a track.
            // Fill it completely; only a zero-length read means end-of-stream.
            while (read < wantedSamples)
            {
                var count = reader.Read(buffer.Slice(read, wantedSamples - read));
                if (count == 0) { break; }
                read += count;
            }
            var readFrames = read / channels;
            for (var frame = 0; frame < readFrames; frame++)
            {
                var absoluteFrame = _readFrames + frame;
                var gain = (float)Math.Min(1d, Math.Min(
                    (absoluteFrame + 1d) / _fadeFrames,
                    (_totalFrames - absoluteFrame) / (double)_fadeFrames));
                for (var channel = 0; channel < channels; channel++)
                {
                    buffer[frame * channels + channel] *= gain;
                }
            }
            _readFrames += readFrames;
            _remainingFrames = readFrames < frames ? 0 : _remainingFrames - readFrames;
            return buffer.Length;
        }
    }
}
