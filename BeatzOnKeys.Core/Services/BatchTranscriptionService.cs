using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Services;

/// <summary>Processes imported tracks sequentially and reports every state transition to the UI.</summary>
public sealed class BatchTranscriptionService
{
    private readonly BasicPitchTranscriber _transcriber;
    private readonly MidiNoteParser _midiParser;

    public BatchTranscriptionService(BasicPitchTranscriber transcriber, MidiNoteParser midiParser)
    {
        _transcriber = transcriber;
        _midiParser = midiParser;
    }

    public async Task<IReadOnlyList<TranscriptionResult>> ProcessAsync(
        IEnumerable<TrackTranscriptionRequest> requests,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new List<TranscriptionResult>();

        foreach (var request in requests)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            progress?.Report(new TranscriptionProgress(request.TrackId, TrackStatus.Processing));

            try
            {
                var midiPath = await _transcriber.TranscribeAsync(
                    request.SourcePath,
                    request.MidiPath,
                    cancellationToken).ConfigureAwait(false);

                IReadOnlyList<MusicalNote> notes;
                try
                {
                    notes = await Task.Run(() => _midiParser.Parse(midiPath), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new TranscriptionException(
                        TranscriptionFailureCode.MidiInvalid,
                        exception.Message,
                        exception);
                }

                if (notes.Count == 0)
                {
                    throw new TranscriptionException(
                        TranscriptionFailureCode.NoNotes,
                        "The generated MIDI file contains no notes.");
                }

                var result = new TranscriptionResult(
                    request.TrackId,
                    midiPath,
                    notes,
                    TranscriptionFailureCode.None,
                    null);
                results.Add(result);
                progress?.Report(new TranscriptionProgress(
                    request.TrackId,
                    TrackStatus.Ready,
                    midiPath,
                    notes.Count));
            }
            catch (OperationCanceledException)
            {
                var cancelled = new TranscriptionResult(
                    request.TrackId,
                    null,
                    [],
                    TranscriptionFailureCode.Cancelled,
                    null);
                results.Add(cancelled);
                progress?.Report(new TranscriptionProgress(
                    request.TrackId,
                    TrackStatus.Pending,
                    FailureCode: TranscriptionFailureCode.Cancelled));
                break;
            }
            catch (TranscriptionException exception)
            {
                AddFailure(results, progress, request.TrackId, exception.FailureCode, exception.Message);
            }
            catch (Exception exception)
            {
                AppLogger.Error("transcription", "Unexpected batch transcription failure.", exception);
                AddFailure(results, progress, request.TrackId, TranscriptionFailureCode.Unknown, exception.Message);
            }
        }

        return results;
    }

    private static void AddFailure(
        ICollection<TranscriptionResult> results,
        IProgress<TranscriptionProgress>? progress,
        Guid trackId,
        TranscriptionFailureCode failureCode,
        string? details)
    {
        results.Add(new TranscriptionResult(trackId, null, [], failureCode, details));
        progress?.Report(new TranscriptionProgress(
            trackId,
            TrackStatus.Error,
            FailureCode: failureCode,
            Details: details));
    }
}
