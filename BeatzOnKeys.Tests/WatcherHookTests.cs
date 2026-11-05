using System.Diagnostics;
using System.IO.Pipes;
using BeatzOnKeys.Core.Input;

namespace BeatzOnKeys.Tests;

internal static class WatcherHookTests
{
    public static async Task KeyPressBytesSurfaceAsKeyDown()
    {
        var pipe = "BeatzOnKeys.Test.Watcher." + Guid.NewGuid().ToString("N");
        using var fake = new FakeWatcher(pipe);
        using var hook = new WatcherKeyboardHook(pipe, fake.Attach);
        var signals = 0;
        var alive = 0;
        hook.KeyDown += () => Interlocked.Increment(ref signals);
        hook.HookAlive += () => Interlocked.Increment(ref alive);

        hook.Install();
        AssertEx.True(hook.IsInstalled, "Install did not connect to the fake watcher.");

        await fake.WriteAsync([WatcherProtocol.Ready, WatcherProtocol.Heartbeat]);
        await AudioTestData.WaitUntilAsync(() => Volatile.Read(ref alive) == 1, TimeSpan.FromSeconds(3),
            "A non-filtered delivery did not raise HookAlive.");
        AssertEx.Equal(0, Volatile.Read(ref signals), "A liveness byte must not count as a typed key.");

        await fake.WriteAsync([WatcherProtocol.KeyPress, WatcherProtocol.KeyPress]);
        await AudioTestData.WaitUntilAsync(() => Volatile.Read(ref signals) == 2, TimeSpan.FromSeconds(3),
            "KeyPress bytes did not reach KeyDown.");
        AssertEx.Equal(1, Volatile.Read(ref alive), "Liveness must stay separate from key delivery.");
        AssertEx.True(hook.DeliveredKeyCount >= 2, "The delivery counter did not observe the key bytes.");

        hook.Uninstall();
        AssertEx.False(hook.IsInstalled, "Uninstall left the hook installed.");
        hook.Dispose();
        hook.Dispose();
    }

    public static Task InstallFailsWhenNoWatcherConnects()
    {
        var pipe = "BeatzOnKeys.Test.Watcher.None." + Guid.NewGuid().ToString("N");
        using var hook = new WatcherKeyboardHook(pipe, static (_, _) => StartIdleChild());
        AssertEx.Throws<TimeoutException>(hook.Install);
        AssertEx.False(hook.IsInstalled);
        hook.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A disposable stand-in for the watcher process. It must never be the test runner itself:
    /// cleanup kills the owned watcher.
    /// </summary>
    private static Process StartIdleChild()
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "ping", "-n", "120", "127.0.0.1" }
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the stand-in watcher.");
    }

    /// <summary>Plays the watcher side of the pipe without installing any native hook.</summary>
    private sealed class FakeWatcher(string pipeName) : IDisposable
    {
        private NamedPipeClientStream? _client;

        public Process? Attach(string executable, string[] arguments)
        {
            // The server is already listening by the time this is called.
            _client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
            _client.Connect(3000);
            // A disposable child stands in for the watcher; cleanup kills whatever it owns.
            return StartIdleChild();
        }

        public async Task WriteAsync(byte[] payload)
        {
            var client = _client ?? throw new InvalidOperationException("The fake watcher is not attached.");
            await client.WriteAsync(payload);
            await client.FlushAsync();
        }

        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
