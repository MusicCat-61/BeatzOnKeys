namespace BeatzOnKeys.Core.Audio;

/// <summary>
/// Measures the audio about to be played: peaks for punch detection and a one-pole low-pass
/// estimate of the 40–120 Hz band that drives the bass-reactive gain. The analyzer only reads
/// the decoded samples; it never changes them.
/// </summary>
internal sealed class AudioAnalyzer
{
    /// <summary>A buffer counts as an onset when its peak grows by at least 30%.</summary>
    public const double OnsetGrowth = 1.3;

    /// <summary>Near-silence never counts as an onset, so track starts stay quiet.</summary>
    public const double OnsetFloor = 0.02;

    /// <summary>Bass energy above this level drives the reactive boost.</summary>
    public const double BassThreshold = 0.1;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly double _bassCutoffHz;
    private double _peak;
    private double _previousPeak;
    private double _lowPassState;
    private double _bassEnergy;
    private long _lastOnset;
    private bool _hasOnset;

    public AudioAnalyzer(TimeProvider? clock = null, double bassCutoffHz = 100)
    {
        _clock = clock ?? TimeProvider.System;
        _bassCutoffHz = bassCutoffHz > 0 ? bassCutoffHz : 100;
    }

    /// <summary>Largest absolute sample of the most recent buffer.</summary>
    public double Peak { get { lock (_gate) { return _peak; } } }

    /// <summary>RMS of the low-passed signal of the most recent buffer.</summary>
    public double BassEnergy { get { lock (_gate) { return _bassEnergy; } } }

    /// <summary>True when an onset was detected within the given window.</summary>
    public bool HitWithin(TimeSpan window)
    {
        lock (_gate)
        {
            if (!_hasOnset || window <= TimeSpan.Zero) { return false; }
            return _clock.GetElapsedTime(_lastOnset, _clock.GetTimestamp()) <= window;
        }
    }

    public void Process(ReadOnlySpan<float> samples, int channels, int sampleRate)
    {
        if (channels <= 0 || sampleRate <= 0) { return; }
        var frames = samples.Length / channels;
        if (frames == 0) { return; }
        var alpha = 1 - Math.Exp(-2 * Math.PI * _bassCutoffHz / sampleRate);
        double peak = 0;
        double bassPower = 0;
        lock (_gate)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                double mono = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var value = samples[frame * channels + channel];
                    mono += value;
                    var magnitude = Math.Abs(value);
                    if (magnitude > peak) { peak = magnitude; }
                }

                _lowPassState += alpha * (mono / channels - _lowPassState);
                bassPower += _lowPassState * _lowPassState;
            }

            _peak = peak;
            _bassEnergy = Math.Sqrt(bassPower / frames);
            if (_previousPeak >= OnsetFloor && peak >= OnsetFloor && peak > _previousPeak * OnsetGrowth)
            {
                _lastOnset = _clock.GetTimestamp();
                _hasOnset = true;
            }

            _previousPeak = peak;
        }
    }
}