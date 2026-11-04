using System.Diagnostics;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using NAudio.Wave;

namespace BeatzOnKeys.Core.Audio;

/// <summary>Low-latency NAudio playback of short, key-triggered slices of the original recording.</summary>
public sealed class AudioFragmentPlayer : IAudioFragmentPlayer
{
    private const int BufferMilliseconds = 20;
    private const int NumberOfBuffers = 2;
    private readonly object _gate = new();
    private readonly Func<IWavePlayer> _outputFactory;
    private readonly Timer _pauseTimer;
    private AudioFileReader? _reader;
    private FragmentSampleProvider? _provider;
    private IWavePlayer? _output;
    private PlaybackTrack? _loadedTrack;
    private double _volume = 0.7;
    private long _pauseDeadline;
    private bool _isPlaying;
    private bool _disposed;

    public AudioFragmentPlayer(Func<IWavePlayer>? outputFactory = null)
    {
        _outputFactory = outputFactory ?? (() => new WaveOut
        {
            BufferMilliseconds = BufferMilliseconds,
            NumberOfBuffers = NumberOfBuffers
        });
        _pauseTimer = new Timer(PauseAfterFragment, null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<Exception>? PlaybackFailed;

    public double Volume
    {
        get { lock (_gate) { return _volume; } }
        set
        {
            lock (_gate)
            {
                _volume = Math.Clamp(double.IsFinite(value) ? value : 0.7, 0, 1);
                if (_provider is not null) { _provider.Volume = _volume; }
            }
        }
    }

    public bool IsPlaying
    {
        get { lock (_gate) { return _isPlaying; } }
    }

    public TimeSpan LoadTrack(PlaybackTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return LoadTrackCore(track);
        }
    }

    private TimeSpan LoadTrackCore(PlaybackTrack track)
    {
        if (_loadedTrack?.Id == track.Id &&
            string.Equals(_loadedTrack.SourcePath, track.SourcePath, StringComparison.OrdinalIgnoreCase) &&
            _reader is not null)
        {
            return _reader.TotalTime;
        }

        // Open first: a bad new file must not destroy the previously loaded decoder.
        var reader = new AudioFileReader(track.SourcePath);
        if (reader.TotalTime <= TimeSpan.Zero)
        {
            reader.Dispose();
            throw new InvalidDataException("The audio file has no readable duration.");
        }
        try
        {
            CancelPauseCore();
            DisposeOutputCore();
            _reader?.Dispose();
            _reader = reader;
            _provider = new FragmentSampleProvider(reader) { Volume = _volume };
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

    public void PrepareOutput()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PrepareOutputCore();
        }
    }

    private void PrepareOutputCore()
    {
        if (_output is not null) { return; }
        var provider = _provider ?? throw new InvalidOperationException("Select an audio track first.");
        // WaveOut posts PlaybackStopped to its constructor's SynchronizationContext.
        // Use a worker context so stopping can be awaited even from the UI thread.
        _output = Task.Run(() =>
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                var output = _outputFactory();
                try
                {
                    output.PlaybackStopped += OnPlaybackStopped;
                    output.Init(provider);
                    return output;
                }
                catch
                {
                    output.PlaybackStopped -= OnPlaybackStopped;
                    output.Dispose();
                    throw;
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }).GetAwaiter().GetResult();
    }

    public void PlayFragment(PlaybackStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LoadTrackCore(step.Track);
            PrepareOutputCore();
            // Flush paused buffers on a new burst. While typing rapidly, keep the device running.
            if (_output!.PlaybackState == PlaybackState.Paused) { StopOutputCore(_output); }
            _volume = Math.Clamp(double.IsFinite(step.Volume) ? step.Volume : 0.7, 0, 1);
            _provider!.Volume = _volume;
            _provider.BeginFragment(step.PositionSeconds, step.FragmentSeconds);
            _output.Play();
            _isPlaying = true;

            // New keys extend this deadline. A queued callback from an old key rechecks it under the lock.
            var pauseAfter = TimeSpan.FromSeconds(step.FragmentSeconds) +
                             TimeSpan.FromMilliseconds(BufferMilliseconds * NumberOfBuffers);
            _pauseDeadline = Stopwatch.GetTimestamp() + (long)(pauseAfter.TotalSeconds * Stopwatch.Frequency);
            _pauseTimer.Change(pauseAfter, Timeout.InfiniteTimeSpan);
        }
    }

    private void PauseAfterFragment(object? state)
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (_disposed || !_isPlaying || _pauseDeadline == 0) { return; }
            var remaining = (_pauseDeadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
            if (remaining > 0)
            {
                _pauseTimer.Change(TimeSpan.FromSeconds(Math.Max(0.001, remaining)), Timeout.InfiniteTimeSpan);
                return;
            }
            try
            {
                _output?.Pause();
                _provider?.ClearFragment();
                _isPlaying = false;
                _pauseDeadline = 0;
            }
            catch (Exception exception)
            {
                _isPlaying = false;
                _pauseDeadline = 0;
                failure = exception;
            }
        }
        if (failure is not null) { PlaybackFailed?.Invoke(failure); }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            CancelPauseCore();
            // Stop flushes queued audio immediately, but the logical track cursor is retained.
            StopOutputCore(_output);
            _provider?.ClearFragment();
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

    private void UnloadCore()
    {
        CancelPauseCore();
        try { DisposeOutputCore(); }
        finally
        {
            _provider = null;
            _reader?.Dispose();
            _reader = null;
            _loadedTrack = null;
        }
    }

    private void CancelPauseCore()
    {
        _pauseDeadline = 0;
        _isPlaying = false;
        _pauseTimer.Change(Timeout.Infinite, Timeout.Infinite);
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
            // WaveOut.Stop resets the driver but does not join its playback thread.
            // A new Play before this event can revive that old thread and also start a second one.
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
        _output = null;
        if (output is null) { return; }
        output.PlaybackStopped -= OnPlaybackStopped;
        try { StopOutputCore(output); }
        finally { output.Dispose(); }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) { PlaybackFailed?.Invoke(args.Exception); }
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
                _pauseTimer.Dispose();
            }
        }
        GC.SuppressFinalize(this);
    }
}
