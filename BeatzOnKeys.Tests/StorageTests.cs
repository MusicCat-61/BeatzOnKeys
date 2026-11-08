using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.Core.YouTube;

namespace BeatzOnKeys.Tests;

/// <summary>
/// Where the app keeps its data. The rules are small but easy to get wrong, and a mistake here
/// scatters files on the desktop or loses the user's playlist, so each one is pinned.
/// </summary>
internal static class StorageTests
{
    /// <summary>A normal writable folder beside the executable means portable: data lives there.</summary>
    public static Task PortableFolderKeepsDataBesideTheExecutable()
    {
        using var folder = new TemporaryDirectory();
        var mode = AppPaths.DetectMode(folder.Path);
        AssertEx.Equal(StorageMode.Portable, mode);
        return Task.CompletedTask;
    }

    /// <summary>The user profile root is not a place to drop application folders.</summary>
    public static Task ProfileRootIsUnsuitable()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AssertEx.True(AppPaths.IsUnsuitablePlace(profile), "The profile root must not host application data.");
        AssertEx.True(AppPaths.IsUnsuitablePlace(Path.GetTempPath()), "The temp folder must not host application data.");
        return Task.CompletedTask;
    }

    /// <summary>A missing folder cannot be written to, so it falls back to the per-user location.</summary>
    public static Task MissingFolderIsNotWritable()
    {
        var missing = Path.Combine(Path.GetTempPath(), "beatzonkeys-missing-" + Guid.NewGuid().ToString("N"));
        AssertEx.True(!AppPaths.CanWrite(missing), "A folder that does not exist is not writable.");
        return Task.CompletedTask;
    }

    /// <summary>Every path follows the root, and the log folder is spelled "Logs".</summary>
    public static Task PathsFollowTheRoot()
    {
        var original = (AppPaths.Mode, AppPaths.RootDirectory, AppPaths.LogDirectory, AppPaths.SettingsFile,
            AppPaths.UpdateDirectory, AppPaths.MusicDirectory, AppPaths.BinDirectory, AppPaths.DownloadDirectory);
        using var folder = new TemporaryDirectory();
        try
        {
            AppPaths.Apply(StorageMode.Portable, folder.Path);
            AssertEx.Equal(StorageMode.Portable, AppPaths.Mode);
            AssertEx.Equal(Path.GetFullPath(folder.Path), AppPaths.RootDirectory);
            AssertEx.Equal(Path.Combine(AppPaths.RootDirectory, "settings.json"), AppPaths.SettingsFile);
            AssertEx.Equal(Path.Combine(AppPaths.RootDirectory, "Logs"), AppPaths.LogDirectory);
            AssertEx.Equal(Path.Combine(AppPaths.RootDirectory, "updates"), AppPaths.UpdateDirectory);
            AssertEx.Equal(Path.Combine(AppPaths.RootDirectory, "Music"), AppPaths.MusicDirectory);
            AssertEx.Equal(Path.Combine(AppPaths.RootDirectory, "bin"), AppPaths.BinDirectory);
            AssertEx.Equal(Path.Combine(AppPaths.RootDirectory, "downloads"), AppPaths.DownloadDirectory);
            AssertEx.Equal(Path.Combine(AppPaths.RootDirectory, "apply-update.cmd"), AppPaths.ApplyScriptPath);
            AssertEx.Equal(Path.Combine(AppPaths.RootDirectory, "Logs", "update-apply.log"), AppPaths.ApplyLogPath);
            AssertEx.Equal(Path.Combine(AppPaths.RootDirectory, "BeatzOnKeys.exe"), AppPaths.StagedFilePath);
        }
        finally
        {
            AppPaths.Apply(original.Item1, original.Item2);
        }

        return Task.CompletedTask;
    }

    /// <summary>The log folder is created in every mode, including the fallback one.</summary>
    public static Task LogsFolderIsCreatedInEveryMode()
    {
        var original = AppPaths.RootDirectory;
        using var folder = new TemporaryDirectory();
        try
        {
            foreach (var mode in new[] { StorageMode.Portable, StorageMode.ChosenDirectory, StorageMode.LocalAppData })
            {
                var root = Path.Combine(folder.Path, mode.ToString());
                AppPaths.Apply(mode, root);
                AppPaths.EnsureCreated();
                AssertEx.True(Directory.Exists(AppPaths.LogDirectory), $"{mode}: Logs must exist.");
            }
        }
        finally
        {
            AppPaths.Apply(StorageMode.LocalAppData, original);
        }

        return Task.CompletedTask;
    }

    /// <summary>The chosen folder gets a subfolder of its own instead of swallowing the pick.</summary>
    public static Task ChosenFolderGetsItsOwnSubfolder()
    {
        AssertEx.Equal(Path.Combine(@"C:\somewhere", "BeatzOnKeys"), AppPaths.ChosenRootFor(@"C:\somewhere"));
        return Task.CompletedTask;
    }

    /// <summary>A recorded folder that still exists is used without asking again.</summary>
    public static Task RecordedFolderIsReused()
    {
        using var folder = new TemporaryDirectory();
        var settings = new StorageSettings { ChosenRoot = folder.Path };
        var plan = StorageResolver.PlanForTesting(StorageMode.ChosenDirectory, settings);
        AssertEx.Equal(StorageMode.ChosenDirectory, plan.Mode);
        AssertEx.Equal(Path.GetFullPath(folder.Path), plan.Root);
        AssertEx.True(!plan.NeedsFolderChoice, "An existing folder must not trigger the picker.");
        return Task.CompletedTask;
    }

    /// <summary>A folder that disappeared asks again, and the recorded answer stays on disk.</summary>
    public static Task MissingRecordedFolderAsksAgainWithoutForgetting()
    {
        using var folder = new TemporaryDirectory();
        var storageFile = Path.Combine(folder.Path, "storage.json");
        var gone = Path.Combine(folder.Path, "gone");
        StorageSettingsStore.Save(new StorageSettings { ChosenRoot = gone }, storageFile);

        var loaded = StorageSettingsStore.Load(storageFile);
        var plan = StorageResolver.PlanForTesting(StorageMode.ChosenDirectory, loaded);

        AssertEx.True(plan.NeedsFolderChoice, "The picker must come back for a folder that disappeared.");
        AssertEx.True(File.Exists(storageFile), "storage.json must not be deleted just because the folder vanished.");
        AssertEx.Equal(gone, StorageSettingsStore.Load(storageFile).ChosenRoot);
        return Task.CompletedTask;
    }

    /// <summary>"Do not ask again" turns a missing folder into a plain fallback, with no dialog.</summary>
    public static Task DontAskFallsBackWithoutAPrompt()
    {
        using var folder = new TemporaryDirectory();
        var settings = new StorageSettings { ChosenRoot = Path.Combine(folder.Path, "gone"), DontAsk = true };
        var plan = StorageResolver.PlanForTesting(StorageMode.ChosenDirectory, settings);
        AssertEx.Equal(StorageMode.LocalAppData, plan.Mode);
        AssertEx.True(!plan.NeedsFolderChoice, "dontAsk means no dialog even when the folder is gone.");
        return Task.CompletedTask;
    }

    /// <summary>The choice is written atomically, so a reader never sees half a file.</summary>
    public static Task StorageIsWrittenAtomically()
    {
        using var folder = new TemporaryDirectory();
        var storageFile = Path.Combine(folder.Path, "storage.json");
        AssertEx.True(StorageSettingsStore.Save(new StorageSettings { ChosenRoot = @"C:\one" }, storageFile),
            "The first save succeeds.");
        AssertEx.True(StorageSettingsStore.Save(new StorageSettings { ChosenRoot = @"C:\two", DontAsk = true }, storageFile),
            "The second save replaces the first.");
        var loaded = StorageSettingsStore.Load(storageFile);
        AssertEx.Equal(@"C:\two", loaded.ChosenRoot);
        AssertEx.True(loaded.DontAsk, "The flag survives the round trip.");
        AssertEx.True(!File.Exists(storageFile + ".tmp"), "The temporary file is gone after a save.");
        return Task.CompletedTask;
    }

    /// <summary>A malformed file is not a crash: it simply means "no choice recorded yet".</summary>
    public static Task BrokenStorageJsonFallsBack()
    {
        using var folder = new TemporaryDirectory();
        var storageFile = Path.Combine(folder.Path, "storage.json");
        File.WriteAllText(storageFile, "{ this is not json ");
        var loaded = StorageSettingsStore.Load(storageFile);
        AssertEx.Equal(string.Empty, loaded.ChosenRoot);
        AssertEx.True(!loaded.DontAsk, "A broken file must not turn the question off.");
        return Task.CompletedTask;
    }

    /// <summary>The migration copies a whole older data set and never moves the originals.</summary>
    public static Task MigrationCopiesEverythingAndKeepsTheOriginal()
    {
        using var source = new TemporaryDirectory();
        using var target = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(source.Path, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(source.Path, "logs"));
        File.WriteAllText(Path.Combine(source.Path, "logs", "beatzonkeys-20260101.log"), "line");
        Directory.CreateDirectory(Path.Combine(source.Path, "Music"));
        File.WriteAllBytes(Path.Combine(source.Path, "Music", "track.m4a"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(source.Path, "bin"));
        File.WriteAllBytes(Path.Combine(source.Path, "bin", "yt-dlp.exe"), [4, 5]);
        Directory.CreateDirectory(Path.Combine(source.Path, "downloads"));
        File.WriteAllBytes(Path.Combine(source.Path, "downloads", "scratch.part"), [6]);
        Directory.CreateDirectory(Path.Combine(source.Path, "updates"));
        File.WriteAllBytes(Path.Combine(source.Path, "updates", "staged.exe"), [7]);

        var copied = StorageResolver.Migrate(source.Path, target.Path);

        AssertEx.Equal(6, copied);
        AssertEx.True(File.Exists(Path.Combine(target.Path, "settings.json")), "settings.json copied.");
        AssertEx.True(File.Exists(Path.Combine(target.Path, "Logs", "beatzonkeys-20260101.log")),
            "The old logs folder lands in Logs.");
        AssertEx.True(File.Exists(Path.Combine(target.Path, "Music", "track.m4a")), "The cache copied.");
        AssertEx.True(File.Exists(Path.Combine(target.Path, "bin", "yt-dlp.exe")), "The binaries copied.");
        AssertEx.True(File.Exists(Path.Combine(target.Path, "downloads", "scratch.part")), "The scratch folder copied.");
        AssertEx.True(File.Exists(Path.Combine(target.Path, "updates", "staged.exe")), "The updates folder copied.");
        AssertEx.True(File.Exists(Path.Combine(source.Path, "settings.json")), "The original settings are untouched.");
        AssertEx.True(File.Exists(Path.Combine(source.Path, "Music", "track.m4a")), "The original cache is untouched.");
        return Task.CompletedTask;
    }

    /// <summary>A second run must not copy again, otherwise every start would duplicate the data.</summary>
    public static Task MigrationDoesNotRunTwice()
    {
        using var source = new TemporaryDirectory();
        using var target = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(source.Path, "settings.json"), "{}");
        StorageResolver.Migrate(source.Path, target.Path);
        var second = StorageResolver.Migrate(source.Path, target.Path);
        AssertEx.Equal(0, second);
        return Task.CompletedTask;
    }

    /// <summary>The recursive transfer walks subfolders and can leave the source in place.</summary>
    public static Task CacheMigratorCopiesRecursively()
    {
        using var source = new TemporaryDirectory();
        using var target = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(source.Path, "nested"));
        File.WriteAllBytes(Path.Combine(source.Path, "nested", "file.bin"), [1, 2]);

        var moved = CacheMigrator.Move(source.Path, target.Path, recursive: true, copyOnly: true);

        AssertEx.Equal(1, moved.Count);
        AssertEx.True(File.Exists(Path.Combine(target.Path, "nested", "file.bin")), "The nested file copied.");
        AssertEx.True(File.Exists(Path.Combine(source.Path, "nested", "file.bin")), "The source stayed in place.");
        return Task.CompletedTask;
    }

    /// <summary>The old settings file carries a field that no longer exists; it must still load.</summary>
    public static Task LegacyCachePathSettingIsIgnored()
    {
        using var folder = new TemporaryDirectory();
        var file = Path.Combine(folder.Path, "settings.json");
        // The cache may live anywhere: an absolute path to an existing file must keep working
        // now that the app no longer stores a cache folder of its own.
        var cached = Path.Combine(folder.Path, "abc.m4a");
        File.WriteAllBytes(cached, [1, 2, 3]);
        // Built with the serializer, because a Windows path in a hand-written JSON string would
        // need escaped backslashes and a typo there would silently look like a broken settings file.
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersion = 5,
            YouTubeCachePath = folder.Path,
            YouTubeTracks = new[] { new { VideoId = "abc", CachedPath = cached, Status = 2 } }
        });
        File.WriteAllText(file, json);

        var settings = new SettingsService(file).LoadAsync().GetAwaiter().GetResult();

        AssertEx.Equal(1, settings.YouTubeTracks.Count);
        AssertEx.Equal(cached, settings.YouTubeTracks[0].CachedPath);
        AssertEx.Equal(YouTubeTrackStatus.Ready, settings.YouTubeTracks[0].Status);
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        return Task.CompletedTask;
    }

}
