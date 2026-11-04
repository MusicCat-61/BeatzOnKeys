using System.Threading.Channels;
using BeatzOnKeys.Core.Input;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.Audio;

/// <summary>The global hook only wakes a worker; gain/output changes stay off the input thread.</summary>
public sealed class TypingPlaybackService : IAsyncDisposable
{
    private readonly IKeyboardHook _keyboardHook;
    private readonly PlaybackController _playbackController;
    private readonly IContinuousAudioPlayer _player;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _playbackGate = new();
    private Channel<byte>? _keyEvents;
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private PlaybackFailureEventArgs? _pendingFailure;
    private long _sessionId;
    private int _pendingKey;
    private PlaybackEndedEventArgs? _pendingTrackEnd;
    private int _isRunning;
    private bool _disposed;

    public TypingPlaybackService(IKeyboardHook keyboardHook, PlaybackController playbackController, IContinuousAudioPlayer player)
    {
        _keyboardHook = keyboardHook;
        _playbackController = playbackController;
        _player = player;
        _keyboardHook.KeyDown += OnKeyDown;
        _player.PlaybackFailed += OnAudioFailed;
        _player.TrackEnded += OnTrackEnded;
    }

    public event Action<PlaybackFailureEventArgs>? PlaybackFailed;
    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;
    public long SessionId => Volatile.Read(ref _sessionId);

    public bool SelectTrack(Guid trackId, bool restart = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_playbackGate)
        {
            var track = _playbackController.GetTrack(trackId);
            if (track is null) { return false; }
            var duration = _player.LoadTrack(track);
            if (duration != track.Duration) { _playbackController.UpsertTrack(track with { Duration = duration }); }
            Interlocked.Exchange(ref _pendingFailure, null);
            Interlocked.Exchange(ref _pendingTrackEnd, null);
            if (!_playbackController.SelectTrack(trackId, restart)) { return false; }
            if (IsRunning) { _player.Start(); }
            return true;
        }
    }

    public void Deselect()
    {
        lock (_playbackGate)
        {
            Interlocked.Exchange(ref _pendingFailure, null);
            Interlocked.Exchange(ref _pendingTrackEnd, null);
            _player.Unload();
            _playbackController.Deselect();
        }
    }

    public void Unload()
    {
        lock (_playbackGate)
        {
            Interlocked.Exchange(ref _pendingFailure, null);
            Interlocked.Exchange(ref _pendingTrackEnd, null);
            _player.Unload();
        }
    }

    // Install/uninstall runs on the caller's UI thread, which pumps Windows hook messages.
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
                var track = _playbackController.CurrentTrack ?? throw new InvalidOperationException("Select a playable audio track first.");
                var duration = _player.LoadTrack(track);
                if (duration != track.Duration) { _playbackController.UpsertTrack(track with { Duration = duration }); }
            }

            // Coalesce bursts so delayed signals cannot prolong activity after typing has stopped.
            var events = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true,
                SingleWriter = false, AllowSynchronousContinuations = false
            });
            var cancellation = new CancellationTokenSource();
            _keyEvents = events;
            _workerCancellation = cancellation;
            _pendingFailure = null;
            _pendingKey = 0;
            _pendingTrackEnd = null;
            var sessionId = Interlocked.Increment(ref _sessionId);
            Volatile.Write(ref _isRunning, 1);
            _worker = Task.Run(() => ProcessEventsAsync(events.Reader, cancellation.Token, sessionId));
            try
            {
                _keyboardHook.Install();
                lock (_playbackGate) { _player.Start(); }
                AppLogger.Info("playback", "Continuous typing-reactive playback started.");
            }
            catch { await StopCoreAsync().ConfigureAwait(true); throw; }
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
        try { if (_worker is not null) { await _worker.ConfigureAwait(true); } }
        finally
        {
            _worker = null;
            _workerCancellation?.Dispose();
            _workerCancellation = null;
            _pendingFailure = null;
            _pendingKey = 0;
            _pendingTrackEnd = null;
            lock (_playbackGate) { _player.Stop(); }
        }
        if (hadWorker) { AppLogger.Info("playback", "Continuous typing-reactive playback stopped."); }
    }

    private void OnKeyDown()
    {
        if (!IsRunning) { return; }
        Interlocked.Exchange(ref _pendingKey, 1);
        _keyEvents?.Writer.TryWrite(0);
    }

    private void OnAudioFailed(PlaybackFailureEventArgs failure)
    {
        if (!IsRunning) { return; }
        lock (_playbackGate)
        {
            if (!IsRunning || failure.PlaybackId != _player.PlaybackId) { return; }
            Interlocked.CompareExchange(ref _pendingFailure, failure with { SessionId = SessionId }, null);
            _keyEvents?.Writer.TryWrite(0);
        }
    }

    private void OnTrackEnded(PlaybackEndedEventArgs ended)
    {
        if (!IsRunning) { return; }
        lock (_playbackGate)
        {
            if (!IsRunning || ended.PlaybackId != _player.PlaybackId) { return; }
            Interlocked.Exchange(ref _pendingTrackEnd, ended);
            _keyEvents?.Writer.TryWrite(0);
        }
    }

    private async Task ProcessEventsAsync(ChannelReader<byte> reader, CancellationToken cancellationToken, long sessionId)
    {
        PlaybackFailureEventArgs? failure = null;
        try
        {
            await foreach (var _ in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_playbackGate)
                {
                    if (!IsRunning || cancellationToken.IsCancellationRequested) { break; }
                    try
                    {
                        var pending = Interlocked.Exchange(ref _pendingFailure, null);
                        if (pending is not null && pending.PlaybackId == _player.PlaybackId)
                        {
                            failure = pending;
                            // Select cannot start another track between observing failure and stopping this worker.
                            Volatile.Write(ref _isRunning, 0);
                            break;
                        }
                        var ended = Interlocked.Exchange(ref _pendingTrackEnd, null);
                        var trackEnded = ended is not null && ended.PlaybackId == _player.PlaybackId;
                        var keyPressed = Interlocked.Exchange(ref _pendingKey, 0) != 0;
                        if (trackEnded && !_player.IsPlaying && !_player.IsIdlePaused &&
                            _playbackController.TryAdvanceAtEnd(out var next) && next is not null)
                        {
                            var duration = _player.LoadTrack(next);
                            if (duration != next.Duration) { _playbackController.UpsertTrack(next with { Duration = duration }); }
                            _player.Start();
                        }
                        if (keyPressed) { _player.NotifyKeyPress(); }
                    }
                    catch (Exception exception)
                    {
                        failure = new(_playbackController.SelectedTrackId, exception) { PlaybackId = _player.PlaybackId, SessionId = sessionId };
                        Volatile.Write(ref _isRunning, 0);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_playbackGate)
            {
                Volatile.Write(ref _isRunning, 0);
                failure = new(_playbackController.SelectedTrackId, exception) { PlaybackId = _player.PlaybackId, SessionId = sessionId };
            }
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            try { lock (_playbackGate) { _player.Stop(); } }
            catch (Exception exception)
            {
                failure ??= new(_playbackController.SelectedTrackId, exception) { PlaybackId = _player.PlaybackId, SessionId = sessionId };
            }
        }
        if (failure is not null)
        {
            AppLogger.Error("playback", "Continuous playback stopped after an audio failure.", failure.Exception);
            try { PlaybackFailed?.Invoke(failure); }
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
                _player.TrackEnded -= OnTrackEnded;
                _keyboardHook.Dispose();
                _player.Dispose();
            }
        }
        finally { _lifecycleGate.Release(); }
        GC.SuppressFinalize(this);
    }
}
