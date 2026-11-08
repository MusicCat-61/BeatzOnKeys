using System.IO;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.YouTube;
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.UI.Localization;

namespace BeatzOnKeys.UI.ViewModels;

/// <summary>Search, download and install: everything that talks to yt-dlp.</summary>
public sealed partial class YouTubeViewModel
{
    public int ActiveDownloads => Downloaded.Count(track => track.IsDownloading);

    public bool CanStartAnotherDownload => ActiveDownloads < YouTubeTrackService.MaxConcurrentDownloads;

    /// <summary>
    /// Stops the background cache run of the previous search and forgets it. The rows of that run
    /// are already gone from the list; letting it finish would only burn the download slots.
    /// </summary>
    private void CancelCacheLoop()
    {
        var loop = _cacheLoop;
        _cacheLoop = null;
        if (loop is null) { return; }
        try { loop.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { loop.Dispose(); }
    }

    /// <summary>
    /// Runs the cache pass with its own token, linked to the lifetime so Dispose still stops it,
    /// and rethrows nothing: a cancelled pass is the normal way a new search takes over.
    /// </summary>
    private async Task StartCacheLoopAsync()
    {
        CancelCacheLoop();
        var loop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _cacheLoop = loop;
        try { await CacheResultsAsync(loop.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        // Fire-and-forget must not leave an unobserved task: a failure here is logged like any
        // other, otherwise the pass would die silently and the rows would stay "caching".
        catch (Exception exception)
        {
            AppLogger.Error("youtube", "The background cache pass failed.", exception);
        }
        finally
        {
            if (ReferenceEquals(_cacheLoop, loop)) { _cacheLoop = null; }
            loop.Dispose();
        }
    }

    /// <summary>
    /// Cancels whatever is running: a search, the background cache pass, or a download started from
    /// the field. One button, because the user pressing it wants "stop", not a choice of what to stop.
    /// </summary>
    public RelayCommand CancelCommand => _cancel ??= new RelayCommand(CancelCurrentWork,
        () => IsSearching || IsDownloading || IsQueryDownloading || _cacheLoop is not null);

    private RelayCommand? _cancel;

    /// <summary>Owns the token of the running search, so Cancel can stop it without a restart.</summary>
    private CancellationTokenSource? _searchCancellation;

    private void CancelCurrentWork()
    {
        var wasSearching = IsSearching;
        var wasCaching = _cacheLoop is not null;
        try { _searchCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        CancelCacheLoop();
        // The status is set here as well as in the catch: cancelling the cache pass has no
        // exception to report, and a silent stop would look like a hang.
        if (wasSearching) { StatusText = Loc.T("L_YtSearchCancelled"); }
        else if (wasCaching) { StatusText = Loc.T("L_YtCachingCancelled"); }
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The second button next to the field: a pasted link downloads without a search.</summary>
    public AsyncRelayCommand DownloadFromQueryCommand => _downloadFromQuery ??= new AsyncRelayCommand(
        () => DownloadFromQueryAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(SearchQuery));

    private AsyncRelayCommand? _downloadFromQuery;

    private async Task SearchAsync()
    {
        var query = SearchQuery.Trim();
        if (query.Length == 0 || IsSearching) { return; }

        // A pasted link is not a search phrase: go straight to the download.
        if (YouTubeUrls.LooksLikeLink(query))
        {
            await DownloadFromQueryAsync().ConfigureAwait(true);
            return;
        }

        // A second search must not share the download slots with the first one: the old run is
        // still caching rows the user no longer sees, and those slots are what the new rows wait for.
        CancelCacheLoop();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCancellation = cancellation;
        IsSearching = true;
        Results.Clear();
        StatusText = Loc.T("L_YtSearching");
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyResults));
        try
        {
            var installProgress = new Progress<double>(value =>
            {
                InstallProgress = value;
                StatusText = Loc.F("L_YtInstallingFormat", Math.Round(value * 100));
            });
            var found = await _service
                .SearchOrInstallAsync(query, 10, installProgress, cancellation.Token)
                .ConfigureAwait(true);
            RefreshBinaryState();
            foreach (var item in found)
            {
                // A fresh search can list something already saved: it starts as a check mark.
                Results.Add(new YouTubeSearchItemViewModel(item)
                {
                    IsAdded = Downloaded.Any(track => track.VideoId == item.VideoId)
                });
            }
            StatusText = found.Count == 0 ? Loc.T("L_YtNotFound") : Loc.F("L_YtFoundFormat", found.Count);
            // Results are fetched right away, so pressing play works at once and the user hears
            // what the row actually is before deciding to keep it.
            _ = StartCacheLoopAsync();
        }
        catch (OperationCanceledException) { StatusText = string.Empty; }
        catch (YouTubeException exception)
        {
            StatusText = Loc.T(FailureKey(exception.Failure));
            AppLogger.Error("youtube", "The search failed.", exception);
        }
        catch (Exception exception)
        {
            StatusText = Loc.T("L_YtSearchFailed");
            AppLogger.Error("youtube", "The search failed unexpectedly.", exception);
        }
        finally
        {
            IsSearching = false;
            InstallProgress = 0;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(ShowEmptyResults));
            RefreshCommands();
        }
    }

    /// <summary>Downloads whatever link sits in the field, or explains why it cannot.</summary>
    private async Task DownloadFromQueryAsync()
    {
        var text = SearchQuery.Trim();
        if (text.Length == 0 || IsBusy) { return; }
        if (!YouTubeUrls.TryParse(text, out var videoId, out var problem))
        {
            StatusText = Loc.T(problem switch
            {
                YouTubeUrlProblem.Playlist => "L_YtFailurePlaylist",
                YouTubeUrlProblem.Channel => "L_YtFailureChannel",
                YouTubeUrlProblem.NotYouTube => "L_YtFailureNotLink",
                _ => "L_YtFailureNotLink"
            });
            return;
        }

        // A link download happens right here, so the field itself has to show how far it is.
        IsQueryDownloading = true;
        QueryProgress = 0;
        try
        {
            await DownloadAsync(videoId, text, value => QueryProgress = value).ConfigureAwait(true);
        }
        finally
        {
            IsQueryDownloading = false;
            QueryProgress = 0;
        }
    }

    /// <summary>Starts a download and drives one row of the cached list to its final state.</summary>
    private async Task DownloadAsync(string videoId, string title, Action<double>? progressReport = null)
    {
        if (IsInstalling) { return; }
        var row = Downloaded.FirstOrDefault(track => track.VideoId == videoId);
        if (row is { IsDownloading: true })
        {
            StatusText = Loc.T("L_YtAlreadyDownloading");
            return;
        }

        if (!CanStartAnotherDownload)
        {
            StatusText = Loc.F("L_YtQueueFullFormat", YouTubeTrackService.MaxConcurrentDownloads);
            return;
        }

        if (row is null)
        {
            row = new YouTubeTrackViewModel(new YouTubeTrackState
            {
                VideoId = videoId,
                Title = string.IsNullOrWhiteSpace(title) || title.Contains("://", StringComparison.Ordinal)
                    ? videoId
                    : title,
                Status = YouTubeTrackStatus.Pending
            });
            Downloaded.Insert(0, row);
            OnPropertyChanged(nameof(HasDownloaded));
            _requestSave();
        }

        row.IsDownloading = true;
        row.Status = YouTubeTrackStatus.Downloading;
        row.Progress = 0;
        row.ErrorMessage = null;
        SelectTrack(row);
        StatusText = Loc.F("L_YtDownloadingFormat", row.Title);
        RefreshCommands();

        try
        {
            var progress = new Progress<double>(value => { row.Progress = value; progressReport?.Invoke(value); });
            var result = await _service.DownloadAsync(videoId, progress, _lifetime.Token).ConfigureAwait(true);
            // A cached file comes back without a title; the row keeps the one it already had.
            if (!string.IsNullOrWhiteSpace(result.Title)) { row.Title = result.Title; }
            row.SetDuration(result.Duration);
            row.MarkReady(result.CachedPath);
            // The same video may still be listed in the search results; its button has to turn into
            // a check, or a second press would download the file again.
            foreach (var item in Results.Where(candidate => candidate.VideoId == videoId)) { item.IsAdded = true; }
            StatusText = Loc.F("L_YtDownloadedFormat", row.Title);
            NotifyCountChanged();
        }
        catch (OperationCanceledException)
        {
            row.MarkFailed(Loc.T("L_YtFailureCancelled"));
            StatusText = Loc.T("L_YtFailureCancelled");
            NotifyCountChanged();
        }
        catch (YouTubeException exception)
        {
            row.MarkFailed(Loc.T(FailureKey(exception.Failure)));
            StatusText = Loc.T(FailureKey(exception.Failure));
            NotifyCountChanged();
            AppLogger.Warning("youtube", $"The download of {videoId} failed: {exception.Message}");
        }
        catch (Exception exception)
        {
            row.MarkFailed(Loc.T("L_YtFailureUnknown"));
            StatusText = Loc.T("L_YtFailureUnknown");
            NotifyCountChanged();
            AppLogger.Error("youtube", $"The download of {videoId} failed unexpectedly.", exception);
        }
        finally
        {
            row.IsDownloading = false;
            RefreshBinaryState();
            RefreshCommands();
            _requestSave();
            await RefreshCacheSizeAsync().ConfigureAwait(true);
        }
    }

    public RelayCommand<YouTubeSearchItemViewModel> PreviewCommand => _previewCommand ??= new RelayCommand<YouTubeSearchItemViewModel>(
        item => { if (item is not null) { Preview(item); } },
        item => item is { CanPlay: true });

    private RelayCommand<YouTubeSearchItemViewModel>? _previewCommand;


    /// <summary>
    /// Asks the window to pause or continue whatever is playing. The view model cannot reach the
    /// player, and the row button is the only place that needs the toggle.
    /// </summary>
    public event Action? PauseToggleRequested;

    /// <summary>The result whose file is in the player right now, if it came from the search list.</summary>
    private YouTubeSearchItemViewModel? _activePreview;

    /// <summary>
    /// Mirrors the player state onto the rows. Called on every progress tick, so it does nothing
    /// unless something actually changed.
    /// </summary>
    public void SyncPreviewPlayback(bool isCurrent, bool isPlaying, bool isPaused)
    {
        if (!isCurrent)
        {
            if (_activePreview is null) { return; }
            _activePreview.IsPlaying = false;
            _activePreview.IsPaused = false;
            _activePreview = null;
            return;
        }

        if (_activePreview is null) { return; }
        // A paused preview still owns the player, so the row keeps its marker: only a stopped
        // track releases it. Otherwise the row would fall back to a plain play button the moment
        // it was paused, and the next press would look like a fresh start.
        _activePreview.IsPlaying = isPlaying || isPaused;
        _activePreview.IsPaused = isPaused;
    }

    /// <summary>
    /// How long one row may hold the background pass. A preview nobody asked for must not keep a
    /// download slot for ten minutes: a deliberate click keeps the full service timeout instead.
    /// </summary>
    /// <summary>Mutable only so the smoke suite can shorten it; production never changes it.</summary>
    internal static TimeSpan CachePassTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fetches every result into the cache in the background, one after another. Sequential on
    /// purpose: the service already limits parallel work, and a burst of ten requests is exactly
    /// what gets a client rate limited. A row that takes too long is set aside and retried once
    /// at the end, so one slow track cannot stall the rest of the list.
    /// </summary>
    private async Task CacheResultsAsync(CancellationToken cancellationToken)
    {
        var skipped = new List<YouTubeSearchItemViewModel>();
        await CacheRowsAsync(Results.ToList(), skipped, cancellationToken).ConfigureAwait(true);
        if (skipped.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            // One second chance, not a loop: a row that is slow twice is slow for a reason, and
            // the buttons on it still download with the full timeout when the user asks.
            foreach (var item in skipped) { item.Skipped = false; }
            var stillSkipped = new List<YouTubeSearchItemViewModel>();
            await CacheRowsAsync(skipped, stillSkipped, cancellationToken).ConfigureAwait(true);
            foreach (var item in stillSkipped) { item.Skipped = true; }
        }

        await RefreshCacheSizeAsync().ConfigureAwait(true);
        // The preview bytes are counted separately from the kept tracks.
        await RefreshSearchCacheSizeAsync().ConfigureAwait(true);
        RefreshBinaryState();
    }

    /// <summary>One sequential pass over the given rows, collecting the ones that timed out.</summary>
    private async Task CacheRowsAsync(
        IReadOnlyList<YouTubeSearchItemViewModel> rows,
        List<YouTubeSearchItemViewModel> skipped,
        CancellationToken cancellationToken)
    {
        foreach (var item in rows)
        {
            if (cancellationToken.IsCancellationRequested) { return; }
            // Only live streams are skipped here: CanDownload waits for the cache, so testing it
            // in the loop that creates the cache would skip every row forever.
            if (item.CanPlay || item.IsLive) { continue; }
            // A separate token per row: the pass keeps going when one row gives up, and a real
            // cancellation of the whole pass is still told apart from a slow row.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(CachePassTimeout);
            try
            {
                item.IsDownloading = true;
                item.Progress = 0;
                var progress = new Progress<double>(value => item.Progress = value);
                var result = await _service.DownloadAsync(item.VideoId, progress, attempt.Token).ConfigureAwait(true);
                item.CachedPath = result.CachedPath;
                item.CacheFailed = false;
                item.Skipped = false;
                // The row button is driven by the command, and a command re-checks only when it
                // is told to: the binding on CanDownload alone leaves it disabled.
                DownloadCommand.NotifyCanExecuteChanged();
                // The occupied space is shown while the list fills, not only at the very end.
                await RefreshCacheSizeAsync().ConfigureAwait(true);
                await RefreshSearchCacheSizeAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Only this row ran out of time; the pass moves on and comes back to it later.
                item.Skipped = true;
                skipped.Add(item);
                AppLogger.Info("youtube", $"Left {item.VideoId} for later: the preview took too long.");
            }
            catch (Exception exception)
            {
                item.CacheFailed = true;
                AppLogger.Warning("youtube", $"Could not cache the search result {item.VideoId}: {exception.Message}");
            }
            finally
            {
                item.IsDownloading = false;
                item.Progress = 0;
                PreviewCommand.NotifyCanExecuteChanged();
                DownloadCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Plays a result that is already cached. Nothing is downloaded here: if the file is not on
    /// disk yet the button stays disabled, so listening never waits on the network.
    /// </summary>
    private void Preview(YouTubeSearchItemViewModel item)
    {
        if (!item.CanPlay) { return; }
        // The same row twice means "stop", not "start again from the top": that is the only
        // thing a second click on a playing track can reasonably mean.
        if (ReferenceEquals(item, _activePreview))
        {
            PauseToggleRequested?.Invoke();
            return;
        }

        // The row that was playing gives up the marker, or it would keep showing a pause button
        // for a track the player no longer holds.
        if (_activePreview is not null)
        {
            _activePreview.IsPlaying = false;
            _activePreview.IsPaused = false;
        }
        _activePreview = item;
        item.IsPlaying = true;
        item.IsPaused = false;
        PlayRequested?.Invoke(item.CachedPath!, item.Title, true);
        StatusText = Loc.F("L_YtPreviewingFormat", item.Title);
    }

    private async Task InstallYtDlpAsync()
    {
        if (IsInstalling) { return; }
        IsInstalling = true;
        InstallProgress = 0;
        StatusText = Loc.F("L_YtInstallingFormat", 0);
        try
        {
            var progress = new Progress<double>(value =>
            {
                InstallProgress = value;
                StatusText = Loc.F("L_YtInstallingFormat", Math.Round(value * 100));
            });
            // Repair and update replace the file; a plain download only fills a gap.
            var force = _ytDlpState is YtDlpState.Broken or YtDlpState.UpdateAvailable;
            await _service.InstallYtDlpAsync(progress, _lifetime.Token, force).ConfigureAwait(true);
            RefreshBinaryState();
            StatusText = Loc.T("L_YtInstallDone");
            await RefreshBinaryVersionAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { StatusText = string.Empty; }
        catch (Exception exception)
        {
            StatusText = Loc.T("L_YtInstallFailed");
            AppLogger.Error("youtube", "Could not download yt-dlp.", exception);
            // The button stays available, so the message points at it as the retry.
            _userMessage(Loc.T("L_YtInstallFailedHint"));
        }
        finally
        {
            IsInstalling = false;
            InstallProgress = 0;
            RefreshCommands();
        }
    }

    /// <summary>Maps a classified failure to its translation key; the wording lives in the dictionary.</summary>
    internal static string FailureKey(YouTubeFailure failure) => failure switch
    {
        YouTubeFailure.YtDlpMissing => "L_YtFailureYtDlpMissing",
        YouTubeFailure.Restricted => "L_YtFailureRestricted",
        YouTubeFailure.Timeout => "L_YtFailureTimeout",
        YouTubeFailure.Cancelled => "L_YtFailureCancelled",
        YouTubeFailure.Playlist => "L_YtFailurePlaylist",
        YouTubeFailure.LiveStream => "L_YtFailureLive",
        YouTubeFailure.UnsupportedAudio => "L_YtFailureUnsupported",
        YouTubeFailure.FfmpegMissing => "L_YtFailureNeedsFfmpeg",
        YouTubeFailure.DownloadFailed => "L_YtFailureDownload",
        YouTubeFailure.NoResult => "L_YtFailureNoResult",
        _ => "L_YtFailureUnknown"
    };
}
