using System.IO;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.UI.Infrastructure;
using BeatzOnKeys.UI.Localization;

namespace BeatzOnKeys.UI.ViewModels;

public sealed class TrackViewModel : ObservableObject
{
    private TrackStatus _status;
    private string? _midiPath;
    private string? _errorCode;
    private string? _errorDetails;
    private int _noteCount;
    private IReadOnlyList<MusicalNote> _notes = [];

    public TrackViewModel(TrackState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Id = state.Id == Guid.Empty ? Guid.NewGuid() : state.Id;
        SourcePath = Path.GetFullPath(state.SourcePath);
        AddedAt = state.AddedAt;
        _midiPath = state.MidiPath;
        _status = state.Status == TrackStatus.Processing ? TrackStatus.Pending : state.Status;
        _errorCode = state.ErrorCode;
        _errorDetails = state.ErrorDetails;
        _noteCount = state.NoteCount;
    }

    public Guid Id { get; }

    public string SourcePath { get; }

    public DateTimeOffset AddedAt { get; }

    public string FileName => Path.GetFileName(SourcePath);

    public string DirectoryName => Path.GetDirectoryName(SourcePath) ?? SourcePath;

    public string Extension => Path.GetExtension(SourcePath).TrimStart('.').ToUpperInvariant();

    public string? MidiPath
    {
        get => _midiPath;
        private set => SetProperty(ref _midiPath, value);
    }

    public TrackStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusDetails));
                OnPropertyChanged(nameof(TooltipText));
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(IsProcessing));
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public int NoteCount
    {
        get => _noteCount;
        private set
        {
            if (SetProperty(ref _noteCount, value))
            {
                OnPropertyChanged(nameof(StatusDetails));
            }
        }
    }

    public IReadOnlyList<MusicalNote> Notes => _notes;

    public bool IsReady => Status == TrackStatus.Ready;

    public bool IsProcessing => Status == TrackStatus.Processing;

    public bool HasError => Status == TrackStatus.Error;

    public string StatusText => Status switch
    {
        TrackStatus.Processing => Loc.T("L_TrackStatusProcessing"),
        TrackStatus.Ready => Loc.T("L_TrackStatusReady"),
        TrackStatus.Error => Loc.T("L_TrackStatusError"),
        _ => Loc.T("L_TrackStatusPending")
    };

    public string StatusDetails => Status switch
    {
        TrackStatus.Ready when NoteCount > 0 => Loc.F("L_NotesFormat", NoteCount),
        TrackStatus.Error => LocalizedError,
        _ => string.Empty
    };

    public string TooltipText => Status == TrackStatus.Error
        ? BuildErrorTooltip()
        : SourcePath;

    public void ApplyProgress(TranscriptionProgress progress)
    {
        Status = progress.Status;
        if (!string.IsNullOrWhiteSpace(progress.MidiPath))
        {
            MidiPath = progress.MidiPath;
        }

        if (progress.NoteCount > 0)
        {
            NoteCount = progress.NoteCount;
        }

        if (progress.FailureCode != TranscriptionFailureCode.None)
        {
            _errorCode = progress.FailureCode.ToString();
            _errorDetails = progress.Details;
        }
        else if (progress.Status != TrackStatus.Error)
        {
            _errorCode = null;
            _errorDetails = null;
        }

        OnPropertyChanged(nameof(StatusDetails));
        OnPropertyChanged(nameof(TooltipText));
    }

    public void SetReady(string midiPath, IReadOnlyList<MusicalNote> notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(midiPath);
        ArgumentNullException.ThrowIfNull(notes);
        _notes = notes;
        MidiPath = midiPath;
        NoteCount = notes.Count;
        _errorCode = null;
        _errorDetails = null;
        Status = TrackStatus.Ready;
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(TooltipText));
    }

    public void SetError(TranscriptionFailureCode code, string? details)
    {
        _notes = [];
        _errorCode = code.ToString();
        _errorDetails = details;
        Status = TrackStatus.Error;
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(StatusDetails));
        OnPropertyChanged(nameof(TooltipText));
    }

    public void SetPending()
    {
        _notes = [];
        NoteCount = 0;
        _errorCode = null;
        _errorDetails = null;
        Status = TrackStatus.Pending;
        OnPropertyChanged(nameof(Notes));
    }

    public TrackState ToState() => new()
    {
        Id = Id,
        SourcePath = SourcePath,
        MidiPath = MidiPath,
        Status = Status == TrackStatus.Processing ? TrackStatus.Pending : Status,
        ErrorCode = _errorCode,
        ErrorDetails = _errorDetails,
        NoteCount = NoteCount,
        AddedAt = AddedAt
    };

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetails));
        OnPropertyChanged(nameof(TooltipText));
    }

    private string LocalizedError
    {
        get
        {
            _ = Enum.TryParse<TranscriptionFailureCode>(_errorCode, out var code);
            return code switch
            {
                TranscriptionFailureCode.SourceMissing => Loc.T("L_ErrorSourceMissing"),
                TranscriptionFailureCode.PythonMissing => Loc.T("L_ErrorPythonMissing"),
                TranscriptionFailureCode.PythonScriptMissing => Loc.T("L_ErrorPythonScriptMissing"),
                TranscriptionFailureCode.ProcessStartFailed => Loc.T("L_ErrorProcessStart"),
                TranscriptionFailureCode.BasicPitchFailed => Loc.T("L_ErrorBasicPitch"),
                TranscriptionFailureCode.MidiOutputMissing => Loc.T("L_ErrorMidiMissing"),
                TranscriptionFailureCode.MidiInvalid => Loc.T("L_ErrorMidiInvalid"),
                TranscriptionFailureCode.NoNotes => Loc.T("L_ErrorNoNotes"),
                _ => Loc.T("L_ErrorUnknown")
            };
        }
    }

    private string BuildErrorTooltip()
    {
        var localized = LocalizedError;
        if (string.IsNullOrWhiteSpace(_errorDetails))
        {
            return localized;
        }

        var details = _errorDetails.Length <= 1200 ? _errorDetails : _errorDetails[^1200..];
        return Loc.F("L_ErrorDetailsFormat", localized, details);
    }
}
