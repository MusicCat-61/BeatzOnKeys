using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.Updates;
using BeatzOnKeys.UI.Localization;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace BeatzOnKeys.UI;

public partial class AboutWindow : Window
{
    private const string ProjectUrl = "https://github.com/MusicCat-61/BeatzOnKeys";
    // The button is labelled "license and third-party notices", so it opens the notices, which
    // carry the full license texts and links to the LICENSE file.
    private const string NoticesUrl = "https://github.com/MusicCat-61/BeatzOnKeys/blob/main/THIRD-PARTY-NOTICES.md";
    private const string TelegramUrl = "https://t.me/music_cat1";

    /// <summary>The program's own channel, kept apart from the author's contact link.</summary>
    private const string TelegramChannelUrl = "https://t.me/beatzonkeys";
    private const string DonateUrl = "https://dalink.to/musiccat61";
    private static readonly TimeSpan FreshResultAge = TimeSpan.FromMinutes(30);
    private UpdateCheckResult? _result;
    private PreparedUpdate? _prepared;
    private bool _busy;

    public AboutWindow()
    {
        InitializeComponent();
        RefreshStaticText();
        UpdateService.Checked += OnUpdateChecked;
        Loc.LanguageChanged += OnLanguageChanged;

        _prepared = UpdateService.FindPreparedUpdate();
        if (_prepared is not null)
        {
            ShowPrepared(_prepared);
        }
        else if (UpdateService.LastResult is { } known &&
                 !UpdateService.IsCheckDue(FreshResultAge, TimeSpan.Zero))
        {
            ShowResult(known);
        }
        else
        {
            _ = RefreshAsync();
        }
    }

    private static string DisplayVersion
    {
        get
        {
            var assembly = typeof(AboutWindow).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (ReleaseVersion.TryParse(informational, out var parsed))
            {
                return parsed.Display;
            }

            return assembly.GetName().Version?.ToString(3) ?? "1.3.3";
        }
    }

    private void RefreshStaticText()
    {
        Title = Loc.T("About_Title");
        VersionText.Text = Loc.F("About_Version", DisplayVersion);
    }

    private void SetButtons(bool check, bool download, bool install, bool release)
    {
        UpdateCheckButton.Visibility = check ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadButton.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
        UpdateInstallButton.Visibility = install ? Visibility.Visible : Visibility.Collapsed;
        UpdateReleaseButton.Visibility = release ? Visibility.Visible : Visibility.Collapsed;
        UpdateCheckButton.IsEnabled = !_busy;
        UpdateDownloadButton.IsEnabled = !_busy;
        UpdateInstallButton.IsEnabled = !_busy;
        UpdateReleaseButton.IsEnabled = !_busy;
    }

    private void ShowResult(UpdateCheckResult result)
    {
        _result = result;
        _prepared = UpdateService.FindPreparedUpdate();
        if (_prepared is not null)
        {
            ShowPrepared(_prepared);
            return;
        }

        UpdateProgress.Visibility = Visibility.Collapsed;
        switch (result.Status)
        {
            case UpdateStatus.UpToDate:
                UpdateStatusText.Text = Loc.T("Upd_UpToDate");
                SetButtons(check: true, download: false, install: false, release: false);
                break;
            case UpdateStatus.NoReleases:
                UpdateStatusText.Text = Loc.T("Upd_NoReleaseYet");
                SetButtons(check: true, download: false, install: false, release: false);
                break;
            case UpdateStatus.UpdateAvailable:
                UpdateStatusText.Text = Loc.F("Upd_Available", result.Latest?.Display ?? "?");
                UpdateDownloadButton.Content = Loc.F("Upd_Download", result.Latest?.Display ?? "?");
                SetButtons(check: true, download: true, install: false, release: true);
                break;
            case UpdateStatus.NoDownload:
                UpdateStatusText.Text = Loc.F("Upd_NoDownload", result.Latest?.Display ?? "?");
                SetButtons(check: true, download: false, install: false, release: true);
                break;
            case UpdateStatus.Failed:
                UpdateStatusText.Text = Loc.F("Upd_Failed", LocalizeUpdateError(result.Error));
                SetButtons(check: true, download: false, install: false, release: false);
                break;
            default:
                UpdateStatusText.Text = Loc.T("Upd_Idle");
                SetButtons(check: true, download: false, install: false, release: false);
                break;
        }
    }

    private void ShowPrepared(PreparedUpdate prepared)
    {
        _prepared = prepared;
        UpdateStatusText.Text = Loc.F("Upd_Ready", prepared.Version);
        UpdateProgress.Value = 100;
        UpdateProgress.Visibility = Visibility.Visible;
        SetButtons(check: false, download: false, install: true, release: false);
    }

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateStatusText.Text = Loc.T("Upd_Checking");
        UpdateProgress.Visibility = Visibility.Collapsed;
        SetButtons(check: false, download: false, install: false, release: false);
        try
        {
            var result = await UpdateService.CheckAsync().ConfigureAwait(true);
            _busy = false;
            ShowResult(result);
        }
        catch (Exception exception)
        {
            _busy = false;
            UpdateStatusText.Text = Loc.F("Upd_Failed", exception.Message);
            SetButtons(check: true, download: false, install: false, release: false);
            AppLogger.Error("update", "Manual update check failed.", exception);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs eventArgs) =>
        await RefreshAsync().ConfigureAwait(true);

    private async void UpdateDownload_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_busy || _result is not { HasUpdate: true } result)
        {
            return;
        }

        if (!UpdateService.CanWriteToInstallFolder())
        {
            UpdateStatusText.Text = Loc.F("Upd_NotWritable", UpdateService.ExecutableDirectory);
            return;
        }

        _busy = true;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        SetButtons(check: false, download: true, install: false, release: false);
        var progress = new Progress<double>(fraction =>
        {
            var percentage = Math.Clamp((int)Math.Round(fraction * 100), 0, 100);
            UpdateProgress.Value = percentage;
            UpdateStatusText.Text = Loc.F("Upd_Downloading", percentage);
        });

        try
        {
            _prepared = await UpdateService.DownloadAndPrepareAsync(result, progress).ConfigureAwait(true);
            _busy = false;
            ShowPrepared(_prepared);
        }
        catch (UpdateStorageException exception)
        {
            // The folder was probed before downloading, so this is a permission problem the user
            // can act on: say so, and keep the manual download path available.
            _busy = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = Loc.F("Upd_NoStorage", string.Join(Environment.NewLine, exception.AttemptedPaths));
            SetButtons(check: true, download: true, install: false, release: true);
            AppLogger.Error("update", "No writable folder accepted the update.", exception);
        }
        catch (Exception exception)
        {
            _busy = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = Loc.F("Upd_DownloadFailed", exception.Message);
            SetButtons(check: true, download: true, install: false, release: true);
            AppLogger.Error("update", "Update download or verification failed.", exception);
        }
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_busy)
        {
            return;
        }

        var prepared = _prepared ?? UpdateService.FindPreparedUpdate();
        if (prepared is null)
        {
            UpdateStatusText.Text = Loc.T("Upd_NothingStaged");
            SetButtons(check: true, download: _result?.HasUpdate == true, install: false, release: false);
            return;
        }

        if (!UpdateService.TryLaunchApplyScript(prepared, out var error))
        {
            UpdateStatusText.Text = Loc.F("Upd_RestartFailed", error);
            return;
        }

        _busy = true;
        UpdateStatusText.Text = Loc.T("Upd_Applying");
        SetButtons(check: false, download: false, install: false, release: false);
        await ((App)System.Windows.Application.Current).ShutdownForUpdateAsync().ConfigureAwait(true);
    }

    private void UpdateRelease_Click(object sender, RoutedEventArgs eventArgs) =>
        OpenUrl(_result?.ReleaseUrl ?? $"https://github.com/{UpdateService.Repository}/releases/latest");

    private void OnUpdateChecked(object? sender, UpdateCheckResult result)
    {
        if (_busy)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_busy)
            {
                ShowResult(result);
            }
        });
    }

    private void OnLanguageChanged()
    {
        RefreshStaticText();
        if (_prepared is not null)
        {
            ShowPrepared(_prepared);
        }
        else if (_result is not null)
        {
            ShowResult(_result);
        }
    }

    private static string LocalizeUpdateError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return string.Empty;
        }

        return error.Contains("repository or its first release", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("Upd_NoReleaseYet")
            : error;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLogger.Error("ui", $"Could not open '{url}'.", exception);
        }
    }

    private void ProjectLink_Click(object sender, RoutedEventArgs eventArgs) => OpenUrl(ProjectUrl);

    private void LicenseLink_Click(object sender, RoutedEventArgs eventArgs) => OpenUrl(NoticesUrl);

    private void TelegramLink_Click(object sender, RoutedEventArgs eventArgs) => OpenUrl(TelegramUrl);

    private void TelegramChannelLink_Click(object sender, RoutedEventArgs eventArgs) => OpenUrl(TelegramChannelUrl);

    private void DonateLink_Click(object sender, RoutedEventArgs eventArgs) => OpenUrl(DonateUrl);

    private void Close_Click(object sender, RoutedEventArgs eventArgs) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        UpdateService.Checked -= OnUpdateChecked;
        Loc.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(eventArgs);
    }
}
