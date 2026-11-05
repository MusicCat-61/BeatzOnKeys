using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Input;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.Updates;
using BeatzOnKeys.UI;
using BeatzOnKeys.UI.Localization;
using BeatzOnKeys.UI.Theming;
using BeatzOnKeys.UI.ViewModels;
using NAudio.Wave;
using Forms = System.Windows.Forms;

namespace BeatzOnKeys.UiSmoke;

internal static class Program
{
    // Launch from any directory: --root locates the source checkout, --session chooses a safe artifact leaf.
    // Default mode isolates UI rendering; --production-tray runs the actual App.Run/OnStartup and native hook path.
    [STAThread]
    public static int Main(string[] args)
    {
        var root = FindRoot(GetArgument(args, "--root"));
        var leaf = GetArgument(args, "--session") ?? DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        if (!Regex.IsMatch(leaf, @"^[A-Za-z0-9_-]+$"))
            throw new ArgumentException("--session must be a simple artifact directory name.");
        if (args.Contains("--production-tray")) return ProductionTraySmoke.Run(root, leaf);
        var session = Path.Combine(root, "artifacts", "ui-smoke", leaf);
        Directory.CreateDirectory(session);
        // LoadComponent requires the exact App assembly, so use a real App, not an App subclass.
        // WPF queues OnStartup in the constructor; capture and abort those operations before any pump.
        var dispatcher = Dispatcher.CurrentDispatcher;
        var startupOperations = new List<DispatcherOperation>();
        DispatcherHookEventHandler captureStartup = (_, e) => startupOperations.Add(e.Operation);
        dispatcher.Hooks.OperationPosted += captureStartup;
        App app;
        try { app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; }
        finally { dispatcher.Hooks.OperationPosted -= captureStartup; }
        var abortedStartupCount = startupOperations.Count(operation => operation.Abort());
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var smoke = new SmokeRun(root, session, app, abortedStartupCount);
        var frame = new DispatcherFrame();
        var task = smoke.RunAsync();
        _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine(error); return 2; }
        return smoke.Failed ? 1 : 0;
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string FindRoot(string? explicitRoot)
    {
        if (explicitRoot is not null) return Path.GetFullPath(explicitRoot);
        for (var current = new DirectoryInfo(Environment.CurrentDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "BeatzOnKeys.UI", "BeatzOnKeys.UI.csproj"))) return current.FullName;
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "BeatzOnKeys.UI", "BeatzOnKeys.UI.csproj"))) return current.FullName;
        throw new DirectoryNotFoundException("Use --root to identify the BeatzOnKeys source checkout.");
    }
}

internal sealed class FakeKeyboardHook : IKeyboardHook
{
    public event Action? KeyDown;
    public bool IsInstalled { get; private set; }
    public bool IsDisposed { get; private set; }
    public int SignalCount { get; private set; }
    public void Install() { ObjectDisposedException.ThrowIf(IsDisposed, this); IsInstalled = true; }
    public void Uninstall() => IsInstalled = false;
    public void Signal() { if (IsInstalled) { SignalCount++; KeyDown?.Invoke(); } }
    public void Dispose() { Uninstall(); IsDisposed = true; KeyDown = null; }
}

internal sealed class BindingErrorListener : TraceListener
{
    public List<string> Errors { get; } = [];
    public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
    public override void WriteLine(string? message) => Write(message);
}

internal sealed record Assertion(string Name, bool Passed, string Detail);
internal sealed record ScreenshotInfo(string Path, int Width, int Height, string Language, string Theme,
    double Progress, string Position, bool Running);

internal sealed class SmokeRun(string root, string session, App app, int abortedStartupCount)
{
    private readonly List<Assertion> _checks = [];
    private readonly List<ScreenshotInfo> _screenshots = [];
    private readonly List<string> _messages = [];
    private readonly BindingErrorListener _bindings = new();
    private readonly Dictionary<string, object?> _info = new();
    private readonly List<MainViewModel> _ownedModels = [];
    private MainWindow? _window;
    private MainViewModel? _vm;
    private FakeKeyboardHook? _hook;
    private ContinuousAudioPlayer? _player;
    private string[] _fixtures = [];
    private string SettingsPath => Path.Combine(session, "settings.json");
    private string ArtifactDirectory => Path.Combine(root, "artifacts", "ui-smoke");
    public bool Failed => _checks.Any(check => !check.Passed);

    public async Task RunAsync()
    {
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.DispatcherUnhandledException += (_, e) =>
        {
            Check("dispatcher.unhandled", false, e.Exception.ToString());
            e.Handled = true;
        };
        try
        {
            // Redirect process-local AppPaths as well as using explicit SettingsService paths.
            // This prevents ordinary audio/update logging and About's prepared-update lookup touching user app-data.
            RedirectAppPaths();
            _info["startupOperationsAborted"] = abortedStartupCount;
            Check("startup.operationAborted", abortedStartupCount > 0,
                "Exact BeatzOnKeys.UI.App constructor-posted startup operations aborted before the dispatcher pump: " + abortedStartupCount);
            _info["session"] = Friendly(session);
            _info["audioImplementation"] = typeof(ContinuousAudioPlayer).FullName;
            _info["keyboard"] = "Signal-only injectable fake; no physical typing, text data, or Notepad automation.";
            _info["loopMatrix"] = "Delegated to the core-test owner; this run tests UI AutoAdvance wiring only.";
            _info["physicalAudibility"] = "Not claimed. Real WaveOut state and MP3 decoder tested; no loopback recording requested.";
            await GenerateFixturesAsync();
            await Section("nativeHook", () => { CheckNativeHook(); return Task.CompletedTask; });
            Loc.Initialize("en");
            Theme.Apply(AppTheme.Dark, notify: false);
            _hook = new FakeKeyboardHook();
            _player = new ContinuousAudioPlayer();
            var settingsService = MakeSettingsService(SettingsPath);
            _vm = Own(new MainViewModel(new AppSettings { Language = "en" }, settingsService, _hook, _player));
            _vm.UserMessageRequested += message => _messages.Add(message);
            await _vm.InitializeAsync();
            Check("defaults.loop", _vm.Loop, "A fresh VM keeps Loop=true.");
            Check("defaults.typingVolumeAndIdle", Near(_vm.MinVolume, 0.2) && Near(_vm.MaxVolume, 1) && Near(_vm.IdleTimeout, 3) && _vm.PauseOnIdle,
                $"Fresh settings: Min={_vm.MinVolumePercent}, Max={_vm.MaxVolumePercent}, Idle={_vm.IdleTimeout}s, Pause={_vm.PauseOnIdle}.");
            Check("initialize.empty", _vm.Tracks.Count == 0 && !_vm.IsRunning && _vm.CanReset, _vm.StatusText);
            await _vm.AddFilesAsync(_fixtures.Take(3));
            Check("import.mp3", _vm.Tracks.Count == 3 && _vm.Tracks.All(track => track.IsAvailable && track.Duration > TimeSpan.Zero),
                string.Join("; ", _vm.Tracks.Select(track => $"{track.FileName}: {track.Duration}")));
            Check("import.activeSelection", _vm.SelectedTrack == _vm.Tracks.FirstOrDefault(), _vm.SelectedTrackText);
            Check("tracks.noProcessingStatusContract", typeof(TrackViewModel).GetProperty("Status") is null &&
                typeof(TrackViewModel).GetProperty("StatusText") is null, "Playlist rows are audio entries, not transcription jobs.");
            _window = new MainWindow(_vm);
            // Avoid unattended modal dialogs on test failures, while keeping the real hide-to-tray and picker lifecycle.
            var userMessage = typeof(MainWindow).GetMethod("ShowUserMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _vm.UserMessageRequested -= (Action<string>)userMessage.CreateDelegate(typeof(Action<string>), _window);
            SetField(app, "_mainWindow", _window);
            SetField(app, "_viewModel", _vm);
            SetField(app, "_settingsService", settingsService);
            app.MainWindow = _window;
            _vm.StateChanged += UpdateTray;
            Invoke(app, "SetupTrayIcon");
            _window.Show();
            await SettleAsync();
            Check("startup.productionBypassed", GetField<SettingsService>(app, "_settingsService") == settingsService &&
                GetField<DispatcherTimer>(app, "_updateTimer") is null && GetField<CancellationTokenSource>(app, "_updateCancellation") is null,
                "Exact App instance: production OnStartup operation aborted before pumping; explicit artifact settings only, no automatic update timer. Aborted startup operations=" + abortedStartupCount);
            Check("window.defaultSize", Near(_window.Width, 1120) && Near(_window.Height, 780),
                $"Actual XAML default {_window.Width}x{_window.Height}; expected 1120x780.");
            Check("window.minimumSize", Near(_window.MinWidth, 920) && Near(_window.MinHeight, 650),
                $"Actual XAML minimum {_window.MinWidth}x{_window.MinHeight}; expected 920x650.");
            Check("render.playlistNoStatuses", !Descendants<TextBlock>(_window).Any(t =>
                Regex.IsMatch(t.Text ?? "", @"^(Queued|Processing|Transcribing|Ready|В очереди|Обработка|Транскрибирование|Готово)$", RegexOptions.IgnoreCase)),
                "Rendered playlist has filename/path/duration/extension only.");
            await Section("lifecycle.audio", AudioLifecycleAsync);
            await Section("render.matrix", CaptureMatrixAsync);
            await Section("autoAdvance", AutoAdvanceAsync);
            await Section("missingTrack", MissingTrackAsync);
            await Section("settings.roundtrip", SettingsRoundtripAsync);
        }
        catch (Exception error) { Check("harness.exception", false, error.ToString()); }
        finally
        {
            await CleanupAsync();
            Check("bindings.errors", _bindings.Errors.Count == 0, string.Join(Environment.NewLine, _bindings.Errors));
            _info["bindingErrors"] = _bindings.Errors;
            _info["userMessages"] = _messages;
            PresentationTraceSources.DataBindingSource.Listeners.Remove(_bindings);
            await WriteReportsAsync();
            app.Shutdown(Failed ? 1 : 0);
        }
    }

    private void RedirectAppPaths()
    {
        var local = Path.Combine(session, "localappdata");
        // A writable ref into an existing backing field is confined to this disposable process.
        // It avoids relying on LOCALAPPDATA environment overrides, which GetFolderPath does not honor on Windows.
        SetProcessPath("RootDirectory", local);
        SetProcessPath("SettingsFile", Path.Combine(local, "unused-default-settings.json"));
        SetProcessPath("LogDirectory", Path.Combine(session, "logs"));
        SetProcessPath("UpdateDirectory", Path.Combine(session, "updates"));
        Check("isolation.appPaths", AppPaths.RootDirectory.StartsWith(session, StringComparison.OrdinalIgnoreCase) &&
            AppPaths.LogDirectory.StartsWith(session, StringComparison.OrdinalIgnoreCase) &&
            AppPaths.UpdateDirectory.StartsWith(session, StringComparison.OrdinalIgnoreCase), "All process-local app-data paths redirected under " + Friendly(session));
    }

    private delegate ref string StaticStringRef();
    private static void SetProcessPath(string property, string value)
    {
        var field = typeof(AppPaths).GetField($"<{property}>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(AppPaths).FullName, property);
        _ = typeof(AppPaths).GetProperty(property)!.GetValue(null); // Initialize before taking the reference.
        var method = new DynamicMethod("UiSmokePath_" + property, typeof(string).MakeByRefType(), Type.EmptyTypes,
            typeof(AppPaths).Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsflda, field);
        il.Emit(OpCodes.Ret);
        var accessor = (StaticStringRef)method.CreateDelegate(typeof(StaticStringRef));
        accessor() = value;
    }

    private SettingsService MakeSettingsService(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(session + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing a settings path outside this run's artifact session: " + path);
        var service = new SettingsService(path);
        var actual = GetField<string>(service, "_settingsFile");
        Check("settings.capturePath." + Path.GetFileName(path), string.Equals(actual, path, StringComparison.OrdinalIgnoreCase), Friendly(actual!));
        return service;
    }

    private async Task GenerateFixturesAsync()
    {
        const string ffmpeg = @"C:\msys64\ucrt64\bin\ffmpeg.exe";
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("ffmpeg is required for genuine MP3 fixtures.", ffmpeg);
        var fixtureDirectory = Path.Combine(ArtifactDirectory, "fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        _fixtures = new[] { "Afterglow - generated fixture.mp3", "Neon Steps - generated fixture.mp3",
            "Quiet Skyline - generated fixture.mp3", "Transition - short generated fixture.mp3" }
            .Select(name => Path.Combine(fixtureDirectory, name)).ToArray();
        var durations = new[] { 142.0, 196.0, 221.0, 0.36 };
        var frequencies = new[] { 440, 554, 659, 330 };
        for (var i = 0; i < _fixtures.Length; i++)
        {
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = false, RedirectStandardError = false, RedirectStandardInput = false };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                $"sine=frequency={frequencies[i]}:sample_rate=44100:duration={durations[i].ToString(CultureInfo.InvariantCulture)}",
                "-af", "volume=0.45", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "128k", "-metadata",
                "comment=Explicit UiSmoke generated fixture, not a commercial recording", _fixtures[i] }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed with exit " + process.ExitCode);
        }
        Check("fixtures.generatedMp3", _fixtures.All(path => new FileInfo(path).Length > 1000),
            "Four explicit generated MP3 fixtures; output inherited, no captured stdio pipe.");
        _info["fixtures"] = _fixtures.Select(Friendly).ToArray();
        _info["fixtureAmplitude"] = "ffmpeg sine x0.45; VM volume 0.15–0.18, modest test output.";
        _info["waveOutDevices"] = WaveOut.DeviceCount;
    }

    private void CheckNativeHook()
    {
        using var hook = new KeyboardHook();
        hook.Install();
        hook.Install();
        Check("nativeHook.installsIdempotently", hook.IsInstalled, "Actual WH_KEYBOARD_LL installed on the pumped STA; no text/event subscription.");
        hook.Uninstall();
        hook.Uninstall();
        Check("nativeHook.uninstallsIdempotently", !hook.IsInstalled, "Actual native hook uninstalled immediately, without generating physical input.");
        var modifier = typeof(KeyboardHook).GetMethod("IsBareModifier", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool IsModifier(uint key) => (bool)modifier.Invoke(null, new object[] { key })!;
        Check("nativeHook.modifierFilter", new uint[] { 0x10, 0x11, 0x12, 0x14, 0x5B, 0x5C, 0xA0, 0xA5 }.All(IsModifier) && !IsModifier(0x41),
            "Real signal filter excludes bare modifiers; letters may signal. This is not a physical typing test.");
        hook.Dispose();
        Check("nativeHook.dispose", !hook.IsInstalled, "Dispose leaves no native hook installed.");
    }

    private void UpdateTray() => Invoke(app, "UpdateTrayState");
    private Forms.NotifyIcon? Tray => GetField<Forms.NotifyIcon>(app, "_trayIcon");
    private IWavePlayer? Output => _player is null ? null : GetField<IWavePlayer>(_player, "_output");

    private async Task AudioLifecycleAsync()
    {
        var vm = _vm!;
        var hook = _hook!;
        var player = _player!;
        if (WaveOut.DeviceCount == 0) throw new InvalidOperationException("No WaveOut output device; real audio smoke cannot pass in this environment.");
        vm.MinVolumePercentValue = 20;
        vm.MaxVolumePercentValue = 100;
        vm.IdleTimeout = 3;
        vm.PauseOnIdle = true;
        vm.AutoAdvance = false;
        vm.Loop = true;
        Check("sliders.vmUnits", Near(vm.MinVolume, 0.2) && Near(vm.MaxVolume, 1) &&
            vm.MinVolumePercent == "20%" && vm.MaxVolumePercent == "100%", $"Min={vm.MinVolumePercent}, Max={vm.MaxVolumePercent}.");
        var minSlider = Descendants<Slider>(_window!).Single(s => BindingOperations.GetBinding(s, RangeBase.ValueProperty)?.Path?.Path == nameof(vm.MinVolumePercentValue));
        var maxSlider = Descendants<Slider>(_window!).Single(s => BindingOperations.GetBinding(s, RangeBase.ValueProperty)?.Path?.Path == nameof(vm.MaxVolumePercentValue));
        var idleSlider = Descendants<Slider>(_window!).Single(s => BindingOperations.GetBinding(s, RangeBase.ValueProperty)?.Path?.Path == nameof(vm.IdleTimeout));
        Check("sliders.boundRanges", Near(minSlider.Minimum, 0) && Near(minSlider.Maximum, 50) &&
            Near(maxSlider.Minimum, 0) && Near(maxSlider.Maximum, 100) && Near(idleSlider.Minimum, 1) && Near(idleSlider.Maximum, 10),
            "Actual rendered Min/Max/IdleTimeout sliders use percent/second units and required ranges.");
        var increment = minSlider.SmallChange;
        Slider.IncreaseSmall.Execute(null, minSlider);
        await SettleAsync();
        Check("sliders.minSmallIncrement", increment > 0 && Near(vm.MinVolumePercentValue, 20 + increment),
            $"Actual IncreaseSmall: min={vm.MinVolumePercentValue}, small={increment}.");
        Slider.DecreaseSmall.Execute(null, minSlider);
        await SettleAsync();
        Check("sliders.minSmallDecrement", Near(vm.MinVolumePercentValue, 20), "DecreaseSmall restores the bound quiet-volume value.");
        var idleCheckbox = Descendants<CheckBox>(_window!).Single(c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path?.Path == nameof(vm.PauseOnIdle));
        Check("idleCheckbox.bound", idleCheckbox.IsChecked == true, "Rendered PauseOnIdle checkbox is bound and enabled by default.");
        vm.StartCommand.Execute(null);
        await UntilAsync(() => vm.IsRunning && _window!.IsVisible && vm.StopCommand.CanExecute(null), 5000, "StartCommand");
        Check("start.windowStaysVisible", _window!.IsVisible && Tray?.Visible == true && hook.IsInstalled && vm.IsRunning,
            $"Visible={_window!.IsVisible}; tray={Tray?.Visible}; hook={hook.IsInstalled}; running={vm.IsRunning}.");
        var startPosition = player.Position;
        var startProgress = vm.PlaybackProgress;
        Check("start.continuousAtMinimum", player.IsPlaying && !player.IsIdlePaused &&
            Output?.PlaybackState == PlaybackState.Playing && Math.Abs(player.CurrentGain - 0.2) < 0.01 && startPosition.TotalSeconds < 0.75,
            $"Output={Output?.GetType().Name}, state={Output?.PlaybackState}, gain={player.CurrentGain:F3}, position={startPosition.TotalSeconds:F3}s.");
        await Task.Delay(600);
        Check("start.advancesWithoutKeys", hook.SignalCount == 0 && player.IsPlaying && player.Position > startPosition + TimeSpan.FromMilliseconds(300) &&
            vm.PlaybackProgress > startProgress, $"No keys: position={player.Position.TotalSeconds:F3}s, progress={vm.PlaybackProgress:F4}%.");
        hook.Signal();
        await UntilAsync(() => player.CurrentGain >= 0.99, 1500, "typing volume attack");
        Check("signal.actualWaveOutPlaying", Output is WaveOut && Output.PlaybackState == PlaybackState.Playing && player.IsPlaying,
            $"Output={Output?.GetType().Name}, state={Output?.PlaybackState}, gain={player.CurrentGain:F3}.");
        var typingPosition = player.Position;
        var gaps = 0;
        var samples = 0;
        for (var key = 0; key < 24; key++)
        {
            hook.Signal();
            for (var sample = 0; sample < 3; sample++)
            {
                await Task.Delay(8);
                samples++;
                if (!player.IsPlaying || Output?.PlaybackState != PlaybackState.Playing) gaps++;
            }
        }
        Check("rapidSignals.volumeOnlyNoDevicePause", gaps == 0 && player.Position > typingPosition && player.CurrentGain >= 0.99,
            $"24 signals; {samples} actual device/state samples, gaps={gaps}; position progresses normally.");
        var releasedPosition = player.Position;
        await UntilAsync(() => Math.Abs(player.CurrentGain - 0.2) < 0.01, 1500, "typing release to minimum");
        Check("release.quietButPlaying", player.IsPlaying && !player.IsIdlePaused && Output?.PlaybackState == PlaybackState.Playing && player.Position > releasedPosition,
            $"After hold/release: gain={player.CurrentGain:F3}, state={Output?.PlaybackState}, position={player.Position.TotalSeconds:F3}s.");
        await UntilAsync(() => player.IsIdlePaused, 4500, "3-second idle pause");
        await SettleAsync();
        var frozenPosition = player.Position;
        var frozen = vm.PlaybackProgress;
        var frozenText = vm.PlaybackPositionText;
        await Task.Delay(350);
        Check("idle.pausedFrozen", !player.IsPlaying && player.IsIdlePaused && player.Position == frozenPosition && Near(vm.PlaybackProgress, frozen) &&
            vm.PlaybackPositionText == frozenText && Output?.PlaybackState == PlaybackState.Paused && vm.IsRunning && hook.IsInstalled,
            $"Progress={frozen:F4}%, position={frozenText}, actual device={Output?.PlaybackState}.");
        hook.Signal();
        await UntilAsync(() => player.IsPlaying && !player.IsIdlePaused, 1000, "idle resume");
        Check("idle.resumeSamePositionInstantMaximum", player.CurrentGain >= 0.99 && player.Position >= frozenPosition &&
            player.Position - frozenPosition < TimeSpan.FromMilliseconds(750) && Output?.PlaybackState == PlaybackState.Playing,
            $"First key: gain={player.CurrentGain:F3}; {frozenPosition.TotalSeconds:F3}s -> {player.Position.TotalSeconds:F3}s (normal output buffering allowed).");
        vm.PauseOnIdle = false;
        var continuousPosition = player.Position;
        var idleClock = Stopwatch.StartNew();
        var idleSamples = 0;
        var idleGaps = 0;
        while (idleClock.Elapsed.TotalSeconds < vm.IdleTimeout + 0.5)
        {
            await Task.Delay(20);
            idleSamples++;
            if (!player.IsPlaying || player.IsIdlePaused || Output?.PlaybackState != PlaybackState.Playing) idleGaps++;
        }
        var continuousAdvance = (player.Position - continuousPosition).TotalSeconds;
        Check("idle.disabledStaysPlayingAtMinimum", idleGaps == 0 && continuousAdvance > 3 &&
            Math.Abs(player.CurrentGain - 0.2) < 0.01, $"{idleSamples} actual state samples over {idleClock.Elapsed.TotalSeconds:F2}s; audio advanced {continuousAdvance:F3}s; gaps={idleGaps}, gain={player.CurrentGain:F3}.");
        vm.PauseOnIdle = true;
        await UntilAsync(() => player.IsIdlePaused, 1500, "idle pause re-enabled");
        app.ShowMainWindow();
        await SettleAsync();
        Check("tray.showRestores", _window!.IsVisible && vm.IsRunning, "ShowMainWindow restores the real window without stopping the fake hook.");
        _window!.Close();
        await SettleAsync();
        Check("window.closeHides", !_window.IsVisible && vm.IsRunning && hook.IsInstalled, "Real OnClosing cancels close and hides to tray.");
        _window.WindowState = WindowState.Minimized;
        app.ShowMainWindow();
        await SettleAsync();
        Check("tray.showNormalizesMinimized", _window.IsVisible && _window.WindowState == WindowState.Normal, _window.WindowState.ToString());
        await StopAsync(vm);
        Check("stop.unhooksSilences", !vm.IsRunning && !hook.IsInstalled && !player.IsPlaying && !player.IsIdlePaused && Output?.PlaybackState == PlaybackState.Stopped,
            $"Running={vm.IsRunning}; hook={hook.IsInstalled}; audio={player.IsPlaying}; device={Output?.PlaybackState}.");
        var afterStop = player.Position;
        hook.Signal();
        await Task.Delay(80);
        Check("stop.ignoresSignals", player.Position == afterStop, "Signal after uninstall is ignored.");
        var restartsAtBeginning = true;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            vm.StartCommand.Execute(null);
            await UntilAsync(() => vm.IsRunning && vm.StopCommand.CanExecute(null), 5000, "restart cycle");
            restartsAtBeginning &= player.IsPlaying && !player.IsIdlePaused && player.Position.TotalSeconds < 0.75;
            await Task.Delay(100);
            await StopAsync(vm);
        }
        Check("lifecycle.repeatedStartStopFromBeginning", restartsAtBeginning && !vm.IsRunning && !hook.IsInstalled && !player.IsPlaying,
            "Three additional Start/Stop cycles play immediately from the beginning without stale worker/hook/audio.");
        // Preserve one genuine idle-paused position for every theme/language preview; never alter audio position.
        vm.StartCommand.Execute(null);
        await UntilAsync(() => vm.IsRunning && vm.StopCommand.CanExecute(null), 5000, "preview start");
        await Task.Delay(700);
        for (var key = 0; key < 64; key++) { hook.Signal(); await Task.Delay(5); }
        await UntilAsync(() => player.IsIdlePaused, 4500, "preview idle pause");
        app.ShowMainWindow();
        await SettleAsync();
    }

    private async Task CaptureMatrixAsync()
    {
        var vm = _vm!;
        var window = _window!;
        var baseline = vm.PlaybackProgress;
        foreach (var light in new[] { false, true })
        {
            if (Theme.IsLight != light) vm.ToggleThemeCommand.Execute(null);
            foreach (var language in new[] { "en", "ru" })
            {
                vm.SetLanguageCommand.Execute(language);
                await SettleAsync();
                Check($"commands.{Theme.CurrentCode}.{language}", vm.IsLightTheme == light && vm.IsRussian == (language == "ru"),
                    "Real theme/language commands update VM and application resources.");
                CheckResourceReferences(window, $"main.{Theme.CurrentCode}.{language}");
                foreach (var size in new[] { new Size(920, 650), new Size(1120, 780) })
                {
                    window.WindowState = WindowState.Normal;
                    window.Width = size.Width;
                    window.Height = size.Height;
                    await SettleAsync();
                    var label = $"main.{Theme.CurrentCode}.{language}.{size.Width}x{size.Height}";
                    CheckVisualTree(window, label);
                    CheckMainLayout(window, label);
                }
                var filename = light ? $"preview-light-{language}.png" : $"preview-{language}.png";
                SaveScreenshot(window, filename);
                Check($"preview.equalState.{Theme.CurrentCode}.{language}", Near(vm.PlaybackProgress, baseline),
                    $"Same actual VM progress {baseline:F4}%; no synthetic VM property edits.");
                if (language == "ru") await CaptureAboutAsync(light);
            }
        }
        window.WindowState = WindowState.Maximized;
        await SettleAsync();
        CheckVisualTree(window, "main.maximized");
        Check("window.maximizeLifecycle", window.WindowState == WindowState.Maximized, $"Actual rendered {window.ActualWidth}x{window.ActualHeight}.");
        window.WindowState = WindowState.Normal;
        window.Width = 1120;
        window.Height = 780;
        await SettleAsync();
    }

    private async Task CaptureAboutAsync(bool light)
    {
        app.ShowAboutWindow();
        var about = GetField<AboutWindow>(app, "_aboutWindow")!;
        await SettleAsync();
        await UntilAsync(() => GetField<bool>(about, "_busy") == false, 20000, "About update API check");
        await SettleAsync();
        var label = "about." + Theme.CurrentCode + ".ru";
        var texts = Descendants<TextBlock>(about).Where(text => text.IsVisible).ToArray();
        var primary = (Brush)about.FindResource("TextPrimaryBrush");
        var titles = new[] { "BeatzOnKeys", Loc.T("About_Developer"), Loc.T("Upd_Title") };
        foreach (var title in titles)
        {
            var text = texts.FirstOrDefault(t => t.Text == title);
            Check(label + ".primary." + title, text is not null && SameBrush(text.Foreground, primary),
                $"Rendered '{title}' foreground={BrushDescription(text?.Foreground)}, expected={BrushDescription(primary)}.");
        }
        Check(label + ".noFoundation", !texts.Any(t => t.Text.Contains("MusicCat Design Foundation", StringComparison.OrdinalIgnoreCase)) &&
            !Descendants<Button>(about).Any(b => (b.Content?.ToString() ?? "").Contains("MusicCat Design Foundation", StringComparison.OrdinalIgnoreCase)),
            "No MusicCat Design Foundation button or text in the actual About visual tree.");
        CheckResourceReferences(about, label);
        CheckVisualTree(about, label);
        var update = UpdateService.LastResult;
        _info["aboutUpdate." + Theme.CurrentCode] = new { current = update?.Current.Display, latest = update?.Latest?.Display,
            status = update?.Status.ToString(), text = texts.FirstOrDefault(t => t.Name == "UpdateStatusText")?.Text };
        SaveScreenshot(about, light ? "about-light-ru.png" : "about-ru.png");
        about.Close();
        await SettleAsync();
        Check(label + ".closed", GetField<AboutWindow>(app, "_aboutWindow") is null && !about.IsVisible, "Real App About Closed clears its private window slot.");
    }

    private void CheckResourceReferences(Window window, string label)
    {
        var missing = new HashSet<string>();
        foreach (var filename in new[] { "MainWindow.xaml", "AboutWindow.xaml", "Styles.xaml" })
        {
            var text = File.ReadAllText(Path.Combine(root, "BeatzOnKeys.UI", filename));
            foreach (Match match in Regex.Matches(text, @"\{(?:Static|Dynamic)Resource\s+([^}\s]+)\}"))
            {
                var key = match.Groups[1].Value;
                if (window.TryFindResource(key) is null) missing.Add(key);
            }
        }
        foreach (var filename in new[] { "App.xaml.cs", "MainWindow.xaml.cs", "AboutWindow.xaml.cs",
            "ViewModels/MainViewModel.cs", "ViewModels/TrackViewModel.cs" })
        {
            var text = File.ReadAllText(Path.Combine(root, "BeatzOnKeys.UI", filename));
            foreach (Match match in Regex.Matches(text, "\\\"((?:L_|About_|Upd_)[A-Za-z0-9_]+)\\\""))
            {
                var key = match.Groups[1].Value;
                if (window.TryFindResource(key) is null) missing.Add(key);
            }
        }
        Check(label + ".resources", missing.Count == 0, missing.Count == 0 ? "All static/dynamic XAML resources and quoted UI-code localization keys resolve at runtime." : string.Join(", ", missing));
    }

    private void CheckVisualTree(Window window, string label)
    {
        var iconFailures = new List<string>();
        var pairFailures = new List<string>();
        var accentFailures = new List<string>();
        var iconCount = 0;
        var pairCount = 0;
        var accentCount = 0;
        foreach (var button in Descendants<Button>(window).Where(b => b.IsVisible && b.ActualWidth > 0 && b.ActualHeight > 0))
        {
            var presenter = Descendants<ContentPresenter>(button).FirstOrDefault(p => ReferenceEquals(p.TemplatedParent, button));
            if (button.Content is TextBlock icon && IsIcon(icon))
            {
                iconCount++;
                var center = new Point(button.ActualWidth / 2, button.ActualHeight / 2);
                if (presenter is null || presenter.HorizontalAlignment != HorizontalAlignment.Center || presenter.VerticalAlignment != VerticalAlignment.Center ||
                    !CentersNear(presenter, button, center) || !CentersNear(icon, button, center))
                    iconFailures.Add($"{icon.Text}: presenter/icon not geometrically centered in {button.ActualWidth}x{button.ActualHeight}");
            }
            if (button.Content is StackPanel panel && panel.Orientation == Orientation.Horizontal)
            {
                var parts = panel.Children.OfType<TextBlock>().ToArray();
                if (parts.Length >= 2 && IsIcon(parts[0]))
                {
                    pairCount++;
                    var first = Bounds(parts[0], panel);
                    var second = Bounds(parts[1], panel);
                    if (Math.Abs(first.Top + first.Height / 2 - second.Top - second.Height / 2) > 1.6 || first.Right > second.Left + 0.5)
                        pairFailures.Add(parts[1].Text + " icon/label not vertically aligned or overlapping");
                }
            }
            if (ReferenceEquals(button.Style, window.TryFindResource("AccentButtonStyle")))
            {
                accentCount++;
                var expected = (Brush)window.FindResource("OnAccentBrush");
                if (!SameBrush(button.Foreground, expected)) accentFailures.Add(button.Content + " button Foreground overridden");
                foreach (var text in Descendants<TextBlock>(button))
                    if (!SameBrush(text.Foreground, expected)) accentFailures.Add(text.Text + " generated TextBlock Foreground overridden");
            }
        }
        Check(label + ".iconCenters", iconCount > 0 && iconFailures.Count == 0, $"{iconCount} icon-only buttons; " + string.Join("; ", iconFailures));
        Check(label + ".iconLabelAlignment", pairFailures.Count == 0, $"{pairCount} icon+label buttons; " + string.Join("; ", pairFailures));
        var foreground = ((SolidColorBrush)window.FindResource("OnAccentBrush")).Color;
        var background = ((SolidColorBrush)window.FindResource("AccentBrush")).Color;
        var contrast = Contrast(foreground, background);
        var expectedForeground = Theme.IsLight ? "#FFFFFFFF" : "#FF04222E";
        Check(label + ".accentContrast", accentCount > 0 && accentFailures.Count == 0 && contrast >= 4.5 && foreground.ToString() == expectedForeground,
            $"{accentCount} accent buttons; fg={foreground}, bg={background}, ratio={contrast:F2}; " + string.Join("; ", accentFailures));
        var sliders = Descendants<Slider>(window).ToArray();
        if (window is MainWindow)
            Check(label + ".sliderLimits", sliders.Length == 3 && sliders.Any(s => Near(s.Minimum, 0) && Near(s.Maximum, 100)) &&
                sliders.Any(s => Near(s.Minimum, 0) && Near(s.Maximum, 50)) && sliders.Any(s => Near(s.Minimum, 1) && Near(s.Maximum, 10)) &&
                sliders.All(s => s.Value >= s.Minimum && s.Value <= s.Maximum),
                string.Join("; ", sliders.Select(s => $"[{s.Minimum},{s.Maximum}]={s.Value}")));
    }

    private void CheckMainLayout(MainWindow window, string label)
    {
        var viewport = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        var failures = new List<string>();
        foreach (var element in Descendants<FrameworkElement>(window).Where(e => e.IsVisible && e.ActualWidth > 0 && e.ActualHeight > 0 &&
            (e is Button || e is Slider || e is CheckBox || e is TextBlock)))
        {
            // Scrolled extent is intentionally allowed to exceed its viewport; it remains reachable by scrolling.
            if (HasAncestor<ScrollContentPresenter>(element)) continue;
            var bounds = Bounds(element, window);
            if (bounds.Left < -1.6 || bounds.Top < -1.6 || bounds.Right > viewport.Right + 1.6 || bounds.Bottom > viewport.Bottom + 1.6)
                failures.Add((element is TextBlock text ? text.Text : element.GetType().Name) + ": " + bounds);
        }
        var drop = (Border)window.FindName("DropBorder");
        var title = Descendants<TextBlock>(drop).First(t => t.Text == Loc.T("L_DropTitle"));
        var add = Descendants<Button>(drop).First();
        var titleBounds = Bounds(title, window);
        var addBounds = Bounds(add, window);
        if (titleBounds.IntersectsWith(addBounds)) failures.Add("Drop title overlaps Add files: " + titleBounds + " / " + addBounds);
        Check(label + ".layoutBounds", failures.Count == 0 && window.ActualWidth >= 918 && window.ActualHeight >= 648,
            $"Actual {window.ActualWidth}x{window.ActualHeight}; intentional scroll content excluded; " + string.Join("; ", failures));
    }

    private async Task AutoAdvanceAsync()
    {
        var hook = new FakeKeyboardHook();
        var player = new ContinuousAudioPlayer();
        var vm = Own(new MainViewModel(new AppSettings { MinVolume = 0.2, MaxVolume = 1, IdleTimeout = 3, PauseOnIdle = true, AutoAdvance = true, Loop = false },
            MakeSettingsService(Path.Combine(session, "auto-advance-settings.json")), hook, player));
        vm.UserMessageRequested += message => _messages.Add("AutoAdvance: " + message);
        await vm.InitializeAsync();
        await vm.AddFilesAsync(new[] { _fixtures[3], _fixtures[1] });
        var shortTrack = vm.Tracks[0];
        var nextTrack = vm.Tracks[1];
        vm.StartCommand.Execute(null);
        await UntilAsync(() => vm.IsRunning && vm.StopCommand.CanExecute(null), 5000, "auto advance start");
        await UntilAsync(() => vm.SelectedTrack == nextTrack, 3000, "natural end auto advance selection");
        Check("autoAdvance.naturalEndWithoutTyping", vm.SelectedTrack == nextTrack && vm.IsRunning && hook.IsInstalled &&
            hook.SignalCount == 0 && player.IsPlaying,
            $"{shortTrack.FileName} ({shortTrack.Duration.TotalSeconds:F3}s) -> {vm.SelectedTrackText}; zero keyboard signals; progress={vm.PlaybackProgress:F4}%.");
        await StopAsync(vm);
        await vm.DisposeAsync();
        _ownedModels.Remove(vm);
        Check("autoAdvance.disposed", hook.IsDisposed && !player.IsPlaying, "Independent custom-settings VM disposed.");
    }

    private async Task MissingTrackAsync()
    {
        var hook = new FakeKeyboardHook();
        var missing = new TrackState { SourcePath = Path.Combine(session, "explicit-missing-fixture.mp3") };
        var vm = Own(new MainViewModel(new AppSettings { Tracks = new List<TrackState> { missing }, SelectedTrackId = missing.Id },
            MakeSettingsService(Path.Combine(session, "missing-track-settings.json")), hook, new ContinuousAudioPlayer()));
        await vm.InitializeAsync();
        var track = vm.Tracks.Single();
        Check("missingTrack.unavailableNoStart", !track.IsAvailable && !vm.StartCommand.CanExecute(null), "A genuinely absent fixture remains unavailable and cannot start.");
        foreach (var language in new[] { "en", "ru" })
        {
            vm.SetLanguageCommand.Execute(language);
            var localized = Loc.T("L_ErrorSourceMissing");
            Check("missingTrack.localizedTooltip." + language, localized != "L_ErrorSourceMissing" && track.TooltipText.Contains(localized) &&
                !track.TooltipText.Contains("L_ErrorSourceMissing", StringComparison.Ordinal), "Real unavailable TrackViewModel.TooltipText: " + track.TooltipText);
        }
        await vm.DisposeAsync();
        _ownedModels.Remove(vm);
        Check("missingTrack.disposed", hook.IsDisposed && !hook.IsInstalled, "Missing-track VM uses custom settings and disposes its signal-only hook.");
    }

    private async Task SettingsRoundtripAsync()
    {
        var vm = _vm!;
        await StopAsync(vm);
        vm.MinVolumePercentValue = 25;
        vm.MaxVolumePercentValue = 80;
        vm.IdleTimeout = 4;
        vm.PauseOnIdle = false;
        vm.AutoAdvance = true;
        vm.Loop = false;
        vm.SetLanguageCommand.Execute("ru");
        if (!Theme.IsLight) vm.ToggleThemeCommand.Execute(null);
        vm.SelectedTrack = vm.Tracks[1];
        await vm.SaveAsync();
        var expectedTracks = vm.Tracks.Select(t => t.ToState()).ToArray();
        var selected = vm.SelectedTrack.Id;
        if (_window is not null) { _window.AllowClose = true; _window.Close(); _window = null; }
        vm.StateChanged -= UpdateTray;
        await vm.DisposeAsync();
        _ownedModels.Remove(vm);
        Check("dispose.unhooksAndSaves", _hook!.IsDisposed && !_hook.IsInstalled && !_player!.IsPlaying, "DisposeAsync waits for the worker, unhooks, and disposes audio.");
        var service = MakeSettingsService(SettingsPath);
        var loaded = await service.LoadAsync();
        Check("settings.allFieldsPersisted", loaded.SchemaVersion == 3 && loaded.SchemaVersion == AppSettings.CurrentSchemaVersion &&
            Near(loaded.MinVolume, 0.25) && Near(loaded.MaxVolume, 0.8) && Near(loaded.IdleTimeout, 4) && !loaded.PauseOnIdle &&
            loaded.AutoAdvance && !loaded.Loop && loaded.Language == "ru" && loaded.Theme == "light" && loaded.SelectedTrackId == selected &&
            loaded.Tracks.Count == expectedTracks.Length && loaded.Tracks.Zip(expectedTracks).All(pair => pair.First.Id == pair.Second.Id &&
                pair.First.SourcePath == pair.Second.SourcePath && pair.First.AddedAt == pair.Second.AddedAt),
            $"schema={loaded.SchemaVersion}, min={loaded.MinVolume}, max={loaded.MaxVolume}, idle={loaded.IdleTimeout}s, pause={loaded.PauseOnIdle}, auto={loaded.AutoAdvance}, loop={loaded.Loop}, language={loaded.Language}, theme={loaded.Theme}, selected={loaded.SelectedTrackId}; path={Friendly(SettingsPath)}.");
        var raw = await File.ReadAllTextAsync(SettingsPath);
        using var document = JsonDocument.Parse(raw);
        var fields = document.RootElement.EnumerateObject().ToArray();
        JsonElement Value(string name) => fields.Single(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        Check("settings.jsonUnits", Near(Value(nameof(AppSettings.MinVolume)).GetDouble(), 0.25) &&
            Near(Value(nameof(AppSettings.MaxVolume)).GetDouble(), 0.8) && Near(Value(nameof(AppSettings.IdleTimeout)).GetDouble(), 4) &&
            !Value(nameof(AppSettings.PauseOnIdle)).GetBoolean(), "JSON volumes are normalized fractions; idle timeout is seconds; PauseOnIdle is boolean.");
        Check("settings.schema3Only", !Regex.IsMatch(raw, @"""(?:Volume|StepSeconds|Status|MidiPath|NoteEvents|Transcription|Sensitivity|Instrument)""", RegexOptions.IgnoreCase),
            "Persisted schema excludes obsolete volume/timing/transcription/status/instrument fields.");
        Loc.Initialize(loaded.Language);
        Theme.Apply(Theme.Parse(loaded.Theme));
        var restoredHook = new FakeKeyboardHook();
        var restored = Own(new MainViewModel(loaded, service, restoredHook, new ContinuousAudioPlayer()));
        await restored.InitializeAsync();
        Check("settings.newVmRoundtrip", Near(restored.MinVolume, 0.25) && Near(restored.MinVolumePercentValue, 25) && restored.MinVolumePercent == "25%" &&
            Near(restored.MaxVolume, 0.8) && Near(restored.MaxVolumePercentValue, 80) && restored.MaxVolumePercent == "80%" &&
            Near(restored.IdleTimeout, 4) && !restored.PauseOnIdle && restored.AutoAdvance && !restored.Loop && restored.IsRussian && restored.IsLightTheme &&
            restored.SelectedTrack?.Id == selected && restored.Tracks.Count == 3 && restored.Tracks.All(t => t.IsAvailable) && !restored.IsRunning && !restoredHook.IsInstalled,
            $"New VM: Min={restored.MinVolumePercent}, Max={restored.MaxVolumePercent}, idle={restored.IdleTimeoutText}, pause={restored.PauseOnIdle}; {restored.SelectedTrackText}; {restored.PlaybackPositionText}.");
        await restored.DisposeAsync();
        _ownedModels.Remove(restored);
        _info["roundtripSettingsPath"] = Friendly(SettingsPath);
        _info["settingsConstructorPolicy"] = "Every SettingsService constructed by UiSmoke receives an explicit file under this session; default path is never read or written.";
    }

    private MainViewModel Own(MainViewModel vm) { _ownedModels.Add(vm); return vm; }
    private async Task CleanupAsync()
    {
        var about = GetField<AboutWindow>(app, "_aboutWindow");
        about?.Close();
        if (_window is not null) { _window.AllowClose = true; _window.Close(); }
        if (_vm is not null) _vm.StateChanged -= UpdateTray;
        foreach (var vm in _ownedModels.ToArray())
        {
            try { await vm.DisposeAsync(); }
            catch (Exception error) { Check("cleanup.dispose", false, error.ToString()); }
        }
        Invoke(app, "DisposeTrayIcon");
        Check("cleanup.windowsTray", app.Windows.Count == 0 && Tray is null, $"Owned windows={app.Windows.Count}, tray disposed={Tray is null}.");
        Check("isolation.defaultSettingsUnused", !File.Exists(AppPaths.SettingsFile), "The redirected default-settings sentinel was not created; production OnStartup was bypassed.");
    }

    private void SaveScreenshot(Window window, string filename)
    {
        // Render real WPF visuals, composited over a fixed active-theme fallback instead of transparent Mica/desktop.
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen())
            drawing.DrawRectangle((Brush)window.FindResource("WindowFallbackBrush"), null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(ArtifactDirectory, filename);
        using (var stream = File.Create(path)) encoder.Save(stream);
        _screenshots.Add(new ScreenshotInfo(Friendly(path), width, height, Loc.CurrentCode, Theme.CurrentCode,
            _vm?.PlaybackProgress ?? 0, _vm?.PlaybackPositionText ?? "", _vm?.IsRunning == true));
        Console.WriteLine("SCREENSHOT " + Friendly(path));
    }

    private async Task WriteReportsAsync()
    {
        var report = new { passed = !Failed, utc = DateTimeOffset.UtcNow, session = Friendly(session),
            assertionCount = _checks.Count, passedCount = _checks.Count(c => c.Passed), failedCount = _checks.Count(c => !c.Passed),
            assertions = _checks, screenshots = _screenshots, info = _info };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(session, "report.json"), json);
        await File.WriteAllTextAsync(Path.Combine(ArtifactDirectory, "report-latest.json"), json);
        var markdown = new StringBuilder("# UiSmoke report\n\n").AppendLine(Failed ? "**FAILED**" : "**PASSED**")
            .AppendLine($"\n{_checks.Count(c => c.Passed)}/{_checks.Count} assertions passed.\n")
            .AppendLine("Real WPF MainWindow/AboutWindow; actual MP3 decoding/WaveOut; signal-only fake hook. No physical typing or audible-loopback assertion. All application paths are process-locally redirected to this artifact session.\n");
        foreach (var check in _checks) markdown.AppendLine($"- [{(check.Passed ? "x" : " ")}] **{check.Name}** — {check.Detail.Replace("\n", " ").Replace("\r", "")}");
        markdown.AppendLine("\n## Screenshots\n");
        foreach (var image in _screenshots) markdown.AppendLine($"- {image.Path} ({image.Width}×{image.Height}; {image.Theme}/{image.Language}; running={image.Running}; {image.Position})");
        await File.WriteAllTextAsync(Path.Combine(session, "report.md"), markdown.ToString());
        Console.WriteLine($"RESULT {(Failed ? "FAIL" : "PASS")} {_checks.Count(c => c.Passed)}/{_checks.Count}; report={Friendly(Path.Combine(session, "report.json"))}");
    }

    private void Check(string name, bool passed, string detail)
    {
        _checks.Add(new Assertion(name, passed, detail));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
    }
    private async Task Section(string name, Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { Check(name + ".exception", false, error.ToString()); }
    }
    private static async Task StopAsync(MainViewModel vm)
    {
        if (!vm.IsRunning) return;
        await UntilAsync(() => vm.StopCommand.CanExecute(null), 5000, "Stop command ready");
        vm.StopCommand.Execute(null);
        await UntilAsync(() => !vm.IsRunning && vm.CanReset, 5000, "Stop command");
    }
    private static async Task SettleAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        foreach (Window window in Application.Current.Windows) window.UpdateLayout();
    }
    private static async Task UntilAsync(Func<bool> predicate, int timeout, string reason)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException($"Timed out after {timeout}ms waiting for {reason}.");
            await Task.Delay(10);
        }
        await Dispatcher.Yield(DispatcherPriority.Background);
    }
    private string Friendly(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.00001;
    private static bool SameBrush(Brush? first, Brush? second) => first is SolidColorBrush a && second is SolidColorBrush b && a.Color == b.Color;
    private static string BrushDescription(Brush? brush) => brush is SolidColorBrush solid ? solid.Color.ToString() : brush?.ToString() ?? "null";
    private static bool IsIcon(TextBlock text) => text.FontFamily.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase) ||
        (text.Text.Length == 1 && text.Text[0] >= '\uE000' && text.Text[0] <= '\uF8FF');
    private static bool CentersNear(FrameworkElement element, Visual ancestor, Point expected)
    {
        var rect = Bounds(element, ancestor);
        return Math.Abs(rect.Left + rect.Width / 2 - expected.X) <= 1.6 && Math.Abs(rect.Top + rect.Height / 2 - expected.Y) <= 1.6;
    }
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte channel) { var value = channel / 255.0; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
        static double Lum(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var a = Lum(first); var b = Lum(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject rootObject) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(rootObject); i++)
        {
            var child = VisualTreeHelper.GetChild(rootObject, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static bool HasAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T) return true;
        return false;
    }
    private static void SetField(object instance, string name, object? value) => typeof(App)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static T? GetField<T>(object instance, string name)
    {
        var type = instance is App ? typeof(App) : instance.GetType();
        return (T?)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    }
    private static void Invoke(object instance, string name) => typeof(App).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
}
