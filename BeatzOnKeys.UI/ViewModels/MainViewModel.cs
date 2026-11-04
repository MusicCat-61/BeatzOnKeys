using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Input;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.UI.Localization;
using BeatzOnKeys.UI.Theming;

namespace BeatzOnKeys.UI.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsService _settingsService;
    private readonly MidiNoteParser _midiParser;
    private readonly BatchTranscriptionService _batchTranscription;
    private readonly PlaybackController _playbackController;
    private readonly TypingPlaybackService _typingPlayback;
    private readonly DispatcherTimer _saveTimer;
    private readonly Guid? _savedSelectedTrackId;
    private CancellationTokenSource? _processingCancellation;
    private TrackViewModel? _selectedTrack;
    private double _volume;
    private double _playbackSpeed;
    private bool _autoAdvance;
    private bool _isBusy;
    private bool _isRunning;
    private string _statusText;
    private string _nowPlayingText = string.Empty;
    private bool _selectionFromPlayback;
    private bool _disposed;

    public MainViewModel(AppSettings settings, SettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settingsService = settingsService;
        _midiParser = new MidiNoteParser();
        _batchTranscription = new BatchTranscriptionService(
            new BasicPitchTranscriber(),
            _midiParser);
        _playbackController = new PlaybackController();
        _typingPlayback = new TypingPlaybackService(
            new KeyboardHook(),
            _playbackController,
            new NoteSynthesizer());
        _savedSelectedTrackId = settings.SelectedTrackId;
        _volume = settings.Volume;
        _playbackSpeed = settings.PlaybackSpeed;
        _autoAdvance = settings.AutoAdvance;
        _statusText = Loc.T("L_StatusRestoring");

        foreach (var state in settings.Tracks)
        {
            try
            {
                Tracks.Add(new TrackViewModel(state));
            }
            catch (Exception exception)
            {
                AppLogger.Error("settings", $"Ignored invalid track path '{state.SourcePath}'.", exception);
            }
        }

        _playbackController.Volume = _volume;
        _playbackController.PlaybackSpeed = _playbackSpeed;
        _playbackController.AutoAdvance = _autoAdvance;
        _playbackController.NotePlayed += OnNotePlayed;
        _playbackController.CurrentTrackChanged += OnPlaybackTrackChanged;

        AddFilesCommand = new RelayCommand(() => FilePickerRequested?.Invoke(), () => !IsBusy);
        ProcessAllCommand = new AsyncRelayCommand(ProcessAllAsync, CanProcessAll);
        CancelProcessingCommand = new RelayCommand(CancelProcessing, () => IsBusy);
        StartCommand = new RelayCommand(StartPlayback, CanStartPlayback);
        StopCommand = new RelayCommand(StopPlayback, () => IsRunning);
        RemoveTrackCommand = new RelayCommand<TrackViewModel>(
            track =>
            {
                if (track is not null)
                {
                    _ = RemoveTrackAsync(track);
                }
            },
            track => track is not null && !IsBusy);
        SetLanguageCommand = new RelayCommand<string>(SetLanguage);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        OpenLogsCommand = new RelayCommand(OpenLogs);

        Tracks.CollectionChanged += OnTracksChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        Theme.Changed += OnThemeChanged;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(550)
        };
        _saveTimer.Tick += SaveTimerOnTick;
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = [];

    public RelayCommand AddFilesCommand { get; }

    public AsyncRelayCommand ProcessAllCommand { get; }

    public RelayCommand CancelProcessingCommand { get; }

    public RelayCommand StartCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand<TrackViewModel> RemoveTrackCommand { get; }

    public RelayCommand<string> SetLanguageCommand { get; }

    public RelayCommand ToggleThemeCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public event Action? FilePickerRequested;

    public event Action? HideToTrayRequested;

    public event Action<string>? UserMessageRequested;

    public event Action? StateChanged;

    public TrackViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (!SetProperty(ref _selectedTrack, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTrackText));
            if (!_selectionFromPlayback && value is { IsReady: true })
            {
                _playbackController.SelectTrack(value.Id);
            }

            QueueSave();
            RefreshCommands();
        }
    }

    public string SelectedTrackText => SelectedTrack?.FileName ?? Loc.T("L_NoTrackSelected");

    public double Volume
    {
        get => _volume;
        set
        {
            var normalized = Math.Clamp(value, 0, 1);
            if (SetProperty(ref _volume, normalized))
            {
                _playbackController.Volume = normalized;
                OnPropertyChanged(nameof(VolumePercent));
                QueueSave();
            }
        }
    }

    public string VolumePercent => $"{Volume:P0}";

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            var normalized = Math.Clamp(value, 0.5, 2);
            if (SetProperty(ref _playbackSpeed, normalized))
            {
                _playbackController.PlaybackSpeed = normalized;
                OnPropertyChanged(nameof(PlaybackSpeedText));
                QueueSave();
            }
        }
    }

    public string PlaybackSpeedText => $"{PlaybackSpeed:F1}×";

    public bool AutoAdvance
    {
        get => _autoAdvance;
        set
        {
            if (SetProperty(ref _autoAdvance, value))
            {
                _playbackController.AutoAdvance = value;
                QueueSave();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanReset));
                RefreshCommands();
                StateChanged?.Invoke();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RefreshCommands();
                StateChanged?.Invoke();
            }
        }
    }

    public bool CanReset => !IsBusy;

    public bool HasTracks => Tracks.Count > 0;

    public string TrackCountText => Loc.F("L_TrackCountFormat", Tracks.Count);

    public bool IsEnglish => Loc.Current == AppLanguage.En;

    public bool IsRussian => Loc.Current == AppLanguage.Ru;

    public bool IsLightTheme => Theme.IsLight;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string NowPlayingText
    {
        get => _nowPlayingText;
        private set => SetProperty(ref _nowPlayingText, value);
    }

    public async Task InitializeAsync()
    {
        var stateChanged = false;
        foreach (var track in Tracks)
        {
            if (track.Status == TrackStatus.Ready)
            {
                var midiPath = track.MidiPath;
                if (string.IsNullOrWhiteSpace(midiPath) || !File.Exists(midiPath))
                {
                    track.SetError(TranscriptionFailureCode.MidiOutputMissing, null);
                    stateChanged = true;
                    continue;
                }

                try
                {
                    var notes = await Task.Run(() => _midiParser.Parse(midiPath)).ConfigureAwait(true);
                    if (notes.Count == 0)
                    {
                        track.SetError(TranscriptionFailureCode.NoNotes, null);
                        stateChanged = true;
                        continue;
                    }

                    track.SetReady(midiPath, notes);
                    _playbackController.UpsertTrack(new PlaybackTrack(track.Id, track.FileName, notes));
                }
                catch (Exception exception)
                {
                    track.SetError(TranscriptionFailureCode.MidiInvalid, exception.Message);
                    AppLogger.Error("midi", $"Failed to restore '{track.MidiPath}'.", exception);
                    stateChanged = true;
                }
            }
            else if (!File.Exists(track.SourcePath))
            {
                track.SetError(TranscriptionFailureCode.SourceMissing, null);
                stateChanged = true;
            }
        }

        var selected = _savedSelectedTrackId is Guid saved
            ? Tracks.FirstOrDefault(track => track.Id == saved)
            : null;
        SelectedTrack = selected ?? Tracks.FirstOrDefault(track => track.IsReady) ?? Tracks.FirstOrDefault();
        if (SelectedTrack is { IsReady: true })
        {
            _playbackController.SelectTrack(SelectedTrack.Id);
        }

        StatusText = IsRunning ? Loc.T("L_StatusListening") : Loc.T("L_ReadyToType");
        RefreshCommands();
        if (stateChanged)
        {
            await SaveAsync().ConfigureAwait(true);
        }
    }

    public void AddFiles(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var added = 0;
        var invalid = false;
        var duplicate = false;

        foreach (var value in files)
        {
            string path;
            try
            {
                path = Path.GetFullPath(value);
            }
            catch
            {
                invalid = true;
                continue;
            }

            var extension = Path.GetExtension(path);
            if (!File.Exists(path) ||
                (!extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) &&
                 !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)))
            {
                invalid = true;
                continue;
            }

            if (Tracks.Any(track => track.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                duplicate = true;
                continue;
            }

            var track = new TrackViewModel(new TrackState
            {
                Id = Guid.NewGuid(),
                SourcePath = path,
                Status = TrackStatus.Pending,
                AddedAt = DateTimeOffset.UtcNow
            });
            Tracks.Add(track);
            SelectedTrack ??= track;
            added++;
        }

        if (added > 0)
        {
            StatusText = Loc.F("L_AddedFormat", added);
            QueueSave();
        }
        else if (invalid)
        {
            UserMessageRequested?.Invoke(Loc.T("L_UnsupportedFiles"));
        }
        else if (duplicate)
        {
            UserMessageRequested?.Invoke(Loc.T("L_DuplicateFiles"));
        }

        RefreshCommands();
    }

    public async Task ResetAsync()
    {
        if (IsBusy)
        {
            return;
        }

        StopPlayback();
        _processingCancellation?.Cancel();

        foreach (var track in Tracks.ToArray())
        {
            DeleteCachedMidi(track.MidiPath);
        }
        ClearAppOwnedCacheDirectories();

        Tracks.Clear();
        _playbackController.Clear();
        SelectedTrack = null;
        Volume = 0.7;
        PlaybackSpeed = 1.0;
        AutoAdvance = false;
        NowPlayingText = string.Empty;
        Loc.SetLanguage(Loc.DetectSystemLanguage());
        Theme.Apply(AppTheme.Dark);
        StatusText = Loc.T("L_ResetComplete");
        await _settingsService.DeleteAsync().ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
        UserMessageRequested?.Invoke(Loc.T("L_ResetComplete"));
    }

    public async Task SaveAsync()
    {
        var snapshot = new AppSettings
        {
            Volume = Volume,
            PlaybackSpeed = PlaybackSpeed,
            AutoAdvance = AutoAdvance,
            SelectedTrackId = SelectedTrack?.Id,
            Language = Loc.CurrentCode,
            Theme = Theme.CurrentCode,
            Tracks = Tracks.Select(track => track.ToState()).ToList()
        };

        try
        {
            await _settingsService.SaveAsync(snapshot).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLogger.Error("settings", "Failed to save settings.", exception);
        }
    }

    private bool CanProcessAll() =>
        !IsBusy && !IsRunning && Tracks.Any(track => track.Status is TrackStatus.Pending or TrackStatus.Error);

    private async Task ProcessAllAsync()
    {
        var queued = Tracks
            .Where(track => track.Status is TrackStatus.Pending or TrackStatus.Error)
            .ToArray();
        if (queued.Length == 0)
        {
            return;
        }

        IsBusy = true;
        StatusText = Loc.T("L_StatusProcessing");
        _processingCancellation = new CancellationTokenSource();
        var cancellationToken = _processingCancellation.Token;
        var requests = queued.Select(track => new TrackTranscriptionRequest(
            track.Id,
            track.SourcePath,
            AppPaths.GetMidiPath(track.Id))).ToArray();

        var progress = new Progress<TranscriptionProgress>(update =>
        {
            var track = Tracks.FirstOrDefault(item => item.Id == update.TrackId);
            if (track is null)
            {
                return;
            }

            track.ApplyProgress(update);
            if (update.Status == TrackStatus.Processing)
            {
                StatusText = Loc.F("L_ProcessingTrackFormat", track.FileName);
            }

            QueueSave();
            RefreshCommands();
        });

        try
        {
            var results = await _batchTranscription.ProcessAsync(requests, progress, cancellationToken)
                .ConfigureAwait(true);

            foreach (var result in results)
            {
                var track = Tracks.FirstOrDefault(item => item.Id == result.TrackId);
                if (track is null)
                {
                    continue;
                }

                if (result.IsSuccess && result.MidiPath is not null)
                {
                    track.SetReady(result.MidiPath, result.Notes);
                    _playbackController.UpsertTrack(new PlaybackTrack(track.Id, track.FileName, result.Notes));
                }
                else if (result.FailureCode == TranscriptionFailureCode.Cancelled)
                {
                    track.SetPending();
                }
                else
                {
                    track.SetError(result.FailureCode, result.Details);
                }
            }

            if (SelectedTrack is null || !SelectedTrack.IsReady)
            {
                SelectedTrack = Tracks.FirstOrDefault(track => track.IsReady) ?? SelectedTrack;
            }

            StatusText = cancellationToken.IsCancellationRequested
                ? Loc.T("L_ProcessingCancelled")
                : Loc.T("L_ProcessingComplete");
        }
        catch (Exception exception)
        {
            AppLogger.Error("transcription", "Batch processing failed.", exception);
            StatusText = Loc.T("L_ErrorUnknown");
            UserMessageRequested?.Invoke(Loc.F("L_ErrorDetailsFormat", Loc.T("L_ErrorUnknown"), exception.Message));
        }
        finally
        {
            _processingCancellation.Dispose();
            _processingCancellation = null;
            IsBusy = false;
            await SaveAsync().ConfigureAwait(true);
            RefreshCommands();
        }
    }

    private void CancelProcessing() => _processingCancellation?.Cancel();

    private bool CanStartPlayback() =>
        !IsBusy && !IsRunning && Tracks.Any(track => track.IsReady && track.Notes.Count > 0);

    private void StartPlayback()
    {
        var track = SelectedTrack is { IsReady: true } && SelectedTrack.Notes.Count > 0
            ? SelectedTrack
            : Tracks.FirstOrDefault(item => item.IsReady && item.Notes.Count > 0);
        if (track is null)
        {
            UserMessageRequested?.Invoke(Loc.T("L_NoReadyTracks"));
            return;
        }

        SelectedTrack = track;
        _playbackController.SelectTrack(track.Id, restart: false);
        _playbackController.Volume = Volume;
        _playbackController.PlaybackSpeed = PlaybackSpeed;
        _playbackController.AutoAdvance = AutoAdvance;

        try
        {
            _typingPlayback.Start();
            IsRunning = true;
            StatusText = Loc.T("L_StatusListening");
            HideToTrayRequested?.Invoke();
        }
        catch (Exception exception)
        {
            AppLogger.Error("playback", "Failed to start playback.", exception);
            UserMessageRequested?.Invoke(Loc.F("L_StartFailedFormat", exception.Message));
        }
    }

    private void StopPlayback()
    {
        _typingPlayback.Stop();
        IsRunning = false;
        StatusText = Loc.T("L_ReadyToType");
    }

    private async Task RemoveTrackAsync(TrackViewModel track)
    {
        if (IsBusy || !Tracks.Contains(track))
        {
            return;
        }

        if (IsRunning && SelectedTrack == track)
        {
            StopPlayback();
        }

        var index = Tracks.IndexOf(track);
        Tracks.Remove(track);
        _playbackController.RemoveTrack(track.Id);
        DeleteCachedMidi(track.MidiPath);
        if (SelectedTrack == track)
        {
            SelectedTrack = Tracks.Count == 0
                ? null
                : Tracks[Math.Min(index, Tracks.Count - 1)];
        }

        await SaveAsync().ConfigureAwait(true);
    }

    private void SetLanguage(string? languageCode)
    {
        Loc.SetLanguage(Loc.Parse(languageCode));
        QueueSave();
    }

    private void ToggleTheme()
    {
        Theme.Toggle();
        QueueSave();
    }

    private static void OpenLogs()
    {
        AppPaths.EnsureCreated();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            AppLogger.Error("logs", "Could not open the logs folder.", exception);
        }
    }

    private void OnNotePlayed(NotePlayedEventArgs args)
    {
        RunOnUiThread(() =>
        {
            NowPlayingText = Loc.F(
                "L_NowPlayingFormat",
                args.TrackName,
                args.Note.DisplayName,
                args.PlayedNoteNumber,
                args.TotalNotes);
        });
    }

    private void OnPlaybackTrackChanged(PlaybackTrackChangedEventArgs args)
    {
        RunOnUiThread(() =>
        {
            var track = Tracks.FirstOrDefault(item => item.Id == args.TrackId);
            if (track is null || ReferenceEquals(track, SelectedTrack))
            {
                return;
            }

            _selectionFromPlayback = true;
            try
            {
                SelectedTrack = track;
            }
            finally
            {
                _selectionFromPlayback = false;
            }
        });
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TrackCountText));
        RefreshCommands();
        QueueSave();
    }

    private void OnLanguageChanged()
    {
        foreach (var track in Tracks)
        {
            track.RefreshLocalization();
        }

        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsRussian));
        OnPropertyChanged(nameof(TrackCountText));
        OnPropertyChanged(nameof(SelectedTrackText));
        StatusText = IsBusy
            ? Loc.T("L_StatusProcessing")
            : IsRunning ? Loc.T("L_StatusListening") : Loc.T("L_ReadyToType");
        StateChanged?.Invoke();
    }

    private void OnThemeChanged()
    {
        OnPropertyChanged(nameof(IsLightTheme));
        StateChanged?.Invoke();
    }

    private void QueueSave()
    {
        if (_disposed)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void SaveTimerOnTick(object? sender, EventArgs eventArgs)
    {
        _saveTimer.Stop();
        await SaveAsync().ConfigureAwait(true);
    }

    private void RefreshCommands()
    {
        AddFilesCommand.NotifyCanExecuteChanged();
        ProcessAllCommand.NotifyCanExecuteChanged();
        CancelProcessingCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RemoveTrackCommand.NotifyCanExecuteChanged();
    }

    private static void ClearAppOwnedCacheDirectories()
    {
        try
        {
            if (Directory.Exists(AppPaths.MidiDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.MidiDirectory, "*.mid", SearchOption.TopDirectoryOnly))
                {
                    File.Delete(file);
                }
            }

            if (Directory.Exists(AppPaths.UpdateDirectory))
            {
                Directory.Delete(AppPaths.UpdateDirectory, recursive: true);
            }

            Directory.CreateDirectory(AppPaths.UpdateDirectory);
        }
        catch (Exception exception)
        {
            AppLogger.Error("cache", "Could not fully clear app-owned cache directories.", exception);
        }
    }

    private static void DeleteCachedMidi(string? midiPath)
    {
        if (string.IsNullOrWhiteSpace(midiPath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(midiPath);
            var cacheRoot = Path.GetFullPath(AppPaths.MidiDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("cache", $"Could not delete cached MIDI '{midiPath}'.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _saveTimer.Stop();
        _processingCancellation?.Cancel();
        StopPlayback();
        await SaveAsync().ConfigureAwait(true);
        Tracks.CollectionChanged -= OnTracksChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        Theme.Changed -= OnThemeChanged;
        _playbackController.NotePlayed -= OnNotePlayed;
        _playbackController.CurrentTrackChanged -= OnPlaybackTrackChanged;
        _typingPlayback.Dispose();
        _processingCancellation?.Dispose();
        _disposed = true;
    }
}
