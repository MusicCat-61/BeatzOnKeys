using System.Net.Http;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
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
/// <summary>What the yt-dlp card offers: nothing to do, a download, a repair or an update.</summary>
public enum YtDlpState
{
    NotInstalled,
    Broken,
    UpToDate,
    UpdateAvailable
}
public sealed partial class YouTubeViewModel : ObservableObject
{
    private readonly YouTubeTrackService _service;
    private readonly Action _requestSave;
    private readonly Action<string> _userMessage;
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>
    /// The background cache run of the current search. A new search cancels it: the old run is
    /// still downloading rows that no longer exist, and the two download slots it holds are
    /// exactly the ones the new rows are waiting for.
    /// </summary>
    private CancellationTokenSource? _cacheLoop;
    private string _searchQuery = string.Empty;
    private string _statusText;
    private bool _cacheMeasured;
    private long _cacheBytes;
    private bool _isSearching;
    private bool _isInstalling;
    private bool _cacheEnabled;
    private string _proxyUrl = string.Empty;
    private string _cookiesFolderPath = string.Empty;
    private double _installProgress;
    private bool _disposed;
    private double _queryProgress;
    private bool _isQueryDownloading;
    private bool _isSearchTab = true;
    /// <summary>Set when the user cleared the field and its results, so the empty list can explain itself.</summary>
    private bool _resultsClearedByUser;
    /// <summary>True once a search has run, so "nothing found" only describes a real search.</summary>
    private bool _hasSearched;

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
        Downloaded.CollectionChanged += OnDownloadedChanged;
        // One view, filtered in place: rebuilding a list on every change made the ListBox drop its
        // selection, and the selection is what "Старт" plays.
        FilteredDownloaded = new ListCollectionView(Downloaded)
        {
            Filter = row => row is YouTubeTrackViewModel track && MatchesFilter(track)
        };

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
            track => { if (track is not null) { PlayRequested?.Invoke(track); } },
            track => track is { CanPlay: true });
        RemoveCachedCommand = new RelayCommand<YouTubeTrackViewModel>(RemoveCached, track => track is not null);
        // Two different caches live in one folder: previews made by the search, and the tracks the
        // user chose to keep. Each button clears only its own share, so neither surprises the other.
        ClearDownloadedCommand = new RelayCommand(() => _ = ClearDownloadedAsync(),
            () => !IsBusy && Downloaded.Count > 0);
        RefreshCacheCommand = new RelayCommand(() => _ = RefreshCacheSizeAsync());
        ClearSearchCacheCommand = new RelayCommand(() => _ = ClearSearchCacheAsync(),
            () => !IsBusy && _searchCacheBytes > 0);
        OpenMusicFolderCommand = new RelayCommand(() => OpenMusicFolderRequested?.Invoke());
        // Exactly the playlist's management model: arrows for one step, drag for any distance.
        MoveTrackUpCommand = new RelayCommand<YouTubeTrackViewModel>(
            track => MoveTrack(track, -1), track => CanMove(track, -1));
        MoveTrackDownCommand = new RelayCommand<YouTubeTrackViewModel>(
            track => MoveTrack(track, 1), track => CanMove(track, 1));
        // The cross in a field clears it and nothing else: no confirmation, no side effect.
        ClearFilterCommand = new RelayCommand(() => FilterText = string.Empty, () => HasFilter);
        // The cross empties the field and the results it produced. A running cache pass is stopped
        // with them, or ten invisible downloads would keep occupying the slots. A preview that is
        // already playing is deliberately left alone: it is a sound the user asked for, and it
        // stops by itself at the end.
        ClearQueryCommand = new RelayCommand(ClearQueryAndResults, () => SearchQuery.Length > 0 || Results.Count > 0);

        RefreshPositions();
        ApplyOptions();
        RefreshBinaryState();
    }

    /// <summary>
    /// Raised when the user asks to listen to a saved track; the window plays it. The row travels
    /// with the request, so the player can use its stable id: the page list is the queue, and an
    /// id invented on every click would make the same row look like a different track.
    /// </summary>
    public event Action<YouTubeTrackViewModel>? PlayRequested;

    /// <summary>
    /// A search preview is not in any list, so it has no row: it is played by path and never
    /// becomes part of the page queue.
    /// </summary>
    public event Action<string, string>? PreviewRequested;

    /// <summary>Raised when the downloaded list gains, loses or reorders a row.</summary>
    public event Action? QueueChanged;

    /// <summary>
    /// Asks the user to confirm a destructive step. Both clear buttons go through it, so neither
    /// deletes silently; the window owns the dialog, the view model only asks the question.
    /// </summary>
    public Func<string, string, bool>? ConfirmRequested { get; set; }

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

    /// <summary>
    /// The filter over the downloaded list. It only narrows what is shown: the counters, the cache
    /// size and both clear buttons keep counting the whole list, because those are facts about the
    /// collection and not about the view.
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            var clean = TextInput.Sanitize(value);
            if (!SetProperty(ref _filterText, clean)) { return; }
            FilteredDownloaded.Refresh();
            OnPropertyChanged(nameof(FilterClearVisible));
            OnPropertyChanged(nameof(HasFilter));
            OnPropertyChanged(nameof(FilteredEmptyVisibility));
            // The cross is bound to a command whose CanExecute depends on this text. Without this
            // call nothing tells WPF to ask again, so the button stayed disabled and a click on it
            // did nothing at all.
            ClearFilterCommand.NotifyCanExecuteChanged();
        }
    }

    private string _filterText = string.Empty;

    public bool HasFilter => !string.IsNullOrWhiteSpace(_filterText);

    public Visibility FilterClearVisible => HasFilter ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The rows the list actually shows. A view instead of a fresh list: a new list on every
    /// change would make the ListBox rebuild its items and drop the selection, which silently
    /// deselected the track that was playing.
    /// </summary>
    public ListCollectionView FilteredDownloaded { get; }

    /// <summary>True when the filter hid the whole list; the hint differs from an empty list.</summary>
    public bool ShowFilteredEmpty => HasFilter && Downloaded.Count > 0 && !Downloaded.Any(MatchesFilter);

    public Visibility FilteredEmptyVisibility => ShowFilteredEmpty ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Title-only substring match, ignoring case: that is what "поиск по названию" means.</summary>
    private bool MatchesFilter(YouTubeTrackViewModel row) =>
        !HasFilter || row.Title.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase);

    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand<YouTubeSearchItemViewModel> DownloadCommand { get; }
    public AsyncRelayCommand InstallYtDlpCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> PlayCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> RemoveCachedCommand { get; }
    public RelayCommand ClearDownloadedCommand { get; }
    public RelayCommand RefreshCacheCommand { get; }
    public RelayCommand ClearSearchCacheCommand { get; }
    public RelayCommand OpenMusicFolderCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> MoveTrackUpCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> MoveTrackDownCommand { get; }
    public RelayCommand ClearFilterCommand { get; }
    public RelayCommand ClearQueryCommand { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            // One line, 200 characters: the same rule every field follows.
            if (!SetProperty(ref _searchQuery, TextInput.Sanitize(value))) { return; }
            SearchCommand.NotifyCanExecuteChanged();
            DownloadFromQueryCommand.NotifyCanExecuteChanged();
            ClearQueryCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSearch));
            OnPropertyChanged(nameof(QueryIsLink));
            OnPropertyChanged(nameof(QueryClearVisible));
        }
    }

    /// <summary>The cross shows only while there is something to clear.</summary>
    public Visibility QueryClearVisible => _searchQuery.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool CanSearch => !IsBusy && !string.IsNullOrWhiteSpace(SearchQuery);

    /// <summary>
    /// A pasted link turns the single button into "Скачать": searching for a URL is never what the
    /// user meant, and two buttons side by side only made them choose every time.
    /// </summary>
    public bool QueryIsLink => YouTubeUrls.LooksLikeLink(SearchQuery);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>"Занято на диске: 14.3 МБ" — the unit follows the measured size.</summary>
    public string CacheSizeText
    {
        // An unmeasured folder is not an empty one, and a blank line here used to read as a broken
        // card whenever a measurement failed. The dash says "not known yet" until a real figure
        // arrives, exactly as the TikTok card does.
        get => _cacheMeasured
            ? Loc.F("L_YtCacheUsedFormat",
                ByteSizeFormatter.Format(KeptCacheBytes, Loc.T("L_YtKb"), Loc.T("L_YtMb"), Loc.T("L_YtGb")))
            : Loc.T("L_YtCacheUnknown");
    }

    public long CacheBytes => _cacheBytes;

    /// <summary>
    /// The real cache folder, not a hardcoded one: the root follows the storage mode now, so a
    /// fixed "%LOCALAPPDATA%" hint would lie as soon as the data moved anywhere else.
    /// </summary>
    public string CacheHintText => Loc.T("L_YtCacheHintShort");

    /// <summary>The path is a tooltip detail: three lines of a system path are noise in a card.</summary>
    public string CachePathText => Loc.F("L_YtCacheHint", AppPaths.MusicDirectory);

    /// <summary>Space the previews take; reported on its own line.</summary>
    public long SearchCacheBytes => _searchCacheBytes;

    /// <summary>Space the kept tracks take; the previews are reported separately.</summary>
    public long KeptCacheBytes => Math.Max(0, _cacheBytes - _searchCacheBytes);

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

    /// <summary>Progress of a link that is being downloaded straight from the search field.</summary>
    public double QueryProgress
    {
        get => _queryProgress;
        private set
        {
            if (!SetProperty(ref _queryProgress, value)) { return; }
            OnPropertyChanged(nameof(QueryProgressText));
        }
    }

    public string QueryProgressText => Loc.F("L_YtQueryProgressFormat", Math.Round(QueryProgress * 100));

    public bool IsQueryDownloading
    {
        get => _isQueryDownloading;
        private set
        {
            if (!SetProperty(ref _isQueryDownloading, value)) { return; }
            OnPropertyChanged(nameof(ShowQueryActivity));
            OnPropertyChanged(nameof(IsBusy));
            RefreshCommands();
        }
    }

    /// <summary>True while the field is doing something the user should see: searching or fetching.</summary>
    public bool ShowQueryActivity => IsSearching || IsQueryDownloading;

    /// <summary>The cancel button exists only while there is something to cancel.</summary>
    public Visibility CancelVisible => IsSearching || IsDownloading || IsQueryDownloading || _cacheLoop is not null
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsBusy => IsSearching || IsInstalling || IsDownloading || IsQueryDownloading;

    public bool IsDownloading => Downloaded.Any(track => track.IsDownloading);

    private void NotifyDownloadingChanged() => OnPropertyChanged(nameof(CancelVisible));

    public bool HasResults => Results.Count > 0;
    public bool HasDownloaded => Downloaded.Count > 0;

    /// <summary>The number inside the badge next to "Found", in the reader's own digits.</summary>
    public string ResultsCountNumber => Results.Count.ToString(System.Globalization.CultureInfo.CurrentUICulture);

    /// <summary>The badge belongs to a list that exists; an empty search shows no counter at all.</summary>
    public Visibility ResultsBadgeVisible => HasResults ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// True when the list is empty and the cross put it there. An emptied list needs a sentence of
    /// its own: "nothing was found" would blame the query for a deliberate clear.
    /// </summary>
    public bool ShowResultsClearedHint => !IsSearching && Results.Count == 0 && _resultsClearedByUser;

    /// <summary>
    /// The list is empty after a search that really returned nothing. The flag exists because an
    /// empty list before the first search is not a failed search, and saying so would be a lie
    /// on a freshly opened page.
    /// </summary>
    public bool ShowEmptyResults => !IsSearching && Results.Count == 0 && _hasSearched && !_resultsClearedByUser;

    /// <summary>Two different sentences, because a cleared list is not a failed search.</summary>
    public Visibility ResultsClearedHintVisible => ShowResultsClearedHint ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyResultsVisible => ShowEmptyResults ? Visibility.Visible : Visibility.Collapsed;

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

    public string? YtDlpPath { get; private set; }

    /// <summary>What the card says in one line: the version, a missing binary, or a damaged one.</summary>
    public string YtDlpStatusText => _ytDlpState switch
    {
        YtDlpState.Broken => Loc.T("L_YtBroken"),
        YtDlpState.UpToDate or YtDlpState.UpdateAvailable =>
            string.IsNullOrWhiteSpace(_ytDlpVersion)
                ? Loc.T("L_YtInstalled")
                : Loc.F("L_YtInstalledVersionFormat", _ytDlpVersion!),
        _ => Loc.T("L_YtNotInstalled")
    };

    /// <summary>
    /// The single action button. Its label follows the state, and the button disappears when there
    /// is nothing to do: an up-to-date binary needs no button at all.
    /// </summary>
    public string YtDlpActionText => _ytDlpState switch
    {
        YtDlpState.Broken => Loc.T("L_YtRepair"),
        YtDlpState.UpdateAvailable => Loc.T("L_YtUpdate"),
        _ => Loc.T("L_YtDownload")
    };

    public bool YtDlpActionVisible => _ytDlpState != YtDlpState.UpToDate;

    public YtDlpState YtDlpState => _ytDlpState;

    /// <summary>The installed version, shown next to the state; null until it is read.</summary>
    public string? YtDlpVersion => _ytDlpVersion;

    private YtDlpState _ytDlpState = YtDlpState.NotInstalled;
    private string? _ytDlpVersion;

    /// <summary>
    /// Asks GitHub for the newest yt-dlp release. Replaceable so tests and the smoke never touch
    /// the network; a null answer means "unknown", which hides the update button.
    /// </summary>
    public static Func<CancellationToken, Task<string?>> LatestYtDlpReleaseFactory { get; set; } = DefaultLatestRelease;

    private static readonly HttpClient ReleaseClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static Task<string?> DefaultLatestRelease(CancellationToken cancellationToken) =>
        YtDlpBinaryManager.FetchLatestReleaseAsync(ReleaseClient, cancellationToken);

    public async Task InitializeAsync()
    {
        RefreshBinaryState();
        await RefreshCacheSizeAsync().ConfigureAwait(true);
    }

    /// <summary>Pushes the user's YouTube preferences into the running service.</summary>
    private void ApplyOptions()
    {
        // `with` instead of rebuilding the record by hand: a field added to YouTubeOptions
        // later would be silently reset to its default by a hand-written literal.
        _service.Options = _service.Options with
        {
            MusicDirectory = AppPaths.MusicDirectory,
            CookiesFolder = string.IsNullOrWhiteSpace(CookiesFolderPath) ? null : CookiesFolderPath,
            ProxyUrl = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl,
            CacheEnabled = CacheEnabled
        };
    }

    public IReadOnlyList<YouTubeTrackState> ToStates() =>
        Downloaded.Select(track => track.State).ToList();

    /// <summary>
    /// Replaces the saved list with the one library.json holds. Used once, after the library is
    /// read; the rows are rebuilt so a row's view state cannot outlive its track.
    /// </summary>
    internal void ReplaceTracks(IEnumerable<YouTubeTrackState> tracks)
    {
        Downloaded.Clear();
        foreach (var state in tracks) { Downloaded.Add(new YouTubeTrackViewModel(state)); }
        RefreshPositions();
        OnPropertyChanged(nameof(HasDownloaded));
        OnPropertyChanged(nameof(TrackCount));
        OnPropertyChanged(nameof(TrackCountNumber));
    }

    public void RefreshBinaryState()
    {
        YtDlpPath = _service.InstalledYtDlp;
        YtDlpInstalled = YtDlpPath is not null;
        // A file that sits in the bin folder but is not a usable executable is a repair, not a
        // download: the user had it before and expects it to still work.
        _ytDlpState = YtDlpInstalled
            ? YtDlpState.UpToDate
            : _service.YtDlpLooksBroken ? YtDlpState.Broken : YtDlpState.NotInstalled;
        NotifyBinaryState();
    }

    /// <summary>
    /// Reads the local version and asks GitHub for the newest one. The button only offers an
    /// update when a newer release really exists.
    /// </summary>
    public async Task RefreshBinaryVersionAsync(CancellationToken cancellationToken = default)
    {
        if (!YtDlpInstalled)
        {
            NotifyBinaryState();
            return;
        }

        try
        {
            _ytDlpVersion = await _service.ReadYtDlpVersionAsync(cancellationToken).ConfigureAwait(true);
            NotifyBinaryState();
            var latest = await LatestYtDlpReleaseFactory(cancellationToken).ConfigureAwait(true);
            _ytDlpState = YtDlpBinaryManager.IsNewerRelease(latest, _ytDlpVersion)
                ? YtDlpState.UpdateAvailable
                : YtDlpState.UpToDate;
            NotifyBinaryState();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception)
        {
            AppLogger.Warning("youtube", $"Could not compare the yt-dlp versions: {exception.Message}");
            _ytDlpState = YtDlpState.UpToDate;
            NotifyBinaryState();
        }
    }

    private void NotifyBinaryState()
    {
        OnPropertyChanged(nameof(YtDlpInstalled));
        OnPropertyChanged(nameof(YtDlpStatusText));
        OnPropertyChanged(nameof(YtDlpPath));
        OnPropertyChanged(nameof(YtDlpState));
        OnPropertyChanged(nameof(YtDlpVersion));
        OnPropertyChanged(nameof(YtDlpActionText));
        OnPropertyChanged(nameof(YtDlpActionVisible));
    }


    public async Task RefreshCacheSizeAsync()
    {
        // The preview share is measured first: the kept-tracks line is the difference, so a
        // stale preview number would make both lines wrong.
        await MeasureSearchCacheAsync().ConfigureAwait(true);
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

        // "Занято треками" is the space the kept tracks take, not the whole folder: the previews
        // have their own line right below, and counting them twice made both lines show the
        // same number.
        _cacheMeasured = true;
        OnPropertyChanged(nameof(CacheSizeText));
        // Rows saved before the duration was stored have none; the file itself knows it, so the
        // dash on the downloaded row goes away instead of staying forever.
        foreach (var track in Downloaded) { track.ReadDurationFromFile(); }
        // The preview line was measured a few lines up, so it is published here instead of being
        // measured a second time by every caller that wants both numbers.
        OnPropertyChanged(nameof(SearchCacheSizeText));
        ClearSearchCacheCommand.NotifyCanExecuteChanged();
    }

    private long _searchCacheBytes;

    /// <summary>Space taken by previews that are not kept in the downloaded list.</summary>
    public string SearchCacheSizeText => Loc.F("L_YtSearchCacheLabel",
        ByteSizeFormatter.Format(_searchCacheBytes, Loc.T("L_YtKb"), Loc.T("L_YtMb"), Loc.T("L_YtGb")));

    /// <summary>
    /// A preview file is anything in the cache folder that no downloaded row points at. That is
    /// exactly what the search tab created and what its clear button should remove.
    /// </summary>
    private List<string> OrphanedCacheFiles()
    {
        var kept = Downloaded
            .Select(track => track.CachedPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _service.ListCachedFiles()
            .Where(path => !kept.Contains(Path.GetFullPath(path)))
            .ToList();
    }

    /// <summary>Measures the previews and publishes the result; used when only that line changed.</summary>
    private async Task RefreshSearchCacheSizeAsync()
    {
        if (!await MeasureSearchCacheAsync().ConfigureAwait(true)) { return; }
        OnPropertyChanged(nameof(SearchCacheSizeText));
        OnPropertyChanged(nameof(CacheHintText));
        ClearSearchCacheCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Recomputes the preview size without touching the kept-tracks line.</summary>
    private async Task<bool> MeasureSearchCacheAsync()
    {
        try
        {
            var files = OrphanedCacheFiles();
            _searchCacheBytes = await Task.Run(() => files.Sum(path =>
            {
                try { return new FileInfo(path).Length; } catch (IOException) { return 0L; }
            }), _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception)
        {
            AppLogger.Warning("youtube", $"Could not measure the preview cache: {exception.Message}");
            return false;
        }

        return true;
    }

    /// <summary>Removes only the preview files; kept tracks and their rows stay untouched.</summary>
    public async Task ClearSearchCacheAsync()
    {
        if (ConfirmRequested is { } confirm &&
            !confirm(Loc.T("L_YtClearSearchCacheConfirm"), Loc.T("L_YtClearSearchCacheTitle")))
        {
            return;
        }

        try
        {
            await _service.DeleteFilesAsync(OrphanedCacheFiles(), _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception)
        {
            AppLogger.Error("youtube", "Could not clear the preview cache.", exception);
            _userMessage(Loc.F("L_YtErrorDetailsFormat", exception.Message));
            return;
        }

        foreach (var item in Results) { item.CachedPath = null; }
        StatusText = Loc.T("L_YtSearchCacheCleared");
        await RefreshCacheSizeAsync().ConfigureAwait(true);
        await RefreshSearchCacheSizeAsync().ConfigureAwait(true);
        RefreshCommands();
    }
    /// <summary>
    /// Removes the kept tracks and their rows; the previews and the local playlist stay. The whole
    /// folder is not wiped here: each button owns its own share, so neither surprises the other.
    /// </summary>
    public async Task ClearDownloadedAsync()
    {
        if (ConfirmRequested is { } confirm &&
            !confirm(Loc.T("L_YtClearDownloadedConfirm"), Loc.T("L_YtClearDownloadedTitle")))
        {
            return;
        }

        var files = Downloaded
            .Select(track => track.CachedPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToList();
        try
        {
            await _service.DeleteFilesAsync(files, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception)
        {
            AppLogger.Error("youtube", "Could not clear the downloaded tracks.", exception);
            _userMessage(Loc.F("L_YtErrorDetailsFormat", exception.Message));
            return;
        }

        Downloaded.Clear();
        SelectedTrack = null;
        NotifyCountChanged();
        _requestSave();
        RefreshPositions();
        // A search row can point at a file that was just removed; the row must stop offering to
        // play something that is no longer there.
        foreach (var item in Results)
        {
            if (item.CachedPath is { } path && !File.Exists(path)) { item.CachedPath = null; }
        }

        await RefreshCacheSizeAsync().ConfigureAwait(true);
        await RefreshSearchCacheSizeAsync().ConfigureAwait(true);
        StatusText = Loc.T("L_YtDownloadedCleared");
        RefreshCommands();
        OnPropertyChanged(nameof(HasDownloaded));
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
        if (ReferenceEquals(SelectedTrack, track)) { SelectedTrack = null; }
        NotifyCountChanged();
        RefreshPositions();
        _requestSave();
        RefreshPositions();
        _requestSave();
        _ = RefreshCacheSizeAsync();
        RefreshCommands();
        OnPropertyChanged(nameof(HasDownloaded));
    }

    private YouTubeTrackViewModel? _selected;
    private YouTubeSearchItemViewModel? _selectedResult;

    /// <summary>
    /// The row the transport controls act on. Clicking a row selects it, which is how the local
    /// playlist already works; there are no per-row play buttons anywhere.
    /// </summary>
    public YouTubeTrackViewModel? SelectedTrack
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) { return; }
            if (_selected is not null) { _selected.IsSelected = false; }
            _selected = value;
            if (_selected is not null) { _selected.IsSelected = true; }
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTrackTitle));
            OnPropertyChanged(nameof(HasSelection));
            RefreshCommands();
        }
    }

    public string SelectedTrackTitle => SelectedTrack?.Title ?? string.Empty;
    public bool HasSelection => SelectedTrack is not null;

    public int TrackCount => Downloaded.Count;
    public string TrackCountNumber => Downloaded.Count.ToString(System.Globalization.CultureInfo.CurrentUICulture);

    /// <summary>Picks a downloaded row as the current one, used by playback and by a download start.</summary>
    public void SelectTrack(YouTubeTrackViewModel? track)
    {
        if (track is null) { return; }
        SelectedTrack = track;
    }

    /// <summary>
    /// The counter follows the collection itself, so any add or remove updates the badge
    /// without the caller having to remember to raise anything.
    /// </summary>
    private void OnDownloadedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(nameof(TrackCount));
        OnPropertyChanged(nameof(TrackCountNumber));
        OnPropertyChanged(nameof(HasDownloaded));
        // The view follows the collection by itself; only the "nothing matched" hint needs a word.
        OnPropertyChanged(nameof(FilteredEmptyVisibility));
        QueueChanged?.Invoke();
    }

    /// <summary>Refreshes the badge next to the heading after any add or remove.</summary>
    public void NotifyCountChanged()
    {
        OnPropertyChanged(nameof(TrackCount));
        OnPropertyChanged(nameof(TrackCountNumber));
        OnPropertyChanged(nameof(HasDownloaded));
        RefreshCommands();
    }

    /// <summary>The search result the user is previewing; "Старт" plays it once downloaded.</summary>
    public YouTubeSearchItemViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (ReferenceEquals(_selectedResult, value)) { return; }
            _selectedResult = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedResultTitle));
            OnPropertyChanged(nameof(CanPreview));
        }
    }

    public string SelectedResultTitle => SelectedResult?.Title ?? string.Empty;
    public bool CanPreview => SelectedResult is { CanDownload: true };
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
        // The result count lives in the badge beside the list, so the sentence above it is not
        // rebuilt here: that line was the only thing bringing "Found: N" back after a language switch.
        foreach (var track in Downloaded) { track.RefreshLocalization(); }
        _ = RefreshCacheSizeAsync();
        // Every derived label is recomputed on a language switch, not just the status line.
        // The cache card is built from keys too: without these two the Russian sentence stayed
        // under an English checkbox, which is how this was reported.
        OnPropertyChanged(nameof(CacheHintText));
        OnPropertyChanged(nameof(CachePathText));
        NotifyBinaryState();
        foreach (var item in Results) { item.RefreshLocalization(); }
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        SearchCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        InstallYtDlpCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        ClearDownloadedCommand.NotifyCanExecuteChanged();
        ClearFilterCommand.NotifyCanExecuteChanged();
        ClearQueryCommand.NotifyCanExecuteChanged();
        // The cancel button depends on the same three flags as everything above, and it was the one
        // command nobody re-checked: it stayed hidden and disabled while a search was running.
        OnPropertyChanged(nameof(CancelVisible));
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
