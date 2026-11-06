using System.Diagnostics;
using System.Text;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Input;
using BeatzOnKeys.Core.Models;
using NAudio.Wave;

namespace BeatzOnKeys.Tests;

internal static class AudioTestData
{
    public static string CreateConstantPcm16Wave(
        string directory,
        string fileName = "fixture.wav",
        int sampleRate = 8_000,
        int channels = 2,
        double durationSeconds = 1,
        short sample = 12_000)
    {
        var path = System.IO.Path.Combine(directory, fileName);
        var frames = checked((int)Math.Round(sampleRate * durationSeconds));
        var blockAlign = checked((short)(channels * sizeof(short)));
        var dataLength = checked(frames * blockAlign);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                writer.Write(sample);
            }
        }

        return path;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        AssertEx.True(condition(), message);
    }
}

internal sealed class FakeWavePlayer : IWavePlayer
{
    private readonly object _gate = new();
    private IWaveProvider? _provider;
    private EventHandler<StoppedEventArgs>? _pendingStopHandler;
    private PlaybackState _state;
    private bool _disposed;
    private float _volume = 1;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public int InitCalls { get; private set; }
    public int PlayCalls { get; private set; }
    public int PauseCalls { get; private set; }
    public int StopCalls { get; private set; }
    public bool DelayStopCompletion { get; set; }
    public ManualResetEventSlim StopCalled { get; } = new(false);
    public SynchronizationContext? InitSynchronizationContext { get; private set; }
    public int InitThreadId { get; private set; }
    public bool IsDisposed { get { lock (_gate) { return _disposed; } } }
    public IWaveProvider? Provider { get { lock (_gate) { return _provider; } } }

    public PlaybackState PlaybackState { get { lock (_gate) { return _state; } } }

    public WaveFormat OutputWaveFormat
    {
        get
        {
            lock (_gate)
            {
                return _provider?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);
            }
        }
    }

    public float Volume
    {
        get { lock (_gate) { return _volume; } }
        set
        {
            if (value is < 0 or > 1) { throw new ArgumentOutOfRangeException(nameof(value)); }
            lock (_gate) { _volume = value; }
        }
    }

    public void Init(IWaveProvider waveProvider)
    {
        ArgumentNullException.ThrowIfNull(waveProvider);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _provider = waveProvider;
            InitSynchronizationContext = SynchronizationContext.Current;
            InitThreadId = Environment.CurrentManagedThreadId;
            InitCalls++;
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = PlaybackState.Playing;
            PlayCalls++;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = PlaybackState.Paused;
            PauseCalls++;
        }
    }

    public void Stop()
    {
        EventHandler<StoppedEventArgs>? handler;
        var delayCompletion = false;
        lock (_gate)
        {
            if (_disposed) { return; }
            _state = PlaybackState.Stopped;
            StopCalls++;
            handler = PlaybackStopped;
            delayCompletion = DelayStopCompletion;
            if (delayCompletion) { _pendingStopHandler = handler; }
        }
        StopCalled.Set();
        if (!delayCompletion) { handler?.Invoke(this, new StoppedEventArgs(null)); }
    }

    public void CompletePendingStop()
    {
        EventHandler<StoppedEventArgs>? handler;
        lock (_gate)
        {
            handler = _pendingStopHandler;
            _pendingStopHandler = null;
        }
        handler?.Invoke(this, new StoppedEventArgs(null));
    }

    public void CompleteTrack()
    {
        EventHandler<StoppedEventArgs>? handler;
        lock (_gate) { _state = PlaybackState.Stopped; handler = PlaybackStopped; }
        handler?.Invoke(this, new StoppedEventArgs(null));
    }

    public void RaiseFailure(Exception exception)
    {
        EventHandler<StoppedEventArgs>? handler;
        lock (_gate) { _state = PlaybackState.Stopped; handler = PlaybackStopped; }
        handler?.Invoke(this, new StoppedEventArgs(exception));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _state = PlaybackState.Stopped;
            _disposed = true;
            _provider = null;
        }
    }
}

internal sealed class FakeKeyboardHook : IKeyboardHook
{
    private readonly object _gate = new();
    private bool _disposed;
    private bool _isInstalled;

    public event Action? KeyDown;

    public Exception? InstallException { get; set; }
    public int InstallCalls { get; private set; }
    public int UninstallCalls { get; private set; }
    public bool IsDisposed { get { lock (_gate) { return _disposed; } } }
    public bool IsInstalled { get { lock (_gate) { return _isInstalled; } } }

    public void Install()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            InstallCalls++;
            if (InstallException is not null) { throw InstallException; }
            _isInstalled = true;
        }
    }

    public void Uninstall()
    {
        lock (_gate)
        {
            UninstallCalls++;
            _isInstalled = false;
        }
    }

    public void RaiseKeyDown() => KeyDown?.Invoke();

    public void Dispose()
    {
        lock (_gate)
        {
            _isInstalled = false;
            _disposed = true;
        }
    }
}

internal sealed class FakeContinuousAudioPlayer : IContinuousAudioPlayer
{
    private readonly object _gate = new();
    private bool _disposed;
    private bool _started;
    private bool _isPlaying;
    private PlaybackTrack? _track;
    private int _keyPressCalls;
    private int _notifyCalls;
    public int NotifyCalls { get { lock (_gate) { return _notifyCalls; } } }

    public event Action<PlaybackFailureEventArgs>? PlaybackFailed;
    public event Action<PlaybackEndedEventArgs>? TrackEnded;
    private Guid _playbackId;
    public Guid PlaybackId { get { lock (_gate) { return _playbackId; } } }
    public double MinVolume { get; set; } = 0.2;
    public double MaxVolume { get; set; } = 1;
    public double IdleTimeout { get; set; } = 3;
    public bool PauseOnIdle { get; set; } = true;
    public bool PunchResponseEnabled { get; set; }
    public double BassReactiveGain { get; set; }
    public bool StereoPulseEnabled { get; set; }
    public bool AdaptiveVolumeEnabled { get; set; }
    public double EffectiveMinVolume => MinVolume;
    public bool IsIdlePaused => false;
    public bool IsPaused { get { lock (_gate) { return _isPaused; } } }
    public int PauseCalls { get; private set; }
    public int ResumeCalls { get; private set; }
    private bool _isPaused;
    public TimeSpan Position { get; set; }
    public TimeSpan Duration => LoadDurationOverride ?? _track?.Duration ?? TimeSpan.Zero;
    public TimeSpan? LoadDurationOverride { get; set; }
    public bool ThrowOnStart { get; set; }
    public bool BlockNotify { get; set; }
    public ManualResetEventSlim PlayEntered { get; } = new(false);
    public ManualResetEventSlim PlayRelease { get; } = new(false);
    public int LoadCalls { get; private set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int UnloadCalls { get; private set; }
    public int KeyPressCalls { get { lock (_gate) { return _keyPressCalls; } } }
    public bool IsDisposed { get { lock (_gate) { return _disposed; } } }
    public bool IsPlaying { get { lock (_gate) { return _isPlaying; } } }

    public TimeSpan LoadTrack(PlaybackTrack track)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LoadCalls++;
            _track = track;
            return Duration;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ThrowOnStart) { throw new IOException("start failed"); }
            StartCalls++;
            _playbackId = Guid.NewGuid();
            Position = TimeSpan.Zero;
            _started = _isPlaying = true;
            _isPaused = false;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started || _isPaused) { return; }
            PauseCalls++;
            _isPaused = true;
            _isPlaying = false;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started || !_isPaused) { return; }
            ResumeCalls++;
            _isPaused = false;
            _isPlaying = true;
        }
    }

    public void NotifyKeyPress()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _notifyCalls++;
            if (!_started) { return; }
            _keyPressCalls++;
        }
        PlayEntered.Set();
        if (BlockNotify && !PlayRelease.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The fake player was not released by the test.");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            StopCalls++;
            _playbackId = Guid.Empty;
            _started = _isPlaying = false;
            _isPaused = false;
        }
    }

    public void Unload()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            UnloadCalls++;
            _playbackId = Guid.Empty;
            _started = _isPlaying = false;
            _isPaused = false;
            _track = null;
        }
    }

    public void RaiseFailure(Exception exception, Guid? playbackId = null, Guid? trackId = null)
    {
        PlaybackFailureEventArgs failure;
        lock (_gate)
        {
            var id = playbackId ?? _playbackId;
            if (id == _playbackId) { _started = _isPlaying = false; }
            failure = new(trackId ?? _track?.Id, exception) { PlaybackId = id };
        }
        PlaybackFailed?.Invoke(failure);
    }

    public void RaiseTrackEnded(Guid? playbackId = null)
    {
        PlaybackEndedEventArgs ended;
        lock (_gate)
        {
            var id = playbackId ?? _playbackId;
            if (id == _playbackId) { _started = _isPlaying = false; }
            ended = new(_track?.Id, id);
        }
        TrackEnded?.Invoke(ended);
    }

    public void Dispose()
    {
        lock (_gate) { _started = _isPlaying = false; _disposed = true; }
        PlayEntered.Dispose();
        PlayRelease.Dispose();
    }
}

/// <summary>Advances deadlines without sleeps, real audio devices or user app-data.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<TestTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) { return _ticks; } }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state);
        lock (_gate) { _timers.Add(timer); }
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(double seconds)
    {
        TestTimer[] due;
        lock (_gate)
        {
            _ticks += TimeSpan.FromSeconds(seconds).Ticks;
            due = _timers.Where(timer => !timer.Disposed && timer.DueAt <= _ticks).ToArray();
            foreach (var timer in due) { timer.DueAt = long.MaxValue; }
        }
        foreach (var timer in due) { timer.Invoke(); }
    }

    public void FireStaleCallbacks()
    {
        TestTimer[] timers;
        lock (_gate) { timers = _timers.ToArray(); }
        foreach (var timer in timers) { timer.Invoke(); }
    }

    private sealed class TestTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt { get; set; } = long.MaxValue;
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (Disposed) { return false; }
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return true;
            }
        }
        public void Invoke() => callback(state);
        public void Dispose() { lock (clock._gate) { Disposed = true; DueAt = long.MaxValue; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
