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

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFile)) { return new AppSettings(); }
            await using var stream = new FileStream(
                _settingsFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, useAsync: true);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream, _jsonOptions, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
            settings.Normalize();
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
        // Capture the complete JSON before waiting so callers cannot mutate an in-flight save.
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
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
