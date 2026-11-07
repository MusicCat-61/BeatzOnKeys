using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    private readonly PlaybackController _playbackController;
    private readonly IContinuousAudioPlayer _audioPlayer;
    private readonly TypingPlaybackService _typingPlayback;
    private readonly YouTubeTrackService _youTube;
    private readonly Dictionary<Guid, string> _friendlyNames = [];
    // Which tracks came from the YouTube cache, so the player card can name the source instead of
    // leaving the user guessing where the sound came from.
    private readonly HashSet<Guid> _youTubeTracks = [];
    private string _playingTrackText = string.Empty;
    private string _playingSourceText = string.Empty;
    private bool _isYouTubePage;
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
        Tracks.CollectionChanged += OnTracksChanged;
        // The YouTube section keeps its own list; only the audio player is shared.
        _youTube = youTubeService ?? CreateDefaultYouTubeService();
        YouTube = new YouTubeViewModel(_youTube, settings.YouTubeTracks, settings.YouTubeCacheEnabled,
            settings.CookiesFolderPath, settings.ProxyUrl, settings.YouTubeCachePath, QueueSave,
            message => UserMessageRequested?.Invoke(message));
        YouTube.PlayRequested += OnYouTubePlayRequested;
        YouTube.PropertyChanged += OnYouTubePropertyChanged;
        YouTube.OpenMusicFolderRequested += OpenMusicFolder;
        Loc.LanguageChanged += OnLanguageChanged;
        Theme.Changed += OnThemeChanged;
        _playbackTimer.Start();
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = [];

    /// <summary>The separate YouTube section: its own search, cache and settings.</summary>
    public YouTubeViewModel YouTube { get; }

    /// <summary>True while the YouTube page is shown; the playlist page is hidden then.</summary>
    public bool IsYouTubePage
    {
        get => _isYouTubePage;
        private set
        {
            if (!SetProperty(ref _isYouTubePage, value)) { return; }
            OnPropertyChanged(nameof(IsPlaylistPage));
            OnPropertyChanged(nameof(SelectedTrackText));
            RefreshCommands();
        }
    }

    public bool IsPlaylistPage => !_isYouTubePage;

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

    public RelayCommand ShowPlaylistCommand => _showPlaylist ??= new RelayCommand(() => IsYouTubePage = false);
    public RelayCommand ShowYouTubeCommand => _showYouTube ??= new RelayCommand(() => IsYouTubePage = true);
    public RelayCommand ShowSettingsCommand => _showSettings ??= new RelayCommand(ToggleSettings);
    public RelayCommand CloseSettingsCommand => _closeSettings ??= new RelayCommand(() => IsSettingsOpen = false);
    private RelayCommand? _showPlaylist;
    private RelayCommand? _showYouTube;
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
    public string SelectedTrackText => IsYouTubePage
        ? (YouTube.SelectedTrack?.Title ?? Loc.T("L_NoTrackSelected"))
        : (SelectedTrack?.FileName ?? Loc.T("L_NoTrackSelected"));
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
        set { if (SetProperty(ref _pauseOnIdle, value)) { _audioPlayer.PauseOnIdle = value; QueueSave(); } }
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
            : Loc.T("L_ReadyToType");

    public async Task InitializeAsync()
    {
        foreach (var track in Tracks.ToArray()) { await LoadTrackInfoAsync(track).ConfigureAwait(true); }
        var saved = _savedSelectedTrackId is Guid id ? Tracks.FirstOrDefault(track => track.Id == id) : null;
        SelectedTrack = saved ?? Tracks.FirstOrDefault(track => track.IsAvailable) ?? Tracks.FirstOrDefault();
        _initializing = false;
        RefreshTrackPositions();
        SyncPlaybackOrder();
        RefreshPlaybackProgress();
        StatusText = Loc.T("L_ReadyToType");
        RefreshCommands();
        // The YouTube section measures its cache and reports whether yt-dlp is installed.
        try { await YouTube.InitializeAsync().ConfigureAwait(true); }
        catch (Exception exception) { AppLogger.Error("youtube", "Could not initialize the YouTube section.", exception); }
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
    private bool CanStartPlayback() => CanEditPlaylist && !IsRunning &&
        (IsYouTubePage ? YouTube.Downloaded.Any(track => track.CanPlay) : Tracks.Any(track => track.IsAvailable));

    private async Task StartPlaybackAsync()
    {
        // The button starts the list the user is looking at, not always the local one.
        if (IsYouTubePage)
        {
            var cached = YouTube.SelectedTrack is { CanPlay: true } chosen
                ? chosen
                : YouTube.Downloaded.FirstOrDefault(item => item.CanPlay);
            if (cached is null) { UserMessageRequested?.Invoke(Loc.T("L_NoTracks")); return; }
            YouTube.SelectTrack(cached);
            await PlayYouTubeAsync(cached.CachedPath, cached.Title).ConfigureAwait(true);
            return;
        }

        var track = SelectedTrack is { IsAvailable: true } ? SelectedTrack : Tracks.FirstOrDefault(item => item.IsAvailable);
        if (track is null) { UserMessageRequested?.Invoke(Loc.T("L_NoTracks")); return; }
        _changingPlayback = true;
        RefreshCommands();
        try
        {
            SelectedTrack = track;
            if (!_typingPlayback.SelectTrack(track.Id, restart: true)) { return; }
            await _typingPlayback.StartAsync().ConfigureAwait(true);
            IsRunning = _typingPlayback.IsRunning;
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
            StatusText = Loc.T("L_ReadyToType");
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

    /// <summary>Playback order follows the visible order, so "next at the end" is the row below.</summary>
    private void SyncPlaybackOrder() =>
        _playbackController.Reorder(Tracks.Select(track => track.Id).ToArray());

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
            PunchResponseEnabled = PunchResponseEnabled, BassReactiveGain = BassReactiveGain,
            StereoPulseEnabled = StereoPulseEnabled, AdaptiveVolumeEnabled = AdaptiveVolumeEnabled,
            SelectedTrackId = SelectedTrack?.Id, Language = Loc.CurrentCode, Theme = Theme.CurrentCode,
            Tracks = Tracks.Select(track => track.ToState()).ToList(),
            YouTubeTracks = YouTube.ToStates().ToList(),
            YouTubeCacheEnabled = YouTube.CacheEnabled,
            YouTubeCachePath = YouTube.CachePath,
            CookiesFolderPath = YouTube.CookiesFolderPath,
            ProxyUrl = YouTube.ProxyUrl
        };
        try { await _settingsService.SaveAsync(snapshot).ConfigureAwait(true); }
        catch (Exception exception)
        {
            AppLogger.Error("settings", "Failed to save settings.", exception);
            // Remember the first failure so the window can explain it once instead of staying silent.
            _storageWarning ??= Loc.F("L_SettingsNotSavedFormat", AppPaths.SettingsFile);
        }
    }

    private void PlaybackTimerOnTick(object? sender, EventArgs args)
    {
        if (!_closing && !_initializing) { RefreshPlaybackProgress(); }
    }

    private void RefreshPlaybackProgress()
    {
        var position = _audioPlayer.Position.TotalSeconds;
        var duration = _audioPlayer.Duration.TotalSeconds;
        position = Math.Max(0, double.IsFinite(position) ? position : 0);
        duration = double.IsFinite(duration) && duration > 0 ? duration : SelectedTrack?.Duration.TotalSeconds ?? 0;
        if (_positionSeconds != position || _durationSeconds != duration)
        {
            _positionSeconds = position;
            _durationSeconds = duration;
            OnPropertyChanged(nameof(PlaybackPositionText));
            OnPropertyChanged(nameof(PlaybackProgress));
        }
        var playing = _playbackController.CurrentTrack;
        var playingName = playing is null ? string.Empty :
            _friendlyNames.TryGetValue(playing.Id, out var friendly) ? friendly : playing.Name;
        NowPlayingText = playing is null ? string.Empty :
            Loc.F("L_NowPlayingFormat", playingName, FormatPosition(position), FormatPosition(duration));
        PlayingTrackText = playing is null
            ? Loc.T("L_NothingPlaying")
            : Loc.F("L_NowPlayingCard", _youTubeTracks.Contains(playing.Id)
                ? Loc.T("L_SourceYouTube")
                : Loc.T("L_SourceLocal"));
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

    /// <summary>Plays a cached YouTube file through the same player, without touching the playlist.</summary>
    private void OnYouTubePlayRequested(string path, string title) => _ = PlayYouTubeAsync(path, title);

    private async Task PlayYouTubeAsync(string path, string title)
    {
        try
        {
            var playback = AudioTrackLoader.Load(Guid.NewGuid(), path);
            _friendlyNames[playback.Id] = string.IsNullOrWhiteSpace(title) ? playback.Name : title;
            _youTubeTracks.Add(playback.Id);
            // The controller holds the playback order; a YouTube file joins it for this run only,
            // and Reorder keeps it at the end when the local playlist changes.
            _playbackController.UpsertTrack(playback);
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
        SyncPlaybackOrder();
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TrackCountText));
        OnPropertyChanged(nameof(TrackCountNumber));
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
        YouTube.RefreshLocalization();
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

    }

    private void OnYouTubePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(YouTubeViewModel.SelectedTrack) or nameof(YouTubeViewModel.TrackCount)))
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
        YouTube.PropertyChanged -= OnYouTubePropertyChanged;
        YouTube.OpenMusicFolderRequested -= OpenMusicFolder;
        YouTube.Dispose();
        await _typingPlayback.DisposeAsync().ConfigureAwait(true);
        _lifetime.Dispose();
        _disposed = true;
    }
}
