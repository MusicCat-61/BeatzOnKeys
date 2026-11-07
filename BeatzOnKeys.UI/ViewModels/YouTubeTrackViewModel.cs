using System.IO;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.UI.Infrastructure;
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

    /// <summary>Live streams cannot be cached, so the row explains it instead of offering a download.</summary>
    public bool CanDownload => !IsLive;

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