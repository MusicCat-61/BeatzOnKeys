using System.Text.Json;
using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Services;

/// <summary>
/// Reads and writes library.json: everything the user collected, kept apart from their preferences.
/// JSON rather than a database because the volume is tens of rows, the whole file is read and
/// written at once, and a plain-text file can be attached to a bug report.
/// </summary>
public sealed class LibraryService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _libraryFile;
    private readonly string _settingsFile;
    private readonly string _musicDirectory;
    private readonly string _tikTokDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LibraryService(
        string? libraryFile = null,
        string? settingsFile = null,
        string? musicDirectory = null,
        string? tikTokDirectory = null)
    {
        _libraryFile = Path.GetFullPath(libraryFile ?? DefaultLibraryFile);
        _settingsFile = Path.GetFullPath(settingsFile ?? AppPaths.SettingsFile);
        _musicDirectory = musicDirectory ?? AppPaths.MusicDirectory;
        _tikTokDirectory = tikTokDirectory ?? Path.Combine(_musicDirectory, "TikTok");
    }

    /// <summary>Beside settings.json, so both files travel together when the root moves.</summary>
    public static string DefaultLibraryFile => Path.Combine(AppPaths.RootDirectory, "library.json");

    /// <summary>
    /// The library that belongs to a given settings file. Named after it rather than fixed, so two
    /// installations in one folder (a test suite, a portable copy beside the real one) never share
    /// a collection; "settings.json" still yields the plain "library.json".
    /// </summary>
    public static string LibraryFileFor(string settingsFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsFile))!;
        var name = Path.GetFileName(settingsFile);
        var libraryName = name.Contains("settings", StringComparison.OrdinalIgnoreCase)
            ? name.Replace("settings", "library", StringComparison.OrdinalIgnoreCase)
            : "library.json";
        return Path.Combine(directory, libraryName);
    }

    public string FilePath => _libraryFile;

    /// <summary>
    /// Loads the library, migrating an older settings.json on the way. The migration happens once:
    /// afterwards the flag in settings.json keeps the old lists from being read again.
    /// </summary>
    public async Task<LibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = File.Exists(_libraryFile)
                ? await ReadFileAsync(_libraryFile, cancellationToken).ConfigureAwait(false)
                : await MigrateFromSettingsAsync(cancellationToken).ConfigureAwait(false);

            // Relocation runs before normalisation on purpose: normalisation clears a cached path
            // whose file is missing, which would destroy the old path the migrator needs in order
            // to recognise the file by name in the new root.
            var relocated = RelocatePaths(file);
            file.Normalize();
            if (relocated) { await WriteFileAsync(file, cancellationToken).ConfigureAwait(false); }

            return ToSnapshot(file);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var file = new LibraryFile
        {
            Tracks = snapshot.Tracks,
            YouTubeTracks = snapshot.YouTubeTracks,
            TikTokTracks = snapshot.TikTokTracks,
            PathsMigratedToLibraryVersion = snapshot.PathsMigratedToLibraryVersion
        };
        file.Normalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteFileAsync(file, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static LibrarySnapshot ToSnapshot(LibraryFile file) => new(
        file.Tracks, file.YouTubeTracks, file.TikTokTracks, file.PathsMigratedToLibraryVersion);

    private async Task<LibraryFile> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, useAsync: true);
            var file = await JsonSerializer.DeserializeAsync<LibraryFile>(
                stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            // Deliberately not normalised here: the caller relocates paths first, and normalisation
            // would already have thrown away the old path the migrator needs.
            return file ?? new LibraryFile();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt library is not worth crashing over: the user gets an empty one and the
            // broken file stays on disk for a bug report.
            AppLogger.Error("library", "Failed to read the library; starting empty.", exception);
            return new LibraryFile();
        }
    }

    /// <summary>
    /// Copies the tracks an older version kept inside settings.json. The originals are left alone;
    /// the flag is what stops them from being read a second time.
    /// </summary>
    private async Task<LibraryFile> MigrateFromSettingsAsync(CancellationToken cancellationToken)
    {
        var file = new LibraryFile();
        if (!File.Exists(_settingsFile)) { return file; }
        try
        {
            var json = await File.ReadAllTextAsync(_settingsFile, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { return file; }
            var settings = document.RootElement.Deserialize<AppSettings>(_jsonOptions);
            if (settings is null || settings.TracksMigratedToLibrary) { return file; }

            var total = settings.Tracks.Count + settings.YouTubeTracks.Count + settings.TikTokTracks.Count;
            if (total == 0) { return file; }

            file.Tracks = settings.Tracks;
            file.YouTubeTracks = settings.YouTubeTracks;
            file.TikTokTracks = settings.TikTokTracks;
            // Relocate first for the same reason as a normal load: the paths an older version
            // stored may point at the previous root, and normalisation would clear them.
            RelocatePaths(file);
            file.Normalize();
            await WriteFileAsync(file, cancellationToken).ConfigureAwait(false);
            await MarkSettingsMigratedAsync(json, cancellationToken).ConfigureAwait(false);
            AppLogger.Info("library", $"Moved {total} tracks out of settings.json.");
            return file;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Error("library", "Could not migrate the tracks out of settings.json.", exception);
            return new LibraryFile();
        }
    }

    /// <summary>
    /// Sets the flag on settings.json without rewriting the whole file, so a migration cannot
    /// change a single preference.
    /// </summary>
    private async Task MarkSettingsMigratedAsync(string json, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var properties = new List<string>();
        var replaced = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, nameof(AppSettings.TracksMigratedToLibrary), StringComparison.OrdinalIgnoreCase))
            {
                properties.Add($"\"{nameof(AppSettings.TracksMigratedToLibrary)}\": true");
                replaced = true;
            }
            else
            {
                properties.Add($"\"{property.Name}\": {property.Value.GetRawText()}");
            }
        }

        if (!replaced) { properties.Add($"\"{nameof(AppSettings.TracksMigratedToLibrary)}\": true"); }
        var updated = "{" + Environment.NewLine + "  " + string.Join("," + Environment.NewLine + "  ", properties) +
                      Environment.NewLine + "}";
        await AtomicWriteAsync(_settingsFile, updated, cancellationToken).ConfigureAwait(false);
    }

    private Task WriteFileAsync(LibraryFile file, CancellationToken cancellationToken) =>
        AtomicWriteAsync(_libraryFile, JsonSerializer.Serialize(file, _jsonOptions), cancellationToken);

    /// <summary>
    /// Write-then-rename, so a crash leaves either the old file or the new one and never half of
    /// either. Same shape as the settings writer, which has proven itself.
    /// </summary>
    private async Task AtomicWriteAsync(string path, string json, CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, path, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 5 &&
                    exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(15 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) { File.Delete(temporaryPath); }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warning("library", $"Could not remove a temporary library file: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Repoints cached files that moved with the app's root. Runs once per version and only looks
    /// in the current music folders: guessing across drives would be worse than a clear "Failed".
    /// Returns true when the file has to be written back.
    /// </summary>
    private bool RelocatePaths(LibraryFile file)
    {
        if (string.Equals(file.PathsMigratedToLibraryVersion, LibraryMigrator.CurrentVersion,
                StringComparison.Ordinal)) { return false; }

        var moved = LibraryMigrator.Relocate(file.YouTubeTracks, _musicDirectory) +
                    LibraryMigrator.Relocate(file.TikTokTracks, _tikTokDirectory);
        file.PathsMigratedToLibraryVersion = LibraryMigrator.CurrentVersion;
        if (moved > 0) { AppLogger.Info("library", $"Relocated {moved} cached paths"); }
        return true;
    }
}

/// <summary>
/// The one-off fix-up that follows a change of the app's root folder. Split out so it can be tested
/// without a file system full of surprises.
/// </summary>
public static class LibraryMigrator
{
    /// <summary>The version whose migration has run; bumping it repeats the scan once.</summary>
    public const string CurrentVersion = "1.4.0";

    /// <summary>
    /// Returns how many rows were repointed. A row whose file is gone from both the recorded path
    /// and the current folder becomes Failed, which is what the row already shows for a lost file.
    /// </summary>
    public static int Relocate(List<YouTubeTrackState> tracks, string currentDirectory)
    {
        var moved = 0;
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.CachedPath)) { continue; }
            if (File.Exists(track.CachedPath)) { continue; }
            var name = Path.GetFileName(track.CachedPath);
            if (string.IsNullOrEmpty(name)) { continue; }
            var candidate = Path.Combine(currentDirectory, name);
            if (File.Exists(candidate))
            {
                track.CachedPath = candidate;
                if (track.Status != YouTubeTrackStatus.Ready) { track.Status = YouTubeTrackStatus.Ready; }
                moved++;
                continue;
            }

            track.CachedPath = string.Empty;
            track.Status = YouTubeTrackStatus.Failed;
            track.ErrorMessage = null;
        }

        return moved;
    }
}