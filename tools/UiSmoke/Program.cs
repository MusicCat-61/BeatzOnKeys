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

using BeatzOnKeys.Core.YouTube;
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
        // Pin the storage mode: the smoke must never touch the real user folders, and the folder
        // picker cannot be answered by a headless run.
        Environment.SetEnvironmentVariable(StorageResolver.ModeVariable, nameof(StorageMode.LocalAppData));
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
            // The folder question is asked in the app's own dialog, so its text must be translated:
            // a missing key once surfaced as a raw "Storage_PickFolder" in a title bar.
            var storageKeys = new[] { "Storage_ExplainTitle", "Storage_ExplainBody", "Storage_ChooseButton",
                "Storage_LaterButton", "Storage_ChosenBody", "Storage_PickFolder", "Storage_RestartButton" };
            var unresolvedStorage = storageKeys.Where(key => Loc.T(key) == key).ToArray();
            // With the mode pinned the question must stay off, or a headless run would hang on a
            // dialog nobody can answer.
            // The question method existed but was never called, so the prompt silently never
            // appeared. Calling it here proves it runs and returns at once when nothing is pending.
            Invoke(app, "OfferStorageFolderAsync");
            Check("storage.questionMethodRuns", typeof(App).GetMethod("OfferStorageFolderAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) is not null &&
                GetField<bool>(app, "_storageChoicePending") == false,
                "OfferStorageFolder is present and does nothing while the mode is pinned.");
            Check("storage.noPromptWhenPinned", GetField<bool>(app, "_storageChoicePending") == false,
                "A pinned storage mode must never open the folder question.");
            Check("storage.dialogTextLocalized", unresolvedStorage.Length == 0,
                unresolvedStorage.Length == 0 ? "All storage dialog strings resolve." : "Raw keys: " + string.Join(", ", unresolvedStorage));
            Theme.Apply(AppTheme.Dark, notify: false);
            // The YouTube section must not read, write or delete the real user cache during tests.
            var youTubeRoot = Path.Combine(session, "youtube");
            Directory.CreateDirectory(youTubeRoot);
            MainViewModel.YouTubeServiceFactory = () => new YouTubeTrackService(
                new SmokeYouTubeBinaries(),
                new SmokeYouTubeRunner(),
                new YouTubeOptions
                {
                    MusicDirectory = Path.Combine(youTubeRoot, "Music"),
                    DownloadDirectory = Path.Combine(youTubeRoot, "downloads")
                });
            _hook = new FakeKeyboardHook();
            _player = new ContinuousAudioPlayer();
            var settingsService = MakeSettingsService(SettingsPath);
            _vm = Own(new MainViewModel(new AppSettings { Language = "en" }, settingsService, _hook, _player));
            _vm.UserMessageRequested += message => _messages.Add(message);
            await _vm.InitializeAsync();
            Check("defaults.loop", _vm.Loop, "A fresh VM keeps Loop=true.");
            Check("defaults.sections", _vm.IsVolumeExpanded && !_vm.IsReactionExpanded && !_vm.IsBehaviorExpanded &&
                _vm.ActiveSection == MainViewModel.SectionVolume,
                $"Fresh sections: volume={_vm.IsVolumeExpanded}, reaction={_vm.IsReactionExpanded}, behavior={_vm.IsBehaviorExpanded}, active={_vm.ActiveSection}.");
            Check("defaults.typingVolumeAndIdle", Near(_vm.MinVolume, 0.2) && Near(_vm.MaxVolume, 1) && Near(_vm.IdleTimeout, 3) && _vm.PauseOnIdle,
                $"Fresh settings: Min={_vm.MinVolumePercent}, Max={_vm.MaxVolumePercent}, Idle={_vm.IdleTimeout}s, Pause={_vm.PauseOnIdle}.");
            Check("defaults.reactive", _vm.PunchResponseEnabled && Near(_vm.BassReactiveGain, 0.15) && !_vm.StereoPulseEnabled &&
                _vm.AdaptiveVolumeEnabled,
                $"Fresh settings: punch={_vm.PunchResponseEnabled}, bass={_vm.BassReactivePercent}, stereo={_vm.StereoPulseEnabled}, adaptive={_vm.AdaptiveVolumeEnabled}.");
            // The fixtures are quiet, so the adaptive threshold would lower the quiet level below the
            // fixed gains these lifecycle checks assert. Core tests cover the adaptive behaviour.
            _vm.AdaptiveVolumeEnabled = false;
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
            Check("window.defaultSize", Near(_window.Width, 1120) && _window.Height >= 780,
                $"Actual default {_window.Width}x{_window.Height}; expected width 1120 and a height that fits the settings column.");
            Check("window.fitsContent", FitsSettingsContent(),
                $"Settings column scrollable height={SettingsScrollableHeight():F0} px at {_window.Height:F0} px tall; a larger font must not hide controls behind a scrollbar.");
            Check("window.startsOnPlaylistPage", _vm.IsPlaylistPage && !_vm.IsYouTubePage,
                $"IsPlaylistPage={_vm.IsPlaylistPage}, IsYouTubePage={_vm.IsYouTubePage}, searchTab={_vm.YouTube.IsSearchTab}.");
            Check("window.minimumSize", Near(_window.MinWidth, 920) && Near(_window.MinHeight, 650),
                $"Actual XAML minimum {_window.MinWidth}x{_window.MinHeight}; expected 920x650.");
            Check("render.playlistNoStatuses", !Descendants<TextBlock>(_window).Any(t =>
                Regex.IsMatch(t.Text ?? "", @"^(Queued|Processing|Transcribing|Ready|В очереди|Обработка|Транскрибирование|Готово)$", RegexOptions.IgnoreCase)),
                "Rendered playlist has filename/path/duration/extension only.");
            await Section("lifecycle.audio", AudioLifecycleAsync);
            await Section("render.matrix", CaptureMatrixAsync);
            await Section("settings.sections", SettingsSectionsAsync);
            await Section("guide", GuideAsync);
            await Section("autoAdvance", AutoAdvanceAsync);
            await Section("playlist", PlaylistManagementAsync);
            await Section("missingTrack", MissingTrackAsync);
            await Section("youtube", YouTubeSectionAsync);
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
        SetProcessPath("LogDirectory", Path.Combine(session, "Logs"));
        SetProcessPath("UpdateDirectory", Path.Combine(session, "updates"));
        // The data folders moved under one root, so they need redirecting too: otherwise the
        // YouTube section would read and measure the real user cache.
        SetProcessPath("MusicDirectory", Path.Combine(local, "Music"));
        SetProcessPath("BinDirectory", Path.Combine(local, "bin"));
        SetProcessPath("DownloadDirectory", Path.Combine(local, "downloads"));
        // The cache folder picker was removed on purpose: the cache now always lives in
        // <Root>\Music, so a leftover property or command would be dead UI code.
        var youTubeType = typeof(YouTubeViewModel);
        var removed = new[] { "CachePath", "CachePathText", "ChooseCacheFolderCommand" }
            .Where(name => youTubeType.GetProperty(name) is not null ||
                youTubeType.GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is not null)
            .ToArray();
        Check("storage.cacheFolderPickerRemoved", removed.Length == 0,
            removed.Length == 0 ? "CachePath and the folder command are gone." : "Still present: " + string.Join(", ", removed));
        // The log folder is created in every mode, and the storage step must not have overridden
        // the paths this harness pinned for itself.
        Check("storage.modePinned", AppPaths.Mode == StorageMode.LocalAppData &&
            Environment.GetEnvironmentVariable(StorageResolver.ModeVariable) == nameof(StorageMode.LocalAppData),
            $"Mode={AppPaths.Mode}; the smoke pins LocalAppData so the real user folders stay untouched.");
        AppPaths.EnsureCreated();
        Check("storage.logsCreated", Directory.Exists(AppPaths.LogDirectory),
            $"Logs folder: {AppPaths.LogDirectory}.");        Check("isolation.dataFolders", AppPaths.MusicDirectory.StartsWith(session, StringComparison.OrdinalIgnoreCase) &&
            AppPaths.BinDirectory.StartsWith(session, StringComparison.OrdinalIgnoreCase) &&
            AppPaths.DownloadDirectory.StartsWith(session, StringComparison.OrdinalIgnoreCase),
            $"Music={AppPaths.MusicDirectory}, Bin={AppPaths.BinDirectory}, Downloads={AppPaths.DownloadDirectory}");
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
        // The paths are shown in the screenshots, so they read like a real music folder instead of
        // exposing the build tree.
        var displayDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "BeatzOnKeys");
        Directory.CreateDirectory(displayDirectory);
        _fixtures = new[] { "Afterglow.mp3", "Neon Steps.mp3",
            "Quiet Skyline.mp3", "Transition.mp3" }
            .Select(name => Path.Combine(displayDirectory, name)).ToArray();
        SmokeYouTubeRunner.FixturePath = _fixtures[0];
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
        // The volume sliders live on the settings page, so it has to be the active one.
        vm.ShowSettingsCommand.Execute(null);
        await SettleAsync();
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
        var punchCheckbox = Descendants<CheckBox>(_window!).Single(c =>
            BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path?.Path == nameof(vm.PunchResponseEnabled));
        var adaptiveCheckbox = Descendants<CheckBox>(_window!).Single(c =>
            BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path?.Path == nameof(vm.AdaptiveVolumeEnabled));
        Check("reactiveCheckboxes.bound", punchCheckbox.IsChecked == true && adaptiveCheckbox.IsChecked == vm.AdaptiveVolumeEnabled &&
            Descendants<CheckBox>(_window!).Any(c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path?.Path == nameof(vm.StereoPulseEnabled)),
            $"Punch={punchCheckbox.IsChecked}, adaptive={adaptiveCheckbox.IsChecked} (VM {vm.AdaptiveVolumeEnabled}), stereo checkbox present.");
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
        // The idle pause also silences the track, so the transport button must offer Resume
        // there rather than trying to pause something already silent.
        var idleButton = PauseButton();
        Check("idle.buttonOffersResume", idleButton is not null && idleButton.IsVisible && idleButton.IsEnabled &&
            vm.IsPaused && vm.PauseResumeText == Loc.T("L_Resume"),
            $"Idle pause surfaces as '{vm.PauseResumeText}' (enabled={idleButton?.IsEnabled}) with paused={vm.IsPaused}.");
        var quietFrozen = player.Position;
        vm.TogglePauseCommand.Execute(null);
        await SettleAsync();
        Check("idle.resumeFromButton", player.IsPlaying && !player.IsIdlePaused && !player.IsPaused && !vm.IsPaused &&
            player.Position >= quietFrozen && player.Position - quietFrozen < TimeSpan.FromMilliseconds(750) &&
            player.CurrentGain < 0.6 && vm.PauseResumeText == Loc.T("L_Pause"),
            $"Button resume: {quietFrozen.TotalSeconds:F3}s -> {player.Position.TotalSeconds:F3}s, gain={player.CurrentGain:F3} (quiet until the next key), text='{vm.PauseResumeText}'.");

        // Enter the idle pause once more, so the key-press path is checked on a fresh pause.
        await UntilAsync(() => player.IsIdlePaused, 4500, "second idle pause");
        await SettleAsync();
        var keyFrozen = player.Position;
        hook.Signal();
        await UntilAsync(() => player.IsPlaying && !player.IsIdlePaused, 1000, "idle resume");
        Check("idle.resumeSamePositionInstantMaximum", player.CurrentGain >= 0.99 && player.Position >= keyFrozen &&
            player.Position - keyFrozen < TimeSpan.FromMilliseconds(750) && Output?.PlaybackState == PlaybackState.Playing,
            $"First key: gain={player.CurrentGain:F3}; {keyFrozen.TotalSeconds:F3}s -> {player.Position.TotalSeconds:F3}s (normal output buffering allowed).");
        // An explicit pause must keep the decoder where it is, and typing must not lift it.
        var beforePause = player.Position;
        // The real button, not just the command: a command that never raises CanExecuteChanged
        // leaves the button greyed out while the command itself reports "can execute".
        var pauseButton = PauseButton();
        Check("pause.buttonEnabled", pauseButton is not null && pauseButton.IsVisible && pauseButton.IsEnabled &&
            vm.TogglePauseCommand.CanExecute(null),
            $"Button={pauseButton?.IsVisible} visible, enabled={pauseButton?.IsEnabled}, command={vm.TogglePauseCommand.CanExecute(null)} while running.");
        vm.TogglePauseCommand.Execute(null);
        await SettleAsync();
        Check("pause.keepsPosition", vm.IsPaused && player.IsPaused && !player.IsPlaying &&
            Output?.PlaybackState == PlaybackState.Paused && player.Position == beforePause && vm.IsRunning,
            $"Paused={vm.IsPaused}, device={Output?.PlaybackState}, position={beforePause.TotalSeconds:F3}s -> {player.Position.TotalSeconds:F3}s.");
        Check("pause.buttonText", vm.PauseResumeText == Loc.T("L_Resume"),
            $"The button offers '{vm.PauseResumeText}' while paused.");
        hook.Signal();
        await Task.Delay(300);
        Check("pause.survivesTyping", vm.IsPaused && player.IsPaused && player.Position == beforePause,
            $"Typing during a pause must not resume it: paused={player.IsPaused}, position={player.Position.TotalSeconds:F3}s.");
        vm.TogglePauseCommand.Execute(null);
        await SettleAsync();
        Check("pause.resumeContinuesFromPosition", !vm.IsPaused && player.IsPlaying && vm.PauseResumeText == Loc.T("L_Pause") &&
            player.Position >= beforePause && player.Position - beforePause < TimeSpan.FromMilliseconds(750),
            $"Resume: {beforePause.TotalSeconds:F3}s -> {player.Position.TotalSeconds:F3}s, button '{vm.PauseResumeText}'.");
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
        foreach (var light in new[] { false, true })
        {
            if (Theme.IsLight != light) vm.ToggleThemeCommand.Execute(null);
            foreach (var language in new[] { "en", "ru" })
            {
                // Read the progress at the start of each pass: audio really is playing, so a
                // baseline from before the whole matrix would drift for reasons that have nothing
                // to do with the screenshots.
                var baseline = vm.PlaybackProgress;
                vm.SetLanguageCommand.Execute(language);
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
                // The player card must name the track, not only the source: it once showed
                // "Сейчас играет · локально" with the file name missing entirely.
                var cardTexts = Descendants<TextBlock>(window).Where(text => text.IsVisible)
                    .Select(text => text.Text ?? string.Empty).ToArray();
                Check($"player.cardNamesTrack.{Theme.CurrentCode}.{language}",
                    !string.IsNullOrWhiteSpace(vm.PlayingTrackText) &&
                    vm.PlayingTrackText != vm.PlayingSourceText &&
                    cardTexts.Contains(vm.PlayingSourceText) &&
                    cardTexts.Contains(vm.PlayingTrackText),
                    $"Source='{vm.PlayingSourceText}' present={cardTexts.Contains(vm.PlayingSourceText)}, " +
                    $"track='{vm.PlayingTrackText}' present={cardTexts.Contains(vm.PlayingTrackText)}; " +
                    $"page playlist={vm.IsPlaylistPage} settingsOpen={vm.IsSettingsOpen}.");
                var filename = light ? $"preview-light-{language}.png" : $"preview-{language}.png";
                SaveScreenshot(window, filename);
                // Audio really is playing, so the progress moves on its own; the check is that it
                // moves only forward and only by what the elapsed capture time could explain.
                var drift = vm.PlaybackProgress - baseline;
                Check($"preview.equalState.{Theme.CurrentCode}.{language}", drift >= -0.0001 && drift < 2,
                    $"Progress moved {drift:F4} points during the captures (baseline {baseline:F4}%); "
                    + "real playback only, no synthetic VM property edits.");
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

    /// <summary>
    /// Each settings section expands on its own, shows its controls, and collapses back to a header
    /// with a live summary. The captures document all three sections for the README.
    /// </summary>
    private async Task SettingsSectionsAsync()
    {
        var vm = _vm!;
        var window = _window!;
        vm.SetLanguageCommand.Execute("ru");
        await SettleAsync();
        // The playback panel has its own page; every control this section drives lives there.
        vm.ShowSettingsCommand.Execute(null);
        await SettleAsync();
        Check("sections.panelOverContent", vm.IsSettingsOpen && (vm.IsPlaylistPage || vm.IsYouTubePage),
            $"IsSettingsOpen={vm.IsSettingsOpen}, IsPlaylistPage={vm.IsPlaylistPage}.");
        CheckVisualTree(window, $"settings.{Theme.CurrentCode}.ru");
        var sections = new (string Key, string Summary, Func<bool> Expanded, int Count)[]
        {
            (MainViewModel.SectionVolume, nameof(MainViewModel.VolumeSummary), () => vm.IsVolumeExpanded, 4),
            (MainViewModel.SectionReaction, nameof(MainViewModel.ReactionSummary), () => vm.IsReactionExpanded, 4),
            (MainViewModel.SectionBehavior, nameof(MainViewModel.BehaviorSummary), () => vm.IsBehaviorExpanded, 2)
        };

        Check("sections.defaultOnlyVolume", vm.IsVolumeExpanded && !vm.IsReactionExpanded && !vm.IsBehaviorExpanded,
            $"Fresh view: volume={vm.IsVolumeExpanded}, reaction={vm.IsReactionExpanded}, behavior={vm.IsBehaviorExpanded}.");

        foreach (var section in sections)
        {
            vm.ShowSection(section.Key);
            await SettleAsync();
            var expanded = section.Expanded();
            var others = sections.Where(other => other.Key != section.Key).All(other => !other.Expanded());
            var controls = VisibleControls(window);
            Check($"sections.{section.Key}.expands", expanded && others && vm.ActiveSection == section.Key,
                $"Active={vm.ActiveSection}; volume={vm.IsVolumeExpanded}, reaction={vm.IsReactionExpanded}, behavior={vm.IsBehaviorExpanded}.");
            Check($"sections.{section.Key}.controlsVisible", controls >= 2,
                $"{section.Key}: {controls} visible sliders and checkboxes, others collapsed={others}; youtubePage={vm.IsYouTubePage}, playlistPage={vm.IsPlaylistPage}.");
            SaveScreenshot(window, $"section-{section.Key}.png");
        }

        // Every section collapsed still names itself and keeps its summary, so nothing is lost.
        foreach (var section in sections)
        {
            if (section.Expanded()) { vm.ToggleSection(section.Key); }
        }

        await SettleAsync();
        var collapsed = !vm.IsVolumeExpanded && !vm.IsReactionExpanded && !vm.IsBehaviorExpanded;
        var summaries = new[] { vm.VolumeSummary, vm.ReactionSummary, vm.BehaviorSummary };
        Check("sections.collapsedKeepsSummaries", collapsed && summaries.All(text => !string.IsNullOrWhiteSpace(text)) &&
            summaries.Distinct().Count() == 3,
            $"Collapsed={collapsed}; summaries: {string.Join(" | ", summaries)}.");
        SaveScreenshot(window, "section-collapsed.png");

        vm.ShowSection(MainViewModel.SectionVolume);
        await SettleAsync();
        Check("sections.badgeCounts", sections.Select(section => section.Count).Sum() == 10,
            "Badges announce 4 volume, 4 reaction, and 2 behaviour controls.");
    }

    private static int VisibleControls(Window window) =>
        Descendants<Slider>(window).Count(control => control.IsVisible) +
        Descendants<CheckBox>(window).Count(control => control.IsVisible);

    /// <summary>
    /// The window is sized to the settings column at start, so at the default font nothing in that
    /// column should need scrolling. This is what breaks first when the font grows.
    /// </summary>
    private double SettingsScrollableHeight()
    {
        var scroller = _window is null ? null : FindByName<ScrollViewer>(_window, "SettingsScroller");
        return scroller?.ScrollableHeight ?? double.NaN;
    }

    private bool FitsSettingsContent()
    {
        var height = SettingsScrollableHeight();
        return !double.IsNaN(height) && height <= 1;
    }

    /// <summary>The actual pause button in the transport row, so its WPF state is what gets checked.</summary>
    private Button? PauseButton()
    {
        if (_window?.DataContext is not MainViewModel viewModel) { return null; }
        return Descendants<Button>(_window).FirstOrDefault(button => ReferenceEquals(button.Command, viewModel.TogglePauseCommand));
    }

    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) { return match; }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var found = FindByName<T>(VisualTreeHelper.GetChild(root, index), name);
            if (found is not null) { return found; }
        }

        return null;
    }

    /// <summary>The guide is a real window: it must open, render every control, and follow the language switch.</summary>
    private async Task GuideAsync()
    {
        // The render matrix leaves Russian active; start deterministically in English.
        _vm!.SetLanguageCommand.Execute("en");
        await SettleAsync();
        app.ShowGuideWindow();
        await SettleAsync();
        var guide = GetField<GuideWindow>(app, "_guideWindow");
        Check("guide.opens", guide is not null && guide.IsVisible,
            $"visible={guide?.IsVisible}; actual={guide?.ActualWidth}x{guide?.ActualHeight}.");
        if (guide is null) { return; }

        await UntilAsync(() => Descendants<TextBlock>(guide).Any(text => text.Text == Loc.T("Guide_SecQuick")), 4000, "guide content");
        // The Zapret block lives inside the YouTube section now, not as a stray card at the end.
        var guideButtons = Descendants<Button>(guide).Where(button => button.IsVisible)
            .Select(button => button.Content?.ToString() ?? "").ToArray();
        Check("guide.zapretInsideYouTube",
            guideButtons.Contains(Loc.T("L_ZapretOpen")) &&
            Descendants<TextBlock>(guide).Any(text => text.IsVisible && text.Text == Loc.T("L_YtZapretTitle")),
            $"Visible guide buttons: {string.Join(" | ", guideButtons)}");
        var english = Descendants<TextBlock>(guide).Where(text => text.IsVisible).Select(text => text.Text ?? "").ToArray();
        var described = new[] { "Guide_StartT", "Guide_StopT", "Guide_ResetT", "Guide_MaxT", "Guide_MinT", "Guide_IdleT",
            "Guide_PauseT", "Guide_AutoT", "Guide_LoopT", "Guide_TrayMenuT", "Guide_StatusT", "Guide_NoSoundT",
            "Guide_ChipsT", "Guide_CollapseT", "Guide_CountT", "Guide_SummaryT", "Guide_UpdStorageT",
            "Guide_PauseResumeT", "Guide_DragT" };
        var missing = described.Where(key => !english.Contains(Loc.T(key))).ToArray();
        Check("guide.coversEveryControl", missing.Length == 0 && english.Length > 40,
            $"{english.Length} visible labels; missing: {string.Join(", ", missing)}.");
        Check("guide.englishFirst", english.Contains("Quick start") && english.Contains("Punch response"),
            "The English capture really renders English text.");
        SaveScreenshot(guide, $"guide-{Theme.CurrentCode}-en.png");

        _vm!.SetLanguageCommand.Execute("ru");
        await SettleAsync();
        var russian = Descendants<TextBlock>(guide).Where(text => text.IsVisible).Select(text => text.Text ?? "").ToArray();
        Check("guide.followsLanguage", russian.Contains(Loc.T("Guide_SecQuick")) && russian.Contains(Loc.T("Guide_MaxT")) &&
            !russian.Contains("Quick start"), "The open guide re-renders its sections after the language switch.");
        SaveScreenshot(guide, $"guide-{Theme.CurrentCode}-ru.png");

        // Scroll to the YouTube section so the Zapret block inside it is visible in the capture.
        var scroller = Descendants<ScrollViewer>(guide).FirstOrDefault(view => view.IsVisible);
        var zapretTitle = Descendants<TextBlock>(guide).FirstOrDefault(text => text.Text == Loc.T("L_YtZapretTitle"));
        if (scroller is not null && zapretTitle is not null)
        {
            zapretTitle.BringIntoView();
            await SettleAsync();
            SaveScreenshot(guide, $"guide-youtube-{Theme.CurrentCode}-ru.png");
            Check("guide.zapretVisible", zapretTitle.IsVisible, "The Zapret block is reachable by scrolling.");
        }
        _vm.SetLanguageCommand.Execute("en");
        await SettleAsync();

        guide.Close();
        await SettleAsync();
        Check("guide.closed", GetField<GuideWindow>(app, "_guideWindow") is null && !guide.IsVisible,
            "The App clears its guide window slot when the user closes it.");
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
        // The update notice renders the release notes: headings, bullets and bold must be real
        // formatting, and only the section for the active language may appear.
        await CaptureUpdateNoticeAsync();        // Two Telegram destinations, labelled so they cannot be confused: the channel and the author.
        var aboutButtons = Descendants<Button>(about).Where(button => button.IsVisible)
            .Select(button => button.Content?.ToString() ?? "").ToArray();
        var aboutTexts = Descendants<TextBlock>(about).Where(text => text.IsVisible)
            .Select(RenderedText).ToArray();
        Check("about.telegramChannel",
            aboutTexts.Contains(Loc.T("About_TelegramChannel")) && aboutTexts.Contains(Loc.T("About_TelegramAuthor")) &&
            aboutTexts.Any(text => text.Contains("beatzonkeys", StringComparison.Ordinal)) &&
            aboutTexts.Any(text => text.Contains("music_cat1", StringComparison.Ordinal)),
            $"Visible About text: {string.Join(" | ", aboutTexts.Where(text => text.Contains("t.me", StringComparison.Ordinal) || text.Contains('@')))}");
        SaveScreenshot(about, light ? "about-light-ru.png" : "about-ru.png");
        about.Close();
        await SettleAsync();
        Check(label + ".closed", GetField<AboutWindow>(app, "_aboutWindow") is null && !about.IsVisible, "Real App About Closed clears its private window slot.");
    }


    /// <summary>
    /// Captures the update notice with the same release body shape GitHub returns, so the Markdown
    /// rendering and the language pick are both visible in one picture.
    /// </summary>
    private async Task CaptureUpdateNoticeAsync()
    {
        var notes = """
            ## English

            English prose that must not appear in the Russian interface.

            ## Русский

            ### Исправлен кеш

            - **Прогресс** молчал: `--print` подразумевает `--quiet`.
            - Обычная строка со [ссылкой](https://example.com) внутри.

            | Файл | Размер |
            |---|---|
            """;
        var dialog = MessageDialog.CreateAnnouncement(
            "Доступна BeatzOnKeys 9.9.9",
            "У вас 1.0.0, на GitHub уже 9.9.9. Открыть «О программе», чтобы скачать и установить?",
            notes);
        dialog.Owner = _window;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.Show();
        await SettleAsync();

        var texts = Descendants<TextBlock>(dialog).Where(text => text.IsVisible).Select(RenderedText).ToArray();
        Check("update.noticeRendersHeadings",
            texts.Contains("Исправлен кеш") && !texts.Any(text => text.StartsWith("###", StringComparison.Ordinal)),
            $"Visible lines: {string.Join(" | ", texts.Take(8))}");
        Check("update.noticeRendersBullets",
            texts.Any(text => text.Contains("--print", StringComparison.Ordinal)) &&
            !texts.Any(text => text.Contains("**", StringComparison.Ordinal)),
            "Markdown markers are gone; the text itself is rendered.");
        Check("update.noticeFollowsLanguage",
            !texts.Any(text => text.Contains("must not appear", StringComparison.Ordinal)) &&
            !texts.Any(text => text.Contains("English", StringComparison.Ordinal)),
            "Only the section for the active interface language is shown.");
        Check("update.noticeHidesAssetTable",
            !texts.Any(text => text.Contains('|', StringComparison.Ordinal)),
            "The asset table is not part of the notice.");

        SaveScreenshot(dialog, $"update-notice-{Loc.CurrentCode}.png");
        dialog.Close();
        await SettleAsync();
    }
    private void CheckResourceReferences(Window window, string label)
    {
        var missing = new HashSet<string>();
        foreach (var filename in new[] { "MainWindow.xaml", "AboutWindow.xaml", "GuideWindow.xaml", "Styles.xaml" })
        {
            var text = File.ReadAllText(Path.Combine(root, "BeatzOnKeys.UI", filename));
            foreach (Match match in Regex.Matches(text, @"\{(?:Static|Dynamic)Resource\s+([^}\s]+)\}"))
            {
                var key = match.Groups[1].Value;
                if (window.TryFindResource(key) is null) missing.Add(key);
            }
        }
        foreach (var filename in new[] { "App.xaml.cs", "MainWindow.xaml.cs", "AboutWindow.xaml.cs", "GuideWindow.xaml.cs",
            "ViewModels/MainViewModel.cs", "ViewModels/TrackViewModel.cs" })
        {
            var text = File.ReadAllText(Path.Combine(root, "BeatzOnKeys.UI", filename));
            foreach (Match match in Regex.Matches(text, "\\\"((?:L_|About_|Upd_|Guide_)[A-Za-z0-9_]+)\\\""))
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
        // The sliders live on the settings page now, so a page without them is not a failure.
        if (window is MainWindow && sliders.Length > 0)
            Check(label + ".sliderLimits", sliders.Length == 4 && sliders.Any(s => Near(s.Minimum, 0) && Near(s.Maximum, 100)) &&
                sliders.Any(s => Near(s.Minimum, 0) && Near(s.Maximum, 50)) && sliders.Any(s => Near(s.Minimum, 1) && Near(s.Maximum, 10)) &&
                sliders.Any(s => Near(s.Minimum, 0) && Near(s.Maximum, 30)) &&
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
        // No network in the smoke: the yt-dlp card compares against a fixed upstream version.
        YouTubeViewModel.LatestYtDlpReleaseFactory = _ => Task.FromResult<string?>("2025.01.15");
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

    /// <summary>Move, remove-current, clear, and persistence checks on an isolated view model.</summary>
    private async Task PlaylistManagementAsync()
    {
        var hook = new FakeKeyboardHook();
        var player = new ContinuousAudioPlayer();
        var settingsPath = Path.Combine(session, "playlist-settings.json");
        var vm = Own(new MainViewModel(new AppSettings { Language = "en", PauseOnIdle = false, AutoAdvance = true, Loop = false },
            MakeSettingsService(settingsPath), hook, player));
        vm.UserMessageRequested += message => _messages.Add("Playlist: " + message);
        await vm.InitializeAsync();
        await vm.AddFilesAsync(_fixtures.Take(3));
        var first = vm.Tracks[0];
        var second = vm.Tracks[1];
        var third = vm.Tracks[2];
        Check("playlist.importedEdges", vm.Tracks.Count == 3 && first.IsFirst && !first.IsLast && second.Index == 1 && third.IsLast,
            $"{string.Join(" | ", vm.Tracks.Select(track => $"{track.FileName}[{track.Index} first={track.IsFirst} last={track.IsLast}]"))}");

        vm.MoveTrackUpCommand.Execute(second);
        await SettleAsync();
        Check("playlist.moveUpReorders", vm.Tracks[0] == second && vm.Tracks[1] == first && second.IsFirst && !second.IsLast &&
            first.Index == 1 && third.IsLast,
            $"Order: {string.Join(" | ", vm.Tracks.Select(track => track.FileName))}");
        Check("playlist.edgesDisabled", !vm.MoveTrackUpCommand.CanExecute(vm.Tracks[0]) && !vm.MoveTrackDownCommand.CanExecute(vm.Tracks[2]),
            "The first row cannot move up and the last cannot move down.");

        // Drag-and-drop goes through MoveTrackTo, which may travel any distance and shows a
        // drop indicator while the pointer hovers a row.
        var orderBeforeDrag = vm.Tracks.ToArray();
        var dragged = vm.Tracks[2];
        vm.SetDropTarget(vm.Tracks[0], above: true);
        await SettleAsync();
        Check("playlist.dropIndicator", vm.Tracks[0].IsDropTarget && vm.Tracks[0].IsDropTargetAbove &&
            vm.Tracks.Count(track => track.IsDropTarget) == 1,
            $"Drop indicator on '{vm.Tracks[0].FileName}' above={vm.Tracks[0].IsDropTargetAbove}.");
        vm.SetDropTarget(null, above: true);
        await SettleAsync();
        Check("playlist.dropIndicatorCleared", vm.Tracks.All(track => !track.IsDropTarget),
            "Clearing the target removes every indicator.");

        var draggedMoved = vm.MoveTrackTo(dragged, 0);
        await SettleAsync();
        Check("playlist.dragReordersToTop", draggedMoved && vm.Tracks[0] == dragged && dragged.IsFirst &&
            vm.Tracks[1].Index == 1 && vm.Tracks[2].IsLast,
            $"Order after drag: {string.Join(" | ", vm.Tracks.Select(track => track.FileName))}.");
        Check("playlist.dragNoOp", !vm.MoveTrackTo(dragged, 0) && vm.Tracks[0] == dragged,
            "Dropping a row onto its own position changes nothing.");

        // Leave the playlist as the rest of the suite expects it.
        for (var index = 0; index < orderBeforeDrag.Length; index++)
        {
            vm.MoveTrackTo(orderBeforeDrag[index], index);
        }

        await SettleAsync();

        // A reorder of the playing track must not restart it.
        vm.StartCommand.Execute(null);
        await UntilAsync(() => vm.IsRunning && player.IsPlaying, 5000, "playlist playback start");
        await Task.Delay(700);
        var beforeMove = player.Position;
        vm.MoveTrackDownCommand.Execute(vm.SelectedTrack!);
        await SettleAsync();
        await Task.Delay(400);
        Check("playlist.moveKeepsPlayback", vm.IsRunning && player.IsPlaying && player.Position > beforeMove &&
            ReferenceEquals(vm.SelectedTrack, first),
            $"{beforeMove.TotalSeconds:F2}s -> {player.Position.TotalSeconds:F2}s while the selected track moved down.");

        // Removing the current track stops playback and selects the neighbour that takes its place.
        await UntilAsync(() => vm.RemoveTrackCommand.CanExecute(vm.SelectedTrack), 5000, "remove current track");
        vm.RemoveTrackCommand.Execute(vm.SelectedTrack);
        await UntilAsync(() => !vm.IsRunning && vm.Tracks.Count == 2, 5000, "remove current track stops playback");
        await SettleAsync();
        Check("playlist.removeCurrentStopsAndSelectsNext", !vm.IsRunning && !player.IsPlaying && vm.SelectedTrack is not null &&
            vm.Tracks.Contains(vm.SelectedTrack) && ReferenceEquals(vm.SelectedTrack, third),
            $"Running={vm.IsRunning}; selected={vm.SelectedTrackText}; order: {string.Join(" | ", vm.Tracks.Select(track => track.FileName))}");

        await vm.ClearPlaylistAsync();
        await SettleAsync();
        var raw = await File.ReadAllTextAsync(settingsPath);
        using var document = JsonDocument.Parse(raw);
        var savedTracks = document.RootElement.GetProperty("Tracks").GetArrayLength();
        var savedSelection = document.RootElement.GetProperty("SelectedTrackId");
        Check("playlist.clearStopsAndPersistsEmpty", vm.Tracks.Count == 0 && vm.SelectedTrack is null && !vm.IsRunning &&
            !vm.CanClearPlaylist && savedTracks == 0 && savedSelection.ValueKind == JsonValueKind.Null,
            $"Visible tracks={vm.Tracks.Count}; saved tracks={savedTracks}; selectedTrackId={savedSelection.ValueKind}; running={vm.IsRunning}.");

        // The moved order must survive a restart.
        await vm.AddFilesAsync(_fixtures.Take(3));
        vm.MoveTrackDownCommand.Execute(vm.Tracks[0]);
        await SettleAsync();
        var expectedOrder = vm.Tracks.Select(track => track.SourcePath).ToArray();
        await vm.SaveAsync();
        await vm.DisposeAsync();
        _ownedModels.Remove(vm);

        var reloadService = MakeSettingsService(settingsPath);
        var reloadedSettings = await reloadService.LoadAsync();
        var reloaded = Own(new MainViewModel(reloadedSettings, reloadService,
            new FakeKeyboardHook(), new ContinuousAudioPlayer()));
        await reloaded.InitializeAsync();
        var reloadedOrder = reloaded.Tracks.Select(track => track.SourcePath).ToArray();
        Check("playlist.orderSurvivesRestart", reloadedOrder.SequenceEqual(expectedOrder) && reloaded.Tracks[0].IsFirst &&
            reloaded.Tracks[^1].IsLast,
            $"Saved: {string.Join(" | ", expectedOrder.Select(Path.GetFileName))}; loaded: {string.Join(" | ", reloadedOrder.Select(Path.GetFileName))}.");
        await reloaded.DisposeAsync();
        _ownedModels.Remove(reloaded);
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

    /// <summary>
    /// The YouTube section is exercised offline: no binary is installed, so nothing downloads.
    /// What is checked is the wiring — page switch, command state, link refusal, cache figure and
    /// that a cached track plays without ever entering the playlist.
    /// </summary>
    private async Task YouTubeSectionAsync()
    {
        var root = Path.Combine(session, "youtube-page");
        // The cache always lives in <Root>\Music now, so the fixtures go where the app looks.
        var music = AppPaths.MusicDirectory;
        Directory.CreateDirectory(music);
        var cached = Path.Combine(music, "dQw4w9WgXcQ.mp3");
        File.Copy(_fixtures[0], cached, overwrite: true);
        var cachedSecond = Path.Combine(music, "kJQP7kiw5Fk.mp3");
        File.Copy(_fixtures[1], cachedSecond, overwrite: true);
        // The window's own view model uses the harness service; the same fixtures go there so a
        // screenshot can show cached search rows without a download.
        var harnessMusic = Path.Combine(session, "youtube", "Music");
        // Kept for the window's own service, which is built with its own folder below.
        Directory.CreateDirectory(harnessMusic);
        File.Copy(_fixtures[0], Path.Combine(harnessMusic, "dQw4w9WgXcQ.mp3"), overwrite: true);
        File.Copy(_fixtures[1], Path.Combine(harnessMusic, "kJQP7kiw5Fk.mp3"), overwrite: true);
        MainViewModel.YouTubeServiceFactory = () => new YouTubeTrackService(
            new SmokeYouTubeBinaries(), new SmokeYouTubeRunner(),
            new YouTubeOptions
            {
                MusicDirectory = music,
                DownloadDirectory = Path.Combine(root, "downloads")
            });

        // No network in the smoke: the yt-dlp card compares against a fixed upstream version.
        YouTubeViewModel.LatestYtDlpReleaseFactory = _ => Task.FromResult<string?>("2025.01.15");
        var hook = new FakeKeyboardHook();
        var player = new ContinuousAudioPlayer();
        var vm = Own(new MainViewModel(
            new AppSettings
            {
                Language = "en",
                YouTubeTracks =
                [
                    new YouTubeTrackState
                    {
                        VideoId = "dQw4w9WgXcQ", Title = "Cached fixture", CachedPath = cached,
                        Status = YouTubeTrackStatus.Ready
                    },
                    new YouTubeTrackState
                    {
                        VideoId = "kJQP7kiw5Fk", Title = "Second fixture", CachedPath = cachedSecond,
                        Status = YouTubeTrackStatus.Ready
                    }
                ],
                YouTubeCacheEnabled = true
            },
            MakeSettingsService(Path.Combine(session, "youtube-page-settings.json")), hook, player));
        vm.UserMessageRequested += message => _messages.Add("YouTube: " + message);
        await vm.InitializeAsync();
        vm.SetLanguageCommand.Execute("ru");
        await SettleAsync();

        Check("youtube.tracksStayOutOfPlaylist", vm.Tracks.Count == 0 && vm.YouTube.Downloaded.Count == 2 &&
            vm.YouTube.Downloaded.All(track => track.IsReady),
            $"Playlist={vm.Tracks.Count}, YouTube={vm.YouTube.Downloaded.Count}.");

        // The section renames the local tab and mirrors the playlist's management model.
        Check("youtube.localTabName", Loc.T("L_TabPlaylist") == (Loc.CurrentCode == "ru" ? "Локально" : "Local"),
            $"The first tab reads '{Loc.T("L_TabPlaylist")}'.");

        Check("youtube.innerTabs", vm.YouTube.IsSearchTab && !vm.YouTube.IsDownloadedTab,
            "Search is the default inner tab.");
        vm.YouTube.ShowDownloadedTabCommand.Execute(null);
        await SettleAsync();
        Check("youtube.innerTabsDownloaded", vm.YouTube.IsDownloadedTab && !vm.YouTube.IsSearchTab,
            "The downloaded tab can be selected.");
        vm.YouTube.ShowSearchTabCommand.Execute(null);
        await SettleAsync();

        // The downloaded tab carries the same counter as the local list and a chip per row.
        // The cache hint used to be a hardcoded %LOCALAPPDATA% path; with a movable root it must
        // read the folder the app is actually using.
        Check("storage.cacheHintUsesRealPath",
            vm.YouTube.CacheHintText.Contains(AppPaths.MusicDirectory, StringComparison.OrdinalIgnoreCase) &&
            !vm.YouTube.CacheHintText.Contains("%LOCALAPPDATA%", StringComparison.Ordinal),
            $"Hint='{vm.YouTube.CacheHintText}', MusicDirectory='{AppPaths.MusicDirectory}'.");
        Check("youtube.downloadedCounter", vm.YouTube.TrackCount == 2 && vm.YouTube.TrackCountNumber == "2",
            $"TrackCount={vm.YouTube.TrackCount}, text='{vm.YouTube.TrackCountNumber}'.");
        Check("youtube.statusChips", vm.YouTube.Downloaded.All(track => track.IsChipReady) &&
            vm.YouTube.Downloaded.All(track => !track.IsChipFailed) &&
            vm.YouTube.Downloaded[0].StatusChipText == Loc.T("L_YtStatusReady"),
            $"Chip text: '{vm.YouTube.Downloaded[0].StatusChipText}'.");

        // Selecting a row is the whole selection model: there is no play button anywhere.
        vm.YouTube.SelectTrack(vm.YouTube.Downloaded[1]);
        await SettleAsync();
        Check("youtube.rowSelection", ReferenceEquals(vm.YouTube.SelectedTrack, vm.YouTube.Downloaded[1]) &&
            vm.YouTube.Downloaded[1].IsSelected && !vm.YouTube.Downloaded[0].IsSelected,
            $"Selected='{vm.YouTube.SelectedTrackTitle}'.");
        var firstRow = vm.YouTube.Downloaded[0];
        var secondRow = vm.YouTube.Downloaded[1];
        Check("youtube.arrowEdges", !vm.YouTube.MoveTrackUpCommand.CanExecute(firstRow) &&
            !vm.YouTube.MoveTrackDownCommand.CanExecute(secondRow),
            "The first row cannot move up and the last cannot move down.");
        vm.YouTube.MoveTrackDownCommand.Execute(firstRow);
        await SettleAsync();
        Check("youtube.arrowsReorder", vm.YouTube.Downloaded[0] == secondRow && vm.YouTube.Downloaded[1] == firstRow &&
            firstRow.IsLast && secondRow.IsFirst,
            $"Order: {string.Join(" | ", vm.YouTube.Downloaded.Select(track => track.Title))}.");
        vm.YouTube.MoveTrackUpCommand.Execute(firstRow);
        await SettleAsync();

        vm.YouTube.SetDropTarget(vm.YouTube.Downloaded[0], above: true);
        await SettleAsync();
        Check("youtube.dropIndicator", vm.YouTube.Downloaded[0].IsDropTarget && vm.YouTube.Downloaded[0].IsDropTargetAbove,
            "A dragged row shows where it will land.");
        vm.YouTube.SetDropTarget(null, above: true);
        await SettleAsync();
        Check("youtube.dropIndicatorCleared", vm.YouTube.Downloaded.All(track => !track.IsDropTarget),
            "Clearing the target removes the indicator.");

        var expected = Loc.F("L_YtCacheUsedFormat", ByteSizeFormatter.Format(vm.YouTube.KeptCacheBytes, "КБ", "МБ", "ГБ"));
        Check("youtube.cacheSize", vm.YouTube.KeptCacheBytes > 0 && vm.YouTube.CacheSizeText == expected &&
            !vm.YouTube.CacheSizeText.Contains("L_YtCacheUsedFormat", StringComparison.Ordinal),
            $"Cache: bytes={vm.YouTube.CacheBytes}, kept={vm.YouTube.KeptCacheBytes}, search={vm.YouTube.SearchCacheBytes}, " +
            $"dir='{AppPaths.MusicDirectory}' exists={Directory.Exists(AppPaths.MusicDirectory)} " +
            $"files={(Directory.Exists(AppPaths.MusicDirectory) ? Directory.GetFiles(AppPaths.MusicDirectory).Length : -1)}, " +
            $"text='{vm.YouTube.CacheSizeText}', expected '{expected}'.");

        Check("youtube.binaryNotInstalled", !vm.YouTube.YtDlpInstalled &&
            vm.YouTube.YtDlpStatusText == Loc.T("L_YtNotInstalled") &&
            vm.YouTube.YtDlpState == YtDlpState.NotInstalled &&
            vm.YouTube.YtDlpActionVisible && vm.YouTube.YtDlpActionText == Loc.T("L_YtDownload"),
            $"yt-dlp: installed={vm.YouTube.YtDlpInstalled}, state={vm.YouTube.YtDlpState}, text='{vm.YouTube.YtDlpStatusText}'.");
        // The card must not offer an update it cannot confirm, and never show a button when the
        // binary is current: that is the whole point of the state-driven button.
        Check("youtube.binaryUpToDateHidesButton", !vm.YouTube.YtDlpActionVisible == (vm.YouTube.YtDlpState == YtDlpState.UpToDate),
            $"state={vm.YouTube.YtDlpState}, visible={vm.YouTube.YtDlpActionVisible}.");

        Check("youtube.searchNeedsQuery", !vm.YouTube.SearchCommand.CanExecute(null), "An empty query cannot search.");
        vm.YouTube.SearchQuery = "linkin park numb";
        Check("youtube.searchEnabledWithQuery", vm.YouTube.SearchCommand.CanExecute(null),
            "A typed query enables the search button.");

        vm.YouTube.SearchQuery = "https://www.youtube.com/playlist?list=PL1234567890";
        vm.YouTube.DownloadFromQueryCommand.Execute(null);
        // A pasted link swaps the single button to "Скачать", words swap it back to "Найти".
        vm.YouTube.SearchQuery = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        Check("youtube.queryIsLink", vm.YouTube.QueryIsLink,
            $"QueryIsLink={vm.YouTube.QueryIsLink} for a watch link.");
        vm.YouTube.SearchQuery = "rick astley";
        Check("youtube.queryIsWords", !vm.YouTube.QueryIsLink,
            $"QueryIsLink={vm.YouTube.QueryIsLink} for a search phrase.");
        vm.YouTube.SearchQuery = string.Empty;
        Check("youtube.queryEmpty", !vm.YouTube.QueryIsLink && !vm.YouTube.CanSearch,
            $"QueryIsLink={vm.YouTube.QueryIsLink}, CanSearch={vm.YouTube.CanSearch}.");
        await UntilAsync(() => vm.YouTube.StatusText == Loc.T("L_YtFailurePlaylist"), 3000, "playlist refusal");
        Check("youtube.playlistRefused", vm.YouTube.StatusText == Loc.T("L_YtFailurePlaylist") &&
            vm.YouTube.Downloaded.Count == 2,
            $"Status='{vm.YouTube.StatusText}', rows={vm.YouTube.Downloaded.Count}.");

        // A result without a cached file cannot be listened to; the command must refuse it.
        var uncached = new YouTubeSearchItemViewModel(new YouTubeSearchResult
        {
            VideoId = "notcached01", Title = "Not cached yet", Author = "Nobody", Duration = TimeSpan.FromSeconds(60)
        });
        Check("youtube.previewDisabledWithoutCache",
            !uncached.CanPlay && !vm.YouTube.PreviewCommand.CanExecute(uncached),
            $"CanPlay={uncached.CanPlay}, canExecute={vm.YouTube.PreviewCommand.CanExecute(uncached)}.");

        // A search fills the list and caches every row, so listening works without a second request.
        SmokeYouTubeBinaries.YtDlpPresent = true;
        vm.YouTube.RefreshBinaryState();
        vm.YouTube.SearchQuery = "rick astley";
        vm.YouTube.SearchCommand.Execute(null);
        await UntilAsync(() => vm.YouTube.Results.Count == 2, 8000, "search results");
        Check("youtube.searchFillsResults", vm.YouTube.Results.Count == 2,
            $"Results={vm.YouTube.Results.Count}.");
        await UntilAsync(() => vm.YouTube.Results.All(item => item.CanPlay), 8000, "cached results");

        var firstResult = vm.YouTube.Results[0];
        foreach (var diag in vm.YouTube.Results)
        {
            Console.WriteLine($"DIAG result {diag.VideoId}: path='{diag.CachedPath}', exists={(diag.CachedPath is null ? "n/a" : File.Exists(diag.CachedPath).ToString())}, downloading={diag.IsDownloading}, failed={diag.CacheFailed}");
        }

        Check("youtube.previewEnabledWithCache",
            firstResult.CanPlay && vm.YouTube.PreviewCommand.CanExecute(firstResult),
            $"CanPlay={firstResult.CanPlay}, state='{firstResult.CacheStateText}'.");
        vm.YouTube.PreviewCommand.Execute(firstResult);
        await UntilAsync(() => vm.IsRunning && player.IsPlaying, 5000, "preview playback");
        Check("youtube.previewPlaysCachedFile",
            vm.IsRunning && player.IsPlaying && vm.NowPlayingText.Contains(firstResult.Title, StringComparison.Ordinal),
            $"Running={vm.IsRunning}, playing={player.IsPlaying}, now='{vm.NowPlayingText}'.");

        // A preview is the user asking to hear the track, so the idle pause must stay away even
        // though the setting is on. The player is watched past the timeout to catch a stale timer.
        Check("youtube.previewKeepsSettingOn", vm.PauseOnIdle, "The saved setting itself must stay on.");
        await Task.Delay(TimeSpan.FromSeconds(vm.IdleTimeout + 1.5));
        Check("youtube.previewSurvivesIdleTimeout",
            vm.IsRunning && player.IsPlaying && !player.IsIdlePaused,
            $"Running={vm.IsRunning}, playing={player.IsPlaying}, idlePaused={player.IsIdlePaused}.");
        await StopAsync(vm);
         // Previews occupy space and can be cleared without touching the kept tracks.
        await UntilAsync(() => vm.YouTube.ClearSearchCacheCommand.CanExecute(null), 5000, "preview cache measured");
        Check("youtube.searchCacheMeasured", vm.YouTube.ClearSearchCacheCommand.CanExecute(null),
            $"Search cache text='{vm.YouTube.SearchCacheSizeText}'.");
        var downloadedBefore = vm.YouTube.Downloaded.Count;
        await vm.YouTube.ClearSearchCacheAsync();
        Check("youtube.searchCacheCleared",
            vm.YouTube.Results.All(item => !item.CanPlay) && vm.YouTube.Downloaded.Count == downloadedBefore,
            $"Results playable={vm.YouTube.Results.Count(item => item.CanPlay)}, downloaded={vm.YouTube.Downloaded.Count}.");
        Check("youtube.keptTracksSurviveSearchClear",
            vm.YouTube.Downloaded.All(track => track.CanPlay),
            $"Kept tracks playable={vm.YouTube.Downloaded.Count(track => track.CanPlay)} of {vm.YouTube.Downloaded.Count}.");
        // A second search while the first one is still caching: the old pass used to keep its
        // rows and its download slots, so the new rows never finished and their button stayed
        // dead. The fake download is slowed down to make the overlap happen every time.
        SmokeYouTubeRunner.Delay = TimeSpan.FromMilliseconds(250);
        try
        {
            vm.YouTube.SearchQuery = "first pass";
            vm.YouTube.SearchCommand.Execute(null);
            await UntilAsync(() => vm.YouTube.Results.Count == 2, 8000, "first pass results");
            await UntilAsync(() => vm.YouTube.Results.Any(item => item.IsDownloading), 8000, "first pass caching");
            vm.YouTube.SearchQuery = "second pass";
            vm.YouTube.SearchCommand.Execute(null);
            await UntilAsync(() => vm.YouTube.Results.Count == 2, 8000, "second pass results");
            await UntilAsync(() => vm.YouTube.Results.All(item => item.CanPlay), 20000, "second pass cached");
            Check("youtube.secondSearchCachesItsRows",
                vm.YouTube.Results.All(item => item.CanPlay),
                $"Playable={vm.YouTube.Results.Count(item => item.CanPlay)} of {vm.YouTube.Results.Count}.");
            // The button is driven by the command, not by the binding alone: a command has to be
            // The button state comes from the command, so what matters is that the command is
            // told to re-check once the file lands. Counting the notifications is the only way to
            // tell a working binding from one that never hears about the new file.
            var notified = 0;
            void OnAddReady(object? sender, EventArgs args) => Interlocked.Increment(ref notified);
            vm.YouTube.DownloadCommand.CanExecuteChanged += OnAddReady;
            try
            {
                // The previous pass must be finished before the next one is asked for: its rows
                // are already cached, so waiting on them would pass before this search even runs.
                await UntilAsync(() => !vm.YouTube.IsSearching, 8000, "third pass idle");
                var previousIds = vm.YouTube.Results.Select(item => item.VideoId).ToHashSet(StringComparer.Ordinal);
                vm.YouTube.SearchQuery = "third pass";
                vm.YouTube.SearchCommand.Execute(null);
                await UntilAsync(() => vm.YouTube.Results.Count == 2 &&
                    vm.YouTube.Results.All(item => !previousIds.Contains(item.VideoId)), 8000, "third pass results");
                var beforeCache = Volatile.Read(ref notified);
                await UntilAsync(() => vm.YouTube.Results.All(item => item.CanPlay), 20000, "third pass cached");
                await UntilAsync(() => Volatile.Read(ref notified) > beforeCache, 5000, "add button notified");
                Check("youtube.secondSearchEnablesAdd", Volatile.Read(ref notified) > beforeCache,
                    $"CanExecuteChanged fired {Volatile.Read(ref notified) - beforeCache} times while caching.");
                Check("youtube.secondSearchEnablesAddNow",
                    vm.YouTube.Results.All(item => vm.YouTube.DownloadCommand.CanExecute(item)),
                    $"Addable={vm.YouTube.Results.Count(item => vm.YouTube.DownloadCommand.CanExecute(item))} of {vm.YouTube.Results.Count}.");
            }
            finally { vm.YouTube.DownloadCommand.CanExecuteChanged -= OnAddReady; }
            // The file lands before the row stops saying "caching", so the busy state is waited
            // for instead of being asserted the moment CanPlay flips.
            await UntilAsync(() => vm.YouTube.Results.All(item => !item.IsDownloading), 15000, "second pass settled");
            Check("youtube.searchCacheCountedAfterSecondSearch",
                vm.YouTube.Results.All(item => !item.IsDownloading),
                $"Still downloading={vm.YouTube.Results.Count(item => item.IsDownloading)}.");
        }
        finally
        {
            SmokeYouTubeRunner.Delay = TimeSpan.Zero;
        }

        // A row that takes longer than the pass allows is set aside and retried once, so one slow
        // track cannot hold the whole list. The fake download is delayed far beyond a shortened
        // pass timeout, which is the same shape as a stalled yt-dlp.
        var passTimeout = YouTubeViewModel.CachePassTimeout;
        YouTubeViewModel.CachePassTimeout = TimeSpan.FromSeconds(1.5);
        SmokeYouTubeRunner.Delay = TimeSpan.FromSeconds(30);
        try
        {
            vm.YouTube.SearchQuery = "slow rows";
            vm.YouTube.SearchCommand.Execute(null);
            await UntilAsync(() => vm.YouTube.Results.Count == 2, 8000, "slow results");
            await UntilAsync(() => vm.YouTube.Results.All(item => item.Skipped), 20000, "slow rows deferred");
            Check("youtube.slowRowsDeferred", vm.YouTube.Results.All(item => item.Skipped && !item.CanPlay),
                $"Skipped={vm.YouTube.Results.Count(item => item.Skipped)} of {vm.YouTube.Results.Count}.");
            Check("youtube.slowRowsShowTheDeferredLabel",
                vm.YouTube.Results.All(item => item.CacheStateText == Loc.T("L_YtCachingLater")),
                $"State={vm.YouTube.Results[0].CacheStateText}, expected={Loc.T("L_YtCachingLater")}.");
            Check("youtube.slowRowsReleaseThePass",
                vm.YouTube.Results.All(item => !item.IsDownloading) && !vm.YouTube.IsSearching,
                $"Still downloading={vm.YouTube.Results.Count(item => item.IsDownloading)}, searching={vm.YouTube.IsSearching}.");
        }
        finally
        {
            YouTubeViewModel.CachePassTimeout = passTimeout;
            SmokeYouTubeRunner.Delay = TimeSpan.Zero;
        }

        await vm.YouTube.ClearSearchCacheAsync();
       vm.YouTube.SearchQuery = string.Empty;
        vm.YouTube.Results.Clear();
        SmokeYouTubeBinaries.YtDlpPresent = false;
        vm.YouTube.RefreshBinaryState();

        var row = vm.YouTube.Downloaded[0];
        Check("youtube.playAvailable", vm.YouTube.PlayCommand.CanExecute(row), "A cached, existing file can be played.");
        vm.YouTube.PlayCommand.Execute(row);
        await UntilAsync(() => vm.IsRunning && player.IsPlaying, 5000, "YouTube playback");
        Check("youtube.playsWithoutPlaylistEntry", vm.IsRunning && player.IsPlaying && vm.Tracks.Count == 0 &&
            vm.NowPlayingText.Contains("Cached fixture", StringComparison.Ordinal),
            $"Running={vm.IsRunning}, playing={player.IsPlaying}, playlist={vm.Tracks.Count}, now='{vm.NowPlayingText}'.");
        // The idle pause is a typing-background feature. A downloaded track keeps it; only the
        // search preview stands aside. The player is watched past the timeout to prove the pause
        // really fires here, which is the opposite of the preview check above.
        await UntilAsync(() => player.IsIdlePaused, (int)(vm.IdleTimeout * 1000) + 4000, "downloaded idle pause");
        Check("youtube.downloadedKeepsIdlePause", player.IsIdlePaused && vm.PauseOnIdle,
            $"idlePaused={player.IsIdlePaused}, setting={vm.PauseOnIdle}, running={vm.IsRunning}.");
        await StopAsync(vm);

        // The two clear buttons own separate shares and both ask first. A preview is created here
        // so the split can be observed: clearing the downloaded tracks must leave the previews.
        SmokeYouTubeBinaries.YtDlpPresent = true;
        vm.YouTube.RefreshBinaryState();
        vm.YouTube.SearchQuery = "clear split test";
        vm.YouTube.SearchCommand.Execute(null);
        await UntilAsync(() => vm.YouTube.Results.Count == 2, 8000, "clear split results");
        await UntilAsync(() => vm.YouTube.Results.All(item => item.CanPlay), 20000, "clear split cached");
        var keptBefore = vm.YouTube.Downloaded.Count;
        var asked = false;
        vm.YouTube.ConfirmRequested = (_, _) => { asked = true; return false; };
        await vm.YouTube.ClearDownloadedAsync();
        Check("youtube.clearAsksFirst", asked && vm.YouTube.Downloaded.Count == keptBefore,
            $"asked={asked}, rows={vm.YouTube.Downloaded.Count} of {keptBefore}.");

        vm.YouTube.ConfirmRequested = (_, _) => true;
        await vm.YouTube.ClearDownloadedAsync();
        Check("youtube.clearDownloadedKeepsPreviews",
            vm.YouTube.Downloaded.Count == 0 && vm.YouTube.Results.All(item => item.CanPlay),
            $"rows={vm.YouTube.Downloaded.Count}, previews playable={vm.YouTube.Results.Count(item => item.CanPlay)} of {vm.YouTube.Results.Count}.");
        Check("youtube.clearDownloadedFollowsRows", !vm.YouTube.ClearDownloadedCommand.CanExecute(null),
            "With no rows left the button must go quiet.");

        await vm.YouTube.ClearSearchCacheAsync();
        Check("youtube.clearSearchLeavesNothing", vm.YouTube.Results.All(item => !item.CanPlay),
            $"playable left={vm.YouTube.Results.Count(item => item.CanPlay)}.");
        vm.YouTube.ConfirmRequested = null;
        vm.YouTube.SearchQuery = string.Empty;
        vm.YouTube.Results.Clear();
        SmokeYouTubeBinaries.YtDlpPresent = false;
        vm.YouTube.RefreshBinaryState();

        // The page switch must really swap the rendered content, so it runs on the window's own
        // view model and is undone afterwards, keeping the rest of the suite on the playlist page.
        // The search animation is pure XAML, so the only thing worth proving is that it is really
        // up while a search runs and gone when the results arrive. A screenshot keeps it visible
        // for a human eye as well.
        {
            var windowAnimation = (FrameworkElement)_window!.FindName("SearchAnimation")!;
            var searching = _vm!.YouTube;
            // The animation lives on the YouTube page, so the page is switched first; the suite
            // switches it anyway a few lines down and the call is idempotent.
            _vm!.ShowYouTubeCommand.Execute(null);
            await SettleAsync();
            // The window view model needs yt-dlp reported as present, or the search bails out
            // before reaching the fake runner and the animation never shows.
            SmokeYouTubeBinaries.YtDlpPresent = true;
            searching.RefreshBinaryState();
            SmokeYouTubeRunner.SearchDelay = TimeSpan.FromSeconds(6);
            try
            {
                searching.SearchQuery = "animation";
                searching.SearchCommand.Execute(null);
                await UntilAsync(() => searching.IsSearching, 5000, "animation search started");
                await SettleAsync();
                Check("youtube.searchAnimationShown", windowAnimation.Visibility == Visibility.Visible,
                    $"Visibility={windowAnimation.Visibility}.");
                SaveScreenshot(_window!, $"youtube-searching-{Loc.CurrentCode}.png");
                await UntilAsync(() => !searching.IsSearching, 25000, "animation search finished");
                await SettleAsync();
                Check("youtube.searchAnimationHidden", windowAnimation.Visibility != Visibility.Visible,
                    $"Visibility={windowAnimation.Visibility}.");
            }
            finally { SmokeYouTubeRunner.SearchDelay = TimeSpan.Zero; }
        }

        var windowViewModel = _vm!;
        windowViewModel.ShowYouTubeCommand.Execute(null);
        await SettleAsync();
        var searchBoxes = Descendants<TextBox>(_window!).Count(box => box.IsVisible);
        Check("youtube.pageSwitch", windowViewModel.IsYouTubePage && !windowViewModel.IsPlaylistPage && searchBoxes >= 1,
            $"IsYouTubePage={windowViewModel.IsYouTubePage}, visible text boxes={searchBoxes}.");

        // The search tab is captured with real rows, so the listen button and the cache state show.
        SmokeYouTubeBinaries.YtDlpPresent = true;
        windowViewModel.YouTube.RefreshBinaryState();
        windowViewModel.YouTube.SearchQuery = "rick astley";
        windowViewModel.YouTube.SearchCommand.Execute(null);
        await UntilAsync(() => windowViewModel.YouTube.Results.Count == 2, 8000, "screenshot results");
        await UntilAsync(() => windowViewModel.YouTube.Results.All(item => item.CanPlay), 8000, "screenshot cache");
        await SettleAsync();
        Check("youtube.searchScreenshot",
            windowViewModel.YouTube.Results.Count == 2 && windowViewModel.YouTube.Results.All(item => item.CanPlay),
            $"Results={windowViewModel.YouTube.Results.Count}, playable={windowViewModel.YouTube.Results.Count(item => item.CanPlay)}.");
        SaveScreenshot(_window!, $"youtube-search-{Loc.CurrentCode}.png");
        windowViewModel.YouTube.Results.Clear();
        SmokeYouTubeBinaries.YtDlpPresent = false;
        windowViewModel.YouTube.RefreshBinaryState();

        // The downloaded tab is captured with the real fixture files, so the counter and the
        // status chips are visible in the picture instead of an empty list.
        windowViewModel.YouTube.Downloaded.Add(new YouTubeTrackViewModel(new YouTubeTrackState
        {
            VideoId = "dQw4w9WgXcQ", Title = "Cached fixture", CachedPath = cached,
            Status = YouTubeTrackStatus.Ready
        }));
        windowViewModel.YouTube.Downloaded.Add(new YouTubeTrackViewModel(new YouTubeTrackState
        {
            VideoId = "kJQP7kiw5Fk", Title = "Second fixture", CachedPath = cachedSecond,
            Status = YouTubeTrackStatus.Ready
        }));
        windowViewModel.YouTube.ShowDownloadedTabCommand.Execute(null);
        windowViewModel.YouTube.SelectTrack(windowViewModel.YouTube.Downloaded[0]);
        await SettleAsync();
        Check("youtube.downloadedScreenshot", windowViewModel.YouTube.TrackCount == 2 &&
            windowViewModel.YouTube.IsDownloadedTab,
            $"TrackCount={windowViewModel.YouTube.TrackCount}.");
        SaveScreenshot(_window!, $"youtube-downloaded-{Loc.CurrentCode}.png");
        windowViewModel.YouTube.Downloaded.Clear();
        windowViewModel.YouTube.ShowSearchTabCommand.Execute(null);
        await SettleAsync();
        windowViewModel.ShowPlaylistCommand.Execute(null);
        await SettleAsync();
        Check("youtube.pageSwitchBack", windowViewModel.IsPlaylistPage &&
            Descendants<TextBox>(_window!).All(box => !box.IsVisible),
            $"IsPlaylistPage={windowViewModel.IsPlaylistPage}, visible text boxes={Descendants<TextBox>(_window!).Count(box => box.IsVisible)}.");

        await vm.DisposeAsync();
        _ownedModels.Remove(vm);
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
        vm.PunchResponseEnabled = false;
        vm.BassReactivePercentValue = 12;
        vm.StereoPulseEnabled = true;
        vm.AdaptiveVolumeEnabled = false;
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
        Check("settings.allFieldsPersisted", loaded.SchemaVersion == AppSettings.CurrentSchemaVersion &&
            Near(loaded.MinVolume, 0.25) && Near(loaded.MaxVolume, 0.8) && Near(loaded.IdleTimeout, 4) && !loaded.PauseOnIdle &&
            loaded.AutoAdvance && !loaded.Loop && loaded.Language == "ru" && loaded.Theme == "light" && loaded.SelectedTrackId == selected &&
            !loaded.PunchResponseEnabled && Near(loaded.BassReactiveGain, 0.12) && loaded.StereoPulseEnabled &&
            !loaded.AdaptiveVolumeEnabled &&
            loaded.Tracks.Count == expectedTracks.Length && loaded.Tracks.Zip(expectedTracks).All(pair => pair.First.Id == pair.Second.Id &&
                pair.First.SourcePath == pair.Second.SourcePath && pair.First.AddedAt == pair.Second.AddedAt),
            $"schema={loaded.SchemaVersion}, min={loaded.MinVolume}, max={loaded.MaxVolume}, idle={loaded.IdleTimeout}s, pause={loaded.PauseOnIdle}, auto={loaded.AutoAdvance}, loop={loaded.Loop}, language={loaded.Language}, theme={loaded.Theme}, selected={loaded.SelectedTrackId}; path={Friendly(SettingsPath)}.");
        var raw = await File.ReadAllTextAsync(SettingsPath);
        using var document = JsonDocument.Parse(raw);
        var fields = document.RootElement.EnumerateObject().ToArray();
        JsonElement Value(string name) => fields.Single(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        Check("settings.jsonUnits", Near(Value(nameof(AppSettings.MinVolume)).GetDouble(), 0.25) &&
            Near(Value(nameof(AppSettings.MaxVolume)).GetDouble(), 0.8) && Near(Value(nameof(AppSettings.IdleTimeout)).GetDouble(), 4) &&
            !Value(nameof(AppSettings.PauseOnIdle)).GetBoolean() &&
            Near(Value(nameof(AppSettings.BassReactiveGain)).GetDouble(), 0.12) &&
            Value(nameof(AppSettings.StereoPulseEnabled)).GetBoolean() &&
            !Value(nameof(AppSettings.PunchResponseEnabled)).GetBoolean() &&
            !Value(nameof(AppSettings.AdaptiveVolumeEnabled)).GetBoolean(),
            "JSON volumes are normalized fractions; the bass gain is a fraction; the three new switches are booleans.");
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
        GetField<GuideWindow>(app, "_guideWindow")?.Close();
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
    /// <summary>
    /// TextBlock.Text stays empty when the content is built from Inlines, so a rendered line has to
    /// be read through its runs.
    /// </summary>
    private static string RenderedText(TextBlock block) =>
        block.Inlines.Count == 0 ? block.Text : string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));

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

    private sealed class SmokeYouTubeBinaries : IYtDlpBinaryProvider
    {
        /// <summary>Flipped by the search scenario so a search can run without a real binary.</summary>
        public static bool YtDlpPresent { get; set; }

        public string? FindYtDlp() => YtDlpPresent ? "yt-dlp.exe" : null;
        public string? FindFfmpeg() => null;
        public Task<string> EnsureYtDlpAsync(
            IProgress<double>? progress, CancellationToken cancellationToken, bool force = false) =>
            throw new InvalidOperationException("The smoke never downloads yt-dlp.");
        public Task<string> EnsureFfmpegAsync(IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The smoke never downloads ffmpeg.");
    }

    /// <summary>
    /// Answers the two calls the search tab makes without a binary: the version probe and the
    /// search itself. A download request still throws, so nothing can silently hit the network.
    /// </summary>
    private sealed class SmokeYouTubeRunner : IYouTubeProcessRunner
    {
        /// <summary>A real MP3 the fake download copies, so the cache holds decodable audio.</summary>
        public static string? FixturePath { get; set; }

/// <summary>
        /// Slows the fake download down so a second search can start while the first cache pass
        /// is still running: that overlap is exactly the bug that left later rows uncacheable.
        /// </summary>
        public static TimeSpan Delay { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// Slows only the search itself, which is what keeps the "searching" state on screen long
        /// enough to photograph. Kept apart from <see cref="Delay"/> so a slow download can be
        /// tested without also delaying the results.
        /// </summary>
        public static TimeSpan SearchDelay { get; set; } = TimeSpan.Zero;

public async Task<YouTubeProcessResult> RunAsync(
            YouTubeProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Arguments.Contains("--version"))
            {
                return new YouTubeProcessResult(0, "2025.01.15\n", string.Empty, false);
            }

            if (request.Arguments.Any(argument => argument.StartsWith("ytsearch", StringComparison.Ordinal)))
            {
                if (SearchDelay > TimeSpan.Zero)
                {
                    await Task.Delay(SearchDelay, cancellationToken).ConfigureAwait(false);
                }
                // The ids follow the query, so a second search really produces different rows: the
                // bug was that the first pass kept caching its own results and starved the new ones.
                // The ids follow the query, so a second search really produces different rows: the
                // bug was that the first pass kept caching its own results and starved the new ones.
                var query = string.Concat(request.Arguments
                    .First(argument => argument.StartsWith("ytsearch", StringComparison.Ordinal))
                    .SkipWhile(character => character != ':')).Substring(1);
                var suffix = Math.Abs(query.GetHashCode(StringComparison.Ordinal)) % 10000;
                var payload =
                    $"{{\"id\":\"preview{suffix:D4}\",\"title\":\"Never Gonna Give You Up\",\"uploader\":\"Rick Astley\",\"duration\":213}}" + "\n" +
                    $"{{\"id\":\"secondr{suffix:D4}\",\"title\":\"Despacito\",\"uploader\":\"Luis Fonsi\",\"duration\":282}}";
                return new YouTubeProcessResult(0, payload, string.Empty, false);
            }

            // A resolve call asks for metadata only.
            if (request.Arguments.Contains("--dump-single-json"))
            {
                return new YouTubeProcessResult(0,
                    """{"id":"previewAAAA","title":"Never Gonna Give You Up","is_live":false}""", string.Empty, false);
            }

            // Only the download is slowed here: the search has its own knob so a slow transfer
            // can be tested without also holding back the results.
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            // Otherwise it is a download: place a real file where -o points and print its path.
            var template = request.Arguments.SkipWhile(a => a != "-o").Skip(1).FirstOrDefault();
            if (template is not null && FixturePath is not null)
            {
                var target = template.Replace("%(ext)s", "mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(FixturePath, target, overwrite: true);
                return new YouTubeProcessResult(0, target, string.Empty, false);
            }

            throw new InvalidOperationException($"The smoke never starts yt-dlp for: {string.Join(' ', request.Arguments)}");
        }
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
