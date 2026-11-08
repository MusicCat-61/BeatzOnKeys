namespace BeatzOnKeys.Core.Services;

/// <summary>What the storage step decided, before any dialog is shown.</summary>
public sealed record StoragePlan(StorageMode Mode, string Root, bool NeedsFolderChoice);

/// <summary>
/// Decides where this run keeps its data. Pure decision-making lives here; asking the user for a
/// folder is the window's job, because a console test cannot answer a folder picker.
/// </summary>
public static class StorageResolver
{
    /// <summary>Lets a test or a smoke run pin the mode without touching the real user folders.</summary>
    public const string ModeVariable = "BEATZONKEYS_FORCE_STORAGE_MODE";

    /// <summary>True when a harness pinned the mode, which also means it owns the paths.</summary>
    public static bool IsForced => ForcedMode() is not null;

    /// <summary>The mode forced through the environment, if any and if it parses.</summary>
    public static StorageMode? ForcedMode()
    {
        var raw = Environment.GetEnvironmentVariable(ModeVariable);
        if (string.IsNullOrWhiteSpace(raw)) { return null; }
        return Enum.TryParse<StorageMode>(raw.Trim(), ignoreCase: true, out var mode) ? mode : null;
    }

    /// <summary>
    /// Works out the mode and root for this run. A portable copy keeps everything beside itself; a
    /// copy on the desktop or in an unsuitable folder asks the user where its data should live, and
    /// falls back to the per-user folder when that answer is not available.
    /// </summary>
    public static StoragePlan Plan(string? executableDirectory, StorageSettings settings)
    {
        var forced = ForcedMode();
        if (forced is { } mode)
        {
            return mode switch
            {
                StorageMode.Portable when !string.IsNullOrWhiteSpace(executableDirectory) =>
                    new StoragePlan(StorageMode.Portable, Path.GetFullPath(executableDirectory), false),
                StorageMode.ChosenDirectory => Chosen(settings),
                _ => new StoragePlan(StorageMode.LocalAppData, AppPaths.DefaultLocalRoot, false)
            };
        }

        return DetectPlan(executableDirectory, settings);
    }

    /// <summary>
    /// The decision for an already-detected mode. The picker branch cannot be reached by placing a
    /// test folder on a real desktop, so the detection result is passed in instead of guessed.
    /// </summary>
    internal static StoragePlan PlanForTesting(StorageMode detected, StorageSettings settings) =>
        detected switch
        {
            StorageMode.Portable => new StoragePlan(StorageMode.Portable, AppPaths.DefaultLocalRoot, false),
            StorageMode.ChosenDirectory => Chosen(settings),
            _ => new StoragePlan(StorageMode.LocalAppData, AppPaths.DefaultLocalRoot, false)
        };

    private static StoragePlan DetectPlan(string? executableDirectory, StorageSettings settings)
    {
        var detected = AppPaths.DetectMode(executableDirectory);
        return detected switch
        {
            StorageMode.Portable => new StoragePlan(
                StorageMode.Portable, Path.GetFullPath(executableDirectory!), false),
            StorageMode.ChosenDirectory => Chosen(settings),
            _ => new StoragePlan(StorageMode.LocalAppData, AppPaths.DefaultLocalRoot, false)
        };
    }

    /// <summary>
    /// Uses the recorded folder when it still exists. A folder that disappeared is not a reason to
    /// forget the choice: the answer stays on disk and the user is asked again.
    /// </summary>
    private static StoragePlan Chosen(StorageSettings settings)
    {
        var recorded = settings.UsableRoot;
        if (recorded is not null && Directory.Exists(recorded) && AppPaths.CanWrite(recorded))
        {
            return new StoragePlan(StorageMode.ChosenDirectory, Path.GetFullPath(recorded), false);
        }

        if (settings.DontAsk)
        {
            return new StoragePlan(StorageMode.LocalAppData, AppPaths.DefaultLocalRoot, false);
        }

        return new StoragePlan(StorageMode.ChosenDirectory, AppPaths.DefaultLocalRoot, true);
    }

    /// <summary>
    /// Copies an older per-user data set into the new root the first time it is used. The originals
    /// stay where they are: a user who tries the portable build and goes back must not lose anything.
    /// Copying is skipped once a settings file exists at the destination, so it never runs twice.
    /// </summary>
    public static int Migrate(string? sourceRoot, string? targetRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(targetRoot)) { return 0; }
        string source;
        string target;
        try
        {
            source = Path.GetFullPath(sourceRoot);
            target = Path.GetFullPath(targetRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return 0;
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) { return 0; }
        if (!Directory.Exists(source)) { return 0; }
        // Anything already in place means this root has been used before, so the migration is done.
        if (File.Exists(Path.Combine(target, "settings.json"))) { return 0; }

        var copied = 0;
        try
        {
            Directory.CreateDirectory(target);
            copied += CopyFile(Path.Combine(source, "settings.json"), Path.Combine(target, "settings.json"));

            // The old build spelled the folder "logs"; the new one spells it "Logs" everywhere.
            copied += CopyFiles(Path.Combine(source, "logs"), Path.Combine(target, "Logs"), "*.log", cancellationToken);

            foreach (var folder in new[] { "updates", "Music", "bin", "downloads" })
            {
                copied += CopyFiles(Path.Combine(source, folder), Path.Combine(target, folder), "*", cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning("storage", $"The data migration stopped early: {exception.Message}");
        }

        if (copied > 0)
        {
            AppLogger.Info("storage", $"Migrated {copied} files from '{source}' to '{target}'; the originals were left in place.");
        }

        return copied;
    }

    private static int CopyFile(string source, string target)
    {
        try
        {
            if (!File.Exists(source)) { return 0; }
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) { Directory.CreateDirectory(parent); }
            File.Copy(source, target, overwrite: false);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static int CopyFiles(string source, string target, string pattern, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) { return 0; }
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, pattern, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            copied += CopyFile(file, Path.Combine(target, relative));
        }

        return copied;
    }
}
