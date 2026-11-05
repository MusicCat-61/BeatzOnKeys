using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Input;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.UI;
using BeatzOnKeys.UI.ViewModels;
using NAudio.Wave;
using Forms = System.Windows.Forms;

namespace BeatzOnKeys.UiSmoke;

/// <summary>Runs the real App.OnStartup/Run path, not the legacy fake-hook smoke setup.</summary>
internal sealed class ProductionTraySmoke(string session)
{
    private readonly List<object> _checks = [];
    private App _app = null!;
    private Window? _inputSink;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private ContinuousAudioPlayer? _player;
    private WatcherKeyboardHook? _hook;
    private bool _failed;
    private bool _finished;

    public static int Run(string root, string leaf)
    {
        var session = Path.Combine(root, "artifacts", "ui-smoke", leaf);
        Directory.CreateDirectory(session);
        var smoke = new ProductionTraySmoke(session);
        smoke.PrepareIsolatedData();
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL production.watchdog: UI/startup did not complete within 75 seconds.");
            File.WriteAllText(Path.Combine(session, "watchdog.txt"), "Real App.Run did not complete within 75 seconds.");
            Environment.Exit(124);
        }, null, TimeSpan.FromSeconds(75), Timeout.InfiniteTimeSpan);
        // Do not replace the synchronization context, abort startup, or construct a MainViewModel here.
        smoke._app = new App();
        smoke._app.InitializeComponent();
        smoke._app.Dispatcher.BeginInvoke(new Action(async () => await smoke.ExerciseAsync()), DispatcherPriority.ApplicationIdle);
        var exit = smoke._app.Run();
        smoke.Check("production.exitCleanup", exit == 0 && smoke._hook?.HasWatcher != true &&
            Field<Forms.NotifyIcon>(smoke._app, "_trayIcon") is null,
            "Normal Exit stops the keyboard watcher and releases the notification icon.");
        smoke.SaveReport();
        return exit == 0 && smoke._finished && !smoke._failed ? 0 : 1;
    }

    private void PrepareIsolatedData()
    {
        SetProcessPath(nameof(AppPaths.RootDirectory), session);
        SetProcessPath(nameof(AppPaths.SettingsFile), Path.Combine(session, "settings.json"));
        SetProcessPath(nameof(AppPaths.LogDirectory), Path.Combine(session, "logs"));
        SetProcessPath(nameof(AppPaths.UpdateDirectory), Path.Combine(session, "updates"));
        var fixture = Path.Combine(session, "quiet-generated.wav");
        var format = new WaveFormat(44100, 16, 2);
        using (var writer = new WaveFileWriter(fixture, format))
        {
            var samples = new float[44100 * 2];
            for (var frame = 0; frame < 44100; frame++)
                samples[frame * 2] = samples[frame * 2 + 1] = (float)(0.015 * Math.Sin(2 * Math.PI * 440 * frame / 44100));
            for (var second = 0; second < 60; second++) writer.WriteSamples(samples, 0, samples.Length);
        }
        var track = new TrackState { SourcePath = fixture };
        new SettingsService().SaveAsync(new AppSettings
        {
            Language = "en", Theme = "dark", Tracks = [track], SelectedTrackId = track.Id,
            MinVolume = 0.2, MaxVolume = 1, IdleTimeout = 1, PauseOnIdle = true, Loop = true
        }).GetAwaiter().GetResult();
    }

    private async Task ExerciseAsync()
    {
        try
        {
            await UntilAsync(() => _app.MainWindow is MainWindow { IsVisible: true } window && window.ViewModel.CanReset,
                10000, "production startup");
            _window = (MainWindow)_app.MainWindow;
            _vm = _window.ViewModel;
            _player = Field<ContinuousAudioPlayer>(_vm, "_audioPlayer")!;
            var service = Field<TypingPlaybackService>(_vm, "_typingPlayback")!;
            var hookObject = Field<IKeyboardHook>(service, "_keyboardHook")!;
            _hook = hookObject as WatcherKeyboardHook;
            var legacyHook = hookObject as KeyboardHook;
            var tray = Field<Forms.NotifyIcon>(_app, "_trayIcon");
            Check("production.startup", _window.IsVisible && hookObject is WatcherKeyboardHook && tray?.Visible == true &&
                Field<DispatcherTimer>(_app, "_updateTimer") is not null,
                $"hook={hookObject.GetType().Name}; tray={tray?.Visible}; context={SynchronizationContext.Current?.GetType().Name}");
            Check("production.isolation", AppPaths.RootDirectory == session &&
                Field<string>(Field<SettingsService>(_app, "_settingsService")!, "_settingsFile") == AppPaths.SettingsFile,
                "Real startup uses only this test session's settings/logs.");

            // The production hook now lives in a separate watcher process, so native callbacks
            // cannot be observed in-process; delivery is measured via the watcher's own counter.
            NativeHookProbe? probe = _hook is null && legacyHook is not null ? new NativeHookProbe(legacyHook) : null;
            _vm.StartCommand.Execute(null);
            await UntilAsync(() => _vm.IsRunning && _vm.StopCommand.CanExecute(null), 5000, "Start");
            Check("production.startVisible", _window.IsVisible && _player.IsPlaying,
                $"window={_window.IsVisible}; audio={_player.IsPlaying}; watcher={_hook?.HasWatcher}; installed={_hook?.IsInstalled}");
            // An input sink owned by THIS test receives harmless F24 probes. No text or foreign window is inspected.
            _inputSink = new Window { Title = "BeatzOnKeys diagnostic input", Width = 320, Height = 100,
                Content = new TextBlock { Text = "Tray test in progress. No typing is recorded.", Margin = new Thickness(12) } };
            _inputSink.Show();
            _inputSink.Activate();
            if (probe is not null) { await ProbeDeliveryAsync(probe, "visible"); }

            var close = typeof(MainWindow).GetMethod("CloseClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            close.Invoke(_window, [_window, new RoutedEventArgs()]);
            await Task.Delay(150);
            Check("production.crossHides", !_window.IsVisible && tray?.Visible == true && _vm.IsRunning,
                $"hidden={!_window.IsVisible}; tray={tray?.Visible}; service={service.IsRunning}; watcher={_hook?.HasWatcher}");
            if (probe is not null) { await ProbeDeliveryAsync(probe, "hidden"); }
            await UntilAsync(() => _player.IsIdlePaused, 4000, "idle pause while hidden");
            // Injected input is filtered in production, so a synthetic probe cannot prove the
            // user-typing path; this asserts the watcher stayed alive and owned by us.
            Check("production.hiddenWatcherAlive", !_window.IsVisible && _vm.IsRunning &&
                (_hook is null || (_hook.HasWatcher && _hook.IsInstalled)),
                $"hidden={!_window.IsVisible}; watcher={_hook?.HasWatcher}; installed={_hook?.IsInstalled}; " +
                $"delivered={_hook?.DeliveredKeyCount ?? 0}.");
            _vm.PauseOnIdle = false;
            var before = _player.Position;
            await Task.Delay(1500);
            Check("production.hiddenContinuous", !_window.IsVisible && _player.IsPlaying && _player.Position > before + TimeSpan.FromSeconds(1),
                "Explicit hide does not stop playback when idle pausing is disabled.");

            Field<Forms.ToolStripMenuItem>(_app, "_openMenuItem")!.PerformClick();
            await Task.Delay(100);
            Check("production.trayOpen", _window.IsVisible && _vm.IsRunning, "Real tray Open handler restores the main window.");
            _window.Close();
            await Task.Delay(100);
            Check("production.systemClose", !_window.IsVisible && _vm.IsRunning &&
                (_hook is null || _hook.IsInstalled || !_hook.HasWatcher),
                "Window.Close/Alt+F4 path also hides without stopping playback.");
            if (probe is not null) { await VerifyUiStallIsolationAsync(probe); }
            Field<Forms.ToolStripMenuItem>(_app, "_stopMenuItem")!.PerformClick();
            await UntilAsync(() => !_vm.IsRunning && _vm.CanReset, 5000, "tray Stop");
            Check("production.trayStop", !_player.IsPlaying && (_hook is null || !_hook.HasWatcher),
                "Only explicit Stop stops audio and stops the watcher.");
            Check("production.logs", Directory.EnumerateFiles(AppPaths.LogDirectory, "beatzonkeys-*.log").Any(),
                "Real production startup and playback generated a current log.");
        }
        catch (Exception error)
        {
            Check("production.exception", false, error.ToString());
        }
        finally
        {
            _inputSink?.Close();
            try
            {
                // Exercise the normal Exit disposal path, but save the report before dispatcher shutdown.
                SaveReport();
                _finished = true;
                await _app.ShutdownForUpdateAsync();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                _failed = true;
                _app.Shutdown(1);
            }
        }
    }

    private void SaveReport()
    {
        var report = new { passed = !_failed, utc = DateTimeOffset.UtcNow, checks = _checks,
            coverage = "Real App.Run/OnStartup, real hook installation/native injected delivery (kept ignored), original synthetic non-injected callback, real WaveOut; not a physical keyboard test." };
        File.WriteAllText(Path.Combine(session, "production-tray-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task VerifyUiStallIsolationAsync(NativeHookProbe probe)
    {
        _inputSink!.Topmost = true;
        _inputSink.Activate();
        var handle = new WindowInteropHelper(_inputSink).Handle;
        NativeHookProbe.SetForegroundWindow(handle);
        await Task.Delay(100);
        var before = probe.NativeDeliveries;
        var sender = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            if (NativeHookProbe.GetForegroundWindow() != handle)
                throw new InvalidOperationException("Test input sink lost focus before the UI-stall probe.");
            probe.SendProbe();
        });
        // Intentional, test-only stall: the old UI-owned WH_KEYBOARD_LL times out and is silently removed.
        Thread.Sleep(1800);
        var deliveriesBeforeUiResumed = probe.NativeDeliveries - before;
        await sender;
        await Task.Delay(100);
        Check("production.deliveryDuringUiStall", deliveriesBeforeUiResumed > 0 &&
            probe.CallbackThread != Environment.CurrentManagedThreadId,
            $"Native deliveries before UI resumed={deliveriesBeforeUiResumed}; hookThread={probe.CallbackThread}; UI thread={Environment.CurrentManagedThreadId}.");
        await ProbeDeliveryAsync(probe, "after-ui-stall");
    }

    private async Task ProbeDeliveryAsync(NativeHookProbe probe, string phase)
    {
        _inputSink!.Topmost = true;
        _inputSink.Activate();
        var handle = new WindowInteropHelper(_inputSink).Handle;
        NativeHookProbe.SetForegroundWindow(handle);
        await Task.Delay(100);
        if (NativeHookProbe.GetForegroundWindow() != handle) throw new InvalidOperationException($"Refusing to send diagnostic F24 outside this test's input sink (foreground={NativeHookProbe.GetForegroundWindow()}, sink={handle}).");
        var before = probe.NativeDeliveries;
        var signals = probe.Signals;
        probe.SendProbe();
        await UntilAsync(() => probe.NativeDeliveries > before, 2000, phase + " native hook delivery");
        Check("production.nativeDelivery." + phase, probe.NativeDeliveries > before && probe.Signals == signals,
            $"Native tagged F24 callback received on thread {probe.CallbackThread}; injected input remains ignored.");
    }

    private void Check(string name, bool success, string detail)
    {
        _failed |= !success;
        _checks.Add(new { name, passed = success, detail });
        Console.WriteLine($"{(success ? "PASS" : "FAIL")} {name}: {detail}");
    }

    private static T? Field<T>(object owner, string name) => (T?)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static async Task UntilAsync(Func<bool> predicate, int timeout, string reason)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException(reason);
            await Task.Delay(20);
        }
    }

    private delegate ref string StaticStringRef();
    private static void SetProcessPath(string property, string value)
    {
        var field = typeof(AppPaths).GetField($"<{property}>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
        _ = typeof(AppPaths).GetProperty(property)!.GetValue(null);
        var method = new DynamicMethod("TraySmoke_" + property, typeof(string).MakeByRefType(), Type.EmptyTypes, typeof(AppPaths).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsflda, field);
        il.Emit(OpCodes.Ret);
        var accessor = (StaticStringRef)method.CreateDelegate(typeof(StaticStringRef));
        accessor() = value;
    }

    private sealed class NativeHookProbe : IDisposable
    {
        private const uint Marker = 0x424F4B;
        private readonly KeyboardHook _hook;
        private readonly Delegate _original;
        private readonly Delegate _wrapper;
        private int _deliveries;
        private int _signals;
        public int NativeDeliveries => Volatile.Read(ref _deliveries);
        public int Signals => Volatile.Read(ref _signals);
        public int CallbackThread { get; private set; }

        public NativeHookProbe(KeyboardHook hook)
        {
            _hook = hook;
            var field = typeof(KeyboardHook).GetField("_callback", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _original = (Delegate)field.GetValue(hook)!;
            _wrapper = Delegate.CreateDelegate(field.FieldType, this, GetType().GetMethod(nameof(Callback), BindingFlags.Instance | BindingFlags.NonPublic)!);
            field.SetValue(hook, _wrapper);
            hook.KeyDown += CountSignal;
        }

        private void CountSignal() => Interlocked.Increment(ref _signals);
        private nint Callback(int code, nint message, nint data)
        {
            if (code >= 0 && message == 0x100)
            {
                var input = Marshal.PtrToStructure<HookData>(data);
                if (input.ExtraInfo == Marker && input.VirtualKey == 0x87)
                {
                    CallbackThread = Environment.CurrentManagedThreadId;
                    Interlocked.Increment(ref _deliveries);
                }
            }
            // Keep ALL production filtering intact, including LLKHF_INJECTED. Never save key codes or text.
            return (nint)_original.DynamicInvoke(code, message, data)!;
        }

        public void SendProbe()
        {
            var inputs = new[] { new Input { Type = 1, Data = new InputUnion { Key = new KeyboardInput { VirtualKey = 0x87, ExtraInfo = Marker } } },
                new Input { Type = 1, Data = new InputUnion { Key = new KeyboardInput { VirtualKey = 0x87, Flags = 2, ExtraInfo = Marker } } } };
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
                throw new InvalidOperationException("Could not send tagged diagnostic F24: " + Marshal.GetLastWin32Error());
        }

        public void DeliverSyntheticNonInjectedKey()
        {
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<HookData>());
            try
            {
                Marshal.StructureToPtr(new HookData { VirtualKey = 0x87, ExtraInfo = Marker }, memory, false);
                _original.DynamicInvoke(0, (nint)0x100, memory);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        public void Dispose()
        {
            _hook.KeyDown -= CountSignal;
            GC.KeepAlive(_wrapper);
        }

        [StructLayout(LayoutKind.Sequential)] private struct HookData { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
        [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Key; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(nint window);
    }
}
