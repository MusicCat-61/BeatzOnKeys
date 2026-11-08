using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.YouTube;
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.UI.Localization;

namespace BeatzOnKeys.UI.ViewModels;

/// <summary>
/// The TikTok section: paste a link, get a file. Deliberately smaller than the YouTube section:
/// there is no search, no cookie, no proxy and no binary to install, so the field is the page.
/// </summary>
public sealed class TikTokViewModel : ObservableObject
{
    private readonly ITikTokAudioService _service;
    private readonly Action _requestSave;
    private readonly Action<string> _userMessage;
    private readonly TikTokAudioOptions _options = new();
    private CancellationTokenSource? _downloadCancellation;
    private string _inputUrl = string.Empty;
    private string _statusText = string.Empty;
    private string _cacheSizeText = string.Empty;
    private double _progress;
    private bool _isBusy;
    private bool _disposed;

    public TikTokViewModel(
        ITikTokAudioService service,
        IEnumerable<YouTubeTrackState> savedTracks,
        Action requestSave,
        Action<string> userMessage)
    {
        _service = service;
        _requestSave = requestSave;
        _userMessage = userMessage;
        foreach (var state in savedTracks) { Tracks.Add(new YouTubeTrackViewModel(state)); }

        AddLinkCommand = new AsyncRelayCommand(AddLinkAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(InputUrl));
        CancelDownloadCommand = new RelayCommand(CancelDownload, () => IsBusy);
        PlayCommand = new RelayCommand<YouTubeTrackViewModel>(
            track => { if (track is not null) { PlayRequested?.Invoke(track.CachedPath, track.Title); } },
            track => track is { CanPlay: true });
        RemoveCachedCommand = new RelayCommand<YouTubeTrackViewModel>(RemoveCached, track => track is not null);
        ClearCacheCommand = new RelayCommand(() => _ = ClearCacheAsync(), () => !IsBusy && Tracks.Count > 0);
        MoveTrackUpCommand = new RelayCommand<YouTubeTrackViewModel>(
            track => MoveTrack(track, -1), track => CanMove(track, -1));
        MoveTrackDownCommand = new RelayCommand<YouTubeTrackViewModel>(
            track => MoveTrack(track, 1), track => CanMove(track, 1));
        OpenMusicFolderCommand = new RelayCommand(() => OpenMusicFolderRequested?.Invoke());
        // The plate is advice, not a modal: it can be put away without acting on it.
        DismissNetworkHintCommand = new RelayCommand(() => ShowNetworkHint = false);
        Tracks.CollectionChanged += (_, _) => NotifyCountChanged();
        RefreshPositions();
    }

    /// <summary>Raised with the path and title of a file to play; the window owns the player.</summary>
    public event Action<string, string>? PlayRequested;

    public event Action? OpenMusicFolderRequested;

    /// <summary>Asks before a destructive step; the window owns the dialog.</summary>
    public Func<string, string, bool>? ConfirmRequested { get; set; }

    public ObservableCollection<YouTubeTrackViewModel> Tracks { get; } = [];

    public AsyncRelayCommand AddLinkCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> PlayCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> RemoveCachedCommand { get; }
    public RelayCommand ClearCacheCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> MoveTrackUpCommand { get; }
    public RelayCommand<YouTubeTrackViewModel> MoveTrackDownCommand { get; }
    public RelayCommand OpenMusicFolderCommand { get; }
    public RelayCommand DismissNetworkHintCommand { get; }

    public string InputUrl
    {
        get => _inputUrl;
        set
        {
            if (!SetProperty(ref _inputUrl, value ?? string.Empty)) { return; }
            AddLinkCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanDownload));
        }
    }

    public string InputHint => Loc.T("L_TikTokInputHint");

    public string DownloadText => Loc.T("L_TikTokDownload");

    public string CancelText => Loc.T("L_Cancel");

    public bool CanDownload => !IsBusy && !string.IsNullOrWhiteSpace(InputUrl);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CacheSizeText
    {
        // The label must never be blank: before the first measurement, or after a refresh that could
        // not run, the card would otherwise lose its heading and look like a broken section.
        get => string.IsNullOrEmpty(_cacheSizeText) ? ZeroCacheLabel() : _cacheSizeText;
        private set => SetProperty(ref _cacheSizeText, value);
    }

    private static string ZeroCacheLabel() => Loc.F("L_TikTokCacheLabel",
        ByteSizeFormatter.Format(0, Loc.T("L_YtKb"), Loc.T("L_YtMb"), Loc.T("L_YtGb")));

    public double Progress
    {
        get => _progress;
        private set
        {
            if (!SetProperty(ref _progress, value)) { return; }
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{Math.Round(Progress * 100)}%";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) { return; }
            OnPropertyChanged(nameof(CancelVisible));
            OnPropertyChanged(nameof(CanDownload));
            AddLinkCommand.NotifyCanExecuteChanged();
            CancelDownloadCommand.NotifyCanExecuteChanged();
            ClearCacheCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The cancel button sits beside the field and only exists while something runs.</summary>
    public Visibility CancelVisible => _isBusy ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Shown only when the network itself refused the request. It is not advice shown to everybody:
    /// it appears after a real failure, because telling people to look at their connection while
    /// everything works is noise.
    /// </summary>
    public Visibility NetworkHintVisible =>
        _showNetworkHint ? Visibility.Visible : Visibility.Collapsed;

    private bool _showNetworkHint;

    public bool ShowNetworkHint
    {
        get => _showNetworkHint;
        private set
        {
            if (!SetProperty(ref _showNetworkHint, value)) { return; }
            OnPropertyChanged(nameof(NetworkHintVisible));
        }
    }

    public bool HasTracks => Tracks.Count > 0;

    public int TrackCount => Tracks.Count;

    public string TrackCountNumber => Tracks.Count.ToString(System.Globalization.CultureInfo.CurrentUICulture);

    /// <summary>The folder the files land in, spelled out so nobody has to guess.</summary>
    public string CacheHintText => Loc.F("L_TikTokCacheHint", _options.MusicDirectory);

    private YouTubeTrackViewModel? _selected;

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
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTrackTitle));
            PlayCommand.NotifyCanExecuteChanged();
            RemoveCachedCommand.NotifyCanExecuteChanged();
            MoveTrackUpCommand.NotifyCanExecuteChanged();
            MoveTrackDownCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasSelection => _selected is not null;

    public string SelectedTrackTitle => _selected?.Title ?? string.Empty;

    public void SelectTrack(YouTubeTrackViewModel? track) => SelectedTrack = track;

    public async Task InitializeAsync()
    {
        foreach (var track in Tracks) { track.ReadDurationFromFile(); }
        await RefreshCacheSizeAsync().ConfigureAwait(true);
    }

    private async Task AddLinkAsync()
    {
        var text = InputUrl.Trim();
        if (text.Length == 0 || IsBusy) { return; }
        if (!MediaUrl.TryParse(text, out var mediaId, out var platform, out var problem))
        {
            StatusText = Loc.T(problem switch
            {
                MediaUrlProblem.Empty => "L_TikTokFailureEmpty",
                MediaUrlProblem.Channel => "L_TikTokFailureProfile",
                _ => "L_TikTokFailureNotTikTok"
            });
            return;
        }

        if (platform != MediaPlatform.TikTok)
        {
            StatusText = Loc.T("L_TikTokFailureNotTikTok");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        IsBusy = true;
        Progress = 0;
        // A fresh attempt clears the previous verdict; it comes back only if the network refuses again.
        ShowNetworkHint = false;
        StatusText = Loc.T("L_TikTokDownloading");
        YouTubeTrackViewModel? row = null;
        try
        {
            var progress = new Progress<double>(value => Progress = value);
            var result = await _service
                .DownloadAsync(mediaId, text, _options, progress, cancellation.Token)
                .ConfigureAwait(true);

            row = Tracks.FirstOrDefault(track => track.VideoId == result.VideoId);
            if (row is null)
            {
                row = new YouTubeTrackViewModel(new YouTubeTrackState
                {
                    VideoId = result.VideoId,
                    Title = string.IsNullOrWhiteSpace(result.Title) ? result.VideoId : result.Title,
                    Status = YouTubeTrackStatus.Pending
                });
                Tracks.Insert(0, row);
            }

            row.Title = result.Title;
            row.MarkReady(result.CachedPath);
            row.ReadDurationFromFile();
            SelectTrack(row);
            InputUrl = string.Empty;
            StatusText = Loc.F("L_TikTokDownloadedFormat", row.Title);
            _requestSave();
            await RefreshCacheSizeAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.T("L_TikTokCancelled");
        }
        catch (TikTokException exception)
        {
            StatusText = Loc.T(FailureKey(exception.Failure));
            // The plate is an offer, not a diagnosis shouted at everyone: it only appears when the
            // network really did refuse, which the service reports as its own kind.
            ShowNetworkHint = exception.Failure == TikTokFailure.NetworkBlocked;
            if (exception.Failure != TikTokFailure.Cancelled)
            {
                AppLogger.Warning("tiktok", $"The TikTok download failed: {exception.Message}");
            }
        }
        catch (Exception exception)
        {
            StatusText = Loc.T("L_TikTokFailureDownload");
            AppLogger.Error("tiktok", "The TikTok download failed unexpectedly.", exception);
        }
        finally
        {
            if (ReferenceEquals(_downloadCancellation, cancellation)) { _downloadCancellation = null; }
            cancellation.Dispose();
            Progress = 0;
            IsBusy = false;
            RefreshPositions();
        }
    }

    /// <summary>One sentence per failure, so "HTTP 403" never reaches the user.</summary>
    internal static string FailureKey(TikTokFailure failure) => failure switch
    {
        TikTokFailure.NotTikTokUrl => "L_TikTokFailureNotTikTok",
        TikTokFailure.VideoNotFound => "L_TikTokFailureNotFound",
        TikTokFailure.Restricted => "L_TikTokFailureRestricted",
        TikTokFailure.NoAudio => "L_TikTokFailureNoAudio",
        TikTokFailure.UnsupportedAudio => "L_TikTokFailureUnsupported",
        TikTokFailure.TooLarge => "L_TikTokFailureTooLarge",
        TikTokFailure.Timeout => "L_TikTokFailureTimeout",
        TikTokFailure.Cancelled => "L_TikTokCancelled",
        TikTokFailure.NetworkBlocked => "L_TikTokFailureNetwork",
        TikTokFailure.ChallengeFailed => "L_TikTokFailureChallenge",
        _ => "L_TikTokFailureDownload"
    };

    private void CancelDownload()
    {
        try { _downloadCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        StatusText = Loc.T("L_TikTokCancelled");
    }

    private void RemoveCached(YouTubeTrackViewModel? track)
    {
        if (track is null) { return; }
        var index = Tracks.IndexOf(track);
        if (index < 0) { return; }
        try
        {
            if (!string.IsNullOrEmpty(track.CachedPath) && File.Exists(track.CachedPath))
            {
                File.Delete(track.CachedPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _userMessage(Loc.F("L_TikTokDeleteFailedFormat", exception.Message));
            return;
        }

        Tracks.RemoveAt(index);
        if (ReferenceEquals(SelectedTrack, track)) { SelectTrack(Tracks.Count > 0 ? Tracks[Math.Min(index, Tracks.Count - 1)] : null); }
        RefreshPositions();
        StatusText = Loc.T("L_TikTokRemoved");
        _requestSave();
        _ = RefreshCacheSizeAsync();
    }

    public async Task ClearCacheAsync()
    {
        if (Tracks.Count == 0 || IsBusy) { return; }
        if (ConfirmRequested is not null && !ConfirmRequested(Loc.T("L_TikTokClearCacheConfirm"), Loc.T("L_TikTokClearCache")))
        {
            return;
        }

        var files = Tracks
            .Select(track => track.CachedPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var failures = 0;
        foreach (var path in files)
        {
            try { if (File.Exists(path)) { File.Delete(path); } }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures++;
                AppLogger.Warning("tiktok", $"Could not delete {path}: {exception.Message}");
            }
        }

        Tracks.Clear();
        SelectTrack(null);
        RefreshPositions();
        StatusText = failures == 0 ? Loc.T("L_TikTokCleared") : Loc.F("L_TikTokClearPartialFormat", failures);
        _requestSave();
        await RefreshCacheSizeAsync().ConfigureAwait(true);
    }

    public async Task RefreshCacheSizeAsync()
    {
        try
        {
            var bytes = await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(_options.MusicDirectory)) { return 0L; }
                    return Directory.EnumerateFiles(_options.MusicDirectory)
                        .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        .Sum(path =>
                        {
                            try { return new FileInfo(path).Length; }
                            catch (IOException) { return 0L; }
                        });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return 0L;
                }
            }).ConfigureAwait(true);
            CacheSizeText = Loc.F("L_TikTokCacheLabel",
                ByteSizeFormatter.Format(bytes, Loc.T("L_YtKb"), Loc.T("L_YtMb"), Loc.T("L_YtGb")));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Fire-and-forget callers cannot see this, and a section without its heading looks
            // broken, so the label is restored rather than left as it was.
            CacheSizeText = ZeroCacheLabel();
            AppLogger.Warning("tiktok", $"Could not measure the TikTok cache: {exception.Message}");
        }
    }

    public IReadOnlyList<YouTubeTrackState> ToStates() => Tracks.Select(track => track.State).ToList();

    /// <summary>
    /// Replaces the list with the one library.json holds. Used once, after the library is read;
    /// the rows are rebuilt so a row's view state cannot outlive its track.
    /// </summary>
    internal void ReplaceTracks(IEnumerable<YouTubeTrackState> tracks)
    {
        Tracks.Clear();
        foreach (var state in tracks) { Tracks.Add(new YouTubeTrackViewModel(state)); }
        SelectTrack(null);
        RefreshPositions();
    }

    private bool CanMove(YouTubeTrackViewModel? track, int offset)
    {
        if (track is null) { return false; }
        var index = Tracks.IndexOf(track);
        var target = index + offset;
        return index >= 0 && target >= 0 && target < Tracks.Count;
    }

    private void MoveTrack(YouTubeTrackViewModel? track, int offset)
    {
        if (track is null) { return; }
        var index = Tracks.IndexOf(track);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Tracks.Count) { return; }
        Tracks.Move(index, target);
        RefreshPositions();
        _requestSave();
    }

    /// <summary>Called after any add, remove or move so the arrow buttons know their edges.</summary>
    private void RefreshPositions()
    {
        for (var index = 0; index < Tracks.Count; index++) { Tracks[index].SetPosition(index, Tracks.Count); }
        MoveTrackUpCommand.NotifyCanExecuteChanged();
        MoveTrackDownCommand.NotifyCanExecuteChanged();
        RemoveCachedCommand.NotifyCanExecuteChanged();
        ClearCacheCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCountChanged()
    {
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TrackCount));
        OnPropertyChanged(nameof(TrackCountNumber));
        ClearCacheCommand.NotifyCanExecuteChanged();
    }

    public void RefreshLocalization()
    {
        foreach (var track in Tracks) { track.RefreshLocalization(); }
        OnPropertyChanged(nameof(InputHint));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(CacheHintText));
        _ = RefreshCacheSizeAsync();
        RefreshPositions();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        try { _downloadCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        _downloadCancellation?.Dispose();
        _downloadCancellation = null;
    }
}
