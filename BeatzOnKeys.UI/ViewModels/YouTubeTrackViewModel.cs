using System.IO;
using System.Windows;
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.UI.Localization;

namespace BeatzOnKeys.UI.ViewModels;

/// <summary>One row of the search results; "Скачать" is a plain command on the row.</summary>
public sealed class YouTubeSearchItemViewModel(YouTubeSearchResult result) : ObservableObject
{
    private double _progress;
    private bool _isDownloading;

    public string VideoId { get; } = result.VideoId;
    public string Title { get; } = result.Title;
    public string Author { get; } = result.Author;
    public string DurationText { get; } = result.DurationText;
    public bool IsLive { get; } = result.IsLive;

    /// <summary>
    /// Adding is offered only once the preview exists: before that the row would fetch the
    /// track a second time, and the user has already waited for the cache.
    /// </summary>
    public bool CanDownload => !IsLive && CanPlay;

    public string LiveBadgeText => Loc.T("L_YtLive");

    /// <summary>
    /// Set while this result is being fetched for a preview, so the row itself shows the progress
    /// instead of leaving the user guessing what the player is doing.
    /// </summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (!SetProperty(ref _isDownloading, value)) { return; }
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ShowProgress));
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (!SetProperty(ref _progress, value)) { return; }
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public bool ShowProgress => IsDownloading;
    public string ProgressText => $"{Math.Round(Progress * 100)}%";

    /// <summary>
    /// Results are cached in the background right after a search, so the listen button can work
    /// without a second network round trip. Listening is offered only once the file exists.
    /// </summary>
    public string? CachedPath
    {
        get => _cachedPath;
        set
        {
            if (!SetProperty(ref _cachedPath, value)) { return; }
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(IsCaching));
            OnPropertyChanged(nameof(CacheStateText));
            // The add button follows the file, so it lights up the moment the cache lands.
            OnPropertyChanged(nameof(CanDownload));
            OnPropertyChanged(nameof(CanAdd));
        }
    }

    private string? _cachedPath;

    public bool CanPlay => !IsLive && !string.IsNullOrEmpty(CachedPath) && File.Exists(CachedPath);

    /// <summary>
    /// True for the one row whose file is playing right now. The button on that row turns into a
    /// pause, because a second click on a playing track can only mean "stop this".
    /// </summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        internal set
        {
            if (!SetProperty(ref _isPlaying, value)) { return; }
            OnPropertyChanged(nameof(PlayGlyph));
            OnPropertyChanged(nameof(PreviewTooltip));
        }
    }

    private bool _isPlaying;

    /// <summary>Set when the playing row was paused, so the same button offers "continue".</summary>
    public bool IsPaused
    {
        get => _isPaused;
        internal set
        {
            if (!SetProperty(ref _isPaused, value)) { return; }
            OnPropertyChanged(nameof(PlayGlyph));
            OnPropertyChanged(nameof(PreviewTooltip));
        }
    }

    private bool _isPaused;

    /// <summary>Segoe MDL2 glyphs: play, pause and continue share one button.</summary>
    public string PlayGlyph => _isPlaying && !_isPaused ? "\uE769" : "\uE768";

    public string PreviewTooltip => _isPlaying && !_isPaused
        ? Loc.T("L_YtPreviewPause")
        : _isPlaying ? Loc.T("L_YtPreviewResume")
        : Loc.T("L_YtPreview");

    /// <summary>
    /// Set once this result is in the saved list. The add button becomes a green check and stops
    /// accepting clicks, so a second press cannot start a download that is already done.
    /// </summary>
    public bool IsAdded
    {
        get => _isAdded;
        internal set
        {
            if (!SetProperty(ref _isAdded, value)) { return; }
            OnPropertyChanged(nameof(CanAdd));
        }
    }

    private bool _isAdded;

    /// <summary>Adding is offered only for a cached result that is not already saved.</summary>
    public bool CanAdd => CanDownload && !_isAdded;
    /// <summary>Recomputes the localized line after a language switch.</summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(CacheStateText));
        OnPropertyChanged(nameof(LiveBadgeText));
        OnPropertyChanged(nameof(PreviewTooltip));
    }

    /// <summary>True while this result is being fetched for listening, so the row shows a state.</summary>
    public bool IsCaching => !CanPlay && !IsLive && !_cacheFailed && !_cacheStopped;

    /// <summary>
    /// Set when the user stopped the cache pass while this row was being fetched. The row keeps its
    /// play and add buttons — nothing was broken — but it must not claim to be caching for ever,
    /// which is what a stopped pass used to leave behind.
    /// </summary>
    public bool CacheStopped
    {
        get => _cacheStopped;
        set
        {
            if (_cacheStopped == value) { return; }
            _cacheStopped = value;
            OnPropertyChanged(nameof(IsCaching));
            OnPropertyChanged(nameof(CacheStateText));
        }
    }

    private bool _cacheStopped;

    /// <summary>
    /// Set when the background pass gave up on this row because it was slow. The row stays
    /// available: the pass retries it once at the end, and the play and add buttons still work
    /// with the full download timeout, which is what a deliberate click deserves.
    /// </summary>
    public bool Skipped
    {
        get => _skipped;
        set
        {
            if (_skipped == value) { return; }
            _skipped = value;
            OnPropertyChanged(nameof(CacheStateText));
        }
    }

    private bool _skipped;

    public bool CacheFailed
    {
        get => _cacheFailed;
        set
        {
            if (_cacheFailed == value) { return; }
            _cacheFailed = value;
            OnPropertyChanged(nameof(IsCaching));
            OnPropertyChanged(nameof(CacheStateText));
            OnPropertyChanged(nameof(CanDownload));
        }
    }

    private bool _cacheFailed;

    /// <summary>The line under the title: the author while caching, a hint when it cannot play.</summary>
    public string CacheStateText => IsLive
        ? LiveBadgeText
        : CanPlay ? Author
        : _cacheFailed ? Loc.T("L_YtCacheFailed")
        : _cacheStopped ? Loc.T("L_YtCacheStopped")
        : _skipped ? Loc.T("L_YtCachingLater")
        : Loc.T("L_YtCaching");
}

/// <summary>A cached YouTube track, with its own status and download progress.</summary>
public sealed class YouTubeTrackViewModel : ObservableObject
{
    private readonly YouTubeTrackState _state;
    private double _progress;
    private bool _isDownloading;

    public YouTubeTrackViewModel(YouTubeTrackState state)
    {
        _state = state;
        _progress = state.Status == YouTubeTrackStatus.Ready ? 1 : 0;
    }

    private int _index = -1;
    private bool _isFirst = true;
    private bool _isLast = true;
    private bool _isDropTarget;
    private bool _isDropTargetAbove = true;
    private bool _isSelected;

    public YouTubeTrackState State => _state;

    /// <summary>Zero-based position in the visible list, refreshed by the owning view model.</summary>
    public int Index => _index;

    public bool IsFirst => _isFirst;
    public bool IsLast => _isLast;

    /// <summary>Set while a dragged row hovers here, so the list can show where the drop lands.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        internal set { if (_isDropTarget != value) { _isDropTarget = value; OnPropertyChanged(); } }
    }

    public bool IsDropTargetAbove
    {
        get => _isDropTargetAbove;
        internal set { if (_isDropTargetAbove != value) { _isDropTargetAbove = value; OnPropertyChanged(); } }
    }

    /// <summary>Called after any add, remove or move so the arrow buttons know their edges.</summary>
    internal void SetPosition(int index, int count)
    {
        var isFirst = index == 0;
        var isLast = index == count - 1;
        if (_index == index && _isFirst == isFirst && _isLast == isLast) { return; }
        _index = index;
        _isFirst = isFirst;
        _isLast = isLast;
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(IsFirst));
        OnPropertyChanged(nameof(IsLast));
    }
    public Guid Id => _state.Id;
    public string VideoId => _state.VideoId;
    public string CachedPath => _state.CachedPath;

    /// <summary>
    /// The file's own extension, upper-cased for the chip on the row. A YouTube cache file is always
    /// m4a and the row never showed it; a TikTok list mixes mp3, m4a and wav, so the format earns
    /// its place there.
    /// </summary>
    public string Extension => string.IsNullOrEmpty(_state.CachedPath)
        ? string.Empty
        : Path.GetExtension(_state.CachedPath).TrimStart('.').ToUpperInvariant();

    /// <summary>Length of the track, shown on the row; a dash when it was never reported.</summary>
    public string DurationText
    {
        get
        {
            if (_state.Duration <= TimeSpan.Zero) { return "—"; }
            // The format string cannot live inside an interpolation hole, hence the local.
            var clock = _state.Duration.ToString(@"mm\:ss");
            return _state.Duration.TotalHours >= 1 ? $"{(int)_state.Duration.TotalHours}:{clock}" : clock;
        }
    }
    /// <summary>Records the length once the download reports it.</summary>
    public void SetDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || _state.Duration == duration) { return; }
        _state.Duration = duration;
        OnPropertyChanged(nameof(DurationText));
    }

    /// <summary>
    /// Reads the length from the cached file. Tracks saved before the duration was stored would
    /// otherwise show a dash forever, even though the search row knew the length all along.
    /// </summary>
    public void ReadDurationFromFile()
    {
        if (_state.Duration > TimeSpan.Zero || string.IsNullOrEmpty(_state.CachedPath)) { return; }
        if (!BeatzOnKeys.Core.Audio.AudioTrackLoader.TryLoad(_state.CachedPath, out var track, out _)) { return; }
        if (track is null || track.Duration <= TimeSpan.Zero) { return; }
        _state.Duration = track.Duration;
        OnPropertyChanged(nameof(DurationText));
    }

    /// <summary>
    /// Points the row at its file after the cache moved. A file that stayed behind keeps its old
    /// path, because the migrator only reports what it actually moved.
    /// </summary>
    public void Relocate(IReadOnlyDictionary<string, string> moved)
    {
        if (string.IsNullOrEmpty(_state.CachedPath)) { return; }
        if (!moved.TryGetValue(_state.CachedPath, out var destination)) { return; }
        _state.CachedPath = destination;
        OnPropertyChanged(nameof(CachedPath));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(TooltipText));
    }

    public string Title
    {
        get => _state.Title;
        set
        {
            if (_state.Title == value) { return; }
            _state.Title = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// True while the row's name is being typed. The list shows a field instead of the label, and
    /// nothing else about the row changes: the file on disk keeps its own name.
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (!SetProperty(ref _isEditing, value)) { return; }
            OnPropertyChanged(nameof(TitleVisibility));
            OnPropertyChanged(nameof(EditVisibility));
        }
    }

    private bool _isEditing;
    private string _editTitle = string.Empty;

    /// <summary>Visibility helpers, so the XAML needs no converter, as everywhere else here.</summary>
    public Visibility TitleVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EditVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The draft the field edits; committed only on Enter or on losing focus.</summary>
    public string EditTitle
    {
        get => _editTitle;
        set => SetProperty(ref _editTitle, TextInput.Sanitize(value));
    }

    /// <summary>Raised after a rename, accepted or not, so the owner can save and refilter.</summary>
    public event Action<YouTubeTrackViewModel>? EditFinished;

    /// <summary>Opens the field with the current name in it.</summary>
    public void BeginEdit()
    {
        if (_isEditing) { return; }
        _editTitle = Title;
        OnPropertyChanged(nameof(EditTitle));
        IsEditing = true;
    }

    /// <summary>
    /// Keeps the typed name. A blank one is not a name, so the old title simply stays; either way
    /// the editor closes and the owner is told, because the list may need a new order or filter.
    /// </summary>
    public bool CommitEdit()
    {
        if (!_isEditing) { return false; }
        var name = TextInput.Sanitize(_editTitle).Trim();
        var changed = name.Length > 0 && name != Title;
        if (changed) { Title = name; }
        IsEditing = false;
        EditFinished?.Invoke(this);
        return changed;
    }

    /// <summary>Closes the field without touching the name.</summary>
    public void CancelEdit()
    {
        if (!_isEditing) { return; }
        IsEditing = false;
        _editTitle = Title;
        OnPropertyChanged(nameof(EditTitle));
        EditFinished?.Invoke(this);
    }

    public YouTubeTrackStatus Status
    {
        get => _state.Status;
        set
        {
            if (_state.Status == value) { return; }
            _state.Status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(SubtitleText));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(StatusChipText));
            OnPropertyChanged(nameof(IsChipReady));
            OnPropertyChanged(nameof(IsChipFailed));
            OnPropertyChanged(nameof(IsChipBusy));
        }
    }

    public string? ErrorMessage
    {
        get => _state.ErrorMessage;
        set
        {
            _state.ErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TooltipText));
            OnPropertyChanged(nameof(SubtitleText));
        }
    }

    public double Progress
    {
        get => _progress;
        set { if (!SetProperty(ref _progress, value)) { return; } OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(StatusChipText)); }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (!SetProperty(ref _isDownloading, value)) { return; }
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(SubtitleText));
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(StatusChipText));
            OnPropertyChanged(nameof(IsChipBusy));
        }
    }

    public bool IsPending => !IsDownloading && Status == YouTubeTrackStatus.Pending;
    public bool IsReady => Status == YouTubeTrackStatus.Ready;
    public bool IsFailed => Status == YouTubeTrackStatus.Failed;

    /// <summary>Playable only when the file is really on disk; a stale cache row cannot be opened.</summary>
    public bool CanPlay => Status == YouTubeTrackStatus.Ready && !string.IsNullOrEmpty(CachedPath) && File.Exists(CachedPath);

    public string ProgressText => $"{Math.Round(Progress * 100)}%";

    /// <summary>Selected row, so the list can highlight it the way the local playlist does.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    /// <summary>Chip colour key: the row shows "Готово", "43%" or "Ошибка" without opening anything.</summary>
    public string StatusChipText => IsDownloading ? ProgressText : Loc.T(Status switch
    {
        YouTubeTrackStatus.Ready => "L_YtStatusReady",
        YouTubeTrackStatus.Failed => "L_YtStatusFailed",
        YouTubeTrackStatus.Downloading => "L_YtStatusDownloading",
        _ => "L_YtStatusPending"
    });

    public bool IsChipReady => !IsDownloading && Status == YouTubeTrackStatus.Ready;
    public bool IsChipFailed => !IsDownloading && Status == YouTubeTrackStatus.Failed;
    public bool IsChipBusy => IsDownloading || Status == YouTubeTrackStatus.Downloading;

    /// <summary>
    /// Clears everything a previous attempt left behind. Without this a retry of the same video
    /// keeps the old failure text and the row still reads "Ошибка" while it downloads again.
    /// </summary>
    public void ResetForRetry()
    {
        ErrorMessage = null;
        Progress = 0;
        Status = YouTubeTrackStatus.Pending;
        OnPropertyChanged(nameof(StatusChipText));
        OnPropertyChanged(nameof(IsChipReady));
        OnPropertyChanged(nameof(IsChipFailed));
        OnPropertyChanged(nameof(IsChipBusy));
    }

    public string StatusText => IsDownloading ? Loc.T("L_YtStatusDownloading") : Loc.T(Status switch
    {
        YouTubeTrackStatus.Ready => "L_YtStatusReady",
        YouTubeTrackStatus.Failed => "L_YtStatusFailed",
        YouTubeTrackStatus.Downloading => "L_YtStatusDownloading",
        _ => "L_YtStatusPending"
    });

    /// <summary>The one line under the title: a failure explains itself instead of saying "Ошибка".</summary>
    public string SubtitleText => IsFailed && !string.IsNullOrEmpty(ErrorMessage) ? ErrorMessage! : StatusText;
    public string TooltipText => ErrorMessage ?? CachedPath ?? VideoId;

    /// <summary>Marks a finished download; the row becomes playable and remembers its file.</summary>
    public void MarkReady(string cachedPath)
    {
        _state.CachedPath = cachedPath;
        ErrorMessage = null;
        Status = YouTubeTrackStatus.Ready;
        Progress = 1;
        OnPropertyChanged(nameof(CachedPath));
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(TooltipText));
    }

    /// <summary>Marks a failure and keeps the reason for the tooltip.</summary>
    public void MarkFailed(string message)
    {
        ErrorMessage = message;
        Status = YouTubeTrackStatus.Failed;
        OnPropertyChanged(nameof(CanPlay));
    }
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TooltipText));
    }
}