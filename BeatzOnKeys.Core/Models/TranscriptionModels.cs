namespace BeatzOnKeys.Core.Models;

public enum TranscriptionFailureCode
{
    None,
    SourceMissing,
    PythonMissing,
    PythonScriptMissing,
    ProcessStartFailed,
    BasicPitchFailed,
    MidiOutputMissing,
    MidiInvalid,
    NoNotes,
    Cancelled,
    Unknown
}

public sealed record TrackTranscriptionRequest(Guid TrackId, string SourcePath, string MidiPath);

public sealed record TranscriptionProgress(
    Guid TrackId,
    TrackStatus Status,
    string? MidiPath = null,
    int NoteCount = 0,
    TranscriptionFailureCode FailureCode = TranscriptionFailureCode.None,
    string? Details = null);

public sealed record TranscriptionResult(
    Guid TrackId,
    string? MidiPath,
    IReadOnlyList<MusicalNote> Notes,
    TranscriptionFailureCode FailureCode,
    string? Details)
{
    public bool IsSuccess => FailureCode == TranscriptionFailureCode.None;
}
