using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using BeatzOnKeys.Core.Services;
using Microsoft.Win32.SafeHandles;

namespace BeatzOnKeys.Core.Input;

/// <summary>
/// A signal-only WH_KEYBOARD_LL hook with its own STA message loop. No text,
/// window titles, or key codes are retained; injected input is ignored.
/// </summary>
public sealed class KeyboardHook : IKeyboardHook
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const uint WmQuit = 0x0012;
    private const uint LlkfInjected = 0x10;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    private readonly object _gate = new();
    private readonly LowLevelKeyboardProc _callback;
    private readonly INativeApi _native;
    private readonly int _lifecycleTimeoutMilliseconds;
    private HookSession? _session;
    private int _isInstalled;
    private long _delivered;
    private bool _disposed;

    public KeyboardHook() : this(new Win32Api(), TimeSpan.FromSeconds(5)) { }

    // The native boundary is injectable only inside the friend test assembly.
    internal KeyboardHook(INativeApi native, TimeSpan lifecycleTimeout)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (lifecycleTimeout <= TimeSpan.Zero || lifecycleTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(lifecycleTimeout));
        }

        _native = native;
        _lifecycleTimeoutMilliseconds = (int)Math.Ceiling(lifecycleTimeout.TotalMilliseconds);
        _callback = HandleKey;
    }

    public event Action? KeyDown;

    /// <summary>
    /// Raised for every delivered key-down, including injected and modifier keys, before any
    /// filtering. The watcher uses it to prove to the app that the native hook is live; no key
    /// code, text, or window title leaves this class.
    /// </summary>
    public event Action? AnyKeyDown;

    /// <summary>Deliveries observed by the current session; proves the hook is being called.</summary>
    public long DeliveredCount => Interlocked.Read(ref _delivered);

    // This is our lifecycle state, not a Windows guarantee against silent hook removal.
    public bool IsInstalled => Volatile.Read(ref _isInstalled) != 0;

    public void Install()
    {
        var deadline = Environment.TickCount64 + _lifecycleTimeoutMilliseconds;
        while (true)
        {
            HookSession session;
            bool stopping;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (Environment.TickCount64 >= deadline)
                {
                    throw new TimeoutException("The keyboard hook did not become available in time.");
                }

                session = _session ?? StartSession();
                stopping = Volatile.Read(ref session.StopRequested) != 0;
                if (ReferenceEquals(Thread.CurrentThread, session.Thread))
                {
                    if (session.StartupSucceeded && !stopping) { return; }
                    throw new InvalidOperationException("Restart the keyboard hook after its callback has returned.");
                }
                session.AddReference();
            }

            try
            {
                if (stopping)
                {
                    // Do not create a second hook while the preceding owner is still stopping.
                    WaitForSignal(session.Stopped, deadline, "Keyboard hook shutdown");
                    continue;
                }

                WaitForSignal(session.Ready, deadline, "Keyboard hook startup");
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    session.Failure?.Throw();
                    if (!ReferenceEquals(_session, session) || Volatile.Read(ref session.StopRequested) != 0)
                    {
                        throw new InvalidOperationException("The keyboard hook was stopped during installation.");
                    }
                    return;
                }
            }
            catch (TimeoutException)
            {
                RequestStop(session);
                throw;
            }
            finally { session.ReleaseReference(); }
        }
    }

    public void Uninstall() => Stop(dispose: false);

    public void Dispose()
    {
        try { Stop(dispose: true); }
        finally { GC.SuppressFinalize(this); }
    }

    private void Stop(bool dispose)
    {
        var deadline = Environment.TickCount64 + _lifecycleTimeoutMilliseconds;
        HookSession? session;
        lock (_gate)
        {
            if (dispose)
            {
                // A racing Install must not publish a new hook after disposal has begun.
                _disposed = true;
                KeyDown = null;
            }
            session = _session;
            session?.AddReference();
        }
        if (session is null) { return; }

        try
        {
            RequestStop(session);
            // A subscriber may stop/dispose us from inside the native callback.
            // Cleanup runs as soon as that callback returns; never wait for ourselves.
            if (!ReferenceEquals(Thread.CurrentThread, session.Thread))
            {
                WaitForSignal(session.Stopped, deadline, "Keyboard hook shutdown");
            }
        }
        finally { session.ReleaseReference(); }
    }

    // Called only under _gate. Starting a thread is not waiting for its message loop.
    private HookSession StartSession()
    {
        var session = new HookSession(this, _callback);
        _session = session;
        session.AddReference(); // The thread owns this reference until its final cleanup.
        try
        {
            // Do not flow caller AsyncLocals or a UI execution context onto the hook owner.
            if (ExecutionContext.IsFlowSuppressed()) { session.Thread.Start(); }
            else
            {
                using (ExecutionContext.SuppressFlow()) { session.Thread.Start(); }
            }
            return session;
        }
        catch
        {
            _session = null;
            session.ReleaseReference();
            session.ReleaseReference();
            throw;
        }
    }

    private void RunMessageLoop(HookSession session)
    {
        nint hook = nint.Zero;
        Exception? loopFailure = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            Volatile.Write(ref session.NativeThreadId, _native.CurrentThreadId);
            _native.CreateMessageQueue();
            Volatile.Write(ref session.QueueReady, 1);
            if (Volatile.Read(ref session.StopRequested) != 0) { return; }

            hook = _native.InstallHook(session.Callback);
            if (hook == nint.Zero) { throw new InvalidOperationException("The keyboard hook installation returned no handle."); }
            lock (_gate)
            {
                if (_disposed || Volatile.Read(ref session.StopRequested) != 0) { return; }
                session.StartupSucceeded = true;
                // The queue and native hook both exist before readiness is observable.
                session.Ready.Set();
                Volatile.Write(ref _isInstalled, 1);
            }

            while (Volatile.Read(ref session.StopRequested) == 0 && _native.GetMessage(out var message))
            {
                if (Volatile.Read(ref session.StopRequested) != 0) { break; }
                _native.TranslateMessage(ref message);
                _native.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                session.Failure = ExceptionDispatchInfo.Capture(exception);
                if (session.StartupSucceeded) { loopFailure = exception; }
            }
        }
        finally
        {
            lock (_gate)
            {
                Volatile.Write(ref session.StopRequested, 1);
                if (ReferenceEquals(_session, session)) { Volatile.Write(ref _isInstalled, 0); }
            }
            try
            {
                if (hook != nint.Zero) { _native.UninstallHook(hook); }
            }
            catch (Exception exception) { ReportFailure("Could not release the native keyboard hook.", exception); }
            finally { GC.KeepAlive(session.Callback); }

            bool releaseOwner;
            lock (_gate)
            {
                if (!session.StartupSucceeded)
                {
                    session.Failure ??= ExceptionDispatchInfo.Capture(
                        new OperationCanceledException("The keyboard hook startup was cancelled."));
                    session.Ready.Set();
                }
                releaseOwner = ReferenceEquals(_session, session);
                if (releaseOwner) { _session = null; }
            }
            if (loopFailure is not null) { ReportFailure("The native keyboard message loop stopped unexpectedly.", loopFailure); }

            // No work or logging that can block follows this signal. Unlike Thread.Join,
            // the native event wait used by callers cannot pump COM/UI messages.
            session.Stopped.Set();
            if (releaseOwner) { session.ReleaseReference(); }
            session.ReleaseReference();
        }
    }

    private void RequestStop(HookSession session)
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session)) { return; }
            Volatile.Write(ref session.StopRequested, 1);
            Volatile.Write(ref _isInstalled, 0);
            if (Volatile.Read(ref session.QueueReady) == 0) { return; }
            try
            {
                // Posting is nonblocking. Keep the owner registered while posting so an
                // exiting thread ID cannot be reused by an unrelated thread in this race.
                _native.PostQuit(Volatile.Read(ref session.NativeThreadId));
            }
            catch (Exception exception) { failure = exception; }
        }
        if (failure is not null) { ReportFailure("Could not signal the keyboard message loop to stop.", failure); }
    }

    private static void WaitForSignal(EventWaitHandle signal, long deadline, string operation)
    {
        var remaining = (uint)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);
        var result = WaitForSingleObject(signal.SafeWaitHandle, remaining);
        if (result == WaitObject0) { return; }
        if (result == WaitTimeout) { throw new TimeoutException($"{operation} exceeded its bounded wait."); }
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"{operation} wait failed.");
    }

    private static void ReportFailure(string message, Exception exception)
    {
        // File logging must not delay hook teardown or retain a hook/session/subscriber.
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(static failure =>
                AppLogger.Error("input", failure.Message, failure.Exception),
                (Message: message, Exception: exception), preferLocal: false);
        }
        catch { /* Diagnostics must not prevent native cleanup. */ }
    }

    private nint HandleKey(int code, nint wParam, nint lParam)
    {
        if (IsInstalled && code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            try { AnyKeyDown?.Invoke(); }
            catch { /* liveness observers must never break the hook */ }
            Interlocked.Increment(ref _delivered);
            try
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                // Nothing may log or block here: this is the hook thread, and blocking I/O makes
                // Windows drop the hook for the whole desktop after LowLevelHooksTimeout.
                if ((data.Flags & LlkfInjected) == 0 && !IsBareModifier(data.VirtualKey))
                {
                    KeyDown?.Invoke();
                }
            }
            catch
            {
                // A hook must always return promptly; consumers handle their own errors.
            }
        }

        // Windows ignores hhk here; passing zero is also safe during callback-side stop.
        return _native.CallNext(code, wParam, lParam);
    }

    private static bool IsBareModifier(uint virtualKey) => virtualKey switch
    {
        0x10 or 0x11 or 0x12 or // Shift, Control, Alt
        0x14 or                 // Caps Lock
        0x5B or 0x5C or        // Windows keys
        >= 0xA0 and <= 0xA5 => true, // left/right Shift, Control, Alt
        _ => false
    };

    private sealed class HookSession
    {
        private int _references = 1; // _session owns the initial reference.
        public readonly EventWaitHandle Ready = new ManualResetEvent(false);
        public readonly EventWaitHandle Stopped = new ManualResetEvent(false);
        public readonly Thread Thread;
        public readonly LowLevelKeyboardProc Callback;
        public uint NativeThreadId;
        public int QueueReady;
        public int StopRequested;
        public bool StartupSucceeded; // Protected by _gate.
        public ExceptionDispatchInfo? Failure; // Protected by _gate.

        public HookSession(KeyboardHook owner, LowLevelKeyboardProc callback)
        {
            Callback = callback;
            try
            {
                Thread = new Thread(() => owner.RunMessageLoop(this))
                {
                    IsBackground = true,
                    Name = "BeatzOnKeys keyboard hook"
                };
                Thread.SetApartmentState(ApartmentState.STA);
            }
            catch { Ready.Dispose(); Stopped.Dispose(); throw; }
        }

        public void AddReference() => Interlocked.Increment(ref _references);
        public void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _references) != 0) { return; }
            Ready.Dispose();
            Stopped.Dispose();
        }
    }

    internal interface INativeApi
    {
        uint CurrentThreadId { get; }
        void CreateMessageQueue();
        nint InstallHook(LowLevelKeyboardProc callback);
        bool GetMessage(out NativeMessage message);
        void TranslateMessage(ref NativeMessage message);
        void DispatchMessage(ref NativeMessage message);
        void PostQuit(uint threadId);
        void UninstallHook(nint hook);
        nint CallNext(int code, nint wParam, nint lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    private sealed class Win32Api : INativeApi
    {
        public uint CurrentThreadId => GetCurrentThreadId();
        public void CreateMessageQueue() => _ = PeekMessage(out _, nint.Zero, 0, 0, 0);
        public nint InstallHook(LowLevelKeyboardProc callback)
        {
            var hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
            if (hook == nint.Zero) { throw NativeFailure("SetWindowsHookEx"); }
            return hook;
        }
        public bool GetMessage(out NativeMessage message)
        {
            var result = KeyboardHook.GetMessage(out message, nint.Zero, 0, 0);
            if (result < 0) { throw NativeFailure("GetMessage"); }
            return result != 0;
        }
        public void TranslateMessage(ref NativeMessage message) => _ = KeyboardHook.TranslateMessage(ref message);
        public void DispatchMessage(ref NativeMessage message) => _ = KeyboardHook.DispatchMessage(ref message);
        public void PostQuit(uint threadId)
        {
            if (threadId == GetCurrentThreadId()) { PostQuitMessage(0); }
            else if (!PostThreadMessage(threadId, WmQuit, 0, 0)) { throw NativeFailure("PostThreadMessage"); }
        }
        public void UninstallHook(nint hook)
        {
            if (!UnhookWindowsHookEx(hook)) { throw NativeFailure("UnhookWindowsHookEx"); }
        }
        public nint CallNext(int code, nint wParam, nint lParam) => CallNextHookEx(nint.Zero, code, wParam, lParam);
        private static Win32Exception NativeFailure(string operation)
        {
            var error = Marshal.GetLastWin32Error();
            return new Win32Exception(error, $"{operation} failed with Windows error {error}.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, nint moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hookHandle, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
