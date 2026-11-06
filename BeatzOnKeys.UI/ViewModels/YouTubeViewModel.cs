using System.Collections.ObjectModel;
using System.Windows;
using System.IO;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.YouTube;
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.UI.Localization;

namespace BeatzOnKeys.UI.ViewModels;

/// <summary>
/// The YouTube section. It owns its own list, its own cache and its own settings, and it never
/// writes into the local playlist: the two collections only meet in the audio player.
/// </summary>
public sealed partial class YouTubeViewModel : ObservableObject
{
    private readonly YouTubeTrackService _service;
    private readonly Action _requestSave;
    private readonly Action<string> _userMessage;
    private readonly CancellationTokenSource _lifetime = new();
    private string _searchQuery = string.Empty;
    private string _statusText;
    private string _cacheSizeText = string.Empty;
    private long _cacheBytes;
    private bool _isSearching;
    private bool _isInstalling;
    private bool _cacheEnabled;
    private string _proxyUrl = string.Empty;
    private string _cookiesFolderPath = string.Empty;
    private double _installProgress;
    private bool _disposed;
    private bool _isSearchTab = true;

    public YouTubeViewModel(
        YouTubeTrackService service,
        IEnumerable<YouTubeTrackState> savedTracks,
        bool cacheEnabled,
        string cookiesFolderPath,
        string proxyUrl,
        Action requestSave,
        Action<string> userMessage)
    {
        _service = service;
        _requestSave = requestSave;
        _userMessage = userMessage;
        _cacheEnabled = cacheEnabled;
        _cookiesFolderPath = cookiesFolderPath ?? string.Empty;
        _proxyUrl = proxyUrl ?? string.Empty;
        // Empty means "nothing to report", so the line does not repeat the hint under the field.
        _statusText = string.Empty;

        foreach (var state in savedTracks)
        {
            Downloaded.Add(new YouTubeTrackViewModel(state));
        }

        SearchCommand = new AsyncRelayCommand(
            () => SearchAsync(),
            () => !IsBusy && !string.IsNullOrWhiteSpace(SearchQuery));
        DownloadCommand = new RelayCommand<YouTubeSearchItemViewModel>(
            item => { if (item is not null) { _ = DownloadAsync(item.VideoId, item.Title); } },
            item => item is { CanDownload: true } && CanStartAnotherDownload);
        InstallYtDlpCommand = new AsyncRelayCommand(InstallYtDlpAsync, () => !IsInstalling);
        PlayCommand = new RelayCommand<YouTubeTrackViewModel>(
            track => { if (track is not null) { PlayRequested?.Invoke(track.CachedPath, track.Title); } },
            track => track is { CanPlay: true });
        RemoveCachedCommand = new RelayCommand<YouTubeTrackViewModel>(RemoveCached, track => track is not null);
        ClearCacheCommand = new RelayCommand(() => _ = ClearCacheAsync(), () => !IsBusy && Downloaded.Count > 0);
        RefreshCacheCommand = new RelayCommand(() => _ = RefreshCacheSizeAsync());
        OpenMusicFolderCommand = new RelayCommand(() => OpenMusicFolderRequested?.Invoke());
        // Exactly the playlist's management model: arrows for one step, drag for any distance.
        MoveTrackUpCommand = new RelayCommand<YouTubeTrackViewModel>(
            track => MoveTrack(track, -1), track => CanMove(track, -1));
        MoveTrackDownCommand = new RelayCommand<YouTubeTrackViewModel>(
            track => MoveTrack(track, 1), track => CanMove(track, 1));

        RefreshPositions();
        ApplyOptions();
        RefreshBinaryState();
    }

    /// <summary>Raised when the user asks to listen to a cached file; the window plays it.</summary>
    public event Action<string, string>? PlayRequested;

    /// <summary>
    /// The section has two inner tabs. Search is the default because that is what the user opens
    /// the section for; the downloaded list is one click away and gets the full width when shown.
    /// </summary>
    public bool IsSearchTab
    {
        get => _isSearchTab;
        private set
        {
            if (!SetProperty(ref _isSearchTab, value)) { return; }
            OnPropertyChanged(nameof(IsDownloadedTab));
            OnPropertyChanged(nameof(SearchTabVisible));
            OnPropertyChanged(nameof(DownloadedTabVisible));
        }
    }

    public bool IsDownloadedTab => !_isSearchTab;

    /// <summary>Visibility helpers, so the XAML needs no converter.</summary>
    public Visibility SearchTabVisible => _isSearchTab ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DownloadedTabVisible => _isSearchTab ? Visibility.Collapsed : Visibility.Visible;

    public RelayCommand ShowSearchTabCommand => _showSearchTab ??= new RelayCommand(() => IsSearchTab = true);
    public RelayCommand ShowDownloadedTabCommand => _showDownloadedTab ??= new RelayCommand(() => IsSearchTab = false);
    private RelayCommand? _showSearchTab;
    private RelayCommand? _showDownloadedTab;

    public event Action? OpenMusicFolderRequested;

    public ObservableCollection<YouTubeSearchItemViewModel> Results { get; } = [];
    public ObservableCollection<YouTubeTrackViewModel> Downloaded { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand<YouTubeSearchItemViewModel> DownloadCommand { get; }
    public AsyncRelayCommand InstallYtDlpCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> PlayCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> RemoveCachedCommand { get; }
    public RelayCommand ClearCacheCommand { get; }
    public RelayCommand RefreshCacheCommand { get; }
    public RelayCommand OpenMusicFolderCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> MoveTrackUpCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> MoveTrackDownCommand { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value)) { return; }
            SearchCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSearch));
        }
    }

    public bool CanSearch => !IsBusy && !string.IsNullOrWhiteSpace(SearchQuery);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>"Занято на диске: 14.3 МБ" — the unit follows the measured size.</summary>
    public string CacheSizeText
    {
        get => _cacheSizeText;
        private set => SetProperty(ref _cacheSizeText, value);
    }

    public long CacheBytes => _cacheBytes;

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (!SetProperty(ref _isSearching, value)) { return; }
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanSearch));
            RefreshCommands();
        }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (!SetProperty(ref _isInstalling, value)) { return; }
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanSearch));
            RefreshCommands();
        }
    }

    public double InstallProgress
    {
        get => _installProgress;
        private set => SetProperty(ref _installProgress, value);
    }

    public bool IsBusy => IsSearching || IsInstalling || IsDownloading;

    public bool IsDownloading => Downloaded.Any(track => track.IsDownloading);

    public bool HasResults => Results.Count > 0;
    public bool HasDownloaded => Downloaded.Count > 0;
    public bool ShowEmptyResults => !IsSearching && Results.Count == 0;

    public bool CacheEnabled
    {
        get => _cacheEnabled;
        set
        {
            if (!SetProperty(ref _cacheEnabled, value)) { return; }
            RefreshPositions();
        ApplyOptions();
            _requestSave();
        }
    }

    public string ProxyUrl
    {
        get => _proxyUrl;
        set
        {
            if (!SetProperty(ref _proxyUrl, value ?? string.Empty)) { return; }
            RefreshPositions();
        ApplyOptions();
            _requestSave();
        }
    }

    public string CookiesFolderPath
    {
        get => _cookiesFolderPath;
        set
        {
            if (!SetProperty(ref _cookiesFolderPath, value ?? string.Empty)) { return; }
            RefreshPositions();
        ApplyOptions();
            _requestSave();
        }
    }

    public bool YtDlpInstalled { get; private set; }

    public string YtDlpStatusText => YtDlpInstalled
        ? Loc.F("L_YtInstalledFormat", YtDlpPath ?? string.Empty)
        : Loc.T("L_YtNotInstalled");

    public string? YtDlpPath { get; private set; }

    public async Task InitializeAsync()
    {
        RefreshBinaryState();
        await RefreshCacheSizeAsync().ConfigureAwait(true);
    }

    /// <summary>Pushes the user's YouTube preferences into the running service.</summary>
    private void ApplyOptions()
    {
        var current = _service.Options;
        _service.Options = new YouTubeOptions
        {
            MusicDirectory = current.MusicDirectory,
            DownloadDirectory = current.DownloadDirectory,
            CookiesFolder = string.IsNullOrWhiteSpace(CookiesFolderPath) ? null : CookiesFolderPath,
            ProxyUrl = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl,
            CacheEnabled = CacheEnabled,
            MaxConcurrentDownloads = current.MaxConcurrentDownloads,
            DownloadTimeout = current.DownloadTimeout,
            SearchTimeout = current.SearchTimeout,
            Mirrors = current.Mirrors
        };
    }

    public IReadOnlyList<YouTubeTrackState> ToStates() =>
        Downloaded.Select(track => track.State).ToList();

    public void RefreshBinaryState()
    {
        YtDlpPath = _service.InstalledYtDlp;
        YtDlpInstalled = YtDlpPath is not null;
        OnPropertyChanged(nameof(YtDlpInstalled));
        OnPropertyChanged(nameof(YtDlpStatusText));
        OnPropertyChanged(nameof(YtDlpPath));
    }


    public async Task RefreshCacheSizeAsync()
    {
        try
        {
            _cacheBytes = await _service.GetCacheSizeAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception)
        {
            AppLogger.Error("youtube", "Could not measure the YouTube cache.", exception);
            return;
        }

        CacheSizeText = Loc.F("L_YtCacheUsedFormat",
            ByteSizeFormatter.Format(_cacheBytes, Loc.T("L_YtKb"), Loc.T("L_YtMb"), Loc.T("L_YtGb")));
    }

    /// <summary>Deletes the cached files and forgets the rows; the local playlist is untouched.</summary>
    public async Task ClearCacheAsync()
    {
        try
        {
            await _service.ClearCacheAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLogger.Error("youtube", "Could not clear the YouTube cache.", exception);
            _userMessage(Loc.F("L_YtErrorDetailsFormat", exception.Message));
            return;
        }

        Downloaded.Clear();
        RefreshPositions();
        Results.Clear();
        _requestSave();
        await RefreshCacheSizeAsync().ConfigureAwait(true);
        StatusText = Loc.T("L_YtCacheCleared");
        RefreshCommands();
        OnPropertyChanged(nameof(HasDownloaded));
        OnPropertyChanged(nameof(HasResults));
    }

    private void RemoveCached(YouTubeTrackViewModel? track)
    {
        if (track is null) { return; }
        try
        {
            if (!string.IsNullOrEmpty(track.CachedPath) && File.Exists(track.CachedPath)) { File.Delete(track.CachedPath); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning("youtube", $"Could not delete {track.CachedPath}: {exception.Message}");
        }

        Downloaded.Remove(track);
        RefreshPositions();
        _requestSave();
        _ = RefreshCacheSizeAsync();
        RefreshCommands();
        OnPropertyChanged(nameof(HasDownloaded));
    }

    private bool CanMove(YouTubeTrackViewModel? track, int offset)
    {
        if (track is null) { return false; }
        var index = Downloaded.IndexOf(track);
        var target = index + offset;
        return index >= 0 && target >= 0 && target < Downloaded.Count;
    }

    /// <summary>
    /// Moves a row by one position. The collection raises Move, so the same instance stays in the
    /// list and a playing track is never restarted.
    /// </summary>
    private void MoveTrack(YouTubeTrackViewModel? track, int offset)
    {
        if (track is null) { return; }
        var index = Downloaded.IndexOf(track);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Downloaded.Count) { return; }
        Downloaded.Move(index, target);
        AfterOrderChanged();
    }

    /// <summary>Drops a dragged row at a target position; unlike the arrows it may travel far.</summary>
    public bool MoveTrackTo(YouTubeTrackViewModel? track, int targetIndex)
    {
        if (track is null) { return false; }
        var index = Downloaded.IndexOf(track);
        if (index < 0 || targetIndex < 0 || targetIndex >= Downloaded.Count || index == targetIndex) { return false; }
        Downloaded.Move(index, targetIndex);
        AfterOrderChanged();
        return true;
    }

    /// <summary>Shows where a drop would land; null clears every indicator.</summary>
    public void SetDropTarget(YouTubeTrackViewModel? target, bool above)
    {
        foreach (var track in Downloaded)
        {
            var isTarget = target is not null && ReferenceEquals(track, target);
            track.IsDropTarget = isTarget;
            track.IsDropTargetAbove = isTarget && above;
        }
    }

    private void AfterOrderChanged()
    {
        RefreshPositions();
        RefreshCommands();
        _requestSave();
    }

    private void RefreshPositions()
    {
        for (var index = 0; index < Downloaded.Count; index++) { Downloaded[index].SetPosition(index, Downloaded.Count); }
        DownloadCommand.NotifyCanExecuteChanged();
        MoveTrackUpCommand.NotifyCanExecuteChanged();
        MoveTrackDownCommand.NotifyCanExecuteChanged();
    }

    public void RefreshLocalization()
    {
        StatusText = HasResults ? Loc.F("L_YtFoundFormat", Results.Count) : string.Empty;
        foreach (var track in Downloaded) { track.RefreshLocalization(); }
        _ = RefreshCacheSizeAsync();
        OnPropertyChanged(nameof(YtDlpStatusText));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        SearchCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        InstallYtDlpCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        ClearCacheCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}