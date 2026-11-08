using System.Text.Json;
using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Services;

/// <summary>Atomic settings persistence; a custom path keeps tests isolated from user data.</summary>
public sealed class SettingsService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _settingsFile;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsService(string? settingsFile = null) =>
        _settingsFile = Path.GetFullPath(settingsFile ?? AppPaths.SettingsFile);

    /// <summary>
    /// Where this instance reads and writes. Exposed so the library can sit beside it: the two
    /// files belong to one installation and must always travel together.
    /// </summary>
    public string FilePath => _settingsFile;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFile)) { return new AppSettings(); }
            await using var stream = new FileStream(
                _settingsFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, useAsync: true);
            using var document = await JsonDocument.ParseAsync(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var settings = document.RootElement.Deserialize<AppSettings>(_jsonOptions) ?? new AppSettings();
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var properties = document.RootElement.EnumerateObject().ToArray();
                if (!properties.Any(property => string.Equals(
                    property.Name, nameof(AppSettings.MaxVolume), StringComparison.OrdinalIgnoreCase)))
                {
                    var legacyVolume = properties.FirstOrDefault(property =>
                        string.Equals(property.Name, "Volume", StringComparison.OrdinalIgnoreCase)).Value;
                    if (legacyVolume.ValueKind == JsonValueKind.Number && legacyVolume.TryGetDouble(out var volume))
                    {
                        settings.MaxVolume = volume;
                    }
                }
            }
            settings.Normalize();
            if (settings.TracksMigratedToLibrary)
            {
                // The tracks live in library.json now; the copies here are a stale leftover.
                settings.Tracks = [];
                settings.YouTubeTracks = [];
                settings.TikTokTracks = [];
            }

            return settings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Error("settings", "Failed to read settings; defaults will be used.", exception);
            return new AppSettings();
        }
        finally { _saveGate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        // The library owns the tracks once it has taken them over. Before that the old fields are
        // still written, so an installation that has not migrated yet keeps its tracks.
        var payload = settings.TracksMigratedToLibrary ? WithoutTracks(settings) : settings;
        // Capture the complete JSON before waiting so callers cannot mutate an in-flight save.
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = _settingsFile + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, _settingsFile, overwrite: true);
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
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warning("settings", $"Could not remove a temporary settings file: {exception.Message}");
            }
            _saveGate.Release();
        }
    }

    /// <summary>A copy with the track lists cleared, so only preferences reach settings.json.</summary>
    private static AppSettings WithoutTracks(AppSettings settings) => new()
    {
        SchemaVersion = settings.SchemaVersion,
        MinVolume = settings.MinVolume,
        MaxVolume = settings.MaxVolume,
        IdleTimeout = settings.IdleTimeout,
        PauseOnIdle = settings.PauseOnIdle,
        AutoAdvance = settings.AutoAdvance,
        Loop = settings.Loop,
        PunchResponseEnabled = settings.PunchResponseEnabled,
        BassReactiveGain = settings.BassReactiveGain,
        StereoPulseEnabled = settings.StereoPulseEnabled,
        AdaptiveVolumeEnabled = settings.AdaptiveVolumeEnabled,
        SelectedTrackId = settings.SelectedTrackId,
        Language = settings.Language,
        Theme = settings.Theme,
        TracksMigratedToLibrary = settings.TracksMigratedToLibrary,
        YouTubeCacheEnabled = settings.YouTubeCacheEnabled,
        CookiesFolderPath = settings.CookiesFolderPath,
        ProxyUrl = settings.ProxyUrl
    };

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_settingsFile))
            {
                File.Delete(_settingsFile);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
