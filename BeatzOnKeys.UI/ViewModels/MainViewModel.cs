using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Input;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.YouTube;
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.UI.Localization;
using BeatzOnKeys.UI.Theming;

namespace BeatzOnKeys.UI.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsService _settingsService;
    /// <summary>
    /// Beside settings.json on purpose: the two files belong to one installation, so pointing a
    /// test at its own settings file also gives it its own library.
    /// </summary>
    private readonly LibraryService _library;
    private bool _libraryLoaded;
    private string? _libraryMigratedVersion;
    private readonly PlaybackController _playbackController;
    private readonly IContinuousAudioPlayer _audioPlayer;
    private readonly TypingPlaybackService _typingPlayback;
    private readonly YouTubeTrackService _youTube;
    private readonly Dictionary<Guid, string> _friendlyNames = [];
    /// <summary>
    /// The search preview currently playing, if any. Only a preview suspends the idle pause: the
    /// user asked to hear that track properly. A downloaded track from the list is ordinary
    /// listening, so the pause keeps working there.
    /// </summary>
    private Guid? _previewTrackId;
    private bool _idlePauseSuppressed;
    private string _playingTrackText = string.Empty;
    private string _playingSourceText = string.Empty;
    private bool _isSettingsOpen;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _playbackTimer;
    private readonly Guid? _savedSelectedTrackId;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _fileImportTask = Task.CompletedTask;
    private TrackViewModel? _selectedTrack;
    private double _positionSeconds;
    private double _durationSeconds;
    private double _minVolume;
    private double _maxVolume;
    private double _idleTimeout;
    private bool _pauseOnIdle;
    private bool _trackEnded;
    private bool _autoAdvance;
    private bool _loop;
    private bool _punchResponseEnabled;
    private double _bassReactiveGain;
    private bool _stereoPulseEnabled;
    private bool _adaptiveVolumeEnabled;
    private bool _volumeExpanded = true;
    private bool _reactionExpanded;
    private bool _behaviorExpanded;
    private string _activeSection = SectionVolume;

    public const string SectionVolume = "volume";
    public const string SectionReaction = "reaction";
    public const string SectionBehavior = "behavior";
    private bool _isRunning;
    private bool _isPaused;
    private bool _changingPlayback;
    private bool _initializing = true;
    private int _pendingImports;
    private bool _selectionFromPlayback;
    private bool _closing;
    private bool _disposed;
    private string? _storageWarning;
    private string _statusText;
    private string _nowPlayingText = string.Empty;

    public MainViewModel(AppSettings settings, SettingsService settingsService,
        IKeyboardHook? keyboardHook = null, IContinuousAudioPlayer? audioPlayer = null,
        YouTubeTrackService? youTubeService = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        _settingsService = settingsService;
        _library = new LibraryService(
            LibraryService.LibraryFileFor(settingsService.FilePath),
            settingsService.FilePath);
        _savedSelectedTrackId = settings.SelectedTrackId;
        _minVolume = Math.Clamp(settings.MinVolume, 0, 0.5);
        _maxVolume = settings.MaxVolume;
        _idleTimeout = settings.IdleTimeout;
        _pauseOnIdle = settings.PauseOnIdle;
        _autoAdvance = settings.AutoAdvance;
        _loop = settings.Loop;
        _punchResponseEnabled = settings.PunchResponseEnabled;
        _bassReactiveGain = Math.Clamp(settings.BassReactiveGain, 0, 0.3);
        _stereoPulseEnabled = settings.StereoPulseEnabled;
        _adaptiveVolumeEnabled = settings.AdaptiveVolumeEnabled;
        _windowWidth = settings.WindowWidth;
        _windowHeight = settings.WindowHeight;
        _statusText = Loc.T("L_StatusRestoring");
        _playbackController = new PlaybackController { AutoAdvance = _autoAdvance, Loop = _loop };
        _audioPlayer = audioPlayer ?? new ContinuousAudioPlayer();
        _audioPlayer.MinVolume = _minVolume;
        _audioPlayer.MaxVolume = _maxVolume;
        _audioPlayer.IdleTimeout = _idleTimeout;
        _audioPlayer.PauseOnIdle = _pauseOnIdle;
        _audioPlayer.PunchResponseEnabled = _punchResponseEnabled;
        _audioPlayer.BassReactiveGain = _bassReactiveGain;
        _audioPlayer.StereoPulseEnabled = _stereoPulseEnabled;
        _audioPlayer.AdaptiveVolumeEnabled = _adaptiveVolumeEnabled;
        _typingPlayback = new TypingPlaybackService(keyboardHook ?? new WatcherKeyboardHook(), _playbackController, _audioPlayer);
        _playbackController.CurrentTrackChanged += OnPlaybackTrackChanged;
        _typingPlayback.PlaybackFailed += OnPlaybackFailed;

        foreach (var state in settings.Tracks)
        {
            try { Tracks.Add(new TrackViewModel(state)); }
            catch (Exception exception) { AppLogger.Error("settings", $"Ignored invalid track path '{state.SourcePath}'.", exception); }
        }

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += SaveTimerOnTick;
        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimerOnTick;
        AddFilesCommand = new RelayCommand(() => FilePickerRequested?.Invoke(), () => !_closing && !_initializing);
        StartCommand = new AsyncRelayCommand(StartPlaybackAsync, CanStartPlayback);
        StopCommand = new AsyncRelayCommand(StopPlaybackAsync, () => IsRunning && !_changingPlayback);
        TogglePauseCommand = new RelayCommand(TogglePause, () => IsRunning && !_changingPlayback);
        RemoveTrackCommand = new RelayCommand<TrackViewModel>(track =>
        {
            if (track is not null) { _ = RemoveTrackAsync(track); }
        }, track => track is not null && CanEditPlaylist);
        MoveTrackUpCommand = new RelayCommand<TrackViewModel>(track => MoveTrack(track, -1),
            track => CanEditPlaylist && CanMove(track, -1));
        MoveTrackDownCommand = new RelayCommand<TrackViewModel>(track => MoveTrack(track, 1),
            track => CanEditPlaylist && CanMove(track, 1));

        SetLanguageCommand = new RelayCommand<string>(code => Loc.SetLanguage(Loc.Parse(code)));
        ToggleThemeCommand = new RelayCommand(Theme.Toggle);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        ClearTrackFilterCommand = new RelayCommand(() => TrackFilter = string.Empty, () => HasTrackFilter);
        Tracks.CollectionChanged += OnTracksChanged;
        // Created once and filtered in place, so the playlist keeps its selection and its scroll
        // position while a filter is typed.
        FilteredTracks = new ListCollectionView(Tracks)
        {
            Filter = row => row is TrackViewModel track && MatchesTrackFilter(track)
        };
        // The YouTube section keeps its own list; only the audio player is shared.
        _youTube = youTubeService ?? CreateDefaultYouTubeService();
        YouTube = new YouTubeViewModel(_youTube, settings.YouTubeTracks, settings.YouTubeCacheEnabled,
            settings.CookiesFolderPath, settings.ProxyUrl, QueueSave,
            message => UserMessageRequested?.Invoke(message));
        YouTube.PlayRequested += OnYouTubePlayRequested;
        YouTube.PreviewRequested += OnYouTubePreviewRequested;
        YouTube.QueueChanged += OnPageQueueChanged;
        YouTube.PropertyChanged += OnYouTubePropertyChanged;
        YouTube.OpenMusicFolderRequested += OpenMusicFolder;
        // The row button doubles as pause: the section cannot reach the player, so it asks here.
        YouTube.PauseToggleRequested += TogglePause;
        // TikTok has no search, no cookie and no binary: just a field and a list of its own.
        TikTok = new TikTokViewModel(
            TikTokServiceFactory?.Invoke() ?? new TikTokAudioService(new YtDlpBinaryManager(), new YouTubeProcessRunner()),
            settings.TikTokTracks,
            QueueSave,
            message => UserMessageRequested?.Invoke(message));
        TikTok.PlayRequested += OnTikTokPlayRequested;
        TikTok.QueueChanged += OnPageQueueChanged;
        TikTok.PropertyChanged += OnTikTokPropertyChanged;
        TikTok.OpenMusicFolderRequested += OpenMusicFolder;
        Loc.LanguageChanged += OnLanguageChanged;
        Theme.Changed += OnThemeChanged;
        _playbackTimer.Start();
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = [];

    /// <summary>
    /// The filter over the local playlist. Like the other two, it narrows the view only: the
    /// counter, the drag order and every command still speak about the whole list.
    /// </summary>
    public string TrackFilter
    {
        get => _trackFilter;
        set
        {
            var clean = TextInput.Sanitize(value);
            if (!SetProperty(ref _trackFilter, clean)) { return; }
            ClearTrackFilterCommand.NotifyCanExecuteChanged();
            FilteredTracks.Refresh();
            OnPropertyChanged(nameof(HasTrackFilter));
            OnPropertyChanged(nameof(TrackFilterClearVisible));
            OnPropertyChanged(nameof(TrackFilteredEmptyVisibility));
        }
    }

    private string _trackFilter = string.Empty;

    public bool HasTrackFilter => !string.IsNullOrWhiteSpace(_trackFilter);

    public Visibility TrackFilterClearVisible => HasTrackFilter ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The rows the playlist shows. A view, not a fresh list: the list rebuilt itself on every
    /// change, which made the ListBox drop its selection and quietly lose the playing track.
    /// </summary>
    public ListCollectionView FilteredTracks { get; }

    /// <summary>Name-only substring match, ignoring case — the rule all three lists share.</summary>
    private bool MatchesTrackFilter(TrackViewModel track) =>
        !HasTrackFilter || track.FileName.Contains(_trackFilter, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>True when the filter hid everything; the hint says so instead of "нет треков".</summary>
    public bool ShowTrackFilteredEmpty =>
        HasTrackFilter && Tracks.Count > 0 && !Tracks.Any(MatchesTrackFilter);

    public Visibility TrackFilteredEmptyVisibility =>
        ShowTrackFilteredEmpty ? Visibility.Visible : Visibility.Collapsed;

    public RelayCommand ClearTrackFilterCommand { get; }

    /// <summary>
    /// The size the user last chose. Zero means "never set", so the window keeps its XAML size;
    /// this is deliberately not a preference the window offers, it is only remembered.
    /// </summary>
    public double WindowWidth => _windowWidth;

    /// <inheritdoc cref="WindowWidth"/>
    public double WindowHeight => _windowHeight;

    private double _windowWidth;
    private double _windowHeight;

    /// <summary>
    /// Remembers a size the user chose. Only a size that is not smaller in either direction is
    /// recorded: shrinking a window is usually a moment, not a preference, and the remembered size
    /// must not decay every time the window is made narrow for a minute. Fitting the window to the
    /// settings panel is not the user's word either, so the window suppresses that call itself.
    /// </summary>
    public void RememberWindowSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) { return; }
        width = Math.Round(width);
        height = Math.Round(height);
        if (width < _windowWidth || height < _windowHeight) { return; }
        if (width == _windowWidth && height == _windowHeight) { return; }
        _windowWidth = width;
        _windowHeight = height;
        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(WindowHeight));
        QueueSave();
    }

    /// <summary>The separate YouTube section: its own search, cache and settings.</summary>
    public YouTubeViewModel YouTube { get; }

    /// <summary>The TikTok section: a link in, a file out, and its own cache.</summary>
    public TikTokViewModel TikTok { get; }

    /// <summary>
    /// Replaceable so tests and the smoke never touch the network. A real download needs TikTok
    /// to answer, which is not something a test may depend on.
    /// </summary>
    public static Func<ITikTokAudioService>? TikTokServiceFactory { get; set; }

    /// <summary>
    /// Exactly one of the three pages is active. A single field instead of two flags, because two
    /// booleans can both be true at once and then the window has no defined content.
    /// </summary>
    private AppPage _page = AppPage.Home;

    private enum AppPage
    {
        Home,
        Playlist,
        YouTube,
        TikTok
    }

    /// <summary>
    /// The start screen: one place that says where the music comes from. It replaced the tab strip,
    /// because a row of tabs asks "which section" before the user has decided anything, and that
    /// question is the first decision of the day.
    /// </summary>
    public bool IsHomePage => _page == AppPage.Home;

    /// <summary>True while the YouTube page is shown.</summary>
    public bool IsYouTubePage => _page == AppPage.YouTube;

    /// <summary>True while the TikTok page is shown.</summary>
    public bool IsTikTokPage => _page == AppPage.TikTok;

    public bool IsPlaylistPage => _page == AppPage.Playlist;

    /// <summary>
    /// The way back to the start screen. Focus mode hides it: that screen keeps its own exit
    /// ("Leave focus mode") in the title bar, and two doors out of one room is a choice, not a
    /// kindness. On the hub itself there is nowhere to go back to, so it is hidden there too.
    /// </summary>
    public Visibility HomeButtonVisible =>
        _isFocusMode || _page == AppPage.Home ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The greeting is the only thing on the start screen that changes by itself, and it is chosen
    /// from the local clock: 5-11 morning, 12-17 afternoon, 18-22 evening, the rest is night.
    /// </summary>
    public string GreetingText => Loc.T(GreetingKey(DateTime.Now.TimeOfDay));

    /// <summary>Exposed for the test: the key for a given time of day, with no clock involved.</summary>
    public static string GreetingKey(TimeSpan timeOfDay)
    {
        var hour = timeOfDay.TotalHours;
        if (hour >= 5 && hour < 12) { return "L_GreetMorning"; }
        if (hour >= 12 && hour < 18) { return "L_GreetAfternoon"; }
        if (hour >= 18 && hour < 23) { return "L_GreetEvening"; }
        return "L_GreetNight";
    }

    /// <summary>
    /// The tile captions say how much music is waiting, not how much work was done: the first is a
    /// reason to press a tile, the second is a score.
    /// </summary>
    public string HomeTracksMeta => Loc.F("L_HomeTracksMeta", Tracks.Count);
    public string HomeYouTubeMeta => Loc.F("L_HomeCachedMeta", YouTube.Downloaded.Count);
    public string HomeTikTokMeta => Loc.F("L_HomeCachedMeta", TikTok.Tracks.Count);

    /// <summary>
    /// Focus mode hides everything that asks the user to choose something — tabs, caches, counters,
    /// advice plates, the track list and the settings panel — and leaves the transport and the
    /// playing track. It is a view state, not a setting: nothing is written to settings.json, and
    /// leaving the mode restores exactly what was there before.
    /// </summary>
    private bool _isFocusMode;

    public bool IsFocusMode
    {
        get => _isFocusMode;
        private set
        {
            if (!SetProperty(ref _isFocusMode, value)) { return; }
            OnPropertyChanged(nameof(TabStripVisible));
            OnPropertyChanged(nameof(FocusChromeVisible));
            OnPropertyChanged(nameof(FocusPanelVisible));
            OnPropertyChanged(nameof(FocusToggleTooltip));
            OnPropertyChanged(nameof(FocusToggleText));
            OnPropertyChanged(nameof(HomeButtonVisible));
            OnPropertyChanged(nameof(StartButtonVisible));
        // The idle line names the screen the user is on, so it follows a page change.
        if (!IsRunning) { StatusText = IdleStatusText; }
            ApplyFocusBehaviour();
            OnPropertyChanged(nameof(CurrentModeTitle));
            OnPropertyChanged(nameof(ModeTitleVisible));
        OnPropertyChanged(nameof(HomePageVisible));
            RefreshCommands();
        }
    }

    public Visibility TabStripVisible => _isFocusMode ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The footer's utility buttons step aside too: they are decisions as well.</summary>
    public Visibility FocusChromeVisible => _isFocusMode ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FocusPanelVisible => _isFocusMode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The start screen is a chooser, not a remote: Start appears once a source is open. Focus mode
    /// keeps it, because there it is the only control that matters.
    /// </summary>
    public Visibility StartButtonVisible =>
        _isFocusMode || _page != AppPage.Home ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The last mode the user was in, so focus mode started from the hub opens a source.</summary>
    private AppPage _lastModePage = AppPage.Playlist;

    /// <summary>The name of the mode on screen, next to the way home.</summary>
    public string CurrentModeTitle => Loc.T(_page switch
    {
        AppPage.Playlist => "L_HomeMyFiles",
        AppPage.YouTube => "L_HomeYouTube",
        AppPage.TikTok => "L_HomeTikTok",
        _ => "L_Home"
    });

    /// <summary>On the hub there is nothing to go back to, so the crumb is empty rather than "Home".</summary>
    public Visibility ModeTitleVisible =>
        _page == AppPage.Home || _isFocusMode ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The start screen itself, hidden while a mode or focus mode is on.</summary>
    public Visibility HomePageVisible =>
        _page == AppPage.Home && !_isFocusMode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The hub and the focus page show what is playing only while it really plays: the controller
    /// remembers its track after Stop, and a card for a silent app is a lie about sound.
    /// </summary>
    public Visibility NowPlayingCardVisible =>
        IsRunning && _playbackController?.CurrentTrack is not null ? Visibility.Visible : Visibility.Collapsed;
    public string FocusToggleTooltip => _isFocusMode ? Loc.T("L_FocusExit") : Loc.T("L_FocusEnter");

    /// <summary>The button says what pressing it will do, so the mode is never a guess.</summary>
    public string FocusToggleText => Loc.T(_isFocusMode ? "L_FocusExit" : "L_FocusEnter");

    public RelayCommand ToggleFocusModeCommand => _toggleFocusMode ??= new RelayCommand(ToggleFocusMode);

    private RelayCommand? _toggleFocusMode;

    private void ToggleFocusMode()
    {
        if (!_isFocusMode)
        {
            // The settings panel is a page of choices, and it floats above everything.
            IsSettingsOpen = false;
            // Focus started from the start screen opens the last source, because a focus screen
            // with no queue would be the one thing it must never be: a dead end.
            if (_page == AppPage.Home) { SetPage(_lastModePage); }
        }

        IsFocusMode = !_isFocusMode;
        AppLogger.Info("ui", $"Focus mode {(_isFocusMode ? "entered" : "left")}.");
    }

    /// <summary>
    /// While focus mode is on, the queue never runs dry: the next track plays, and the last one
    /// loops. Only the controller is touched, never the saved preferences, so leaving the mode
    /// gives the user back exactly the behaviour they chose.
    /// </summary>
    private void ApplyFocusBehaviour()
    {
        if (_playbackController is null) { return; }
        _playbackController.AutoAdvance = _isFocusMode || _autoAdvance;
        _playbackController.Loop = _isFocusMode || _loop;
        OnPropertyChanged(nameof(BehaviorSummary));
    }

    private void SetPage(AppPage page)
    {
        if (_page == page) { return; }
        _page = page;
        if (page != AppPage.Home) { _lastModePage = page; }
        // Entering a mode means leaving the settings panel: it floats above everything, and a mode
        // that opens behind a panel is not a mode the user asked for.
        if (page != AppPage.Home) { IsSettingsOpen = false; }
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsPlaylistPage));
        OnPropertyChanged(nameof(IsYouTubePage));
        OnPropertyChanged(nameof(IsTikTokPage));
        OnPropertyChanged(nameof(HomeButtonVisible));
        OnPropertyChanged(nameof(StartButtonVisible));
        OnPropertyChanged(nameof(SelectedTrackText));
        OnPropertyChanged(nameof(CurrentModeTitle));
        OnPropertyChanged(nameof(ModeTitleVisible));
        OnPropertyChanged(nameof(HomePageVisible));
        // The idle line names the screen the user is on, so it follows a page change.
        if (!IsRunning) { StatusText = IdleStatusText; }
        // The queue follows the page: "next" is the row below in the list the user is looking at.
        SyncPlaybackQueue();
        RefreshCommands();
    }

    /// <summary>
    /// The playback panel floats above whichever tab is active, so that tab keeps its content
    /// visible behind the panel instead of being replaced by it.
    /// </summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set
        {
            if (!SetProperty(ref _isSettingsOpen, value)) { return; }
            RefreshCommands();
        }
    }

    public RelayCommand ShowPlaylistCommand => _showPlaylist ??= new RelayCommand(() => SetPage(AppPage.Playlist));
    public RelayCommand ShowYouTubeCommand => _showYouTube ??= new RelayCommand(() => SetPage(AppPage.YouTube));
    public RelayCommand ShowTikTokCommand => _showTikTok ??= new RelayCommand(() => SetPage(AppPage.TikTok));
    public RelayCommand ShowSettingsCommand => _showSettings ??= new RelayCommand(ToggleSettings);

    /// <summary>The way back to the start screen, present in every mode.</summary>
    public RelayCommand ShowHomeCommand => _showHome ??= new RelayCommand(() => SetPage(AppPage.Home));
    public RelayCommand CloseSettingsCommand => _closeSettings ??= new RelayCommand(() => IsSettingsOpen = false);
    private RelayCommand? _showHome;
    private RelayCommand? _showPlaylist;
    private RelayCommand? _showYouTube;
    private RelayCommand? _showTikTok;
    private RelayCommand? _showSettings;
    private RelayCommand? _closeSettings;

    /// <summary>The same button opens and closes the panel, so it never traps the user.</summary>
    private void ToggleSettings()
    {
        // Opening twice must not close the panel: the tab is a way in, and the panel has its own
        // close button, so a stray second click cannot hide it by surprise.
        if (IsSettingsOpen) { return; }
        IsSettingsOpen = true;
        FitRequested?.Invoke();
    }

    /// <summary>Raised when the panel opens, so the window can size itself to its content.</summary>
    public event Action? FitRequested;
    public RelayCommand AddFilesCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand<TrackViewModel> RemoveTrackCommand { get; }
    public RelayCommand<TrackViewModel> MoveTrackUpCommand { get; }
    public RelayCommand<TrackViewModel> MoveTrackDownCommand { get; }

    public RelayCommand<string> SetLanguageCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public event Action? FilePickerRequested;
    public event Action<string>? UserMessageRequested;
    public event Action? StateChanged;

    public TrackViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (!SetProperty(ref _selectedTrack, value)) { return; }
            OnPropertyChanged(nameof(SelectedTrackText));
            if (!_selectionFromPlayback && !_closing)
            {
                if (value is { IsAvailable: true })
                {
                    try { _typingPlayback.SelectTrack(value.Id); }
                    catch (Exception exception)
                    {
                        value.SetUnavailable(exception.Message, exception is FileNotFoundException or DirectoryNotFoundException);
                        _playbackController.RemoveTrack(value.Id);
                        _typingPlayback.Deselect();
                        AppLogger.Error("audio", $"Could not select '{value.SourcePath}'.", exception);
                        UserMessageRequested?.Invoke(Loc.F("L_LoadFailedFormat", value.FileName, exception.Message));
                    }
                }
                else { _typingPlayback.Deselect(); }
            }
            _trackEnded = false;
            RefreshPlaybackProgress();
            QueueSave();
            RefreshCommands();
        }
    }

    /// <summary>
    /// The player card follows the active page: it names the local file on "Локально" and the
    /// cached YouTube track on "YouTube", because those are what "Старт" would play.
    /// </summary>
    public string SelectedTrackText => _page switch
    {
        AppPage.YouTube => YouTube.SelectedTrack?.Title ?? Loc.T("L_NoTrackSelected"),
        AppPage.TikTok => TikTok.SelectedTrack?.Title ?? Loc.T("L_NoTrackSelected"),
        _ => SelectedTrack?.FileName ?? Loc.T("L_NoTrackSelected")
    };
    public double MaxVolume
    {
        get => _maxVolume;
        set
        {
            var normalized = Math.Clamp(double.IsFinite(value) ? value : 1, 0, 1);
            if (!SetProperty(ref _maxVolume, normalized)) { return; }
            _audioPlayer.MaxVolume = normalized;
            OnPropertyChanged(nameof(MaxVolumePercentValue));
            OnPropertyChanged(nameof(MaxVolumePercent));
            OnPropertyChanged(nameof(VolumeSummary));
            QueueSave();
        }
    }
    public double MaxVolumePercentValue { get => MaxVolume * 100; set => MaxVolume = value / 100; }
    public string MaxVolumePercent => $"{Math.Round(MaxVolume * 100):0}%";
    public double MinVolume
    {
        get => _minVolume;
        set
        {
            var normalized = Math.Clamp(double.IsFinite(value) ? value : 0.2, 0, 0.5);
            if (!SetProperty(ref _minVolume, normalized)) { return; }
            // Keep configured limits independent; the player caps the effective minimum at MaxVolume.
            _audioPlayer.MinVolume = normalized;
            OnPropertyChanged(nameof(MinVolumePercentValue));
            OnPropertyChanged(nameof(MinVolumePercent));
            OnPropertyChanged(nameof(VolumeSummary));
            QueueSave();
        }
    }
    public double MinVolumePercentValue { get => MinVolume * 100; set => MinVolume = value / 100; }
    public string MinVolumePercent => $"{Math.Round(MinVolume * 100):0}%";
    public double IdleTimeout
    {
        get => _idleTimeout;
        set
        {
            var normalized = Math.Clamp(double.IsFinite(value) ? value : 3, 1, 10);
            if (!SetProperty(ref _idleTimeout, normalized)) { return; }
            _audioPlayer.IdleTimeout = normalized;
            OnPropertyChanged(nameof(IdleTimeoutText));
            OnPropertyChanged(nameof(VolumeSummary));
            QueueSave();
        }
    }
    public string IdleTimeoutText => Loc.F("L_IdleTimeoutSecondsFormat", IdleTimeout.ToString("0.#", CultureInfo.CurrentUICulture));
    public bool PauseOnIdle
    {
        get => _pauseOnIdle;
        set { if (SetProperty(ref _pauseOnIdle, value)) { SyncIdlePause(); QueueSave(); } }
    }
    /// <summary>
    /// The idle pause exists for the typing background. A YouTube track — a search preview or a
    /// saved one — is the user asking to hear it properly, so the pause stays out of the way.
    /// Called from the progress tick, so an auto-advance back to a local track restores the setting
    /// by itself without a second code path to forget.
    /// </summary>
    private void SyncIdlePauseFor(PlaybackTrack? playing)
    {
        var suppressed = playing is not null && playing.Id == _previewTrackId;
        if (suppressed == _idlePauseSuppressed) { return; }
        _idlePauseSuppressed = suppressed;
        SyncIdlePause();
    }

    /// <summary>The setting is kept as the user left it; only the effective value is suspended.</summary>
    private void SyncIdlePause()
    {
        var effective = _pauseOnIdle && !_idlePauseSuppressed;
        if (_audioPlayer.PauseOnIdle != effective) { _audioPlayer.PauseOnIdle = effective; }
    }

    public bool AutoAdvance
    {
        get => _autoAdvance;
        set { if (SetProperty(ref _autoAdvance, value)) { _playbackController.AutoAdvance = value; OnPropertyChanged(nameof(BehaviorSummary)); QueueSave(); } }
    }
    public bool Loop
    {
        get => _loop;
        set { if (SetProperty(ref _loop, value)) { _playbackController.Loop = value; OnPropertyChanged(nameof(BehaviorSummary)); QueueSave(); } }
    }

    /// <summary>
    /// Sections start expanded, so every control stays visible on first launch and nothing hides
    /// behind a click. Collapsing is a per-session convenience and is never persisted.
    /// </summary>
    public bool IsVolumeExpanded
    {
        get => _volumeExpanded;
        private set => SetProperty(ref _volumeExpanded, value);
    }

    public bool IsReactionExpanded
    {
        get => _reactionExpanded;
        private set => SetProperty(ref _reactionExpanded, value);
    }

    public bool IsBehaviorExpanded
    {
        get => _behaviorExpanded;
        private set => SetProperty(ref _behaviorExpanded, value);
    }

    /// <summary>The chip of the section the user last opened, so it stays highlighted.</summary>
    public string ActiveSection
    {
        get => _activeSection;
        private set
        {
            if (!SetProperty(ref _activeSection, value)) { return; }
            OnPropertyChanged(nameof(IsVolumeChipActive));
            OnPropertyChanged(nameof(IsReactionChipActive));
            OnPropertyChanged(nameof(IsBehaviorChipActive));
        }
    }

    public bool IsVolumeChipActive => ActiveSection == SectionVolume;
    public bool IsReactionChipActive => ActiveSection == SectionReaction;
    public bool IsBehaviorChipActive => ActiveSection == SectionBehavior;

    /// <summary>Live one-line summary that keeps a collapsed section readable.</summary>
    public string VolumeSummary => Loc.F("L_VolumeSummaryFormat", MaxVolumePercent, MinVolumePercent, IdleTimeoutText);
    public string ReactionSummary => Loc.F("L_ReactionSummaryFormat", BassReactivePercent, StateWord(StereoPulseEnabled));
    public string BehaviorSummary => Loc.F("L_BehaviorSummaryFormat", StateWord(AutoAdvance), StateWord(Loop));

    private static string StateWord(bool value) => Loc.T(value ? "L_StateOn" : "L_StateOff");

    /// <summary>
    /// Opens one section and closes the others: a chip means "take me there", and a single open
    /// section keeps the panel free of scrolling. The headers still toggle freely.
    /// </summary>
    public void ShowSection(string section)
    {
        IsVolumeExpanded = section == SectionVolume;
        IsReactionExpanded = section == SectionReaction;
        IsBehaviorExpanded = section == SectionBehavior;
        ActiveSection = section;
    }

    public void ToggleSection(string section)
    {
        switch (section)
        {
            case SectionReaction:
                IsReactionExpanded = !IsReactionExpanded;
                break;
            case SectionBehavior:
                IsBehaviorExpanded = !IsBehaviorExpanded;
                break;
            default:
                IsVolumeExpanded = !IsVolumeExpanded;
                break;
        }

        ActiveSection = section;
    }


    public bool PunchResponseEnabled
    {
        get => _punchResponseEnabled;
        set { if (SetProperty(ref _punchResponseEnabled, value)) { _audioPlayer.PunchResponseEnabled = value; QueueSave(); } }
    }

    /// <summary>Bass-reactive gain in 0..0.3; the slider works in percent.</summary>
    public double BassReactiveGain
    {
        get => _bassReactiveGain;
        set
        {
            var normalized = Math.Clamp(double.IsFinite(value) ? value : 0.15, 0, 0.3);
            if (!SetProperty(ref _bassReactiveGain, normalized)) { return; }
            _audioPlayer.BassReactiveGain = normalized;
            OnPropertyChanged(nameof(BassReactivePercentValue));
            OnPropertyChanged(nameof(BassReactivePercent));
            OnPropertyChanged(nameof(ReactionSummary));
            QueueSave();
        }
    }

    public double BassReactivePercentValue { get => BassReactiveGain * 100; set => BassReactiveGain = value / 100; }
    public string BassReactivePercent => $"{Math.Round(BassReactiveGain * 100):0}%";

    public bool StereoPulseEnabled
    {
        get => _stereoPulseEnabled;
        set { if (SetProperty(ref _stereoPulseEnabled, value)) { _audioPlayer.StereoPulseEnabled = value; OnPropertyChanged(nameof(ReactionSummary)); QueueSave(); } }
    }

    public bool AdaptiveVolumeEnabled
    {
        get => _adaptiveVolumeEnabled;
        set { if (SetProperty(ref _adaptiveVolumeEnabled, value)) { _audioPlayer.AdaptiveVolumeEnabled = value; QueueSave(); } }
    }
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (SetProperty(ref _isRunning, value)) { RefreshCommands(); StateChanged?.Invoke(); } }
    }

    /// <summary>
    /// True whenever the audio is silenced with its position kept — either because the user
    /// paused explicitly or because the idle timer paused it. The transport button offers
    /// Resume in both cases, which is why idle pause counts here.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (!SetProperty(ref _isPaused, value)) { return; }
            OnPropertyChanged(nameof(PauseResumeText));
        YouTube.RefreshLocalization();
            OnPropertyChanged(nameof(PauseResumeGlyph));
            RefreshCommands();
        }
    }

    public string PauseResumeText => Loc.T(IsPaused ? "L_Resume" : "L_Pause");
    public string PauseResumeGlyph => IsPaused ? "\uE768" : "\uE769";
    /// <summary>
    /// Any playlist edit — adding, removing, moving, clearing, starting — needs a settled window:
    /// not shutting down, not restoring tracks, not changing playback, and no import in flight.
    /// </summary>
    public bool CanEditPlaylist => !_closing && !_initializing && !_changingPlayback && _pendingImports == 0;
    public bool CanReset => CanEditPlaylist;
    public bool CanClearPlaylist => CanEditPlaylist && Tracks.Count > 0;
    public bool HasTracks => Tracks.Count > 0;
    public string TrackCountText => Loc.F("L_TrackCountFormat", Tracks.Count);

    /// <summary>The bare number for the playlist badge, so the count reads at a glance.</summary>
    public string TrackCountNumber => Tracks.Count.ToString(CultureInfo.CurrentUICulture);
    public bool IsEnglish => Loc.Current == AppLanguage.En;
    public bool IsRussian => Loc.Current == AppLanguage.Ru;
    public bool IsLightTheme => Theme.IsLight;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>
    /// The idle line tells the truth about where the user is: on the hub there is no Start button,
    /// so telling them to press Start would point at something that is not on screen.
    /// </summary>
    public string IdleStatusText =>
        _page == AppPage.Home && !_isFocusMode ? Loc.T("L_HomeStatusHint") : Loc.T("L_ReadyToType");
    public string NowPlayingText { get => _nowPlayingText; private set => SetProperty(ref _nowPlayingText, value); }

    /// <summary>
    /// What the shared player is actually playing, whichever page made it play. The card used to
    /// name the track selected on the current page, so switching tabs made it claim a local file
    /// was playing while a cached YouTube track really was.
    /// </summary>
    public string PlayingTrackText
    {
        get => _playingTrackText;
        private set => SetProperty(ref _playingTrackText, value);
    }

    /// <summary>The card's heading, naming where the sound comes from: local files or YouTube.</summary>
    public string PlayingSourceText
    {
        get => _playingSourceText;
        private set => SetProperty(ref _playingSourceText, value);
    }

    /// <summary>
    /// Set once when a settings save fails, so startup can tell the user that preferences will
    /// not survive a restart instead of losing them silently.
    /// </summary>
    public string? StorageWarning => _storageWarning;
    public double PlaybackProgress => _durationSeconds > 0
        ? Math.Clamp(_positionSeconds / _durationSeconds * 100, 0, 100) : 0;
    public string PlaybackPositionText => $"{FormatPosition(_positionSeconds)} / {FormatPosition(_durationSeconds)}";
    private string PlaybackStatusText => _trackEnded ? Loc.T("L_TrackEnded") :
        IsRunning ? Loc.T(_audioPlayer.IsIdlePaused ? "L_StatusIdlePaused"
            : IsPaused ? "L_StatusPaused"
            : "L_StatusListening")
            // Idle means the page decides what to say: the hub has no Start button to point at.
        : IdleStatusText;

    public async Task InitializeAsync()
    {
        // The library lives in its own file now. It is loaded before the playlist is built, so an
        // existing installation sees its tracks on the very first start after the update.
        await LoadLibraryAsync().ConfigureAwait(true);
        foreach (var track in Tracks.ToArray()) { await LoadTrackInfoAsync(track).ConfigureAwait(true); }
        var saved = _savedSelectedTrackId is Guid id ? Tracks.FirstOrDefault(track => track.Id == id) : null;
        SelectedTrack = saved ?? Tracks.FirstOrDefault(track => track.IsAvailable) ?? Tracks.FirstOrDefault();
        _initializing = false;
        RefreshTrackPositions();
        SyncPlaybackQueue();
        RefreshPlaybackProgress();
        StatusText = IdleStatusText;
        RefreshCommands();
        // The YouTube section measures its cache and reports whether yt-dlp is installed.
        try { await YouTube.InitializeAsync().ConfigureAwait(true); }
        catch (Exception exception) { AppLogger.Error("youtube", "Could not initialize the YouTube section.", exception); }
        // The TikTok section has to be measured too. Without this call its counter was never filled
        // at startup, and the page showed "0 KB" for a folder that was not empty.
        try { await TikTok.InitializeAsync().ConfigureAwait(true); }
        catch (Exception exception) { AppLogger.Error("tiktok", "Could not initialize the TikTok section.", exception); }
        // Rewrite older settings as the new schema without discarding the user's playlist/preferences.
        await SaveAsync().ConfigureAwait(true);
    }

    private async Task<bool> LoadTrackInfoAsync(TrackViewModel track)
    {
        try
        {
            var audio = await Task.Run(() => AudioTrackLoader.Load(track.Id, track.SourcePath), _lifetime.Token).ConfigureAwait(true);
            track.SetAudioInfo(audio.Duration);
            _playbackController.UpsertTrack(audio);
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            track.SetUnavailable(exception.Message, exception is FileNotFoundException or DirectoryNotFoundException);
            AppLogger.Error("audio", $"Could not open '{track.SourcePath}'.", exception);
            return false;
        }
    }

    public void AddFiles(IEnumerable<string> files) => _ = AddFilesAsync(files);

    public Task AddFilesAsync(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (_closing) { return Task.CompletedTask; }
        var paths = files.ToArray();
        _pendingImports++;
        RefreshCommands();
        _fileImportTask = ImportFilesAsync(_fileImportTask, paths);
        return _fileImportTask;
    }

    private async Task ImportFilesAsync(Task previousImport, string[] files)
    {
        await previousImport.ConfigureAwait(true);
        var added = 0;
        var invalid = false;
        var duplicate = false;
        string? firstAudioError = null;
        try
        {
            foreach (var file in files)
            {
                if (_closing) { break; }
                string path;
                try { path = Path.GetFullPath(file); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                { invalid = true; continue; }
                var extension = Path.GetExtension(path);
                if (!File.Exists(path) || (!extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) &&
                                          !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)))
                { invalid = true; continue; }
                if (Tracks.Any(track => track.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                { duplicate = true; continue; }
                var track = new TrackViewModel(new TrackState { SourcePath = path });
                if (!await LoadTrackInfoAsync(track).ConfigureAwait(true))
                {
                    firstAudioError ??= track.TooltipText;
                    continue;
                }
                Tracks.Add(track);
                if (SelectedTrack is null) { SelectedTrack = track; }
                added++;
            }
            if (_closing) { return; }
            if (added > 0) { StatusText = IsRunning ? Loc.T("L_StatusListening") : Loc.F("L_AddedFormat", added); }
            if (firstAudioError is not null) { UserMessageRequested?.Invoke(firstAudioError); }
            else if (invalid) { UserMessageRequested?.Invoke(Loc.T("L_UnsupportedFiles")); }
            else if (added == 0 && duplicate) { UserMessageRequested?.Invoke(Loc.T("L_DuplicateFiles")); }
        }
        catch (Exception exception)
        {
            AppLogger.Error("audio", "Could not add audio files.", exception);
            if (!_closing) { UserMessageRequested?.Invoke(Loc.F("L_ErrorDetailsFormat", Loc.T("L_ErrorAudioLoad"), exception.Message)); }
        }
        finally
        {
            _pendingImports--;
            RefreshCommands();
            QueueSave();
        }
    }

    /// <summary>"Старт" is enabled when the active page has something to play.</summary>
    private bool CanStartPlayback() => CanEditPlaylist && !IsRunning && _page switch
    {
        AppPage.YouTube => YouTube.Downloaded.Any(track => track.CanPlay),
        AppPage.TikTok => TikTok.Tracks.Any(track => track.CanPlay),
        _ => Tracks.Any(track => track.IsAvailable)
    };

    private async Task StartPlaybackAsync()
    {
        // The button starts the list the user is looking at, not always the local one.
        if (_page == AppPage.YouTube)
        {
            var cached = YouTube.SelectedTrack is { CanPlay: true } chosen
                ? chosen
                : YouTube.Downloaded.FirstOrDefault(item => item.CanPlay);
            if (cached is null) { ReportNothingToPlay(); return; }
            YouTube.SelectTrack(cached);
            await PlayCachedAsync(cached, isPreview: false).ConfigureAwait(true);
            return;
        }

        if (_page == AppPage.TikTok)
        {
            var cached = TikTok.SelectedTrack is { CanPlay: true } chosen
                ? chosen
                : TikTok.Tracks.FirstOrDefault(item => item.CanPlay);
            if (cached is null) { ReportNothingToPlay(); return; }
            TikTok.SelectTrack(cached);
            await PlayCachedAsync(cached, isPreview: false).ConfigureAwait(true);
            return;
        }

        var track = SelectedTrack is { IsAvailable: true } ? SelectedTrack : Tracks.FirstOrDefault(item => item.IsAvailable);
        if (track is null) { ReportNothingToPlay(); return; }
        await PlayPlaylistTrackAsync(track).ConfigureAwait(true);
    }

    /// <summary>
    /// "Nothing to play" in focus mode is a line of text, not a dialog: a modal window is exactly
    /// the kind of interruption the mode exists to remove.
    /// </summary>
    private void ReportNothingToPlay()
    {
        if (_isFocusMode)
        {
            StatusText = Loc.T("L_FocusNothingToPlay");
            return;
        }

        UserMessageRequested?.Invoke(Loc.T("L_NoTracks"));
    }

    /// <summary>
    /// Играет строку локального плейлиста. Один вход для кнопки «Старт» и для клика по строке:
    /// клик, как и в списках YouTube и TikTok, включает трек, а не только выделяет его.
    /// </summary>
    public async Task PlayPlaylistTrackAsync(TrackViewModel? track)
    {
        if (track is not { IsAvailable: true }) { return; }
        _changingPlayback = true;
        RefreshCommands();
        try
        {
            SelectedTrack = track;
            // The queue follows the track that is about to play, not whatever was selected before:
            // without this the local playlist inherited the previous tab's queue and never advanced.
            SyncPlaybackQueue(track.Id);
            if (!_typingPlayback.SelectTrack(track.Id, restart: true)) { return; }
            // Воспроизведение уже идёт — клик переключает трек, а не перезапускает всю сессию,
            // поэтому StartAsync зовётся только когда транспорт стоит.
            if (!IsRunning)
            {
                await _typingPlayback.StartAsync().ConfigureAwait(true);
                IsRunning = _typingPlayback.IsRunning;
            }

            IsPaused = false;
            _trackEnded = false;
            RefreshPlaybackProgress();
            StatusText = PlaybackStatusText;
            if (!IsRunning) { return; }
            // Start persists preferences immediately even if debounce has not fired.
            await SaveAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLogger.Error("playback", "Failed to start continuous playback.", exception);
            UserMessageRequested?.Invoke(Loc.F("L_StartFailedFormat", exception.Message));
        }
        finally { _changingPlayback = false; RefreshCommands(); }
    }

    /// <summary>
    /// Pause is a player state, not a stop: the worker, the hook and the decoder stay alive, so
    /// resuming continues the same run from the exact sample instead of restarting at 0:00.
    /// </summary>
    private void TogglePause()
    {
        try
        {
            // The idle pause silences the track too, so the button offers Resume there as well
            // instead of trying to "pause" something that is already silent.
            if (_audioPlayer.IsPaused || _audioPlayer.IsIdlePaused) { _typingPlayback.Resume(); }
            else { _typingPlayback.Pause(); }
        }
        catch (Exception exception)
        {
            AppLogger.Error("playback", "Could not change the pause state.", exception);
            return;
        }

        IsPaused = _audioPlayer.IsPaused || _audioPlayer.IsIdlePaused;
        RefreshPlaybackProgress();
        StatusText = PlaybackStatusText;
        StateChanged?.Invoke();
    }

    private async Task StopPlaybackAsync()
    {
        _changingPlayback = true;
        RefreshCommands();
        try { await _typingPlayback.StopAsync().ConfigureAwait(true); }
        catch (Exception exception) { AppLogger.Error("playback", "Could not stop continuous playback cleanly.", exception); }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _trackEnded = false;
            _changingPlayback = false;
            RefreshPlaybackProgress();
            StatusText = IdleStatusText;
            RefreshCommands();
        }
    }

    private async Task RemoveTrackAsync(TrackViewModel track)
    {
        if (!CanEditPlaylist || !Tracks.Contains(track)) { return; }
        if (SelectedTrack == track)
        {
            if (IsRunning) { await StopPlaybackAsync().ConfigureAwait(true); }
            _typingPlayback.Deselect();
        }
        _playbackController.RemoveTrack(track.Id);
        var index = Tracks.IndexOf(track);
        Tracks.Remove(track);
        if (SelectedTrack == track)
        {
            SelectedTrack = Tracks.Count == 0 ? null : Tracks[Math.Min(index, Tracks.Count - 1)];
        }
        await SaveAsync().ConfigureAwait(true);
    }

    private bool CanMove(TrackViewModel? track, int offset)
    {
        if (track is null) { return false; }
        var index = Tracks.IndexOf(track);
        var target = index + offset;
        return index >= 0 && target >= 0 && target < Tracks.Count;
    }

    /// <summary>
    /// Moves a row by one position. The collection raises Move, so the same view-model instance
    /// stays selected: reordering never restarts the track that is playing.
    /// </summary>
    private void MoveTrack(TrackViewModel? track, int offset)
    {
        if (track is null || !CanEditPlaylist) { return; }
        var index = Tracks.IndexOf(track);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Tracks.Count) { return; }
        Tracks.Move(index, target);
    }

    /// <summary>
    /// The playback queue is the list of the active page. "Next" therefore means the row below in
    /// the list the user is looking at, and Loop closes inside that page: a TikTok track no longer
    /// hands over to a local file, which is what the old single flat order did.
    /// </summary>
    /// <summary>
    /// The playback queue belongs to the tab the playing track came from, not to the tab in view.
    /// Switching tabs is only looking around: a YouTube track keeps advancing through YouTube while
    /// the local playlist is on screen, and never crawls into another list. When nothing plays the
    /// visible tab supplies the queue, because "Start" has to know what it would play.
    /// </summary>
    private void SyncPlaybackQueue(Guid? aboutToPlay = null)
    {
        var playing = _playbackController.CurrentTrack;
        // The track that is about to play decides the page when it is known: the queue is rebuilt
        // before the row is handed to the player, and asking the controller at that moment would
        // answer with the previous track's page.
        var owner = aboutToPlay ?? playing?.Id;
        var page = owner is Guid id ? PageOf(id) : _page;
        var queue = BuildQueue(page);
        _playbackController.Replace(queue, playing);
        // Every track of this page is part of its queue again. The marks are cleared first because
        // they outlive a page switch: a local track that was playing while another tab was open got
        // marked there, and the mark then stopped it from advancing on its own page.
        foreach (var track in queue) { _playbackController.SetStandalone(track.Id, false); }
        var upcoming = aboutToPlay ?? playing?.Id;
        if (upcoming is not Guid upcomingId) { return; }
        // Only a preview is a one-off now: it belongs to no tab, so it ends instead of continuing.
        // A track of another tab is no longer parked here at all, because the queue follows it.
        _playbackController.SetStandalone(upcomingId, upcomingId == _previewTrackId);
    }

    /// <summary>
    /// Which tab owns this track. A preview belongs to the YouTube section it was started from, but
    /// it is handled by the mark above, so it never reaches the queue as an ordinary row.
    /// </summary>
    private AppPage PageOf(Guid id)
    {
        if (Tracks.Any(track => track.Id == id)) { return AppPage.Playlist; }
        if (YouTube.Downloaded.Any(track => track.Id == id)) { return AppPage.YouTube; }
        if (TikTok.Tracks.Any(track => track.Id == id)) { return AppPage.TikTok; }
        // A preview or a file that has left its list: the visible tab is the honest fallback.
        return _page;
    }

    private List<PlaybackTrack> BuildQueue(AppPage page)
    {
        var queue = new List<PlaybackTrack>();
        switch (page)
        {
            case AppPage.YouTube:
                AddCached(queue, YouTube.Downloaded);
                break;
            case AppPage.TikTok:
                AddCached(queue, TikTok.Tracks);
                break;
            default:
                foreach (var track in Tracks)
                {
                    if (track.IsAvailable && track.Duration > TimeSpan.Zero) { queue.Add(track.ToPlaybackTrack()); }
                }

                break;
        }

        return queue;
    }

    /// <summary>
    /// Both cached lists share one shape, so one loop covers them. A row joins the queue only when
    /// it can really play and its length is known: the controller refuses a zero-length track, and
    /// a row whose length is still unknown gets it from the decoder when it is played.
    /// </summary>
    private static void AddCached(List<PlaybackTrack> queue, IEnumerable<YouTubeTrackViewModel> rows)
    {
        foreach (var row in rows)
        {
            if (row.CanPlay && row.State.Duration > TimeSpan.Zero)
            {
                queue.Add(new PlaybackTrack(row.Id, row.Title, row.CachedPath, row.State.Duration));
            }
        }
    }

    /// <summary>Any add, remove or reorder in a cached list reshapes the queue of that page.</summary>
    private void OnPageQueueChanged() => SyncPlaybackQueue();

    /// <summary>
    /// Drops a dragged row at a target position. Unlike <see cref="MoveTrack"/> it may travel any
    /// distance, which is what a drag implies; the selected instance survives because the
    /// collection raises Move rather than Remove/Insert.
    /// </summary>
    public bool MoveTrackTo(TrackViewModel? track, int targetIndex)
    {
        if (track is null || !CanEditPlaylist) { return false; }
        var index = Tracks.IndexOf(track);
        if (index < 0 || targetIndex < 0 || targetIndex >= Tracks.Count || index == targetIndex) { return false; }
        Tracks.Move(index, targetIndex);
        return true;
    }

    /// <summary>
    /// Shows where a drop would land. Passing null clears every indicator, so a cancelled drag
    /// never leaves a stray line behind.
    /// </summary>
    public void SetDropTarget(TrackViewModel? target, bool above)
    {
        foreach (var track in Tracks)
        {
            var isTarget = target is not null && ReferenceEquals(track, target);
            track.IsDropTarget = isTarget;
            track.IsDropTargetAbove = isTarget && above;
        }
    }

    private void RefreshTrackPositions()
    {
        for (var index = 0; index < Tracks.Count; index++)
        {
            Tracks[index].SetPosition(index, Tracks.Count);
        }
    }

    /// <summary>
    /// Stops playback, releases the audio file and empties both the visible list and the controller.
    /// "Clear playlist" and "reset" share it so the two can never drift apart.
    /// </summary>
    private async Task ClearPlaylistCoreAsync()
    {
        await StopPlaybackAsync().ConfigureAwait(true);
        _typingPlayback.Unload();
        _playbackController.Clear();
        Tracks.Clear();
        SelectedTrack = null;
    }

    /// <summary>Empties the playlist only: playback settings, language, and theme stay untouched.</summary>
    public async Task ClearPlaylistAsync()
    {
        if (!CanEditPlaylist || Tracks.Count == 0) { return; }
        await ClearPlaylistCoreAsync().ConfigureAwait(true);
        StatusText = Loc.T("L_PlaylistCleared");
        await SaveAsync().ConfigureAwait(true);
    }

    public async Task ResetAsync()
    {
        if (!CanEditPlaylist) { return; }
        // Soft reset: playlist and playback settings return to their defaults; the chosen language
        // and theme stay exactly as the user left them, and audio files are never touched.
        await ClearPlaylistCoreAsync().ConfigureAwait(true);
        MaxVolume = 1;
        MinVolume = 0.2;
        IdleTimeout = 3;
        PauseOnIdle = true;
        AutoAdvance = false;
        Loop = true;
        StatusText = Loc.T("L_ResetComplete");
        await SaveAsync().ConfigureAwait(true);
        UserMessageRequested?.Invoke(Loc.T("L_ResetComplete"));
    }

    public async Task SaveAsync()
    {
        var snapshot = new AppSettings
        {
            MinVolume = MinVolume, MaxVolume = MaxVolume, IdleTimeout = IdleTimeout, PauseOnIdle = PauseOnIdle,
            AutoAdvance = AutoAdvance, Loop = Loop,
            // The window size is remembered, not offered: it is written with the other preferences
            // so it survives a restart like they do.
            WindowWidth = _windowWidth, WindowHeight = _windowHeight,
            PunchResponseEnabled = PunchResponseEnabled, BassReactiveGain = BassReactiveGain,
            StereoPulseEnabled = StereoPulseEnabled, AdaptiveVolumeEnabled = AdaptiveVolumeEnabled,
            SelectedTrackId = SelectedTrack?.Id, Language = Loc.CurrentCode, Theme = Theme.CurrentCode,
            YouTubeCacheEnabled = YouTube.CacheEnabled,
            CookiesFolderPath = YouTube.CookiesFolderPath,
            ProxyUrl = YouTube.ProxyUrl,
            // Once the library has taken the tracks over, settings.json stops carrying them: one
            // owner per piece of data, so a reset of the preferences cannot wipe the collection.
            TracksMigratedToLibrary = _libraryLoaded
        };
        try { await _settingsService.SaveAsync(snapshot).ConfigureAwait(true); }
        catch (Exception exception)
        {
            AppLogger.Error("settings", "Failed to save settings.", exception);
            // Remember the first failure so the window can explain it once instead of staying silent.
            _storageWarning ??= Loc.F("L_SettingsNotSavedFormat", AppPaths.SettingsFile);
        }

        if (!_libraryLoaded) { return; }
        try
        {
            await _library.SaveAsync(new LibrarySnapshot(
                Tracks.Select(track => track.ToState()).ToList(),
                YouTube.ToStates().ToList(),
                TikTok.ToStates().ToList(),
                _libraryMigratedVersion)).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLogger.Error("library", "Failed to save the library.", exception);
            _storageWarning ??= Loc.F("L_SettingsNotSavedFormat", _library.FilePath);
        }
    }

    /// <summary>
    /// Loads library.json, migrating an older settings.json on the way. The three collections are
    /// replaced rather than appended to, so running twice cannot duplicate a track.
    /// </summary>
    private async Task LoadLibraryAsync()
    {
        try
        {
            var snapshot = await _library.LoadAsync(_lifetime.Token).ConfigureAwait(true);
            _libraryMigratedVersion = snapshot.PathsMigratedToLibraryVersion;
            _libraryLoaded = true;
            if (snapshot.Tracks.Count == 0 && snapshot.YouTubeTracks.Count == 0 && snapshot.TikTokTracks.Count == 0)
            {
                // Nothing was stored yet; whatever the constructor built from settings stays.
                return;
            }

            Tracks.Clear();
            foreach (var state in snapshot.Tracks)
            {
                try { Tracks.Add(new TrackViewModel(state)); }
                catch (Exception exception) { AppLogger.Error("library", $"Ignored invalid track path '{state.SourcePath}'.", exception); }
            }

            YouTube.ReplaceTracks(snapshot.YouTubeTracks);
            TikTok.ReplaceTracks(snapshot.TikTokTracks);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLogger.Error("library", "Could not load the library; the playlist may be incomplete.", exception);
        }
    }

    private void PlaybackTimerOnTick(object? sender, EventArgs args)
    {
        if (!_closing && !_initializing) { RefreshPlaybackProgress(); }
    }

    private void RefreshPlaybackProgress()
    {
        // A stopped player keeps its position, so the card showed "00:04.60 / 04:03.38" under the
        // words "Nothing playing" and a progress bar for a track that was not running. The timers
        // are a claim about sound, so they go to zero with the sound.
        var running = IsRunning;
        var position = running ? _audioPlayer.Position.TotalSeconds : 0;
        var duration = running ? _audioPlayer.Duration.TotalSeconds : 0;
        position = Math.Max(0, double.IsFinite(position) ? position : 0);
        duration = double.IsFinite(duration) && duration > 0 ? duration : running ? SelectedTrack?.Duration.TotalSeconds ?? 0 : 0;
        if (_positionSeconds != position || _durationSeconds != duration)
        {
            _positionSeconds = position;
            _durationSeconds = duration;
            OnPropertyChanged(nameof(PlaybackPositionText));
            OnPropertyChanged(nameof(PlaybackProgress));
        }
        var playing = _playbackController.CurrentTrack;
        SyncIdlePauseFor(playing);
        // The search row's button doubles as pause, so it has to follow the player, not its own
        // memory of what it started.
        YouTube.SyncPreviewPlayback(
            playing is not null && playing.Id == _previewTrackId,
            _audioPlayer.IsPlaying,
            _audioPlayer.IsPaused || _audioPlayer.IsIdlePaused);
        var playingName = playing is null ? string.Empty :
            _friendlyNames.TryGetValue(playing.Id, out var friendly) ? friendly : playing.Name;
        // "Now playing" is a claim about sound, not about a selection. The controller keeps its
        // track after Stop, so an ungated label told the user that a stopped app was playing a
        // file at 00:00 — reported as "nothing is running, but it says it is playing".
        var active = IsRunning && playing is not null;
        NowPlayingText = active
            ? Loc.F("L_NowPlayingFormat", playingName, FormatPosition(position), FormatPosition(duration))
            : string.Empty;
        PlayingTrackText = active ? playingName : Loc.T("L_NothingPlaying");
        // The source is read from the lists themselves, not remembered per click: an auto-advance
        // never passes through the play path, so a remembered set would call the next TikTok row a
        // local file. A preview belongs to no list and is named after the section it came from.
        // A missing quarter was shown raw as "Now playing · {0}": the format string cannot stand in
        // for the sentence it is only half of, so the idle case gets its own complete label.
        PlayingSourceText = active
            ? Loc.F("L_NowPlayingCard", SourceNameFor(playing!.Id))
            : Loc.T("L_NowPlayingIdle");
        if (IsRunning)
        {
            // A paused run is not an ended one: the position stays short of the duration.
            IsPaused = _audioPlayer.IsPaused || _audioPlayer.IsIdlePaused;
            _trackEnded = !_audioPlayer.IsPlaying && !_audioPlayer.IsIdlePaused && !_audioPlayer.IsPaused &&
                duration > 0 && position >= duration;
            StatusText = PlaybackStatusText;
        }
    }

    private void OnPlaybackTrackChanged(PlaybackTrackChangedEventArgs args) => RunOnUiThread(() =>
    {
        if (_closing || args.TrackId != _playbackController.SelectedTrackId) { return; }
        // The highlight follows the page that owns the queue: an advance inside the TikTok list
        // must move the selection down that list, not into the local playlist.
        switch (_page)
        {
            case AppPage.YouTube:
            {
                var row = YouTube.Downloaded.FirstOrDefault(item => item.Id == args.TrackId);
                if (row is not null && !ReferenceEquals(row, YouTube.SelectedTrack)) { YouTube.SelectTrack(row); }
                return;
            }
            case AppPage.TikTok:
            {
                var row = TikTok.Tracks.FirstOrDefault(item => item.Id == args.TrackId);
                if (row is not null && !ReferenceEquals(row, TikTok.SelectedTrack)) { TikTok.SelectTrack(row); }
                return;
            }
        }

        var track = Tracks.FirstOrDefault(item => item.Id == args.TrackId);
        if (track is null || ReferenceEquals(track, SelectedTrack)) { return; }
        _selectionFromPlayback = true;
        try { SelectedTrack = track; }
        finally { _selectionFromPlayback = false; }
    });

    private void OnPlaybackFailed(PlaybackFailureEventArgs args) => RunOnUiThread(async () =>
    {
        if (_closing || args.SessionId != _typingPlayback.SessionId) { return; }
        await StopPlaybackAsync().ConfigureAwait(true);
        StatusText = Loc.T("L_PlaybackStopped");
        if (args.Exception is IOException or UnauthorizedAccessException)
        {
            var track = Tracks.FirstOrDefault(item => item.Id == args.TrackId);
            track?.SetUnavailable(args.Exception.Message, args.Exception is FileNotFoundException or DirectoryNotFoundException);
            if (track is not null) { _playbackController.RemoveTrack(track.Id); }
            RefreshCommands();
        }
        UserMessageRequested?.Invoke(Loc.F("L_StartFailedFormat", args.Exception.Message));
    });

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); }
        else if (!dispatcher.HasShutdownStarted) { _ = dispatcher.BeginInvoke(action, DispatcherPriority.Background); }
    }

    /// <summary>
    /// Where the sound comes from, decided by which list holds the track. The lists are the source
    /// of truth, so a row that arrived by auto-advance is named as correctly as a clicked one.
    /// </summary>
    private string SourceNameFor(Guid id) =>
        id == _previewTrackId ? Loc.T("L_SourceYouTube")
        : TikTok.Tracks.Any(track => track.Id == id) ? Loc.T("L_SourceTikTok")
        : YouTube.Downloaded.Any(track => track.Id == id) ? Loc.T("L_SourceYouTube")
        : Loc.T("L_SourceLocal");

    private static string FormatPosition(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time:mm\\:ss\\.ff}" : time.ToString(@"mm\:ss\.ff");
    }

    private static void OpenLogs()
    {
        AppPaths.EnsureCreated();
        try { Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDirectory, UseShellExecute = true }); }
        catch (Exception exception) { AppLogger.Error("ui", "Could not open the logs folder.", exception); }
    }

    /// <summary>
    /// Lets a harness point the YouTube section at a scratch directory, so UI tests never read or
    /// delete the real cache and never touch the network.
    /// </summary>
    public static Func<YouTubeTrackService>? YouTubeServiceFactory { get; set; }

    private static YouTubeTrackService CreateDefaultYouTubeService() =>
        YouTubeServiceFactory?.Invoke() ?? new(new YtDlpBinaryManager(), new YouTubeProcessRunner());

    /// <summary>
    /// Plays a saved row of the YouTube page. The queue is that page's list, so the track that
    /// follows is the row below it, never a file from another tab.
    /// </summary>
    private void OnYouTubePlayRequested(YouTubeTrackViewModel track) =>
        _ = PlayCachedAsync(track, isPreview: false);

    /// <summary>Plays a saved row of the TikTok page, with the same rule.</summary>
    private void OnTikTokPlayRequested(YouTubeTrackViewModel track) =>
        _ = PlayCachedAsync(track, isPreview: false);

    /// <summary>
    /// A search preview belongs to no list, so it never joins the page queue: it is heard once and
    /// stops, which is what a preview is.
    /// </summary>
    private void OnYouTubePreviewRequested(string path, string title) => _ = PlayPreviewAsync(path, title);

    private Task PlayCachedAsync(YouTubeTrackViewModel track, bool isPreview) =>
        string.IsNullOrEmpty(track.CachedPath)
            ? Task.CompletedTask
            : PlayFileAsync(track.Id, track.CachedPath, track.Title, isPreview, track);

    private Task PlayPreviewAsync(string path, string title) =>
        PlayFileAsync(Guid.NewGuid(), path, title, isPreview: true, track: null);

    private async Task PlayFileAsync(Guid id, string path, string title, bool isPreview,
        YouTubeTrackViewModel? track)
    {
        try
        {
            var playback = AudioTrackLoader.Load(id, path);
            // A row that never learned its length learns it here, so it can join the queue too.
            if (track is not null && track.State.Duration <= TimeSpan.Zero) { track.SetDuration(playback.Duration); }
            _friendlyNames[playback.Id] = string.IsNullOrWhiteSpace(title) ? playback.Name : title;
            // Rebuild before the upsert: the page list is the queue, and the row below it is what
            // plays next. A preview stays outside the queue on purpose.
            if (!isPreview) { SyncPlaybackQueue(playback.Id); }
            _playbackController.UpsertTrack(playback);
            // A preview is heard once. Without this it sat in the page queue, and with Loop on, the
            // end of the preview handed over to the first downloaded track — reported as "the
            // preview finished and something else started playing".
            _playbackController.SetStandalone(playback.Id, isPreview);
            // The preview is remembered before the first note, so a short idle time cannot be armed
            // and fire a moment later; a downloaded track clears it and keeps the pause working.
            _previewTrackId = isPreview ? playback.Id : null;
            if (!_typingPlayback.SelectTrack(playback.Id, restart: true)) { return; }
            if (!IsRunning)
            {
                await _typingPlayback.StartAsync().ConfigureAwait(true);
                IsRunning = _typingPlayback.IsRunning;
                IsPaused = false;
            }

            RefreshPlaybackProgress();
            StatusText = PlaybackStatusText;
            StateChanged?.Invoke();
        }
        catch (Exception exception)
        {
            AppLogger.Error("youtube", "Could not play the cached YouTube track.", exception);
            UserMessageRequested?.Invoke(Loc.F("L_StartFailedFormat", exception.Message));
        }
    }

    private static void OpenMusicFolder()
    {
        try
        {
            AppPaths.EnsureYouTubeCreated();
            Process.Start(new ProcessStartInfo { FileName = AppPaths.MusicDirectory, UseShellExecute = true });
        }
        catch (Exception exception) { AppLogger.Error("youtube", "Could not open the music folder.", exception); }
    }
    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RefreshTrackPositions();
        SyncPlaybackQueue();
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TrackCountText));
        OnPropertyChanged(nameof(TrackCountNumber));
        // The view follows the collection by itself; only the "nothing matched" hint needs a word.
        OnPropertyChanged(nameof(TrackFilteredEmptyVisibility));
        RefreshCommands();
        QueueSave();
    }
    private void OnLanguageChanged()
    {
        foreach (var track in Tracks) { track.RefreshLocalization(); }
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsRussian));
        OnPropertyChanged(nameof(TrackCountText));
        OnPropertyChanged(nameof(SelectedTrackText));
        OnPropertyChanged(nameof(IdleTimeoutText));
        OnPropertyChanged(nameof(VolumeSummary));
        OnPropertyChanged(nameof(ReactionSummary));
        OnPropertyChanged(nameof(BehaviorSummary));
        OnPropertyChanged(nameof(PauseResumeText));
        OnPropertyChanged(nameof(FocusToggleText));
        OnPropertyChanged(nameof(FocusToggleTooltip));
        OnPropertyChanged(nameof(GreetingText));
        YouTube.RefreshLocalization();
        TikTok.RefreshLocalization();
        RefreshPlaybackProgress();
        StatusText = PlaybackStatusText;
        StateChanged?.Invoke();
        QueueSave();
    }
    private void OnThemeChanged()
    {
        OnPropertyChanged(nameof(IsLightTheme));
        StateChanged?.Invoke();
        QueueSave();
    }
    private void QueueSave()
    {
        if (_closing || _disposed) { return; }
        _saveTimer.Stop();
        _saveTimer.Start();
    }
    private async void SaveTimerOnTick(object? sender, EventArgs args)
    {
        _saveTimer.Stop();
        await SaveAsync().ConfigureAwait(true);
    }
    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEditPlaylist));
        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(CanClearPlaylist));
        AddFilesCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        TogglePauseCommand.NotifyCanExecuteChanged();
        RemoveTrackCommand.NotifyCanExecuteChanged();
        MoveTrackUpCommand.NotifyCanExecuteChanged();
        MoveTrackDownCommand.NotifyCanExecuteChanged();
        // Focus mode names the dead end instead of offering a Start that cannot work, so the line
        // follows the same conditions the button does.
        OnPropertyChanged(nameof(HasSomethingToPlay));
        OnPropertyChanged(nameof(FocusNeedsMusicVisible));
        OnPropertyChanged(nameof(StartButtonVisible));
        OnPropertyChanged(nameof(HomeTracksMeta));
        OnPropertyChanged(nameof(HomeYouTubeMeta));
        OnPropertyChanged(nameof(HomeTikTokMeta));
    }

    /// <summary>True when the active page has something playable, so Start would do something.</summary>
    public bool HasSomethingToPlay => _page switch
    {
        AppPage.YouTube => YouTube.Downloaded.Any(track => track.CanPlay),
        AppPage.TikTok => TikTok.Tracks.Any(track => track.CanPlay),
        _ => Tracks.Any(track => track.IsAvailable)
    };

    /// <summary>
    /// In focus mode an empty page is named, not clicked into. Start is already disabled there; this
    /// line explains why without leaving the scene.
    /// </summary>
    public Visibility FocusNeedsMusicVisible =>
        _isFocusMode && !HasSomethingToPlay ? Visibility.Visible : Visibility.Collapsed;

    private void OnYouTubePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(YouTubeViewModel.SelectedTrack) or nameof(YouTubeViewModel.TrackCount)))
        { return; }
        OnPropertyChanged(nameof(SelectedTrackText));
        RefreshCommands();
    }

    private void OnTikTokPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(TikTokViewModel.SelectedTrack) or nameof(TikTokViewModel.TrackCount)))
        { return; }
        OnPropertyChanged(nameof(SelectedTrackText));
        RefreshCommands();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed || _closing) { return; }
        _closing = true;
        _saveTimer.Stop();
        _playbackTimer.Stop();
        _lifetime.Cancel();
        await _fileImportTask.ConfigureAwait(true);
        await StopPlaybackAsync().ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
        Tracks.CollectionChanged -= OnTracksChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        Theme.Changed -= OnThemeChanged;
        _saveTimer.Tick -= SaveTimerOnTick;
        _playbackTimer.Tick -= PlaybackTimerOnTick;
        _playbackController.CurrentTrackChanged -= OnPlaybackTrackChanged;
        _typingPlayback.PlaybackFailed -= OnPlaybackFailed;
        YouTube.PlayRequested -= OnYouTubePlayRequested;
        YouTube.PreviewRequested -= OnYouTubePreviewRequested;
        YouTube.QueueChanged -= OnPageQueueChanged;
        YouTube.PropertyChanged -= OnYouTubePropertyChanged;
        YouTube.OpenMusicFolderRequested -= OpenMusicFolder;
        YouTube.PauseToggleRequested -= TogglePause;
        YouTube.Dispose();
        TikTok.PlayRequested -= OnTikTokPlayRequested;
        TikTok.QueueChanged -= OnPageQueueChanged;
        TikTok.PropertyChanged -= OnTikTokPropertyChanged;
        TikTok.OpenMusicFolderRequested -= OpenMusicFolder;
        TikTok.Dispose();
        await _typingPlayback.DisposeAsync().ConfigureAwait(true);
        _lifetime.Dispose();
        _disposed = true;
    }
}
