namespace BeatzOnKeys.Core.Updates;

/// <summary>
/// Raised when no folder accepts the downloaded update. It exists so the UI can explain the
/// permission problem in plain words instead of showing a raw "Access is denied" message.
/// </summary>
public sealed class UpdateStorageException(string message, IReadOnlyList<string> attemptedPaths, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Every folder that was tried, in order, for the diagnostics and the UI.</summary>
    public IReadOnlyList<string> AttemptedPaths { get; } = attemptedPaths;
}