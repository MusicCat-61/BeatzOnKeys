using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Audio;

/// <summary>A thread-safe, key-driven cursor. Wall-clock time never advances the playlist.</summary>
public sealed class PlaybackController
{
    private readonly object _gate = new();
    private readonly List<PlaybackTrack> _tracks = [];
    private Guid? _selectedTrackId;
    private double _currentPosition;
    private double _volume = 0.7;
    private double _stepSeconds = AppSettings.DefaultStepSeconds;
    private bool _autoAdvance;
    private bool _loop = true;

    public event Action<PlaybackPositionEventArgs>? PositionChanged;
    public event Action<PlaybackTrackChangedEventArgs>? CurrentTrackChanged;

    public double Volume
    {
        get { lock (_gate) { return _volume; } }
        set { lock (_gate) { _volume = Math.Clamp(double.IsFinite(value) ? value : 0.7, 0, 1); } }
    }

    public double StepSeconds
    {
        get { lock (_gate) { return _stepSeconds; } }
        set
        {
            lock (_gate)
            {
                _stepSeconds = Math.Clamp(double.IsFinite(value) ? value : AppSettings.DefaultStepSeconds,
                    AppSettings.MinimumStepSeconds, AppSettings.MaximumStepSeconds);
            }
        }
    }

    public bool AutoAdvance
    {
        get { lock (_gate) { return _autoAdvance; } }
        set { lock (_gate) { _autoAdvance = value; } }
    }

    public bool Loop
    {
        get { lock (_gate) { return _loop; } }
        set { lock (_gate) { _loop = value; } }
    }

    public Guid? SelectedTrackId
    {
        get { lock (_gate) { return _selectedTrackId; } }
    }

    public double CurrentPosition
    {
        get { lock (_gate) { return _currentPosition; } }
    }

    public PlaybackTrack? CurrentTrack
    {
        get { lock (_gate) { return _tracks.FirstOrDefault(track => track.Id == _selectedTrackId); } }
    }

    public PlaybackTrack? GetTrack(Guid trackId)
    {
        lock (_gate) { return _tracks.FirstOrDefault(track => track.Id == trackId); }
    }

    public void UpsertTrack(PlaybackTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Id == Guid.Empty || string.IsNullOrWhiteSpace(track.SourcePath) || track.Duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("A playable track needs an ID, a source path and a positive duration.", nameof(track));
        }

        lock (_gate)
        {
            var index = _tracks.FindIndex(item => item.Id == track.Id);
            if (index >= 0)
            {
                _tracks[index] = track;
                if (_selectedTrackId == track.Id)
                {
                    _currentPosition = Math.Min(_currentPosition, track.Duration.TotalSeconds);
                }
            }
            else
            {
                _tracks.Add(track);
            }
        }
    }

    public void RemoveTrack(Guid trackId)
    {
        PlaybackPositionEventArgs? position = null;
        lock (_gate)
        {
            _tracks.RemoveAll(track => track.Id == trackId);
            if (_selectedTrackId == trackId)
            {
                _selectedTrackId = null;
                _currentPosition = 0;
                position = EmptyPosition();
            }
        }
        if (position is not null) { PositionChanged?.Invoke(position); }
    }

    public bool SelectTrack(Guid trackId, bool restart = true)
    {
        PlaybackTrack selected;
        PlaybackPositionEventArgs position;
        lock (_gate)
        {
            var track = _tracks.FirstOrDefault(item => item.Id == trackId);
            if (track is null) { return false; }
            selected = track;
            if (restart || _selectedTrackId != trackId) { _currentPosition = 0; }
            _selectedTrackId = trackId;
            position = PositionFor(selected);
        }
        CurrentTrackChanged?.Invoke(new PlaybackTrackChangedEventArgs(selected.Id, selected.Name, false));
        PositionChanged?.Invoke(position);
        return true;
    }

    public void Deselect()
    {
        lock (_gate)
        {
            _selectedTrackId = null;
            _currentPosition = 0;
        }
        PositionChanged?.Invoke(EmptyPosition());
    }

    public void Clear()
    {
        lock (_gate)
        {
            _tracks.Clear();
            _selectedTrackId = null;
            _currentPosition = 0;
        }
        PositionChanged?.Invoke(EmptyPosition());
    }

    public bool TryTakeNext(out PlaybackStep? step)
    {
        PlaybackTrackChangedEventArgs? changed = null;
        PlaybackPositionEventArgs position;
        lock (_gate)
        {
            step = null;
            var index = _tracks.FindIndex(track => track.Id == _selectedTrackId);
            if (index < 0) { return false; }
            var track = _tracks[index];
            var nextPosition = _currentPosition + _stepSeconds;
            // Allow for floating-point rounding at an exact multiple of the step.
            if (nextPosition >= track.Duration.TotalSeconds - 1e-9)
            {
                if (_autoAdvance && (index + 1 < _tracks.Count || _loop))
                {
                    index = (index + 1) % _tracks.Count;
                    track = _tracks[index];
                    _selectedTrackId = track.Id;
                    _currentPosition = 0;
                    changed = new PlaybackTrackChangedEventArgs(track.Id, track.Name, true);
                }
                else if (!_autoAdvance && _loop)
                {
                    _currentPosition = 0;
                }
                else
                {
                    _currentPosition = track.Duration.TotalSeconds;
                    position = PositionFor(track, reachedEnd: true);
                    goto Notify;
                }
            }
            else
            {
                _currentPosition = nextPosition;
            }

            step = new PlaybackStep(track, _currentPosition,
                Math.Min(_stepSeconds, track.Duration.TotalSeconds - _currentPosition), _volume);
            position = PositionFor(track);
        Notify:;
        }

        // Subscribers run outside the cursor lock, including UI dispatch and settings saves.
        if (changed is not null) { CurrentTrackChanged?.Invoke(changed); }
        PositionChanged?.Invoke(position);
        return step is not null;
    }

    private PlaybackPositionEventArgs PositionFor(PlaybackTrack track, bool reachedEnd = false) =>
        new(track.Id, track.Name, _currentPosition, track.Duration.TotalSeconds, reachedEnd);

    private static PlaybackPositionEventArgs EmptyPosition() => new(null, string.Empty, 0, 0);
}
