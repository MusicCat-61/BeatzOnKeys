using System.IO;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.UI.Localization;

namespace BeatzOnKeys.UI.ViewModels;

/// <summary>A simple playlist row: filename, directory and duration, without processing states.</summary>
public sealed class TrackViewModel : ObservableObject
{
    private TimeSpan _duration;
    private bool _isAvailable;
    private bool _sourceMissing;
    private string? _errorDetails;
    private int _index = -1;
    private bool _isFirst = true;
    private bool _isLast = true;

    public TrackViewModel(TrackState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Id = state.Id == Guid.Empty ? Guid.NewGuid() : state.Id;
        SourcePath = Path.GetFullPath(state.SourcePath);
        AddedAt = state.AddedAt;
    }

    public Guid Id { get; }
    public string SourcePath { get; }
    public DateTimeOffset AddedAt { get; }
    public string FileName => Path.GetFileName(SourcePath);
    public string DirectoryName => Path.GetDirectoryName(SourcePath) ?? SourcePath;
    public string Extension => Path.GetExtension(SourcePath).TrimStart('.').ToUpperInvariant();
    public TimeSpan Duration => _duration;
    public bool IsAvailable => _isAvailable;

    /// <summary>Zero-based position in the visible playlist; refreshed by the owning view model.</summary>
    public int Index => _index;

    /// <summary>True for the first row, where "move up" cannot do anything.</summary>
    public bool IsFirst => _isFirst;

    /// <summary>True for the last row, where "move down" cannot do anything.</summary>
    public bool IsLast => _isLast;
    public string DurationText => _duration <= TimeSpan.Zero ? "—" :
        _duration.TotalHours >= 1 ? $"{(int)_duration.TotalHours}:{_duration:mm\\:ss}" : _duration.ToString(@"mm\:ss");

    public string TooltipText
    {
        get
        {
            if (IsAvailable) { return SourcePath; }
            var message = Loc.T(_sourceMissing ? "L_ErrorSourceMissing" : "L_ErrorAudioLoad");
            return string.IsNullOrWhiteSpace(_errorDetails)
                ? $"{message}\n{SourcePath}"
                : Loc.F("L_ErrorDetailsFormat", message, _errorDetails.Length <= 1200 ? _errorDetails : _errorDetails[^1200..]);
        }
    }

    public void SetAudioInfo(TimeSpan duration)
    {
        _duration = duration;
        _isAvailable = duration > TimeSpan.Zero;
        _sourceMissing = false;
        _errorDetails = null;
        RefreshProperties();
    }

    public void SetUnavailable(string? details, bool sourceMissing = false)
    {
        _isAvailable = false;
        _sourceMissing = sourceMissing;
        _errorDetails = details;
        RefreshProperties();
    }

    public PlaybackTrack ToPlaybackTrack() => new(Id, FileName, SourcePath, Duration);
    public TrackState ToState() => new() { Id = Id, SourcePath = SourcePath, AddedAt = AddedAt };
    public void RefreshLocalization() => OnPropertyChanged(nameof(TooltipText));

    /// <summary>Called by the playlist owner after any add, remove, move or clear.</summary>
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

    private void RefreshProperties()
    {
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(TooltipText));
    }
}
