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
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.UI.Localization;
using BeatzOnKeys.UI.Theming;

namespace BeatzOnKeys.UI.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsService _settingsService;
    private readonly PlaybackController _playbackController;
    private readonly IAudioFragmentPlayer _audioPlayer;
    private readonly TypingPlaybackService _typingPlayback;
    private readonly DispatcherTimer _saveTimer;
    private readonly Guid? _savedSelectedTrackId;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _fileImportTask = Task.CompletedTask;
    private TrackViewModel? _selectedTrack;
    private PlaybackPositionEventArgs? _lastPosition;
    private double _volume;
    private double _stepSeconds;
    private bool _autoAdvance;
    private bool _loop;
    private bool _isRunning;
    private bool _changingPlayback;
    private bool _initializing = true;
    private int _pendingImports;
    private bool _selectionFromPlayback;
    private bool _closing;
    private bool _disposed;
    private string _statusText;
    private string _nowPlayingText = string.Empty;

    public MainViewModel(AppSettings settings, SettingsService settingsService,
        IKeyboardHook? keyboardHook = null, IAudioFragmentPlayer? audioPlayer = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        _settingsService = settingsService;
        _savedSelectedTrackId = settings.SelectedTrackId;
        _volume = settings.Volume;
        _stepSeconds = settings.StepSeconds;
        _autoAdvance = settings.AutoAdvance;
        _loop = settings.Loop;
        _statusText = Loc.T("L_StatusRestoring");
        _playbackController = new PlaybackController
        {
            Volume = _volume, StepSeconds = _stepSeconds, AutoAdvance = _autoAdvance, Loop = _loop
        };
        _audioPlayer = audioPlayer ?? new AudioFragmentPlayer();
        _audioPlayer.Volume = _volume;
        _typingPlayback = new TypingPlaybackService(keyboardHook ?? new KeyboardHook(), _playbackController, _audioPlayer);
        _playbackController.PositionChanged += OnPositionChanged;
        _playbackController.CurrentTrackChanged += OnPlaybackTrackChanged;
        _typingPlayback.PlaybackFailed += OnPlaybackFailed;

        foreach (var state in settings.Tracks)
        {
            try { Tracks.Add(new TrackViewModel(state)); }
            catch (Exception exception) { AppLogger.Error("settings", $"Ignored invalid track path '{state.SourcePath}'.", exception); }
        }

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += SaveTimerOnTick;
        AddFilesCommand = new RelayCommand(() => FilePickerRequested?.Invoke(), () => !_closing && !_initializing);
        StartCommand = new AsyncRelayCommand(StartPlaybackAsync, CanStartPlayback);
        StopCommand = new AsyncRelayCommand(StopPlaybackAsync, () => IsRunning && !_changingPlayback);
        RemoveTrackCommand = new RelayCommand<TrackViewModel>(track =>
        {
            if (track is not null) { _ = RemoveTrackAsync(track); }
        }, track => track is not null && CanReset);
        SetLanguageCommand = new RelayCommand<string>(code => Loc.SetLanguage(Loc.Parse(code)));
        ToggleThemeCommand = new RelayCommand(Theme.Toggle);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        Tracks.CollectionChanged += OnTracksChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        Theme.Changed += OnThemeChanged;
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = [];
    public RelayCommand AddFilesCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
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
            var normalized = Math.Clamp(double.IsFinite(value) ? value : 0.7, 0, 1);
            if (!SetProperty(ref _volume, normalized)) { return; }
            _playbackController.Volume = normalized;
            _audioPlayer.Volume = normalized;
            OnPropertyChanged(nameof(VolumePercentValue));
            OnPropertyChanged(nameof(VolumePercent));
            QueueSave();
        }
    }
    public double VolumePercentValue { get => Volume * 100; set => Volume = value / 100; }
    public string VolumePercent => $"{Math.Round(Volume * 100):0}%";
    public double StepSeconds
    {
        get => _stepSeconds;
        set
        {
            var normalized = Math.Clamp(double.IsFinite(value) ? value : AppSettings.DefaultStepSeconds,
                AppSettings.MinimumStepSeconds, AppSettings.MaximumStepSeconds);
            if (!SetProperty(ref _stepSeconds, normalized)) { return; }
            _playbackController.StepSeconds = normalized;
            OnPropertyChanged(nameof(StepSecondsText));
            QueueSave();
        }
    }
    public string StepSecondsText => Loc.F("L_StepSecondsFormat", StepSeconds.ToString("0.00", CultureInfo.CurrentUICulture));
    public bool AutoAdvance
    {
        get => _autoAdvance;
        set { if (SetProperty(ref _autoAdvance, value)) { _playbackController.AutoAdvance = value; QueueSave(); } }
    }
    public bool Loop
    {
        get => _loop;
        set { if (SetProperty(ref _loop, value)) { _playbackController.Loop = value; QueueSave(); } }
    }
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (SetProperty(ref _isRunning, value)) { RefreshCommands(); StateChanged?.Invoke(); } }
    }
    public bool CanReset => !_closing && !_initializing && !_changingPlayback && _pendingImports == 0;
    public bool HasTracks => Tracks.Count > 0;
    public string TrackCountText => Loc.F("L_TrackCountFormat", Tracks.Count);
    public bool IsEnglish => Loc.Current == AppLanguage.En;
    public bool IsRussian => Loc.Current == AppLanguage.Ru;
    public bool IsLightTheme => Theme.IsLight;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string NowPlayingText { get => _nowPlayingText; private set => SetProperty(ref _nowPlayingText, value); }
    public double PlaybackProgress => _lastPosition is { DurationSeconds: > 0 } position
        ? Math.Clamp(position.PositionSeconds / position.DurationSeconds * 100, 0, 100) : 0;
    public string PlaybackPositionText => $"{FormatPosition(_lastPosition?.PositionSeconds ?? 0)} / " +
        FormatPosition(_lastPosition?.DurationSeconds ?? SelectedTrack?.Duration.TotalSeconds ?? 0);

    public async Task InitializeAsync()
    {
        foreach (var track in Tracks.ToArray()) { await LoadTrackInfoAsync(track).ConfigureAwait(true); }
        var saved = _savedSelectedTrackId is Guid id ? Tracks.FirstOrDefault(track => track.Id == id) : null;
        SelectedTrack = saved ?? Tracks.FirstOrDefault(track => track.IsAvailable) ?? Tracks.FirstOrDefault();
        _initializing = false;
        StatusText = Loc.T("L_ReadyToType");
        RefreshCommands();
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

    private bool CanStartPlayback() => CanReset && !IsRunning && Tracks.Any(track => track.IsAvailable);

    private async Task StartPlaybackAsync()
    {
        var track = SelectedTrack is { IsAvailable: true } ? SelectedTrack : Tracks.FirstOrDefault(item => item.IsAvailable);
        if (track is null) { UserMessageRequested?.Invoke(Loc.T("L_NoTracks")); return; }
        _changingPlayback = true;
        RefreshCommands();
        try
        {
            SelectedTrack = track;
            var ended = _playbackController.CurrentPosition >= track.Duration.TotalSeconds;
            if (!_typingPlayback.SelectTrack(track.Id, restart: ended)) { return; }
            await _typingPlayback.StartAsync().ConfigureAwait(true);
            IsRunning = true;
            StatusText = Loc.T("L_StatusListening");
            // Start persists preferences before leaving the visible window, even if debounce has not fired.
            await SaveAsync().ConfigureAwait(true);
            HideToTrayRequested?.Invoke();
        }
        catch (Exception exception)
        {
            AppLogger.Error("playback", "Failed to start audio scrubbing.", exception);
            UserMessageRequested?.Invoke(Loc.F("L_StartFailedFormat", exception.Message));
        }
        finally { _changingPlayback = false; RefreshCommands(); }
    }

    private async Task StopPlaybackAsync()
    {
        _changingPlayback = true;
        RefreshCommands();
        try { await _typingPlayback.StopAsync().ConfigureAwait(true); }
        catch (Exception exception) { AppLogger.Error("playback", "Could not stop audio scrubbing cleanly.", exception); }
        finally
        {
            IsRunning = false;
            _changingPlayback = false;
            StatusText = Loc.T("L_ReadyToType");
            RefreshCommands();
        }
    }

    private async Task RemoveTrackAsync(TrackViewModel track)
    {
        if (!CanReset || !Tracks.Contains(track)) { return; }
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

    public async Task ResetAsync()
    {
        if (!CanReset) { return; }
        await StopPlaybackAsync().ConfigureAwait(true);
        _typingPlayback.Unload();
        _playbackController.Clear();
        Tracks.Clear();
        SelectedTrack = null;
        Volume = 0.7;
        StepSeconds = AppSettings.DefaultStepSeconds;
        AutoAdvance = false;
        Loop = true;
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
            Volume = Volume, StepSeconds = StepSeconds, AutoAdvance = AutoAdvance, Loop = Loop,
            SelectedTrackId = SelectedTrack?.Id, Language = Loc.CurrentCode, Theme = Theme.CurrentCode,
            Tracks = Tracks.Select(track => track.ToState()).ToList()
        };
        try { await _settingsService.SaveAsync(snapshot).ConfigureAwait(true); }
        catch (Exception exception) { AppLogger.Error("settings", "Failed to save settings.", exception); }
    }

    private void OnPositionChanged(PlaybackPositionEventArgs position) => RunOnUiThread(() =>
    {
        if (_closing || position.TrackId != _playbackController.SelectedTrackId) { return; }
        _lastPosition = position;
        OnPropertyChanged(nameof(PlaybackPositionText));
        OnPropertyChanged(nameof(PlaybackProgress));
        NowPlayingText = position.TrackId is null ? string.Empty :
            Loc.F("L_NowPlayingFormat", position.TrackName, FormatPosition(position.PositionSeconds), FormatPosition(position.DurationSeconds));
        if (position.ReachedEnd) { StatusText = Loc.T("L_TrackEnded"); }
        else if (IsRunning) { StatusText = Loc.T("L_StatusListening"); }
    });

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
        if (_closing) { return; }
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
    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TrackCountText));
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
        OnPropertyChanged(nameof(StepSecondsText));
        if (_lastPosition is { } position) { OnPositionChanged(position); }
        StatusText = _lastPosition?.ReachedEnd == true ? Loc.T("L_TrackEnded") :
            IsRunning ? Loc.T("L_StatusListening") : Loc.T("L_ReadyToType");
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
        OnPropertyChanged(nameof(CanReset));
        AddFilesCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RemoveTrackCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed || _closing) { return; }
        _closing = true;
        _saveTimer.Stop();
        _lifetime.Cancel();
        await _fileImportTask.ConfigureAwait(true);
        await StopPlaybackAsync().ConfigureAwait(true);
        await SaveAsync().ConfigureAwait(true);
        Tracks.CollectionChanged -= OnTracksChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        Theme.Changed -= OnThemeChanged;
        _playbackController.PositionChanged -= OnPositionChanged;
        _playbackController.CurrentTrackChanged -= OnPlaybackTrackChanged;
        _typingPlayback.PlaybackFailed -= OnPlaybackFailed;
        await _typingPlayback.DisposeAsync().ConfigureAwait(true);
        _lifetime.Dispose();
        _disposed = true;
    }
}
