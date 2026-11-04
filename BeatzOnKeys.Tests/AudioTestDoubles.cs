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
        double durationSeconds = 1)
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
        const short sample = 12_000;
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

    public void RaiseFailure(Exception exception)
    {
        EventHandler<StoppedEventArgs>? handler;
        lock (_gate) { handler = PlaybackStopped; }
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

internal sealed class FakeAudioFragmentPlayer : IAudioFragmentPlayer
{
    private readonly object _gate = new();
    private readonly List<PlaybackStep> _steps = [];
    private bool _disposed;
    private bool _isPlaying;
    private double _volume = 0.7;

    public event Action<Exception>? PlaybackFailed;

    public TimeSpan? LoadDurationOverride { get; set; }
    public bool ThrowOnPrepare { get; set; }
    public bool BlockPlay { get; set; }
    public ManualResetEventSlim PlayEntered { get; } = new(false);
    public ManualResetEventSlim PlayRelease { get; } = new(false);
    public int LoadCalls { get; private set; }
    public int PrepareCalls { get; private set; }
    public int PauseCalls { get; private set; }
    public int UnloadCalls { get; private set; }
    public bool IsDisposed { get { lock (_gate) { return _disposed; } } }
    public IReadOnlyList<PlaybackStep> Steps { get { lock (_gate) { return _steps.ToArray(); } } }

    public double Volume
    {
        get { lock (_gate) { return _volume; } }
        set { lock (_gate) { _volume = value; } }
    }

    public bool IsPlaying { get { lock (_gate) { return _isPlaying; } } }

    public TimeSpan LoadTrack(PlaybackTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LoadCalls++;
            return LoadDurationOverride ?? track.Duration;
        }
    }

    public void PrepareOutput()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PrepareCalls++;
            if (ThrowOnPrepare) { throw new InvalidOperationException("prepare failed"); }
        }
    }

    public void PlayFragment(PlaybackStep step)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _steps.Add(step);
            _isPlaying = true;
        }
        PlayEntered.Set();
        if (BlockPlay && !PlayRelease.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The fake audio player was not released by the test.");
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            PauseCalls++;
            _isPlaying = false;
        }
    }

    public void Unload()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            UnloadCalls++;
            _isPlaying = false;
        }
    }

    public void RaiseFailure(Exception exception) => PlaybackFailed?.Invoke(exception);

    public void Dispose()
    {
        lock (_gate)
        {
            _isPlaying = false;
            _disposed = true;
        }
        PlayEntered.Dispose();
        PlayRelease.Dispose();
    }
}
