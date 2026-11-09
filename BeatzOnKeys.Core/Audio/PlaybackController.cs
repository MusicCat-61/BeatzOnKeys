using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Audio;

/// <summary>A thread-safe playlist; only natural track completion selects the next entry.</summary>
public sealed class PlaybackController
{
    private readonly object _gate = new();
    private readonly List<PlaybackTrack> _tracks = [];
    private Guid? _selectedTrackId;
    private bool _autoAdvance;
    private bool _loop = true;

    public event Action<PlaybackTrackChangedEventArgs>? CurrentTrackChanged;
    public bool AutoAdvance { get { lock (_gate) { return _autoAdvance; } } set { lock (_gate) { _autoAdvance = value; } } }
    public bool Loop { get { lock (_gate) { return _loop; } } set { lock (_gate) { _loop = value; } } }
    public Guid? SelectedTrackId { get { lock (_gate) { return _selectedTrackId; } } }
    public PlaybackTrack? CurrentTrack { get { lock (_gate) { return _tracks.FirstOrDefault(track => track.Id == _selectedTrackId); } } }
    public PlaybackTrack? GetTrack(Guid id) { lock (_gate) { return _tracks.FirstOrDefault(track => track.Id == id); } }

    public void UpsertTrack(PlaybackTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Id == Guid.Empty || string.IsNullOrWhiteSpace(track.SourcePath) || track.Duration <= TimeSpan.Zero)
        { throw new ArgumentException("A track needs an ID, source path and positive duration.", nameof(track)); }
        lock (_gate)
        {
            var index = _tracks.FindIndex(item => item.Id == track.Id);
            if (index < 0) { _tracks.Add(track); } else { _tracks[index] = track; }
        }
    }

    public bool SelectTrack(Guid id, bool restart = true)
    {
        PlaybackTrack? track;
        lock (_gate)
        {
            track = _tracks.FirstOrDefault(item => item.Id == id);
            if (track is null) { return false; }
            _selectedTrackId = id;
        }
        CurrentTrackChanged?.Invoke(new(id, track.Name, false));
        return true;
    }

    public bool TryAdvanceAtEnd(out PlaybackTrack? next)
    {
        lock (_gate)
        {
            next = null;
            var index = _tracks.FindIndex(track => track.Id == _selectedTrackId);
            if (index < 0) { return false; }
            if (_autoAdvance)
            {
                if (index + 1 < _tracks.Count) { index++; }
                else if (_loop) { index = 0; }
                else { return false; }
            }
            else if (!_loop) { return false; }
            next = _tracks[index];
            _selectedTrackId = next.Id;
        }
        CurrentTrackChanged?.Invoke(new(next.Id, next.Name, true));
        return true;
    }

    public void RemoveTrack(Guid id)
    {
        lock (_gate)
        {
            _tracks.RemoveAll(track => track.Id == id);
            if (_selectedTrackId == id) { _selectedTrackId = null; }
        }
    }

    /// <summary>Position of a known track in playback order, or -1 when it is not part of the list.</summary>
    public int IndexOf(Guid id) { lock (_gate) { return _tracks.FindIndex(track => track.Id == id); } }

    /// <summary>True when moving this track by the given offset is possible.</summary>
    public bool CanMove(Guid id, int offset)
    {
        lock (_gate)
        {
            var index = _tracks.FindIndex(track => track.Id == id);
            var target = index + offset;
            return index >= 0 && target >= 0 && target < _tracks.Count;
        }
    }

    /// <summary>
    /// Moves one track by the given offset in playback order and returns its new index, or -1 when
    /// the move is impossible. The selection and the track-changed event stay untouched, so a
    /// reorder never restarts the track that is currently playing.
    /// </summary>
    public int MoveTrack(Guid id, int offset)
    {
        lock (_gate)
        {
            var index = _tracks.FindIndex(track => track.Id == id);
            if (index < 0) { return -1; }
            var target = index + offset;
            if (target < 0 || target >= _tracks.Count) { return -1; }
            var track = _tracks[index];
            _tracks.RemoveAt(index);
            _tracks.Insert(target, track);
            return target;
        }
    }

    /// <summary>
    /// Applies an external (visual) order to the tracks this controller knows. Ids it does not hold
    /// are ignored, and tracks the caller omits keep their relative order at the end. The selection
    /// is untouched and no event is raised, because reordering must not interrupt playback.
    /// </summary>
    public void Reorder(IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        lock (_gate)
        {
            if (_tracks.Count == 0) { return; }
            var remaining = new Dictionary<Guid, PlaybackTrack>(_tracks.Count);
            foreach (var track in _tracks) { remaining[track.Id] = track; }
            var reordered = new List<PlaybackTrack>(_tracks.Count);
            foreach (var id in orderedIds)
            {
                if (remaining.Remove(id, out var track)) { reordered.Add(track); }
            }

            foreach (var track in _tracks)
            {
                if (remaining.Remove(track.Id, out var kept)) { reordered.Add(kept); }
            }

            _tracks.Clear();
            _tracks.AddRange(reordered);
        }
    }
    /// <summary>
    /// Swaps the whole order for the list of one page. The selected track survives when its id is
    /// in the new order; a track that is playing but belongs to another page is kept at the front,
    /// so switching tabs never loses the name of what is currently heard. No event is raised,
    /// because a rebuild is not a transition: the playing track must not restart.
    /// </summary>
    public void Replace(IReadOnlyList<PlaybackTrack> tracks, PlaybackTrack? keepFirst = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        lock (_gate)
        {
            _tracks.Clear();
            if (keepFirst is not null && tracks.All(track => track.Id != keepFirst.Id))
            {
                _tracks.Add(keepFirst);
            }

            _tracks.AddRange(tracks);
            if (_selectedTrackId is Guid selected && _tracks.All(track => track.Id != selected))
            {
                _selectedTrackId = null;
            }
        }
    }

    public void Deselect() { lock (_gate) { _selectedTrackId = null; } }
    public void Clear() { lock (_gate) { _tracks.Clear(); _selectedTrackId = null; } }
}
