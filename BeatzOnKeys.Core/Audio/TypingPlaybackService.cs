using System.Threading.Channels;
using BeatzOnKeys.Core.Input;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.Audio;

/// <summary>Keeps the global hook callback tiny and performs synthesis on a worker task.</summary>
public sealed class TypingPlaybackService : IDisposable
{
    private readonly KeyboardHook _keyboardHook;
    private readonly PlaybackController _playbackController;
    private readonly NoteSynthesizer _synthesizer;
    private Channel<byte>? _keyEvents;
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private bool _disposed;

    public TypingPlaybackService(
        KeyboardHook keyboardHook,
        PlaybackController playbackController,
        NoteSynthesizer synthesizer)
    {
        _keyboardHook = keyboardHook;
        _playbackController = playbackController;
        _synthesizer = synthesizer;
        _keyboardHook.KeyDown += OnKeyDown;
    }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return;
        }

        _synthesizer.Start();
        _keyEvents = Channel.CreateBounded<byte>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        _workerCancellation = new CancellationTokenSource();
        _worker = Task.Run(() => ProcessKeysAsync(_keyEvents.Reader, _workerCancellation.Token));

        try
        {
            _keyboardHook.Install();
            IsRunning = true;
            AppLogger.Info("playback", "Global keyboard playback started.");
        }
        catch
        {
            _workerCancellation.Cancel();
            _keyEvents.Writer.TryComplete();
            _keyEvents = null;
            _workerCancellation.Dispose();
            _workerCancellation = null;
            _worker = null;
            throw;
        }
    }

    public void Stop()
    {
        if (!IsRunning && _worker is null)
        {
            return;
        }

        _keyboardHook.Uninstall();
        IsRunning = false;
        _keyEvents?.Writer.TryComplete();
        _workerCancellation?.Cancel();
        _synthesizer.StopAll();
        _workerCancellation?.Dispose();
        _workerCancellation = null;
        _worker = null;
        _keyEvents = null;
        AppLogger.Info("playback", "Global keyboard playback stopped.");
    }

    private void OnKeyDown()
    {
        if (IsRunning)
        {
            _keyEvents?.Writer.TryWrite(0);
        }
    }

    private async Task ProcessKeysAsync(ChannelReader<byte> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_playbackController.TryTakeNext(out var step) && step is not null)
                {
                    _synthesizer.Play(step.Note, step.Volume, step.Speed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (Exception exception)
        {
            AppLogger.Error("playback", "Keyboard playback worker failed.", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _keyboardHook.KeyDown -= OnKeyDown;
        _keyboardHook.Dispose();
        _synthesizer.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
