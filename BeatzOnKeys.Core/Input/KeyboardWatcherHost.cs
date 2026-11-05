using System.Diagnostics;
using System.IO.Pipes;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.Input;

/// <summary>
/// Headless keyboard watcher (<c>BeatzOnKeys.exe --watcher</c>).
///
/// It exists so the low-level hook never lives inside the window process. A hook that stops
/// answering makes Windows silently drop input for the whole desktop, and an owner that is
/// busy repainting, decoding audio, or shutting down cannot answer in time. Keeping the hook
/// in its own short-lived process also means hiding or minimising the window cannot affect it.
/// Only the fact that a key went down crosses the pipe: no key codes, no text, and no window
/// titles are ever read.
/// </summary>
public static class KeyboardWatcherHost
{
    /// <summary>Runs the watcher until the app disconnects; returns a process exit code.</summary>
    public static int Run(string pipeName, int? parentProcessId)
    {
        using var stop = new CancellationTokenSource();
        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);

        try
        {
            client.Connect(5000);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            return 2; // The app is not waiting for us.
        }

        using var hook = new KeyboardHook();
        var queue = new Queue<byte>();
        var queueGate = new object();
        var signal = new SemaphoreSlim(0);

        hook.KeyDown += () => Enqueue(WatcherProtocol.KeyPress);
        // Proof of life: any delivered key, even a filtered or injected one, means Windows is
        // still routing input to this hook.
        hook.AnyKeyDown += () => Enqueue(WatcherProtocol.Heartbeat);

        void Enqueue(byte value)
        {
            lock (queueGate) { queue.Enqueue(value); }
            signal.Release();
        }

        try
        {
            hook.Install();
        }
        catch (Exception exception)
        {
            AppLogger.Error("input", "The keyboard watcher could not install the native hook.", exception);
            return 3;
        }

        using var writer = new BinaryWriter(client, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(WatcherProtocol.Ready);
        writer.Flush();

        var lifetime = WatchParentAsync(parentProcessId, stop, signal);
        var delivered = 0L;
        var reportedFirst = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (!signal.Wait(250)) { continue; }
                lock (queueGate)
                {
                    while (queue.Count > 0)
                    {
                        var value = queue.Dequeue();
                        writer.Write(value);
                        if (value == WatcherProtocol.Heartbeat)
                        {
                            delivered++;
                            if (!reportedFirst)
                            {
                                reportedFirst = true;
                                AppLogger.Info("input", "Keyboard watcher received its first native key event.");
                            }
                        }
                    }
                }
                writer.Flush();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The app went away, which is the normal way to stop.
        }
        finally
        {
            AppLogger.Info("input", $"Keyboard watcher stopping after {delivered} delivered key events.");
            try { hook.Uninstall(); } catch { /* shutdown */ }
            stop.Cancel();
            try { lifetime.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutdown */ }
        }

        return 0;
    }

    private static Task WatchParentAsync(int? parentProcessId, CancellationTokenSource stop, SemaphoreSlim signal) =>
        Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (parentProcessId is { } id && !IsAlive(id))
                {
                    stop.Cancel();
                    signal.Release();
                    return;
                }
                if (stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1))) { return; }
            }
        });

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }
}
