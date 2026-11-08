using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.Updates;
using BeatzOnKeys.UI.Localization;
using BeatzOnKeys.UI.Theming;
using BeatzOnKeys.UI.ViewModels;
using Forms = System.Windows.Forms;

namespace BeatzOnKeys.UI;

public partial class App : System.Windows.Application
{
    private SettingsService? _settingsService;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private AboutWindow? _aboutWindow;
    private GuideWindow? _guideWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _openMenuItem;
    private Forms.ToolStripMenuItem? _aboutMenuItem;
    private Forms.ToolStripMenuItem? _guideMenuItem;
    private Forms.ToolStripMenuItem? _stopMenuItem;
    private Forms.ToolStripMenuItem? _exitMenuItem;
    private DispatcherTimer? _updateTimer;
    private CancellationTokenSource? _updateCancellation;
    private string? _notifiedUpdateVersion;
    private StorageSettings? _storageSettings;
    private bool _storageChoicePending;
    private Icon? _trayIconImage;
    private bool _isExiting;
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private Thread? _activateThread;

    private const string SingleInstanceName = @"Local\BeatzOnKeys.SingleInstance.v1";
    private const string ActivateEventName = @"Local\BeatzOnKeys.Activate.v1";

    /// <summary>
    /// A second launch does not open a dialog: it signals this instance, which restores its
    /// window from the tray. The listener is a background thread so no UI work blocks here.
    /// </summary>
    private void StartActivationListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception exception)
        {
            AppLogger.Error("ui", "Could not create the single-instance activation event.", exception);
            return;
        }

        _activateThread = new Thread(() =>
        {
            var handle = _activateEvent;
            while (handle is not null && !_isExiting)
            {
                try
                {
                    if (!handle.WaitOne(500)) { continue; }
                }
                catch (ObjectDisposedException) { return; }

                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppLogger.Info("ui", "Another launch asked this instance to show its window.");
                        ShowMainWindow();
                    }));
                }
                catch (Exception exception) when (exception is TaskCanceledException or InvalidOperationException)
                {
                    // Shutting down; nothing left to show.
                }
            }
        })
        {
            IsBackground = true,
            Name = "BeatzOnKeys activation listener"
        };
        _activateThread.Start();
    }

    /// <summary>True only while the user really wants the process to end (tray Exit, update, logoff).</summary>
    public static bool IsExplicitExit { get; private set; }

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        try
        {
            // One instance only: a second copy would install a second hook and fight over audio.
            _singleInstance = new Mutex(true, SingleInstanceName, out var createdNew);
            if (!createdNew)
            {
                // Ask the running copy to show itself, then leave quietly: no dialog, no second hook.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
                    {
                        using (activate) { activate.Set(); }
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
                {
                    // Nothing to signal; exiting is still the right outcome.
                }

                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown(0);
                return;
            }

            ResolveStorage();
            AppLogger.Initialize();
            StartActivationListener();
            AppLogger.Info("ui", $"Starting BeatzOnKeys {typeof(App).Assembly.GetName().Version}; pid={Environment.ProcessId}.");
            _settingsService = new SettingsService();
            var settings = await _settingsService.LoadAsync().ConfigureAwait(true);
            Loc.Initialize(settings.Language);
            Theme.Apply(Theme.Parse(settings.Theme), notify: false);

            _viewModel = new MainViewModel(settings, _settingsService);
            _mainWindow = new MainWindow(_viewModel);
            MainWindow = _mainWindow;
            _viewModel.StateChanged += UpdateTrayState;
            SetupTrayIcon();
            await _viewModel.InitializeAsync().ConfigureAwait(true);
            _mainWindow.Show();
            _mainWindow.Activate();
            // Hiding the window must not end the process; only an explicit exit does.
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            NotifyStartupHints();
            SetupAutomaticUpdates();
            var previousUpdate = UpdateService.ConsumeLastApplyReport();
            if (previousUpdate is { Succeeded: false })
            {
                ShowTrayNotice(Loc.T("Upd_PreviousFailed"));
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("startup", "Application startup failed.", exception);
            System.Windows.MessageBox.Show(
                exception.Message,
                "BeatzOnKeys",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Marks an intentional shutdown so the window close handler does not hide instead.</summary>
    public static void RequestExit() => IsExplicitExit = true;

    public void HideMainWindow()
    {
        Dispatcher.VerifyAccess();
        if (_mainWindow is null || _isExiting)
        {
            return;
        }
        if (_trayIcon is null)
        {
            AppLogger.Warning("ui", "Window kept visible because the tray icon is not ready.");
            return;
        }

        _trayIcon.Visible = true;
        _mainWindow.Hide();
        AppLogger.Info("ui", $"Window hidden to tray; playback running={_viewModel?.IsRunning == true}; pid={Environment.ProcessId}.");

        // A balloon is a hint that never blocks typing or playback; a modal dialog would freeze
        // the app until dismissed, which is exactly what this app must not do.
        ShowTrayNotice(Loc.T("L_TrayHint"));
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null || _isExiting)
        {
            return;
        }

        // The window is only ever hidden, but guard anyway: Show() on a closed window throws.
        try
        {
            _mainWindow.Show();
        }
        catch (InvalidOperationException exception)
        {
            AppLogger.Error("ui", "The main window can no longer be shown.", exception);
            return;
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
        AppLogger.Info("ui", $"Window restored from tray; pid={Environment.ProcessId}.");
    }

    public void ShowAboutWindow()
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow
        {
            Owner = _mainWindow?.IsVisible == true ? _mainWindow : null
        };
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
        _aboutWindow.Activate();
    }

    /// <summary>Opens the reference window; a second request just focuses the open one.</summary>
    public void ShowGuideWindow()
    {
        if (_guideWindow is not null)
        {
            _guideWindow.Activate();
            return;
        }

        _guideWindow = new GuideWindow
        {
            Owner = _mainWindow?.IsVisible == true ? _mainWindow : null
        };
        _guideWindow.Closed += (_, _) => _guideWindow = null;
        _guideWindow.Show();
        _guideWindow.Activate();
    }

    private void SetupTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "BeatzOnKeys.ico");
        _trayIconImage = File.Exists(iconPath)
            ? new Icon(iconPath, Forms.SystemInformation.SmallIconSize)
            : LoadEmbeddedIcon() ?? (Icon)SystemIcons.Application.Clone();

        _openMenuItem = new Forms.ToolStripMenuItem();
        _openMenuItem.Click += (_, _) => ShowMainWindow();
        _aboutMenuItem = new Forms.ToolStripMenuItem();
        _aboutMenuItem.Click += (_, _) => ShowAboutWindow();
        _guideMenuItem = new Forms.ToolStripMenuItem();
        _guideMenuItem.Click += (_, _) => ShowGuideWindow();
        _stopMenuItem = new Forms.ToolStripMenuItem();
        _stopMenuItem.Click += (_, _) =>
        {
            if (_viewModel?.StopCommand.CanExecute(null) == true)
            {
                _viewModel.StopCommand.Execute(null);
            }
        };
        _exitMenuItem = new Forms.ToolStripMenuItem();
        _exitMenuItem.Click += async (_, _) => await ExitApplicationAsync().ConfigureAwait(true);

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.AddRange([
            _openMenuItem,
            _guideMenuItem,
            _aboutMenuItem,
            new Forms.ToolStripSeparator(),
            _stopMenuItem,
            new Forms.ToolStripSeparator(),
            _exitMenuItem
        ]);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconImage,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        _trayIcon.BalloonTipClicked += (_, _) => ShowAboutWindow();
        UpdateTrayState();
    }

    /// <summary>
    /// A single-file build has no Assets folder on disk, so the icon must come from the
    /// executable's own resources. Without this the tray would show a generic Windows icon.
    /// </summary>
    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var process = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(process))
            {
                var extracted = Icon.ExtractAssociatedIcon(process);
                if (extracted is not null) { return extracted; }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("ui", "Could not read the application icon from the executable.", exception);
        }

        return null;
    }

    private void UpdateTrayState()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var tooltip = Loc.T("L_TrayText");
        _trayIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
        if (_openMenuItem is not null)
        {
            _openMenuItem.Text = Loc.T("L_TrayOpen");
        }

        if (_aboutMenuItem is not null)
        {
            _aboutMenuItem.Text = Loc.T("L_About");
        }

        if (_guideMenuItem is not null)
        {
            _guideMenuItem.Text = Loc.T("L_Guide");
        }

        if (_stopMenuItem is not null)
        {
            _stopMenuItem.Text = Loc.T("L_TrayStop");
            _stopMenuItem.Enabled = _viewModel?.IsRunning == true;
        }

        if (_exitMenuItem is not null)
        {
            _exitMenuItem.Text = Loc.T("L_TrayExit");
        }
    }

    /// <summary>
    /// Two conditions make a working player look broken: preferences that cannot be written and a
    /// Windows output volume so low that nothing is audible. Both are reported once, as a balloon,
    /// so the app never blocks on a dialog during startup.
    /// </summary>
    private void NotifyStartupHints()
    {
        var hints = new List<string>();
        if (_viewModel?.StorageWarning is { Length: > 0 } storageWarning)
        {
            AppLogger.Warning("settings", storageWarning);
            hints.Add(storageWarning);
        }

        if (SystemVolumeProbe.TryRead() is { } output && output.IsEffectivelySilent)
        {
            hints.Add(Loc.F("L_SystemVolumeLowFormat", output.Percent));
        }

        if (hints.Count > 0)
        {
            ShowTrayNotice(string.Join(Environment.NewLine, hints));
        }
    }

    private void SetupAutomaticUpdates()
    {
        _updateCancellation = new CancellationTokenSource();
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(15)
        };
        _updateTimer.Tick += async (_, _) =>
        {
            if (UpdateService.IsCheckDue(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5)))
            {
                await CheckForUpdatesSilentlyAsync(_updateCancellation.Token).ConfigureAwait(true);
            }
        };
        _updateTimer.Start();
        _ = RunInitialUpdateCheckAsync(_updateCancellation.Token);
    }

    private async Task RunInitialUpdateCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(true);
            await CheckForUpdatesSilentlyAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Normal application shutdown.
        }
    }

    /// <summary>
    /// Decides where this run keeps its data, asks for a folder when the executable sits somewhere
    /// unsuitable, and copies an older per-user data set into the new root the first time it is used.
    /// Runs before logging starts, so the log folder is the one the chosen root provides.
    /// </summary>
    private void ResolveStorage()
    {
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        _storageSettings = StorageSettingsStore.Load();
        var plan = StorageResolver.Plan(executableDirectory, _storageSettings);

        // A harness that pinned the mode may also have redirected the paths itself, so the plan
        // must not overwrite a root that something else already chose.
        if (!StorageResolver.IsForced || AppPaths.RootDirectory == AppPaths.DefaultLocalRoot)
        {
            AppPaths.Apply(plan.Mode, plan.Root);
        }

        // The log folder exists in every mode, including the fallback.
        AppPaths.EnsureCreated();

        if (plan.Mode != StorageMode.LocalAppData && !StorageResolver.IsForced)
        {
            StorageResolver.Migrate(AppPaths.DefaultLocalRoot, AppPaths.RootDirectory);
        }

        // The question is asked after the window is up, in the app's own dialog: a bare system
        // folder picker before anything is drawn explains nothing and reads as a bug.
        _storageChoicePending = plan.NeedsFolderChoice && !StorageResolver.IsForced;
    }

    /// <summary>
    /// The first-run question, shown once the main window exists. Cancelling is a valid answer:
    /// the run continues on the per-user folder and the question returns next time, because
    /// nothing was recorded.
    /// </summary>
    private void OfferStorageFolder()
    {
        if (!_storageChoicePending || _storageSettings is null || _mainWindow is null) { return; }
        _storageChoicePending = false;
        try
        {
            var wants = MessageDialog.Ask(
                _mainWindow,
                Loc.F("Storage_ExplainBody", AppPaths.DefaultLocalRoot),
                Loc.T("Storage_ExplainTitle"),
                Loc.T("Storage_ChooseButton"),
                Loc.T("Storage_LaterButton"));
            if (!wants) { return; }

            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Loc.T("Storage_PickFolder"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            // Cancelling the picker leaves the run on the per-user folder without a second
            // message: the user just said "not now", and scolding them for it is rude.
            if (picker.ShowDialog() != true) { return; }

            // The picked folder is a home for the data, not the data itself: a subfolder keeps
            // our files apart from whatever else the user keeps there.
            var root = AppPaths.ChosenRootFor(picker.FolderName);
            _storageSettings.ChosenRoot = root;
            StorageSettingsStore.Save(_storageSettings);
            // The data this run already opened stays where it is; the next start uses the folder,
            // and the copy happens then, so nothing is read while it is being moved.
            StorageResolver.Migrate(AppPaths.DefaultLocalRoot, root);
            MessageDialog.Notify(
                _mainWindow,
                Loc.F("Storage_ChosenBody", root),
                Loc.T("Storage_ExplainTitle"));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("storage", $"Could not ask for a storage folder: {exception.Message}");
        }
    }

    private async Task CheckForUpdatesSilentlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await UpdateService.CheckAsync(cancellationToken).ConfigureAwait(true);
            if (!result.HasUpdate || result.Latest is null || _notifiedUpdateVersion == result.Latest.Display)
            {
                return;
            }

            if (_aboutWindow is { IsVisible: true })
            {
                // The About window already shows the update state and the download button, so a
                // dialog on top of it only repeats itself and blocks what the user opened it for.
                // It counts as announced too, otherwise it would pop up right after they close it.
                _notifiedUpdateVersion = result.Latest.Display;
                return;
            }

            _notifiedUpdateVersion = result.Latest.Display;
            // A tray balloon is easy to miss, and it cannot carry the release notes. The window
            // itself shows the notice, and the About window is where the download happens.
            var notes = result.Notes ?? string.Empty;
            var wantsUpdate = MessageDialog.Announce(
                MainWindow,
                Loc.F("Upd_NoticeTitle", result.Latest.Display),
                Loc.F("Upd_AvailableBody", UpdateService.CurrentVersion.Display, result.Latest.Display),
                notes);
            if (wantsUpdate) { ShowAboutWindow(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    private void ShowTrayNotice(string message)
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.BalloonTipTitle = Loc.T("L_AppName");
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(6000);
    }

    public Task ShutdownForUpdateAsync() => ExitApplicationAsync();

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        IsExplicitExit = true;
        _updateTimer?.Stop();
        _updateCancellation?.Cancel();
        _aboutWindow?.Close();
        _guideWindow?.Close();
        try
        {
            if (_viewModel is not null)
            {
                _viewModel.StateChanged -= UpdateTrayState;
                await _viewModel.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("shutdown", "Graceful shutdown failed.", exception);
        }
        finally
        {
            _updateCancellation?.Dispose();
            _updateCancellation = null;
            DisposeTrayIcon();
            StopActivationListener();
            _singleInstance?.Dispose();
            _singleInstance = null;

            // Deliberately do NOT Close() the window. MusicCatRGB keeps it alive and relies on
            // Shutdown(); closing it here makes any later Show() by the tray throw
            // "cannot set Visibility ... after a Window has been closed".
            Shutdown(0);
        }
    }

    private void StopActivationListener()
    {
        var handle = _activateEvent;
        _activateEvent = null;
        try { handle?.Set(); } catch (ObjectDisposedException) { }
        try { handle?.Dispose(); } catch (ObjectDisposedException) { }
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayIconImage?.Dispose();
        _trayIconImage = null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        AppLogger.Error("ui", "Unhandled UI exception.", eventArgs.Exception);
        System.Windows.MessageBox.Show(
            eventArgs.Exception.Message,
            Loc.T("L_AppName"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        eventArgs.Handled = true;
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        AppLogger.Info("ui", $"Application exiting; code={eventArgs.ApplicationExitCode}; pid={Environment.ProcessId}.");
        _updateTimer?.Stop();
        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        DisposeTrayIcon();
        base.OnExit(eventArgs);
    }
}
