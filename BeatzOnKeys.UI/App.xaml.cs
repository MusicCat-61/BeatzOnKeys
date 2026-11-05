using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _openMenuItem;
    private Forms.ToolStripMenuItem? _aboutMenuItem;
    private Forms.ToolStripMenuItem? _stopMenuItem;
    private Forms.ToolStripMenuItem? _exitMenuItem;
    private DispatcherTimer? _updateTimer;
    private CancellationTokenSource? _updateCancellation;
    private string? _notifiedUpdateVersion;
    private Icon? _trayIconImage;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        try
        {
            AppLogger.Initialize();
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
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
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

    private void SetupTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "BeatzOnKeys.ico");
        _trayIconImage = File.Exists(iconPath)
            ? new Icon(iconPath, Forms.SystemInformation.SmallIconSize)
            : (Icon)SystemIcons.Application.Clone();

        _openMenuItem = new Forms.ToolStripMenuItem();
        _openMenuItem.Click += (_, _) => ShowMainWindow();
        _aboutMenuItem = new Forms.ToolStripMenuItem();
        _aboutMenuItem.Click += (_, _) => ShowAboutWindow();
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

    private async Task CheckForUpdatesSilentlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await UpdateService.CheckAsync(cancellationToken).ConfigureAwait(true);
            if (!result.HasUpdate || result.Latest is null || _notifiedUpdateVersion == result.Latest.Display)
            {
                return;
            }

            _notifiedUpdateVersion = result.Latest.Display;
            ShowTrayNotice(Loc.F("Upd_TrayAvailable", result.Latest.Display));
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
        _updateTimer?.Stop();
        _updateCancellation?.Cancel();
        _aboutWindow?.Close();
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
            if (_mainWindow is not null)
            {
                _mainWindow.AllowClose = true;
                _mainWindow.Close();
            }

            Shutdown(0);
        }
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
