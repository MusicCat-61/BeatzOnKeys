using System.Runtime.InteropServices;

namespace BeatzOnKeys.Core.Services;

/// <summary>Where the application keeps its data for this run.</summary>
public enum StorageMode
{
    /// <summary>The executable sits in an ordinary writable folder, so data lives beside it.</summary>
    Portable,

    /// <summary>The executable sits somewhere unsuitable; the user picked a folder to hold the data.</summary>
    ChosenDirectory,

    /// <summary>Nothing else worked, so the data falls back to the per-user application data folder.</summary>
    LocalAppData
}

/// <summary>
/// Stable paths used by BeatzOnKeys. All data lives under one root: settings, logs, updates, the
/// YouTube cache, third-party binaries and download scratch space. The root depends on where the
/// executable sits, because a build dropped on the desktop or into Program Files must not write
/// next to itself.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "BeatzOnKeys";

    /// <summary>Folder name created inside a folder the user picks; keeps our files in one place.</summary>
    private const string ChosenFolderName = ProductName;

    public static StorageMode Mode { get; private set; } = StorageMode.LocalAppData;

    /// <summary>Set once the root has been resolved; the fallback until then is the per-user folder.</summary>
    public static string RootDirectory { get; private set; } = DefaultLocalRoot;

    public static string SettingsFile { get; private set; } = Path.Combine(DefaultLocalRoot, "settings.json");

    /// <summary>Log folder. Spelled "Logs" everywhere so the two spellings cannot drift apart.</summary>
    public static string LogDirectory { get; private set; } = Path.Combine(DefaultLocalRoot, "Logs");

    public static string UpdateDirectory { get; private set; } = Path.Combine(DefaultLocalRoot, "updates");

    /// <summary>Downloaded YouTube audio, always under the root so it moves with the rest.</summary>
    public static string MusicDirectory { get; private set; } = Path.Combine(DefaultLocalRoot, "Music");

    /// <summary>Third-party executables (yt-dlp, ffmpeg).</summary>
    public static string BinDirectory { get; private set; } = Path.Combine(DefaultLocalRoot, "bin");

    /// <summary>Scratch space for a download before the finished file is moved into the cache.</summary>
    public static string DownloadDirectory { get; private set; } = Path.Combine(DefaultLocalRoot, "downloads");

    /// <summary>The staged update and the script that swaps it in both live under the root.</summary>
    public static string StagedFilePath => Path.Combine(RootDirectory, "BeatzOnKeys.exe");

    public static string ApplyScriptPath => Path.Combine(RootDirectory, "apply-update.cmd");

    public static string ApplyLogPath => Path.Combine(LogDirectory, "update-apply.log");

    /// <summary>
    /// The per-user folder, used as the fallback and as the place the choice is recorded. Computed
    /// rather than assigned, so the fields above can safely refer to it in their initializers.
    /// </summary>
    public static string DefaultLocalRoot => LocalRoot();

    /// <summary>Records the folder the user picked, and whether they asked not to be asked again.</summary>
    public static string StorageFile { get; } = Path.Combine(DefaultLocalRoot, "storage.json");

    /// <summary>The folder inside a picked directory that holds everything this app writes.</summary>
    public static string ChosenRootFor(string folder) => Path.Combine(folder, ChosenFolderName);

    /// <summary>Points every path at <paramref name="root"/> and remembers the mode that chose it.</summary>
    public static void Apply(StorageMode mode, string root)
    {
        var full = Path.GetFullPath(root);
        Mode = mode;
        RootDirectory = full;
        SettingsFile = Path.Combine(full, "settings.json");
        LogDirectory = Path.Combine(full, "Logs");
        UpdateDirectory = Path.Combine(full, "updates");
        MusicDirectory = Path.Combine(full, "Music");
        BinDirectory = Path.Combine(full, "bin");
        DownloadDirectory = Path.Combine(full, "downloads");
    }

    /// <summary>
    /// Picks the mode for this run. The desktop and the places Windows treats as shared or
    /// protected cannot hold data next to the executable, so they lead to the folder picker
    /// instead of scattering files where the user does not expect them.
    /// </summary>
    public static StorageMode DetectMode(string? executableDirectory)
    {
        if (string.IsNullOrWhiteSpace(executableDirectory)) { return StorageMode.LocalAppData; }
        if (!CanWrite(executableDirectory)) { return StorageMode.LocalAppData; }
        if (IsDesktop(executableDirectory) || IsUnsuitablePlace(executableDirectory))
        {
            return StorageMode.ChosenDirectory;
        }

        return StorageMode.Portable;
    }

    /// <summary>
    /// True for the folders where dropping our files would be surprising or forbidden: the user
    /// profile root, the shared profile, a drive root, Windows, Program Files and temp.
    /// </summary>
    public static bool IsUnsuitablePlace(string directory)
    {
        string full;
        try { full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        foreach (var candidate in UnsuitableDirectories())
        {
            if (string.Equals(full, candidate, StringComparison.OrdinalIgnoreCase)) { return true; }
        }

        return false;
    }

    private static IEnumerable<string> UnsuitableDirectories()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var publicProfile = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // The shared profile lives beside the user profile; CommonDocuments is one level too deep.
        var publicRoot = string.IsNullOrEmpty(publicProfile) ? string.Empty : Path.GetDirectoryName(publicProfile) ?? string.Empty;
        foreach (var candidate in new[] { profile, publicRoot, publicProfile, windows, programFiles, programFilesX86,
                     Path.GetPathRoot(Path.GetTempPath()), Path.GetTempPath() })
        {
            if (string.IsNullOrWhiteSpace(candidate)) { continue; }
            string full;
            try { full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (full.Length > 0) { yield return full; }
        }
    }

    /// <summary>True when the executable sits on the user's desktop, which is not a place for data.</summary>
    public static bool IsDesktop(string directory)
    {
        var desktop = DesktopDirectory();
        if (string.IsNullOrEmpty(desktop)) { return false; }
        try
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, desktop, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The real desktop folder. The shell is asked rather than the environment variable, because a
    /// redirected desktop is exactly the case where guessing wrong would put files in the wrong place.
    /// </summary>
    public static string DesktopDirectory()
    {
        try
        {
            var folder = Guid.Parse("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
            var result = SHGetKnownFolderPath(folder, 0, IntPtr.Zero, out var path);
            if (result != 0 || path == IntPtr.Zero) { return string.Empty; }
            try { return Marshal.PtrToStringUni(path) ?? string.Empty; }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return string.Empty;
        }
    }

    /// <summary>Probes the folder with a real file, because permission bits lie on Windows.</summary>
    public static bool CanWrite(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) { return false; }
        try
        {
            if (!Directory.Exists(directory)) { return false; }
            var probe = Path.Combine(directory, $".beatzonkeys-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(UpdateDirectory);
    }

    /// <summary>The folders the YouTube section needs; created on demand, not at startup.</summary>
    public static void EnsureYouTubeCreated()
    {
        Directory.CreateDirectory(MusicDirectory);
        Directory.CreateDirectory(BinDirectory);
        Directory.CreateDirectory(DownloadDirectory);
    }

    private static string LocalRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local)
            ? Path.Combine(Path.GetTempPath(), ProductName)
            : Path.Combine(local, ProductName);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
