using System.Threading.Channels;
using BeatzOnKeys.Core.Input;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.Audio;

/// <summary>Keeps the hook callback tiny and performs audio work on one cancellable worker.</summary>
public sealed class TypingPlaybackService : IAsyncDisposable
{
    private readonly IKeyboardHook _keyboardHook;
    private readonly PlaybackController _playbackController;
    private readonly IAudioFragmentPlayer _player;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _playbackGate = new();
    private Channel<byte>? _keyEvents;
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private Exception? _pendingFailure;
    private int _isRunning;
    private bool _disposed;

    public TypingPlaybackService(
        IKeyboardHook keyboardHook,
        PlaybackController playbackController,
        IAudioFragmentPlayer player)
    {
        _keyboardHook = keyboardHook;
        _playbackController = playbackController;
        _player = player;
        _keyboardHook.KeyDown += OnKeyDown;
        _player.PlaybackFailed += OnAudioFailed;
    }

    public event Action<PlaybackFailureEventArgs>? PlaybackFailed;
    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    /// <summary>Serialize UI selection and decoder changes with the key worker.</summary>
    public bool SelectTrack(Guid trackId, bool restart = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_playbackGate)
        {
            var track = _playbackController.GetTrack(trackId);
            if (track is null) { return false; }
            var duration = _player.LoadTrack(track);
            if (duration != track.Duration)
            {
                _playbackController.UpsertTrack(track with { Duration = duration });
            }
            return _playbackController.SelectTrack(trackId, restart);
        }
    }

    public void Deselect()
    {
        lock (_playbackGate)
        {
            _player.Unload();
            _playbackController.Deselect();
        }
    }

    public void Unload()
    {
        lock (_playbackGate) { _player.Unload(); }
    }

    // Install/uninstall is deliberately performed on the caller's UI thread, which pumps hook messages.
    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) { return; }
            if (_worker is not null) { await StopCoreAsync().ConfigureAwait(true); }
            lock (_playbackGate)
            {
                var track = _playbackController.CurrentTrack ??
                            throw new InvalidOperationException("Select a playable audio track first.");
                _player.LoadTrack(track);
                _player.PrepareOutput();
            }

            var events = Channel.CreateBounded<byte>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            var cancellation = new CancellationTokenSource();
            _keyEvents = events;
            _workerCancellation = cancellation;
            _pendingFailure = null;
            Volatile.Write(ref _isRunning, 1);
            _worker = Task.Run(() => ProcessKeysAsync(events.Reader, cancellation.Token));
            try
            {
                _keyboardHook.Install();
                AppLogger.Info("playback", "Global keyboard audio scrubbing started.");
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(true);
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try { await StopCoreAsync().ConfigureAwait(true); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var hadWorker = _worker is not null || IsRunning;
        Volatile.Write(ref _isRunning, 0);
        _keyboardHook.Uninstall();
        var events = _keyEvents;
        _keyEvents = null;
        _workerCancellation?.Cancel();
        events?.Writer.TryComplete();
        try
        {
            // Do not destroy the CTS/player until every previously queued key has been cancelled.
            if (_worker is not null) { await _worker.ConfigureAwait(true); }
        }
        finally
        {
            _worker = null;
            _workerCancellation?.Dispose();
            _workerCancellation = null;
            _pendingFailure = null;
            lock (_playbackGate) { _player.Pause(); }
        }
        if (hadWorker) { AppLogger.Info("playback", "Global keyboard audio scrubbing stopped."); }
    }

    private void OnKeyDown()
    {
        if (IsRunning) { _keyEvents?.Writer.TryWrite(0); }
    }

    private void OnAudioFailed(Exception exception)
    {
        if (!IsRunning) { return; }
        Interlocked.CompareExchange(ref _pendingFailure, exception, null);
        _keyEvents?.Writer.TryWrite(0);
    }

    private async Task ProcessKeysAsync(ChannelReader<byte> reader, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var _ in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref _pendingFailure, null) is { } audioFailure) { throw audioFailure; }
                lock (_playbackGate)
                {
                    if (!IsRunning || cancellationToken.IsCancellationRequested) { break; }
                    if (_playbackController.TryTakeNext(out var step) && step is not null)
                    {
                        _player.PlayFragment(step);
                    }
                    else
                    {
                        _player.Pause();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal Stop or shutdown.
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            try { lock (_playbackGate) { _player.Pause(); } }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null)
        {
            AppLogger.Error("playback", "Audio scrubbing stopped after a playback failure.", failure);
            try { PlaybackFailed?.Invoke(new PlaybackFailureEventArgs(_playbackController.SelectedTrackId, failure)); }
            catch (Exception exception) { AppLogger.Error("playback", "A failure subscriber failed.", exception); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_disposed) { return; }
            _disposed = true;
            try { await StopCoreAsync().ConfigureAwait(true); }
            finally
            {
                _keyboardHook.KeyDown -= OnKeyDown;
                _player.PlaybackFailed -= OnAudioFailed;
                _keyboardHook.Dispose();
                _player.Dispose();
            }
        }
        finally { _lifecycleGate.Release(); }
        GC.SuppressFinalize(this);
    }
}
