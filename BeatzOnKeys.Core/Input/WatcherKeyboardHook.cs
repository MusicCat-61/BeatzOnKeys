using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace BeatzOnKeys.Core.Input;

/// <summary>
/// Wire contract between the app and its headless keyboard watcher.
/// The watcher is a separate process because a low-level keyboard hook that stops
/// answering makes Windows drop input for the whole desktop. Keeping it out of the
/// window process also means hiding, minimising, or a busy UI cannot silence typing.
/// </summary>
public static class WatcherProtocol
{
    public const string Switch = "--watcher";
    public const string ParentProcessVariable = "BEATZONKEYS_PARENT_PID";
    public const string PipeName = "BeatzOnKeys.Watcher.v2";

    /// <summary>One byte per delivered, non-modifier, non-injected key press.</summary>
    public const byte KeyPress = 0x01;

    /// <summary>Any delivered key, including filtered ones; proves the hook is still live.</summary>
    public const byte Heartbeat = 0x02;

    /// <summary>Sent once the watcher has installed the native hook.</summary>
    public const byte Ready = 0x03;
}

/// <summary>
/// Keyboard source backed by a separate watcher process. Install launches the watcher and
/// waits until it reports that the native hook is live; Uninstall closes the pipe, which
/// makes the watcher exit on its own.
/// </summary>
public sealed class WatcherKeyboardHook : IKeyboardHook
{
    private readonly object _gate = new();
    private readonly string _pipeName;
    private readonly Func<string, string[], Process?> _startWatcher;
    private NamedPipeServerStream? _server;
    private Process? _watcher;
    private CancellationTokenSource? _stop;
    private Task? _pump;
    private int _installed;
    private long _lastDeliveredTicks;
    private long _deliveredKeys;
    private bool _disposed;

    public WatcherKeyboardHook(string? pipeName = null, Func<string, string[], Process?>? startWatcher = null)
    {
        _pipeName = pipeName ?? WatcherProtocol.PipeName;
        _startWatcher = startWatcher ?? StartWatcherProcess;
    }

    public event Action? KeyDown;

    /// <summary>Raised when the watcher proves the native hook is still receiving input.</summary>
    public event Action? HookAlive;

    /// <summary>Number of deliveries the watcher reported, for diagnostics only.</summary>
    public long DeliveredKeyCount => Interlocked.Read(ref _deliveredKeys);

    /// <summary>Seconds since the watcher last proved the hook is live; null until it does.</summary>
    public TimeSpan? SinceLastDelivered
    {
        get
        {
            var ticks = Volatile.Read(ref _lastDeliveredTicks);
            if (ticks == 0) { return null; }
            return TimeSpan.FromSeconds((TimeProvider.System.GetTimestamp() - ticks) /
                (double)TimeProvider.System.TimestampFrequency);
        }
    }

    public bool IsInstalled => Volatile.Read(ref _installed) != 0;

    /// <summary>True when a watcher process is currently owned by this instance.</summary>
    public bool HasWatcher { get { lock (_gate) { return _watcher is { HasExited: false }; } } }

    public void Install()
    {
        NamedPipeServerStream server;
        CancellationTokenSource cancellation;
        Process? watcher = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsInstalled) { return; }

            cancellation = new CancellationTokenSource();
            server = new NamedPipeServerStream(
                _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _stop = cancellation;
            _server = server;

            try
            {
                watcher = _startWatcher(ResolveExecutablePath(),
                    [WatcherProtocol.Switch, WatcherProtocol.ParentProcessVariable + "=" +
                     Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                if (watcher is null)
                {
                    throw new InvalidOperationException("The keyboard watcher could not be started.");
                }

                _watcher = watcher;
                if (!WaitForConnection(server, cancellation.Token))
                {
                    throw new TimeoutException(
                        "The keyboard watcher did not become ready within five seconds. " +
                        "Keep the whole application folder together and run BeatzOnKeys from a writable location.");
                }

                Volatile.Write(ref _installed, 1);
            }
            catch
            {
                CleanupLocked();
                throw;
            }
        }

        _pump = Task.Run(() => PumpAsync(server, cancellation.Token));
    }

    private static bool WaitForConnection(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var connection = server.WaitForConnectionAsync(cancellationToken);
        try { return connection.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException exception) { throw exception.InnerException ?? exception; }
    }

    private async Task PumpAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var buffer = new byte[128];
        try
        {
            while (server.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var read = await server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0) { break; }
                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    if (value == WatcherProtocol.Ready)
                    {
                        BeatzOnKeys.Core.Services.AppLogger.Info("input", "Keyboard watcher reported the native hook is live.");
                        continue;
                    }
                    Volatile.Write(ref _lastDeliveredTicks, TimeProvider.System.GetTimestamp());
                    if (Interlocked.Increment(ref _deliveredKeys) == 1)
                    {
                        BeatzOnKeys.Core.Services.AppLogger.Info("input", "Keyboard watcher delivered its first key signal.");
                    }
                    if (value == WatcherProtocol.Heartbeat)
                    {
                        try { HookAlive?.Invoke(); }
                        catch { /* observers must not break the pipe */ }
                        continue;
                    }
                    if (value != WatcherProtocol.KeyPress || !IsInstalled) { continue; }
                    try { KeyDown?.Invoke(); }
                    catch { /* A subscriber must never break the pipe. */ }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Normal shutdown, or the watcher exited.
        }
        finally
        {
            // A dropped connection means typing no longer works; never claim otherwise.
            var wasInstalled = Volatile.Read(ref _installed) != 0;
            Volatile.Write(ref _installed, 0);
            if (wasInstalled)
            {
                BeatzOnKeys.Core.Services.AppLogger.Error("input",
                    "The keyboard watcher connection ended; typing will not reach playback until the next Start.");
            }
        }
    }

    public void Uninstall()
    {
        lock (_gate)
        {
            if (_watcher is null && _server is null) { return; }
            Volatile.Write(ref _installed, 0);
            CleanupLocked();
        }
    }

    private void CleanupLocked()
    {
        try { _stop?.Cancel(); } catch { /* disposed */ }
        try { _server?.Dispose(); } catch { /* disposed */ }
        _server = null;
        _stop?.Dispose();
        _stop = null;
        _pump = null;

        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) { return; }
        try
        {
            if (!watcher.HasExited) { watcher.Kill(entireProcessTree: true); }
        }
        catch { /* already gone */ }
        finally { watcher.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        try { Uninstall(); }
        finally
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// The watcher must always be the application executable, never a host such as a test
    /// runner that happens to load the same assemblies.
    /// </summary>
    private static string ResolveExecutablePath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "BeatzOnKeys.exe");
        if (File.Exists(beside)) { return beside; }
        return Environment.ProcessPath ?? "BeatzOnKeys.exe";
    }

    private static Process? StartWatcherProcess(string executable, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments) { startInfo.ArgumentList.Add(argument); }
        return Process.Start(startInfo);
    }
}
