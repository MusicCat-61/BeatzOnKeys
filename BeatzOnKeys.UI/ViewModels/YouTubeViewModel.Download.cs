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
                .SearchOrInstallAsync(query, 10, installProgress, _lifetime.Token)
                .ConfigureAwait(true);
            RefreshBinaryState();
            foreach (var item in found) { Results.Add(new YouTubeSearchItemViewModel(item)); }
            StatusText = found.Count == 0 ? Loc.T("L_YtNotFound") : Loc.F("L_YtFoundFormat", found.Count);
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

        await DownloadAsync(videoId, text).ConfigureAwait(true);
    }

    /// <summary>Starts a download and drives one row of the cached list to its final state.</summary>
    private async Task DownloadAsync(string videoId, string title)
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
            var progress = new Progress<double>(value => row.Progress = value);
            var result = await _service.DownloadAsync(videoId, progress, _lifetime.Token).ConfigureAwait(true);
            row.Title = result.Title;
            row.MarkReady(result.CachedPath);
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
        item => { if (item is not null) { _ = PreviewAsync(item); } },
        item => item is { CanDownload: true, IsDownloading: false } && CanStartAnotherDownload);

    private RelayCommand<YouTubeSearchItemViewModel>? _previewCommand;

    /// <summary>
    /// Plays a search result: it is fetched first when it is not cached yet, then handed to the
    /// player. That is the only way to hear what a result actually is before keeping it.
    /// </summary>
    private async Task PreviewAsync(YouTubeSearchItemViewModel item)
    {
        if (item.IsDownloading || IsInstalling) { return; }
        if (!CanStartAnotherDownload)
        {
            StatusText = Loc.F("L_YtQueueFullFormat", YouTubeTrackService.MaxConcurrentDownloads);
            return;
        }

        var existing = Downloaded.FirstOrDefault(track => track.VideoId == item.VideoId);
        if (existing is { CanPlay: true })
        {
            SelectTrack(existing);
            PlayRequested?.Invoke(existing.CachedPath, existing.Title);
            return;
        }

        item.IsDownloading = true;
        item.Progress = 0;
        StatusText = Loc.F("L_YtDownloadingFormat", item.Title);
        RefreshCommands();
        try
        {
            var progress = new Progress<double>(value => item.Progress = value);
            var result = await _service.DownloadAsync(item.VideoId, progress, _lifetime.Token).ConfigureAwait(true);
            // Listening does not keep the track: it is only added to "Скачанные" when the user
            // presses Download afterwards, which then reuses the file already in the cache.
            await RefreshCacheSizeAsync().ConfigureAwait(true);
            PlayRequested?.Invoke(result.CachedPath, result.Title);
            StatusText = Loc.F("L_YtPreviewingFormat", result.Title);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.T("L_YtFailureCancelled");
        }
        catch (YouTubeException exception)
        {
            StatusText = Loc.T(FailureKey(exception.Failure));
            AppLogger.Warning("youtube", $"The preview of {item.VideoId} failed: {exception.Message}");
        }
        catch (Exception exception)
        {
            StatusText = Loc.T("L_YtFailureUnknown");
            AppLogger.Error("youtube", $"The preview of {item.VideoId} failed unexpectedly.", exception);
        }
        finally
        {
            item.IsDownloading = false;
            item.Progress = 0;
            RefreshBinaryState();
            RefreshCommands();
        }
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
            await _service.InstallYtDlpAsync(progress, _lifetime.Token).ConfigureAwait(true);
            RefreshBinaryState();
            StatusText = Loc.T("L_YtInstallDone");
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