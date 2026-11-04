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
    public void Deselect() { lock (_gate) { _selectedTrackId = null; } }
    public void Clear() { lock (_gate) { _tracks.Clear(); _selectedTrackId = null; } }
}
