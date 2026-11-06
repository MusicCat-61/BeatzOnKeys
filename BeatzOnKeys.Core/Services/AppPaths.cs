namespace BeatzOnKeys.Core.Services;

/// <summary>An update staging folder that the current process can actually write to.</summary>
/// <param name="Path">The resolved folder.</param>
/// <param name="IsFallback">True when the per-user folder was blocked and the temp folder is used.</param>
/// <param name="PrimaryFailure">Why the per-user folder was rejected, for the log and the UI.</param>
public readonly record struct WritableUpdateDirectory(string Path, bool IsFallback, string? PrimaryFailure);

/// <summary>Stable per-user paths used by BeatzOnKeys.</summary>
public static class AppPaths
{
    public const string ProductName = "BeatzOnKeys";

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    public static string SettingsFile { get; } = Path.Combine(RootDirectory, "settings.json");
    public static string LogDirectory { get; } = Path.Combine(RootDirectory, "logs");
    public static string UpdateDirectory { get; } = Path.Combine(RootDirectory, "updates");

    /// <summary>
    /// Where updates go when the per-user folder refuses writes, which happens when the app runs
    /// under a restricted token or the profile folder lost its permissions. Kept as a field so the
    /// test harnesses can redirect it the same way they redirect the other paths.
    /// </summary>
    public static string FallbackUpdateDirectory { get; } =
        Path.Combine(Path.GetTempPath(), ProductName, "updates");

    /// <summary>Every folder that may hold a staged update, preferred first.</summary>
    public static IReadOnlyList<string> UpdateDirectories => [UpdateDirectory, FallbackUpdateDirectory];

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(UpdateDirectory);
    }

    /// <summary>
    /// Picks the first update folder that is genuinely writable by creating and removing a probe
    /// file, so a permission problem is discovered before a large download starts. Returns null
    /// when every candidate is blocked; the caller must report that instead of a raw access error.
    /// </summary>
    public static WritableUpdateDirectory? ResolveUpdateDirectory()
    {
        string? primaryFailure = null;
        foreach (var candidate in UpdateDirectories)
        {
            if (IsWritable(candidate, out var failure))
            {
                var isFallback = !string.Equals(candidate, UpdateDirectory, StringComparison.OrdinalIgnoreCase);
                return new WritableUpdateDirectory(candidate, isFallback, isFallback ? primaryFailure : null);
            }

            primaryFailure ??= failure;
        }

        return null;
    }

    /// <summary>True when a staged update lives under one of the known update folders.</summary>
    public static bool IsInsideUpdateDirectory(string path)
    {
        foreach (var root in UpdateDirectories)
        {
            var prefix = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWritable(string directory, out string? failure)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                                              or NotSupportedException or System.Security.SecurityException
                                              or ArgumentException)
        {
            failure = $"{directory}: {exception.Message}";
            return false;
        }
    }
}