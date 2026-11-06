namespace BeatzOnKeys.Core.Audio;

/// <summary>A monotonic, sample-smooth gain envelope, independent of track position.</summary>
public sealed class VolumeEnvelope
{
    public const double AttackSeconds = 0.05;
    public const double HoldSeconds = 0.15;
    public const double ReleaseSeconds = 0.2;

    /// <summary>The punch attack used when the audio itself just hit a transient.</summary>
    public const double PunchAttackSeconds = 0.015;

    /// <summary>How long a stereo pulse shifts the balance after a key press.</summary>
    public const double PulseSeconds = 0.05;

    /// <summary>Balance offset of one stereo pulse, relative to the other channel.</summary>
    public const double PulsePan = 0.1;

    private const double MaxBassBoost = 0.3;
    private const double BassSmoothing = 0.35;

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
    private double _bassBoost;
    private double _pulseUntil;
    private int _pulseSign = -1;
    private bool _stereoPulseEnabled;

    public VolumeEnvelope(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _origin = _clock.GetTimestamp();
    }

    private double Now => _clock.GetElapsedTime(_origin, _clock.GetTimestamp()).TotalSeconds;

    /// <summary>When true, a key press briefly shifts the left/right balance.</summary>
    public bool StereoPulseEnabled
    {
        get { lock (_gate) { return _stereoPulseEnabled; } }
        set
        {
            lock (_gate)
            {
                _stereoPulseEnabled = value;
                if (!value) { _pulseUntil = 0; }
            }
        }
    }

    /// <summary>Current bass-reactive addition, exposed for diagnostics and tests.</summary>
    public double CurrentBassBoost { get { lock (_gate) { return _bassBoost; } } }

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
            _bassBoost = 0;
            _pulseUntil = 0;
        }
    }

    /// <summary>
    /// Raises the gain to the maximum. <paramref name="attackSeconds"/> lets a detected drum hit
    /// shorten the attack for this key press only; the default stays 50 ms.
    /// </summary>
    public void Trigger(bool instant = false, double attackSeconds = AttackSeconds)
    {
        lock (_gate)
        {
            var now = Now;
            var time = Math.Max(now, _renderTime);
            var attack = double.IsFinite(attackSeconds) && attackSeconds > 0 ? attackSeconds : AttackSeconds;
            var alreadyRising = !instant && _releaseAt is { } deadline && time < deadline && _target == _max;
            // A punch attack may restart a running ramp; a repeated key during the hold may not.
            if (!alreadyRising || attack < _rampDuration)
            {
                _from = instant ? _max : GainAt(time);
                _target = _max;
                _rampStart = time;
                _rampDuration = instant ? 0 : attack;
            }

            // Rapid keys extend the hold, not the in-progress attack.
            _releaseAt = Math.Max(time, now + HoldSeconds);
            if (_stereoPulseEnabled)
            {
                _pulseSign = -_pulseSign;
                _pulseUntil = time + PulseSeconds;
            }
        }
    }

    /// <summary>
    /// Adds a bass-reactive gain on top of the current envelope, smoothed across buffers so a
    /// kick drum cannot click, and never above the maximum level.
    /// </summary>
    public void SetBassBoost(double boost)
    {
        lock (_gate)
        {
            var target = Math.Clamp(double.IsFinite(boost) ? boost : 0, 0, MaxBassBoost);
            _bassBoost += (target - _bassBoost) * BassSmoothing;
            if (target <= 0 && _bassBoost < 0.002) { _bassBoost = 0; }
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
            var panning = _stereoPulseEnabled && channels == 2;
            for (var frame = 0; frame < frames; frame++)
            {
                var frameTime = time + frame / (double)sampleRate;
                var gain = GainAt(frameTime);
                var pan = panning && frameTime < _pulseUntil ? PulsePan * _pulseSign : 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var channelGain = pan == 0 ? gain : gain * (1 + (channel == 0 ? pan : -pan));
                    samples[frame * channels + channel] *= (float)channelGain;
                }
            }

            _renderTime = time + frames / (double)sampleRate;
        }
    }

    private double GainAt(double time)
    {
        var gain = _releaseAt is { } deadline && time >= deadline
            ? Ramp(RampGain(deadline), _min, deadline, ReleaseSeconds, time)
            : RampGain(time);
        if (_bassBoost > 0)
        {
            gain = Math.Min(gain + _bassBoost, _max);
        }

        return gain;
    }

    private double RampGain(double time) => Ramp(_from, _target, _rampStart, _rampDuration, time);
    private static double Ramp(double from, double target, double start, double duration, double time) =>
        duration <= 0 ? target : Math.Clamp(from + (target - from) * Math.Clamp((time - start) / duration, 0, 1), 0, 1);
}