using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using BeatzOnKeys.Core.Input;

namespace BeatzOnKeys.Tests;

internal static class KeyboardHookTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    public static Task NativeLifecycleDoesNotNeedCallerPump()
    {
        // Real Win32 installation/message loop, but no generated or recorded user input.
        using var hook = new KeyboardHook();
        var previous = SynchronizationContext.Current;
        var context = new RejectingSynchronizationContext();
        Thread? first = null;
        Thread? second = null;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            AssertEx.False(hook.IsInstalled);
            hook.Uninstall();
            hook.Install();
            first = GetOwnerThread(hook);
            AssertEx.True(hook.IsInstalled);
            AssertEx.True(first.IsAlive && first.IsBackground);
            AssertEx.Equal(ApartmentState.STA, first.GetApartmentState());
            AssertEx.False(ReferenceEquals(Thread.CurrentThread, first));
            hook.Install();
            AssertEx.True(ReferenceEquals(first, GetOwnerThread(hook)), "An idempotent Install created another owner.");
            hook.Uninstall();
            AssertEx.False(hook.IsInstalled);
            hook.Uninstall();
            hook.Install();
            second = GetOwnerThread(hook);
            AssertEx.False(ReferenceEquals(first, second), "Restart reused a terminated message loop.");
            hook.Dispose();
            hook.Dispose();
            hook.Uninstall();
            AssertEx.False(hook.IsInstalled);
            AssertEx.Throws<ObjectDisposedException>(hook.Install);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            hook.Dispose();
            if (first is not null) { AssertEx.True(first.Join(TestTimeout), "The first native owner leaked."); }
            if (second is not null) { AssertEx.True(second.Join(TestTimeout), "The restarted native owner leaked."); }
        }
        AssertEx.Equal(0, context.Calls, "Lifecycle used the caller's synchronization context.");
        return Task.CompletedTask;
    }

    public static Task ContendedGateNeverPumpsTheCallerContext()
    {
        // The reported failure: on a loaded CI runner Monitor.Enter took its slow path while the
        // gate was contended, and that path calls SynchronizationContext.Wait — which pumps the
        // message queue on a thread that has one. Contention is forced here rather than hoped
        // for: the sequential test above passed on every local run and failed on the runner, so
        // "it passed" was never evidence.
        using var native = new FakeNativeApi();
        using var hook = Create(native);
        // Holding the gate is what makes contention certain; the type is the gate the hook uses.
        var gate = (NonPumpingGate)typeof(KeyboardHook)
            .GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hook)!;
        using var holderInside = new ManualResetEventSlim();
        using var letHolderGo = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (gate.Enter())
            {
                holderInside.Set();
                letHolderGo.Wait(TestTimeout);
            }
        }) { IsBackground = true };
        holder.Start();
        AssertEx.True(holderInside.Wait(TestTimeout), "The helper thread never took the gate.");

        var context = new RejectingSynchronizationContext();
        Exception? failure = null;
        var installer = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { hook.Install(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        installer.Start();
        // Long enough for the installer to reach the gate and be turned away by it.
        Thread.Sleep(250);
        letHolderGo.Set();
        AssertEx.True(installer.Join(TestTimeout), "Install never returned after the gate was released.");
        AssertEx.True(holder.Join(TestTimeout), "The helper thread leaked.");
        hook.Uninstall();

        AssertEx.True(failure is null, $"Install failed: {failure}");
        AssertEx.Equal(0, context.Calls, "A contended lifecycle pumped the caller's context.");
        return Task.CompletedTask;
    }

    public static async Task StartupWaitsForQueueAndNativeInstallation()
    {
        using var native = new FakeNativeApi();
        native.QueueRelease.Reset();
        native.InstallRelease.Reset();
        using var hook = Create(native);
        var install = Task.Run(hook.Install);
        try
        {
            await native.QueueEntered.Task.WaitAsync(TestTimeout);
            AssertEx.False(hook.IsInstalled);
            AssertEx.False(install.IsCompleted);
            AssertEx.Equal(0, native.InstallCalls);
            native.QueueRelease.Set();
            await native.InstallEntered.Task.WaitAsync(TestTimeout);
            AssertEx.False(hook.IsInstalled, "Readiness was published before native installation completed.");
            AssertEx.False(install.IsCompleted);
            native.InstallRelease.Set();
            await install.WaitAsync(TestTimeout);
            AssertEx.True(hook.IsInstalled);
            AssertEx.Null(native.LatestOwner.Context, "The owner inherited a synchronization context.");
            AssertEx.Equal(ApartmentState.STA, native.LatestOwner.Thread.GetApartmentState());
            await native.InvokeAsync(() => 7).WaitAsync(TestTimeout);
            AssertEx.True(native.TranslateCalls > 0 && native.DispatchCalls > 0);
        }
        finally
        {
            native.QueueRelease.Set();
            native.InstallRelease.Set();
            try { await install.WaitAsync(TestTimeout); }
            finally { hook.Dispose(); }
        }
        AssertEx.Equal(1, native.InstallCalls);
        AssertEx.Equal(1, native.UninstallCalls);
    }

    public static Task InstallationFailurePropagatesAndCanRetry()
    {
        using var native = new FakeNativeApi();
        using var hook = Create(native);
        var failure = new IOException("Controlled native installation failure.");
        native.InstallException = failure;
        var observed = AssertEx.Throws<IOException>(hook.Install);
        AssertEx.True(ReferenceEquals(failure, observed), "The installation error was replaced or swallowed.");
        AssertEx.False(hook.IsInstalled);
        AssertEx.True(native.LatestOwner.Thread.Join(TestTimeout));
        hook.Uninstall();
        AssertEx.Equal(0, native.UninstallCalls, "A failed installation attempted to release an absent handle.");
        native.InstallException = null;
        native.ReturnZeroHandle = true;
        AssertEx.Throws<InvalidOperationException>(hook.Install);
        AssertEx.False(hook.IsInstalled);
        AssertEx.True(native.LatestOwner.Thread.Join(TestTimeout));
        native.ReturnZeroHandle = false;
        hook.Install();
        AssertEx.True(hook.IsInstalled);
        hook.Uninstall();
        AssertEx.Equal(3, native.InstallCalls);
        AssertEx.Equal(1, native.UninstallCalls);
        return Task.CompletedTask;
    }

    public static async Task ConcurrentLifecycleKeepsOneNativeOwner()
    {
        using var native = new FakeNativeApi();
        using var hook = Create(native);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(hook.Install))).WaitAsync(TestTimeout);
        AssertEx.True(hook.IsInstalled);
        AssertEx.Equal(1, native.InstallCalls);
        var owner = native.LatestOwner;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(hook.Uninstall))).WaitAsync(TestTimeout);
        AssertEx.False(hook.IsInstalled);
        AssertEx.Equal(1, native.UninstallCalls);
        AssertEx.True(owner.Thread.Join(TestTimeout));
        AssertEx.Equal(1, native.MaximumActiveHooks);
        hook.Install();
        hook.Dispose();
        AssertEx.Equal(2, native.InstallCalls);
        AssertEx.Equal(2, native.UninstallCalls);
        AssertEx.Equal(1, native.MaximumActiveHooks);
    }

    public static async Task StopBeforeQueueReadinessCancelsStartup()
    {
        using var native = new FakeNativeApi();
        native.QueueRelease.Reset();
        using var hook = Create(native);
        var install = Task.Run(() => AssertEx.Throws<OperationCanceledException>(hook.Install));
        Task? stop = null;
        try
        {
            await native.QueueEntered.Task.WaitAsync(TestTimeout);
            // A callback is not required: stopping must also work before a queue exists.
            stop = Task.Run(hook.Uninstall);
            await AudioTestData.WaitUntilAsync(() => IsStopping(hook), TestTimeout, "Stop did not mark the starting session.");
            AssertEx.False(hook.IsInstalled);
            AssertEx.False(native.LatestOwner.QuitPosted.Task.IsCompleted, "Posted to a thread before its queue existed.");
            native.QueueRelease.Set();
            await Task.WhenAll(install, stop).WaitAsync(TestTimeout);
            AssertEx.Equal(0, native.InstallCalls);
            AssertEx.Equal(0, native.UninstallCalls);
            AssertEx.True(native.LatestOwner.Thread.Join(TestTimeout));
        }
        finally
        {
            native.QueueRelease.Set();
            if (stop is not null) { await stop.WaitAsync(TestTimeout); }
            await install.WaitAsync(TestTimeout);
        }
    }

    public static async Task DisposeWinsRacingInstallation()
    {
        using var native = new FakeNativeApi();
        native.InstallRelease.Reset();
        using var hook = Create(native);
        var install = Task.Run(() => AssertEx.Throws<ObjectDisposedException>(hook.Install));
        Task? dispose = null;
        try
        {
            await native.InstallEntered.Task.WaitAsync(TestTimeout);
            var owner = native.LatestOwner;
            dispose = Task.Run(hook.Dispose);
            await owner.QuitPosted.Task.WaitAsync(TestTimeout);
            AssertEx.False(hook.IsInstalled);
            AssertEx.Throws<ObjectDisposedException>(hook.Install);
            native.InstallRelease.Set();
            await Task.WhenAll(install, dispose).WaitAsync(TestTimeout);
            AssertEx.True(owner.Thread.Join(TestTimeout));
            AssertEx.False(hook.IsInstalled);
            AssertEx.Equal(1, native.UninstallCalls, "A handle returned after Dispose was leaked.");
            AssertEx.Null(owner.Callback, "The fake native boundary retained the unhooked delegate.");
            hook.Dispose();
            hook.Uninstall();
            AssertEx.Throws<ObjectDisposedException>(hook.Install);
        }
        finally
        {
            native.InstallRelease.Set();
            if (dispose is not null) { await dispose.WaitAsync(TestTimeout); }
            await install.WaitAsync(TestTimeout);
        }
    }

    public static async Task StartupTimeoutCleansUpLateHandle()
    {
        using var native = new FakeNativeApi();
        native.InstallRelease.Reset();
        using var hook = Create(native, TimeSpan.FromMilliseconds(350));
        var clock = Stopwatch.StartNew();
        var install = Task.Run(() => AssertEx.Throws<TimeoutException>(hook.Install));
        try
        {
            await native.InstallEntered.Task.WaitAsync(TestTimeout);
            await install.WaitAsync(TestTimeout);
            AssertEx.True(clock.Elapsed < TestTimeout, "Install did not respect its bounded wait.");
            AssertEx.False(hook.IsInstalled);
        }
        finally { native.InstallRelease.Set(); }
        await native.LatestOwner.Unhooked.Task.WaitAsync(TestTimeout);
        AssertEx.True(native.LatestOwner.Thread.Join(TestTimeout));
        AssertEx.Equal(1, native.UninstallCalls);
        hook.Install();
        AssertEx.True(hook.IsInstalled, "A timed-out session prevented later recovery.");
        hook.Uninstall();
        AssertEx.Equal(2, native.UninstallCalls);
    }

    public static async Task CallbackCanStopOrDisposeWithoutSelfWait()
    {
        foreach (var dispose in new[] { false, true })
        {
            using var native = new FakeNativeApi();
            using var hook = Create(native);
            var stopped = NewCompletion();
            hook.KeyDown += () =>
            {
                try
                {
                    if (dispose) { hook.Dispose(); }
                    else
                    {
                        hook.Uninstall();
                        AssertEx.Throws<InvalidOperationException>(hook.Install);
                    }
                    AssertEx.False(hook.IsInstalled);
                    stopped.TrySetResult(true);
                }
                catch (Exception exception) { stopped.TrySetException(exception); }
            };
            hook.Install();
            var owner = native.LatestOwner;
            var result = await native.EmitKeyAsync(0x41).WaitAsync(TestTimeout);
            await stopped.Task.WaitAsync(TestTimeout);
            AssertEx.Equal(FakeNativeApi.ForwardResult, result, "Stopping swallowed the current native message.");
            AssertEx.True(owner.Thread.Join(TestTimeout), "Callback-side shutdown left its owner alive.");
            AssertEx.Equal(owner.Thread.ManagedThreadId, owner.UninstallThreadId);
            AssertEx.Equal(1, native.UninstallCalls);
            AssertEx.Null(owner.Callback);
            if (dispose) { AssertEx.Throws<ObjectDisposedException>(hook.Install); }
            else { hook.Install(); AssertEx.True(hook.IsInstalled); hook.Uninstall(); }
        }
    }

    public static async Task ExternalStopAllowsReentrantCallbackShutdown()
    {
        using var native = new FakeNativeApi();
        using var hook = Create(native);
        using var release = new ManualResetEventSlim();
        var entered = NewCompletion();
        var completed = NewCompletion();
        hook.KeyDown += () =>
        {
            entered.TrySetResult(true);
            try
            {
                AssertEx.True(release.Wait(TestTimeout), "The test did not release the callback.");
                hook.Uninstall();
                hook.Dispose();
                completed.TrySetResult(true);
            }
            catch (Exception exception) { completed.TrySetException(exception); }
        };
        hook.Install();
        var delivery = native.EmitKeyAsync(0x41);
        Task? stop = null;
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            stop = Task.Run(hook.Uninstall);
            await native.LatestOwner.QuitPosted.Task.WaitAsync(TestTimeout);
            AssertEx.False(hook.IsInstalled);
            release.Set();
            await Task.WhenAll(stop, completed.Task, delivery).WaitAsync(TestTimeout);
            AssertEx.True(native.LatestOwner.Thread.Join(TestTimeout));
            AssertEx.Equal(1, native.UninstallCalls);
        }
        finally
        {
            release.Set();
            if (stop is not null) { await stop.WaitAsync(TestTimeout); }
            await delivery.WaitAsync(TestTimeout);
        }
    }

    public static async Task ShutdownTimeoutDoesNotLoseCleanupOwnership()
    {
        using var native = new FakeNativeApi();
        using var hook = Create(native, TimeSpan.FromMilliseconds(350));
        using var release = new ManualResetEventSlim();
        var entered = NewCompletion();
        hook.KeyDown += () =>
        {
            entered.TrySetResult(true);
            if (!release.Wait(TestTimeout)) { throw new TestFailureException("Callback was not released."); }
        };
        hook.Install();
        var owner = native.LatestOwner;
        var delivery = native.EmitKeyAsync(0x41);
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            var clock = Stopwatch.StartNew();
            await Task.Run(() => AssertEx.Throws<TimeoutException>(hook.Uninstall)).WaitAsync(TestTimeout);
            AssertEx.True(clock.Elapsed < TestTimeout);
            AssertEx.False(hook.IsInstalled);
            AssertEx.Equal(0, native.UninstallCalls, "The native handle was released while its callback still ran.");
        }
        finally { release.Set(); }
        await delivery.WaitAsync(TestTimeout);
        hook.Dispose(); // Retry the wait; the stopped session still owns its cleanup.
        AssertEx.True(owner.Thread.Join(TestTimeout));
        AssertEx.Equal(1, native.UninstallCalls);
        AssertEx.Null(owner.Callback);
    }

    public static async Task PrivacyFilterForwardsButNeverPublishesInjectedOrModifierKeys()
    {
        using var native = new FakeNativeApi();
        using var hook = Create(native);
        var signals = 0;
        var wrongThread = false;
        hook.KeyDown += () =>
        {
            signals++;
            wrongThread |= !ReferenceEquals(Thread.CurrentThread, native.LatestOwner.Thread);
        };
        hook.Install();
        AssertEx.Equal(typeof(Action), typeof(IKeyboardHook).GetEvent(nameof(IKeyboardHook.KeyDown))!.EventHandlerType);
        AssertEx.Equal(typeof(Action), typeof(KeyboardHook).GetEvent(nameof(KeyboardHook.KeyDown))!.EventHandlerType);
        var before = native.ForwardCalls;
        AssertEx.Equal(FakeNativeApi.ForwardResult, await native.EmitKeyAsync(0x41).WaitAsync(TestTimeout));
        AssertEx.Equal(FakeNativeApi.ForwardResult, await native.EmitKeyAsync(0x41, message: 0x0104).WaitAsync(TestTimeout));
        AssertEx.Equal(2, signals);
        var filtered = 0;
        foreach (var key in new uint[] { 0x10, 0x11, 0x12, 0x14, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
        {
            await native.EmitKeyAsync(key).WaitAsync(TestTimeout);
            await native.EmitKeyAsync(key, message: 0x0104).WaitAsync(TestTimeout);
            filtered += 2;
        }
        foreach (var flags in new uint[] { 0x10, 0x12 })
        {
            await native.EmitKeyAsync(0x41, flags).WaitAsync(TestTimeout);
            filtered++;
        }
        await native.EmitKeyAsync(0x41, message: 0x0101).WaitAsync(TestTimeout);
        await native.EmitKeyAsync(0x41, message: 0x0105).WaitAsync(TestTimeout);
        // Invalid lParam must not even be read for a negative hook code or another message.
        await native.InvokeRawAsync(-1, 0x0100, nint.Zero).WaitAsync(TestTimeout);
        await native.InvokeRawAsync(0, 0, nint.Zero).WaitAsync(TestTimeout);
        filtered += 4;
        AssertEx.Equal(2, signals);
        AssertEx.Equal(before + 2 + filtered, native.ForwardCalls, "A filtered event was not forwarded.");
        AssertEx.False(wrongThread);
        Action throwing = () => throw new InvalidOperationException("Controlled subscriber failure.");
        hook.KeyDown += throwing;
        AssertEx.Equal(FakeNativeApi.ForwardResult, await native.EmitKeyAsync(0x41).WaitAsync(TestTimeout));
        hook.KeyDown -= throwing;
        AssertEx.Equal(FakeNativeApi.ForwardResult, await native.EmitKeyAsync(0x41).WaitAsync(TestTimeout));
        AssertEx.Equal(4, signals, "A subscriber exception disabled later callbacks.");
        var callback = AssertEx.NotNull(native.LatestOwner.Callback);
        hook.Uninstall();
        AssertEx.Equal(FakeNativeApi.ForwardResult, InvokePacket(callback, 0x41, 0, 0x0100));
        AssertEx.Equal(4, signals, "A late callback published a signal after shutdown.");
    }

    public static async Task MessageLoopFailureUnhooksAndAllowsRestart()
    {
        using var native = new FakeNativeApi();
        using var hook = Create(native);
        hook.Install();
        var owner = native.LatestOwner;
        owner.Messages.Add(new WorkItem(null, new IOException("Controlled message-loop failure."), false));
        await owner.Unhooked.Task.WaitAsync(TestTimeout);
        AssertEx.True(owner.Thread.Join(TestTimeout));
        AssertEx.False(hook.IsInstalled);
        AssertEx.Equal(1, native.UninstallCalls);
        AssertEx.Null(owner.Callback);
        hook.Install();
        AssertEx.True(hook.IsInstalled);
        hook.Uninstall();
        AssertEx.Equal(2, native.UninstallCalls);
    }

    private static KeyboardHook Create(FakeNativeApi native, TimeSpan? timeout = null) =>
        new(native, timeout ?? TimeSpan.FromSeconds(5));

    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Thread GetOwnerThread(KeyboardHook hook)
    {
        var session = AssertEx.NotNull(typeof(KeyboardHook).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hook));
        return (Thread)session.GetType().GetField("Thread")!.GetValue(session)!;
    }

    private static bool IsStopping(KeyboardHook hook)
    {
        var session = typeof(KeyboardHook).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hook);
        return session is not null && (int)session.GetType().GetField("StopRequested")!.GetValue(session)! != 0;
    }

    private static nint InvokePacket(KeyboardHook.LowLevelKeyboardProc callback, uint key, uint flags, int message)
    {
        // Synthetic packet tests the privacy filter, not physical/OS delivery.
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<KeyboardPacket>());
        try
        {
            Marshal.StructureToPtr(new KeyboardPacket { VirtualKey = key, Flags = flags }, memory, false);
            return callback(0, message, memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardPacket
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private sealed class RejectingSynchronizationContext : SynchronizationContext
    {
        public int Calls;
        public RejectingSynchronizationContext() => SetWaitNotificationRequired();
        public override void Post(SendOrPostCallback callback, object? state) { Calls++; throw new TestFailureException("Unexpected context Post."); }
        public override void Send(SendOrPostCallback callback, object? state) { Calls++; throw new TestFailureException("Unexpected context Send."); }
        public override int Wait(nint[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            Calls++;
            throw new TestFailureException("Lifecycle used a pumping managed wait.");
        }
    }

    private sealed record WorkItem(Action? Action, Exception? Failure, bool Quit);

    private sealed class FakeOwner(Thread thread)
    {
        public Thread Thread { get; } = thread;
        public SynchronizationContext? Context { get; } = SynchronizationContext.Current;
        public BlockingCollection<WorkItem> Messages { get; } = new();
        public TaskCompletionSource<bool> QuitPosted { get; } = NewCompletion();
        public TaskCompletionSource<bool> Unhooked { get; } = NewCompletion();
        public KeyboardHook.LowLevelKeyboardProc? Callback;
        public Action? Pending;
        public bool QueueReady;
        public nint Hook;
        public int UninstallThreadId;
    }

    private sealed class FakeNativeApi : KeyboardHook.INativeApi, IDisposable
    {
        public static readonly nint ForwardResult = 123;
        private readonly ConcurrentDictionary<uint, FakeOwner> _owners = new();
        private FakeOwner? _latestOwner;
        private int _nextHandle = 100;
        private int _activeHooks;
        public int InstallCalls;
        public int UninstallCalls;
        public int ForwardCalls;
        public int TranslateCalls;
        public int DispatchCalls;
        public int MaximumActiveHooks;
        public Exception? InstallException;
        public bool ReturnZeroHandle;
        public ManualResetEventSlim QueueRelease { get; } = new(true);
        public ManualResetEventSlim InstallRelease { get; } = new(true);
        public TaskCompletionSource<bool> QueueEntered { get; } = NewCompletion();
        public TaskCompletionSource<bool> InstallEntered { get; } = NewCompletion();
        public FakeOwner LatestOwner => AssertEx.NotNull(Volatile.Read(ref _latestOwner));

        public uint CurrentThreadId
        {
            get
            {
                var id = (uint)Environment.CurrentManagedThreadId;
                var owner = _owners.GetOrAdd(id, _ => new FakeOwner(Thread.CurrentThread));
                Volatile.Write(ref _latestOwner, owner);
                return id;
            }
        }
        private FakeOwner CurrentOwner => _owners[(uint)Environment.CurrentManagedThreadId];

        public void CreateMessageQueue()
        {
            QueueEntered.TrySetResult(true);
            if (!QueueRelease.Wait(TimeSpan.FromSeconds(6))) { throw new TimeoutException("Test queue creation was not released."); }
            CurrentOwner.QueueReady = true;
        }

        public nint InstallHook(KeyboardHook.LowLevelKeyboardProc callback)
        {
            AssertEx.True(CurrentOwner.QueueReady, "Native installation preceded queue creation.");
            Interlocked.Increment(ref InstallCalls);
            InstallEntered.TrySetResult(true);
            if (!InstallRelease.Wait(TimeSpan.FromSeconds(6))) { throw new TimeoutException("Test installation was not released."); }
            if (InstallException is not null) { throw InstallException; }
            if (ReturnZeroHandle) { return nint.Zero; }
            var owner = CurrentOwner;
            owner.Callback = callback;
            owner.Hook = Interlocked.Increment(ref _nextHandle);
            var active = Interlocked.Increment(ref _activeHooks);
            AssertEx.Equal(1, active, "Concurrent sessions installed multiple native hooks.");
            MaximumActiveHooks = Math.Max(MaximumActiveHooks, active);
            return owner.Hook;
        }

        public bool GetMessage(out KeyboardHook.NativeMessage message)
        {
            var work = CurrentOwner.Messages.Take();
            message = default;
            if (work.Failure is not null) { throw work.Failure; }
            if (work.Quit) { return false; }
            CurrentOwner.Pending = work.Action;
            message.Id = 0x8001;
            return true;
        }

        public void TranslateMessage(ref KeyboardHook.NativeMessage message) => Interlocked.Increment(ref TranslateCalls);
        public void DispatchMessage(ref KeyboardHook.NativeMessage message)
        {
            Interlocked.Increment(ref DispatchCalls);
            var action = CurrentOwner.Pending;
            CurrentOwner.Pending = null;
            action?.Invoke();
        }

        public void PostQuit(uint threadId)
        {
            var owner = _owners[threadId];
            AssertEx.True(owner.QueueReady, "Quit was posted before the queue existed.");
            owner.Messages.Add(new WorkItem(null, null, true));
            owner.QuitPosted.TrySetResult(true);
        }

        public void UninstallHook(nint hook)
        {
            var owner = CurrentOwner;
            AssertEx.Equal(owner.Hook, hook);
            AssertEx.True(owner.Hook != nint.Zero, "A native handle was released twice.");
            owner.Hook = nint.Zero;
            owner.Callback = null;
            owner.UninstallThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Decrement(ref _activeHooks);
            Interlocked.Increment(ref UninstallCalls);
            owner.Unhooked.TrySetResult(true);
        }

        public nint CallNext(int code, nint wParam, nint lParam)
        {
            Interlocked.Increment(ref ForwardCalls);
            return ForwardResult;
        }

        public Task<nint> EmitKeyAsync(uint key, uint flags = 0, int message = 0x0100) =>
            InvokeAsync(() => InvokePacket(AssertEx.NotNull(LatestOwner.Callback), key, flags, message));

        public Task<nint> InvokeRawAsync(int code, nint message, nint data) =>
            InvokeAsync(() => AssertEx.NotNull(LatestOwner.Callback)(code, message, data));

        public Task<nint> InvokeAsync(Func<nint> action)
        {
            var completion = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
            LatestOwner.Messages.Add(new WorkItem(() =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }, null, false));
            return completion.Task;
        }

        public void Dispose()
        {
            QueueRelease.Set();
            InstallRelease.Set();
            foreach (var owner in _owners.Values) { owner.Messages.Add(new WorkItem(null, null, true)); }
            foreach (var owner in _owners.Values)
            {
                AssertEx.True(owner.Thread.Join(TestTimeout), "A test native owner leaked.");
                owner.Messages.Dispose();
            }
            QueueRelease.Dispose();
            InstallRelease.Dispose();
        }
    }
}
