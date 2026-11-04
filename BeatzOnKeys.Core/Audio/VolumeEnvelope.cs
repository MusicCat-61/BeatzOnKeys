namespace BeatzOnKeys.Core.Audio;

/// <summary>A monotonic, sample-smooth gain envelope, independent of track position.</summary>
public sealed class VolumeEnvelope
{
    public const double AttackSeconds = 0.05;
    public const double HoldSeconds = 0.15;
    public const double ReleaseSeconds = 0.2;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private double _min = 0.2;
    private double _max = 1;
    private double _from = 0.2;
    private double _target = 0.2;
    private double _rampStart;
    private double _rampDuration;
    private double? _releaseAt;
    private double _renderTime;

    public VolumeEnvelope(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _origin = _clock.GetTimestamp();
    }

    private double Now => _clock.GetElapsedTime(_origin, _clock.GetTimestamp()).TotalSeconds;

    public void Configure(double minVolume, double maxVolume)
    {
        lock (_gate)
        {
            var time = Math.Max(Now, _renderTime);
            var gain = GainAt(time);
            var held = _releaseAt is { } deadline && time < deadline;
            _max = Math.Clamp(double.IsFinite(maxVolume) ? maxVolume : 1, 0, 1);
            // MaxVolume is the master ceiling, even when its slider is below MinVolume.
            _min = Math.Min(Math.Clamp(double.IsFinite(minVolume) ? minVolume : 0.2, 0, 0.5), _max);
            _from = gain;
            _target = held ? _max : _min;
            _rampStart = time;
            _rampDuration = held ? AttackSeconds : ReleaseSeconds;
            if (!held) { _releaseAt = null; }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _from = _target = _min;
            _rampStart = _renderTime = Now;
            _rampDuration = 0;
            _releaseAt = null;
        }
    }

    public void Trigger(bool instant = false)
    {
        lock (_gate)
        {
            var now = Now;
            var time = Math.Max(now, _renderTime);
            var alreadyRising = !instant && _releaseAt is { } deadline && time < deadline && _target == _max;
            if (!alreadyRising)
            {
                _from = instant ? _max : GainAt(time);
                _target = _max;
                _rampStart = time;
                _rampDuration = instant ? 0 : AttackSeconds;
            }
            // Rapid keys extend the hold, not the in-progress attack.
            _releaseAt = Math.Max(time, now + HoldSeconds);
        }
    }

    public double GetGain() { lock (_gate) { return GainAt(Now); } }

    public void Apply(Span<float> samples, int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (samples.Length % channels != 0) { throw new ArgumentException("Samples must contain complete frames.", nameof(samples)); }
        lock (_gate)
        {
            // WaveOut can request consecutive buffers before wall-clock time has advanced.
            // Keep their sample times contiguous, so a rising/falling gain never jumps backwards.
            var time = Math.Max(Now, _renderTime);
            var frames = samples.Length / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var gain = (float)GainAt(time + frame / (double)sampleRate);
                for (var channel = 0; channel < channels; channel++) { samples[frame * channels + channel] *= gain; }
            }
            _renderTime = time + frames / (double)sampleRate;
        }
    }

    private double GainAt(double time)
    {
        if (_releaseAt is { } deadline && time >= deadline)
        {
            return Ramp(RampGain(deadline), _min, deadline, ReleaseSeconds, time);
        }
        return RampGain(time);
    }

    private double RampGain(double time) => Ramp(_from, _target, _rampStart, _rampDuration, time);
    private static double Ramp(double from, double target, double start, double duration, double time) =>
        duration <= 0 ? target : Math.Clamp(from + (target - from) * Math.Clamp((time - start) / duration, 0, 1), 0, 1);
}
