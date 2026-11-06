using System.IO;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.UI.Localization;

namespace BeatzOnKeys.UI.ViewModels;

/// <summary>One row of the search results; "Скачать" is a plain command on the row.</summary>
public sealed class YouTubeSearchItemViewModel(YouTubeSearchResult result)
{
    public string VideoId { get; } = result.VideoId;
    public string Title { get; } = result.Title;
    public string Author { get; } = result.Author;
    public string DurationText { get; } = result.DurationText;
    public bool IsLive { get; } = result.IsLive;

    /// <summary>Live streams cannot be cached, so the row explains it instead of offering a download.</summary>
    public bool CanDownload => !IsLive;

    public string LiveBadgeText => Loc.T("L_YtLive");
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
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(CanPlay));
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
        }
    }

    public double Progress
    {
        get => _progress;
        set { if (!SetProperty(ref _progress, value)) { return; } OnPropertyChanged(nameof(ProgressText)); }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (!SetProperty(ref _isDownloading, value)) { return; }
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsPending));
        }
    }

    public bool IsPending => !IsDownloading && Status == YouTubeTrackStatus.Pending;
    public bool IsReady => Status == YouTubeTrackStatus.Ready;
    public bool IsFailed => Status == YouTubeTrackStatus.Failed;

    /// <summary>Playable only when the file is really on disk; a stale cache row cannot be opened.</summary>
    public bool CanPlay => Status == YouTubeTrackStatus.Ready && !string.IsNullOrEmpty(CachedPath) && File.Exists(CachedPath);

    public string ProgressText => $"{Math.Round(Progress * 100)}%";

    public string StatusText => IsDownloading ? Loc.T("L_YtStatusDownloading") : Loc.T(Status switch
    {
        YouTubeTrackStatus.Ready => "L_YtStatusReady",
        YouTubeTrackStatus.Failed => "L_YtStatusFailed",
        YouTubeTrackStatus.Downloading => "L_YtStatusDownloading",
        _ => "L_YtStatusPending"
    });

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