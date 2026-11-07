namespace BeatzOnKeys.Core.YouTube;

/// <summary>
/// Moves the YouTube cache to another folder. The cache is a flat set of files named after the
/// video id, so a move is a rename per file; the caller gets a map from the old path to the new one
/// and rewrites whatever it stores.
/// </summary>
public static class CacheMigrator
{
    /// <summary>
    /// Moves every file from <paramref name="from"/> into <paramref name="to"/>. A file that already
    /// exists at the destination is left alone and mapped to it, because the names are video ids and
    /// therefore the same track. Returns an empty map when the folders are the same.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Move(
        string? from,
        string? to,
        CancellationToken cancellationToken = default)
    {
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) { return moved; }

        string source;
        string target;
        try
        {
            source = Path.GetFullPath(from);
            target = Path.GetFullPath(to);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return moved;
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) { return moved; }
        Directory.CreateDirectory(target);
        if (!Directory.Exists(source)) { return moved; }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                // The same track is already there; pointing the caller at it is enough.
                moved[file] = destination;
                continue;
            }

            try
            {
                File.Move(file, destination);
                moved[file] = destination;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file that is playing right now cannot move; it stays put and keeps its old path.
            }
        }

        return moved;
    }
}
