namespace BeatzOnKeys.Core.Models;

/// <summary>A typed failure from the bundled Basic Pitch process.</summary>
public sealed class TranscriptionException : Exception
{
    public TranscriptionException(
        TranscriptionFailureCode failureCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    public TranscriptionFailureCode FailureCode { get; }
}
