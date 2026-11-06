namespace BeatzOnKeys.Core.Services;

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

    /// <summary>Downloaded YouTube audio. Survives a reinstall because it is per user, not per install.</summary>
    public static string MusicDirectory { get; } = Path.Combine(RootDirectory, "Music");

    /// <summary>Third-party executables (yt-dlp, ffmpeg). The install folder may be read-only.</summary>
    public static string BinDirectory { get; } = Path.Combine(RootDirectory, "bin");

    /// <summary>Scratch space for a download before the finished file is moved into the cache.</summary>
    public static string DownloadDirectory { get; } = Path.Combine(RootDirectory, "downloads");

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
}