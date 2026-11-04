using BeatzOnKeys.Core.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatzOnKeys.Core.Audio;

/// <summary>A tiny polyphonic synth built on NAudio; no soundfont or external MIDI device is required.</summary>
public sealed class NoteSynthesizer : IDisposable
{
    private const int SampleRate = 44_100;
    private const int Channels = 2;
    private readonly object _gate = new();
    private WaveOut? _output;
    private MixingSampleProvider? _mixer;
    private bool _disposed;

    public bool IsStarted => _output is not null;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_output is not null)
            {
                return;
            }

            var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
            var mixer = new MixingSampleProvider(format) { ReadFully = true };
            var output = new WaveOut
            {
                BufferMilliseconds = 35,
                NumberOfBuffers = 3
            };
            try
            {
                output.Init(new SoftLimiterSampleProvider(mixer));
                output.Play();
                _mixer = mixer;
                _output = output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
    }

    public void Play(MusicalNote note, double volume, double speed)
    {
        ArgumentNullException.ThrowIfNull(note);
        Start();

        var adjustedDuration = note.Duration.TotalSeconds / Math.Clamp(speed, 0.5, 2);
        adjustedDuration = Math.Clamp(adjustedDuration, 0.07, 2.4);
        var velocity = 0.30 + (Math.Clamp(note.Velocity, (byte)1, (byte)127) / 127d * 0.70);
        var gain = Math.Clamp(volume, 0, 1) * velocity * 0.24;
        var provider = new SynthNoteSampleProvider(
            note.Frequency,
            adjustedDuration,
            gain,
            SampleRate,
            Channels);

        lock (_gate)
        {
            _mixer?.AddMixerInput(provider);
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            _mixer?.RemoveAllMixerInputs();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _mixer?.RemoveAllMixerInputs();
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _mixer = null;
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private sealed class SynthNoteSampleProvider : ISampleProvider
    {
        private readonly double _frequency;
        private readonly double _durationSeconds;
        private readonly float _gain;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly long _totalFrames;
        private long _frame;

        public SynthNoteSampleProvider(
            double frequency,
            double durationSeconds,
            double gain,
            int sampleRate,
            int channels)
        {
            _frequency = Math.Clamp(frequency, 20, 20_000);
            _durationSeconds = durationSeconds;
            _gain = (float)gain;
            _sampleRate = sampleRate;
            _channels = channels;
            _totalFrames = Math.Max(1, (long)Math.Ceiling(durationSeconds * sampleRate));
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            var requestedFrames = buffer.Length / _channels;
            var availableFrames = (int)Math.Min(requestedFrames, _totalFrames - _frame);
            if (availableFrames <= 0)
            {
                return 0;
            }

            var attackSeconds = Math.Min(0.012, _durationSeconds * 0.2);
            var releaseSeconds = Math.Clamp(_durationSeconds * 0.35, 0.045, 0.22);

            for (var frameOffset = 0; frameOffset < availableFrames; frameOffset++)
            {
                var time = (_frame + frameOffset) / (double)_sampleRate;
                var remaining = _durationSeconds - time;
                var attack = attackSeconds <= 0 ? 1 : Math.Min(1, time / attackSeconds);
                var release = Math.Clamp(remaining / releaseSeconds, 0, 1);
                var naturalDecay = 0.42 + (0.58 * Math.Exp(-2.2 * time / _durationSeconds));
                var phase = 2d * Math.PI * _frequency * time;
                var tone =
                    (0.78 * Math.Sin(phase)) +
                    (0.16 * Math.Sin((2d * phase) + 0.12)) +
                    (0.06 * Math.Sin((3d * phase) + 0.31));
                var sample = (float)(tone * attack * release * naturalDecay) * _gain;

                var sampleOffset = frameOffset * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    buffer[sampleOffset + channel] = sample;
                }
            }

            _frame += availableFrames;
            return availableFrames * _channels;
        }
    }

    private sealed class SoftLimiterSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            var read = source.Read(buffer);
            for (var index = 0; index < read; index++)
            {
                buffer[index] = MathF.Tanh(buffer[index]);
            }

            return read;
        }
    }
}
