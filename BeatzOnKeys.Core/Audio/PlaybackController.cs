using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Audio;

/// <summary>Thread-safe note cursor with optional wrap-around to the next ready track.</summary>
public sealed class PlaybackController
{
    private readonly object _gate = new();
    private readonly List<PlaybackTrack> _tracks = [];
    private Guid? _selectedTrackId;
    private int _noteIndex;

    public event Action<NotePlayedEventArgs>? NotePlayed;

    public event Action<PlaybackTrackChangedEventArgs>? CurrentTrackChanged;

    public double Volume { get; set; } = 0.7;

    public double PlaybackSpeed { get; set; } = 1.0;

    public bool AutoAdvance { get; set; }

    public Guid? SelectedTrackId
    {
        get
        {
            lock (_gate)
            {
                return _selectedTrackId;
            }
        }
    }

    public void UpsertTrack(PlaybackTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_gate)
        {
            var index = _tracks.FindIndex(item => item.Id == track.Id);
            if (index >= 0)
            {
                _tracks[index] = track;
                if (_selectedTrackId == track.Id)
                {
                    _noteIndex = Math.Min(_noteIndex, track.Notes.Count);
                }
            }
            else
            {
                _tracks.Add(track);
            }

            _selectedTrackId ??= track.Id;
        }
    }

    public void RemoveTrack(Guid trackId)
    {
        PlaybackTrackChangedEventArgs? changed = null;
        lock (_gate)
        {
            var removedIndex = _tracks.FindIndex(track => track.Id == trackId);
            if (removedIndex < 0)
            {
                return;
            }

            _tracks.RemoveAt(removedIndex);
            if (_selectedTrackId == trackId)
            {
                var next = _tracks.Count == 0 ? null : _tracks[Math.Min(removedIndex, _tracks.Count - 1)];
                _selectedTrackId = next?.Id;
                _noteIndex = 0;
                if (next is not null)
                {
                    changed = new PlaybackTrackChangedEventArgs(next.Id, next.Name, false);
                }
            }
        }

        if (changed is not null)
        {
            CurrentTrackChanged?.Invoke(changed);
        }
    }

    public bool SelectTrack(Guid trackId, bool restart = true)
    {
        PlaybackTrack? selected;
        lock (_gate)
        {
            selected = _tracks.FirstOrDefault(track => track.Id == trackId);
            if (selected is null)
            {
                return false;
            }

            var changed = _selectedTrackId != trackId;
            _selectedTrackId = trackId;
            if (restart || changed)
            {
                _noteIndex = 0;
            }
        }

        CurrentTrackChanged?.Invoke(new PlaybackTrackChangedEventArgs(selected.Id, selected.Name, false));
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _tracks.Clear();
            _selectedTrackId = null;
            _noteIndex = 0;
        }
    }

    public bool TryTakeNext(out PlaybackStep? step)
    {
        NotePlayedEventArgs? played = null;
        PlaybackTrackChangedEventArgs? changed = null;

        lock (_gate)
        {
            step = null;
            if (_tracks.Count == 0)
            {
                return false;
            }

            var trackIndex = _selectedTrackId is null
                ? -1
                : _tracks.FindIndex(track => track.Id == _selectedTrackId.Value);
            if (trackIndex < 0)
            {
                trackIndex = 0;
                _selectedTrackId = _tracks[0].Id;
                _noteIndex = 0;
                changed = new PlaybackTrackChangedEventArgs(_tracks[0].Id, _tracks[0].Name, false);
            }

            var track = _tracks[trackIndex];
            if (track.Notes.Count == 0)
            {
                return false;
            }

            if (_noteIndex >= track.Notes.Count)
            {
                if (!AutoAdvance)
                {
                    return false;
                }

                trackIndex = (trackIndex + 1) % _tracks.Count;
                track = _tracks[trackIndex];
                _selectedTrackId = track.Id;
                _noteIndex = 0;
                changed = new PlaybackTrackChangedEventArgs(track.Id, track.Name, true);
            }

            if (track.Notes.Count == 0)
            {
                return false;
            }

            var note = track.Notes[_noteIndex++];
            var playedNumber = _noteIndex;
            step = new PlaybackStep(
                track.Id,
                track.Name,
                note,
                playedNumber,
                track.Notes.Count,
                Math.Clamp(Volume, 0, 1),
                Math.Clamp(PlaybackSpeed, 0.5, 2));
            played = new NotePlayedEventArgs(
                track.Id,
                track.Name,
                note,
                playedNumber,
                track.Notes.Count);

            if (_noteIndex >= track.Notes.Count && AutoAdvance)
            {
                var nextIndex = (trackIndex + 1) % _tracks.Count;
                var next = _tracks[nextIndex];
                _selectedTrackId = next.Id;
                _noteIndex = 0;
                changed = new PlaybackTrackChangedEventArgs(next.Id, next.Name, true);
            }
        }

        if (played is not null)
        {
            NotePlayed?.Invoke(played);
        }

        if (changed is not null)
        {
            CurrentTrackChanged?.Invoke(changed);
        }

        return step is not null;
    }
}
