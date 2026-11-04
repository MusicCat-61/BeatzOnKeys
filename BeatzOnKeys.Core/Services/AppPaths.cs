namespace BeatzOnKeys.Core.Services;

/// <summary>Stable per-user paths used by BeatzOnKeys.</summary>
public static class AppPaths
{
    public const string ProductName = "BeatzOnKeys";

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    public static string SettingsFile { get; } = Path.Combine(RootDirectory, "settings.json");

    public static string MidiDirectory { get; } = Path.Combine(RootDirectory, "midi");

    public static string LogDirectory { get; } = Path.Combine(RootDirectory, "logs");

    public static string UpdateDirectory { get; } = Path.Combine(RootDirectory, "updates");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(MidiDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(UpdateDirectory);
    }

    public static string GetMidiPath(Guid trackId) =>
        Path.Combine(MidiDirectory, $"{trackId:N}.mid");
}
