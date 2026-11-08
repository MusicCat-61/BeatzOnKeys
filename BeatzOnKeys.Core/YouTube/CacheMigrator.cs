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
        CancellationToken cancellationToken = default) =>
        Transfer(from, to, recursive: false, copyOnly: false, cancellationToken);

    /// <summary>
    /// The same transfer, but able to walk subfolders and to copy instead of move. Migrating a whole
    /// data root needs both: the old folder must stay usable, and it has nested folders.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Move(
        string? from,
        string? to,
        bool recursive,
        bool copyOnly,
        CancellationToken cancellationToken = default) =>
        Transfer(from, to, recursive, copyOnly, cancellationToken);

    private static IReadOnlyDictionary<string, string> Transfer(
        string? from,
        string? to,
        bool recursive,
        bool copyOnly,
        CancellationToken cancellationToken)
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

        var options = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            if (File.Exists(destination))
            {
                // The same track is already there; pointing the caller at it is enough.
                moved[file] = destination;
                continue;
            }

            try
            {
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) { Directory.CreateDirectory(parent); }
                if (copyOnly) { File.Copy(file, destination); }
                else { File.Move(file, destination); }
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
