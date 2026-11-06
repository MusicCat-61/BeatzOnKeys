using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using NAudio.Wave;

namespace BeatzOnKeys.Core.Audio;

/// <summary>One sequential decoder per run; keys change gain, never the audio position.</summary>
public sealed class ContinuousAudioPlayer : IContinuousAudioPlayer
{
    private readonly object _gate = new();
    private readonly Func<IWavePlayer> _outputFactory;
    private readonly TimeProvider _clock;
    private readonly ITimer _idleTimer;
    private readonly VolumeEnvelope _envelope;
    private AudioFileReader? _reader;
    private ContinuousSampleProvider? _provider;
    private IWavePlayer? _output;
    private EventHandler<StoppedEventArgs>? _outputStoppedHandler;
    private PlaybackTrack? _loadedTrack;
    private Guid _playbackId;
    private double _minVolume = AppSettings.DefaultMinVolume;
    private double _maxVolume = AppSettings.DefaultMaxVolume;
    private double _idleTimeout = AppSettings.DefaultIdleTimeout;
    private bool _pauseOnIdle = true;
    private bool _punchResponseEnabled;
    private double _bassReactiveGain;
    private bool _stereoPulseEnabled;
    private bool _adaptiveVolumeEnabled;
    private double? _adaptiveMinVolume;
    private long _lastActivity;
    private bool _started;
    private bool _isPlaying;
    private bool _isIdlePaused;
    private bool _isPaused;
    private bool _disposed;

    public ContinuousAudioPlayer(Func<IWavePlayer>? outputFactory = null, TimeProvider? clock = null)
    {
        // Sub-period WinMM buffers can underrun while PlaybackState still says Playing.
        // Two 50 ms buffers keep output steady without changing the 50/150/200 ms envelope.
        _outputFactory = outputFactory ?? (() => new WaveOut { BufferMilliseconds = 50, NumberOfBuffers = 2 });
        _clock = clock ?? TimeProvider.System;
        _envelope = new VolumeEnvelope(_clock);
        _idleTimer = _clock.CreateTimer(PauseAfterIdle, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<PlaybackFailureEventArgs>? PlaybackFailed;
    public event Action<PlaybackEndedEventArgs>? TrackEnded;
    public Guid PlaybackId { get { lock (_gate) { return _playbackId; } } }

    public double MinVolume
    {
        get { lock (_gate) { return _minVolume; } }
        set
        {
            lock (_gate)
            {
                _minVolume = Math.Clamp(double.IsFinite(value) ? value : AppSettings.DefaultMinVolume, 0, 0.5);
                _envelope.Configure(EffectiveMinVolumeCore, _maxVolume);
            }
        }
    }

    public double MaxVolume
    {
        get { lock (_gate) { return _maxVolume; } }
        set
        {
            lock (_gate)
            {
                _maxVolume = Math.Clamp(double.IsFinite(value) ? value : AppSettings.DefaultMaxVolume, 0, 1);
                _envelope.Configure(EffectiveMinVolumeCore, _maxVolume);
            }
        }
    }

    /// <summary>
    /// The quiet level actually in force: the adaptive value for this track when enabled, capped
    /// by the maximum. The user's slider keeps its own value in <see cref="MinVolume"/>.
    /// </summary>
    public double EffectiveMinVolume { get { lock (_gate) { return EffectiveMinVolumeCore; } } }

    private double EffectiveMinVolumeCore => Math.Min(_adaptiveMinVolume ?? _minVolume, _maxVolume);

    /// <summary>Shortens the attack to 15 ms when the track itself just hit a transient.</summary>
    public bool PunchResponseEnabled
    {
        get { lock (_gate) { return _punchResponseEnabled; } }
        set { lock (_gate) { _punchResponseEnabled = value; } }
    }

    /// <summary>Extra gain added while the 40–120 Hz band is loud, capped by the maximum.</summary>
    public double BassReactiveGain
    {
        get { lock (_gate) { return _bassReactiveGain; } }
        set
        {
            lock (_gate)
            {
                _bassReactiveGain = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 0.3);
                if (_provider is not null) { _provider.BassReactiveGain = _bassReactiveGain; }
            }
        }
    }

    /// <summary>Shifts the left/right balance for 50 ms after each key press.</summary>
    public bool StereoPulseEnabled
    {
        get { lock (_gate) { return _stereoPulseEnabled; } }
        set
        {
            lock (_gate)
            {
                _stereoPulseEnabled = value;
                _envelope.StereoPulseEnabled = value;
            }
        }
    }

    /// <summary>Lets the quiet level follow the measured loudness of the selected track.</summary>
    public bool AdaptiveVolumeEnabled
    {
        get { lock (_gate) { return _adaptiveVolumeEnabled; } }
        set
        {
            lock (_gate)
            {
                _adaptiveVolumeEnabled = value;
                RecomputeAdaptiveMinCore();
                _envelope.Configure(EffectiveMinVolumeCore, _maxVolume);
            }
        }
    }

    public double IdleTimeout
    {
        get { lock (_gate) { return _idleTimeout; } }
        set
        {
            lock (_gate)
            {
                _idleTimeout = Math.Clamp(double.IsFinite(value) ? value : AppSettings.DefaultIdleTimeout, 1, 10);
                if (!_disposed) { ArmIdleCore(); }
            }
        }
    }

    public bool PauseOnIdle
    {
        get { lock (_gate) { return _pauseOnIdle; } }
        set
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _pauseOnIdle = value;
                if (!value && _started && _isIdlePaused)
                {
                    _envelope.Reset();
                    _output!.Play();
                    _isPlaying = true;
                    _isIdlePaused = false;
                }
                ArmIdleCore();
            }
        }
    }

    public bool IsPlaying { get { lock (_gate) { return _isPlaying; } } }
    public bool IsIdlePaused { get { lock (_gate) { return _isIdlePaused; } } }
    public bool IsPaused { get { lock (_gate) { return _isPaused; } } }
    public TimeSpan Position { get { lock (_gate) { return _provider?.Position ?? TimeSpan.Zero; } } }
    public TimeSpan Duration { get { lock (_gate) { return _reader?.TotalTime ?? TimeSpan.Zero; } } }
    public double CurrentGain => _envelope.GetGain();

    public TimeSpan LoadTrack(PlaybackTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return LoadTrackCore(track);
        }
    }

    private TimeSpan LoadTrackCore(PlaybackTrack track, bool reload = false)
    {
        if (!reload && _loadedTrack?.Id == track.Id && _reader is not null &&
            string.Equals(_loadedTrack.SourcePath, track.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return _reader.TotalTime;
        }

        // Validate the new file before releasing a working decoder.
        if (!File.Exists(track.SourcePath))
        {
            throw new FileNotFoundException("The audio source file no longer exists.", track.SourcePath);
        }
        var reader = new AudioFileReader(track.SourcePath);
        if (reader.TotalTime <= TimeSpan.Zero)
        {
            reader.Dispose();
            throw new InvalidDataException("The audio file has no readable duration.");
        }
        try
        {
            CancelIdleCore();
            _playbackId = Guid.Empty;
            DisposeOutputCore();
            _reader?.Dispose();
            _reader = reader;
            _isPaused = false;
            _envelope.Reset();
            _provider = new ContinuousSampleProvider(reader, _envelope, _clock) { BassReactiveGain = _bassReactiveGain };
            RecomputeAdaptiveMinCore();
            _envelope.Configure(EffectiveMinVolumeCore, _maxVolume);
            _loadedTrack = track with { Duration = reader.TotalTime };
            AppLogger.Info("audio", $"Loaded '{track.SourcePath}' ({reader.TotalTime.TotalSeconds:F2} s).");
            return reader.TotalTime;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Every explicit Start begins at zero by reopening, without seeking.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var track = _loadedTrack ?? throw new InvalidOperationException("Select an audio track first.");
            LoadTrackCore(track, reload: _output is not null || (_provider?.Position ?? TimeSpan.Zero) > TimeSpan.Zero);
            _playbackId = Guid.NewGuid();
            PrepareOutputCore();
            _envelope.Reset();
            _lastActivity = _clock.GetTimestamp();
            _started = true;
            _isIdlePaused = false;
            _isPaused = false;
            _output!.Play();
            _isPlaying = true;
            ArmIdleCore();
        }
    }

    /// <summary>
    /// Silences the output without touching the decoder: the position survives, so
    /// <see cref="Resume"/> continues the same run instead of starting the track over.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started || _isPaused) { return; }
            try
            {
                // An idle pause already stopped the stream; converting it keeps the decoder put.
                if (!_isIdlePaused) { _output!.Pause(); }
            }
            catch (Exception exception)
            {
                AppLogger.Error("audio", "Could not pause the output device.", exception);
                return;
            }

            _isPlaying = false;
            _isPaused = true;
            _isIdlePaused = false;
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            AppLogger.Info("audio", $"Playback paused by the user at {_provider?.Position.TotalSeconds ?? 0:F2} s.");
        }
    }

    /// <summary>
    /// Continues from the exact sample the playback stopped at, whether the silence came from
    /// <see cref="Pause"/> or from the idle timer. The run itself is never recreated.
    /// </summary>
    public void Resume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) { return; }
            // Nothing to resume: either the audio is already running, or it never paused.
            if (!_isPaused && !_isIdlePaused) { return; }
            var resumedIdle = _isIdlePaused;
            try
            {
                _output!.Play();
            }
            catch (Exception exception)
            {
                AppLogger.Error("audio", "Could not resume the output device.", exception);
                return;
            }

            _isPlaying = true;
            _isPaused = false;
            _isIdlePaused = false;
            _lastActivity = _clock.GetTimestamp();
            ArmIdleCore();
            // Resuming with the mouse is not typing, so the volume stays at its quiet baseline
            // and the next keystroke is what raises it. A key press still resumes instantly loud.
            AppLogger.Info("audio", resumedIdle
                ? $"Playback resumed by the user after the idle pause at {_provider?.Position.TotalSeconds ?? 0:F2} s."
                : $"Playback resumed by the user at {_provider?.Position.TotalSeconds ?? 0:F2} s.");
        }
    }

    private void PrepareOutputCore()
    {
        if (_output is not null) { return; }
        var provider = _provider ?? throw new InvalidOperationException("Select an audio track first.");
        // Capture this run without taking _gate on the device's completion thread.
        var playbackId = _playbackId;
        EventHandler<StoppedEventArgs> stoppedHandler = (sender, args) =>
            ThreadPool.QueueUserWorkItem(_ => HandlePlaybackStopped(sender, args, playbackId));
        // WaveOut must not capture the UI context: Stop waits for its completion event.
        _output = Task.Run(() =>
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                var output = _outputFactory();
                try
                {
                    output.PlaybackStopped += stoppedHandler;
                    output.Init(provider);
                    return output;
                }
                catch
                {
                    output.PlaybackStopped -= stoppedHandler;
                    output.Dispose();
                    throw;
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }).GetAwaiter().GetResult();
        _outputStoppedHandler = stoppedHandler;
    }

    public void NotifyKeyPress()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) { return; }
            // An explicit pause is the user's decision; typing must not quietly undo it.
            if (_isPaused) { return; }
            _lastActivity = _clock.GetTimestamp();
            var punch = _punchResponseEnabled && _provider?.Analyzer.HitWithin(PunchDetectionWindow) == true;
            _envelope.Trigger(instant: _isIdlePaused,
                attackSeconds: punch ? VolumeEnvelope.PunchAttackSeconds : VolumeEnvelope.AttackSeconds);
            if (_isIdlePaused)
            {
                // Do not flush or replace the paused stream: resume precisely the same audio.
                _output!.Play();
                _isPlaying = true;
                _isIdlePaused = false;
                AppLogger.Info("audio", "Playback resumed by a key press after the idle pause.");
            }
            ArmIdleCore();
        }
    }

    /// <summary>An onset this recent still counts as a hit for the next key press.</summary>
    private static readonly TimeSpan PunchDetectionWindow = TimeSpan.FromMilliseconds(200);

    /// <summary>Tracks below this RMS get a lower quiet level, loud tracks a higher one.</summary>
    private const double QuietTrackRms = 0.1;
    private const double LoudTrackRms = 0.5;

    private void RecomputeAdaptiveMinCore()
    {
        _adaptiveMinVolume = null;
        if (!_adaptiveVolumeEnabled || _reader is null) { return; }
        var rms = MeasureTrackRms(_reader);
        _adaptiveMinVolume = rms < QuietTrackRms ? 0.1 : rms > LoudTrackRms ? 0.3 : null;
        AppLogger.Info("audio", $"Track loudness RMS={rms:F3}; adaptive minimum={(_adaptiveMinVolume?.ToString("F2") ?? "unchanged")}.");
    }

    /// <summary>
    /// Samples sixteen short windows across the file instead of decoding it twice, and restores the
    /// decoder to the start so Start still begins at zero.
    /// </summary>
    private static double MeasureTrackRms(AudioFileReader reader)
    {
        var total = reader.TotalTime.TotalSeconds;
        if (!double.IsFinite(total) || total <= 0) { return 0; }
        var format = reader.WaveFormat;
        var windowSeconds = Math.Min(0.25, total);
        var frames = Math.Max(1, (int)Math.Round(format.SampleRate * windowSeconds));
        var buffer = new float[frames * format.Channels];
        const int windows = 16;
        double power = 0;
        long samples = 0;
        try
        {
            for (var index = 0; index < windows; index++)
            {
                var start = Math.Clamp(total * (index + 0.5) / windows, 0, Math.Max(0, total - windowSeconds));
                reader.CurrentTime = TimeSpan.FromSeconds(start);
                var read = 0;
                while (read < buffer.Length)
                {
                    var count = ((ISampleProvider)reader).Read(buffer.AsSpan(read, buffer.Length - read));
                    if (count == 0) { break; }
                    read += count;
                }

                for (var sample = 0; sample < read; sample++) { power += buffer[sample] * buffer[sample]; }
                samples += read;
            }
        }
        finally
        {
            try { reader.CurrentTime = TimeSpan.Zero; }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // A non-seekable source keeps whatever position it reached; Start still reopens.
            }
        }

        return samples == 0 ? 0 : Math.Sqrt(power / samples);
    }

    private void ArmIdleCore()
    {
        if (!_started || !_pauseOnIdle || _isIdlePaused || _isPaused)
        {
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        var elapsed = _clock.GetElapsedTime(_lastActivity, _clock.GetTimestamp()).TotalSeconds;
        _idleTimer.Change(TimeSpan.FromSeconds(Math.Max(0, _idleTimeout - elapsed)), Timeout.InfiniteTimeSpan);
    }

    private void PauseAfterIdle(object? state)
    {
        PlaybackFailureEventArgs? failure = null;
        lock (_gate)
        {
            if (_disposed || !_started || !_pauseOnIdle || _isIdlePaused || _isPaused) { return; }
            // A callback queued before a new key or settings change cannot pause a newer run.
            var elapsed = _clock.GetElapsedTime(_lastActivity, _clock.GetTimestamp()).TotalSeconds;
            if (elapsed < _idleTimeout) { ArmIdleCore(); return; }
            try
            {
                _output!.Pause();
                _isPlaying = false;
                _isIdlePaused = true;
                _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                AppLogger.Info("audio", "Playback paused after idle timeout.");
            }
            catch (Exception exception)
            {
                CancelIdleCore();
                failure = new(_loadedTrack?.Id, exception) { PlaybackId = _playbackId };
            }
        }
        if (failure is not null) { PlaybackFailed?.Invoke(failure); }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            CancelIdleCore();
            _isPaused = false;
            _playbackId = Guid.Empty;
            StopOutputCore(_output);
        }
    }

    public void Unload()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            UnloadCore();
        }
    }

    private void CancelIdleCore()
    {
        _started = _isPlaying = _isIdlePaused = false;
        _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void UnloadCore()
    {
        CancelIdleCore();
        _playbackId = Guid.Empty;
        try { DisposeOutputCore(); }
        finally
        {
            _provider = null;
            _reader?.Dispose();
            _reader = null;
            _loadedTrack = null;
        }
    }

    private static void StopOutputCore(IWavePlayer? output)
    {
        if (output is null || output.PlaybackState == PlaybackState.Stopped) { return; }
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs> handler = (_, _) => stopped.TrySetResult(true);
        output.PlaybackStopped += handler;
        try
        {
            output.Stop();
            if (!stopped.Task.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("The audio output did not finish stopping within two seconds.");
            }
        }
        finally { output.PlaybackStopped -= handler; }
    }

    private void DisposeOutputCore()
    {
        var output = _output;
        var stoppedHandler = _outputStoppedHandler;
        _output = null;
        _outputStoppedHandler = null;
        if (output is null) { return; }
        output.PlaybackStopped -= stoppedHandler;
        try { StopOutputCore(output); }
        finally { output.Dispose(); }
    }

    private void HandlePlaybackStopped(object? sender, StoppedEventArgs args, Guid playbackId)
    {
        PlaybackFailureEventArgs? failure = null;
        PlaybackEndedEventArgs? ended = null;
        lock (_gate)
        {
            if (_disposed || !_started || playbackId != _playbackId || !ReferenceEquals(sender, _output)) { return; }
            CancelIdleCore();
            if (args.Exception is not null)
            {
                failure = new(_loadedTrack?.Id, args.Exception) { PlaybackId = playbackId };
            }
            else { ended = new(_loadedTrack?.Id, playbackId); }
        }
        if (failure is not null) { PlaybackFailed?.Invoke(failure); }
        else if (ended is not null) { TrackEnded?.Invoke(ended); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            try { UnloadCore(); }
            finally
            {
                _disposed = true;
                _idleTimer.Dispose();
            }
        }
        GC.SuppressFinalize(this);
    }
}
