using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeatzOnKeys.Core.Services;

/// <summary>
/// The folder the user picked for application data, plus the answer to "stop asking me". It lives
/// in the per-user folder, not under the root it describes, so the app can find it before the root
/// is known.
/// </summary>
public sealed class StorageSettings
{
    [JsonPropertyName("chosenRoot")]
    public string ChosenRoot { get; set; } = string.Empty;

    [JsonPropertyName("dontAsk")]
    public bool DontAsk { get; set; }

    /// <summary>The folder as a path we can compare; empty when nothing usable was recorded.</summary>
    public string? UsableRoot =>
        string.IsNullOrWhiteSpace(ChosenRoot) ? null : ChosenRoot.Trim();
}

/// <summary>Reads and writes <see cref="StorageSettings"/> without ever losing the previous answer.</summary>
public static class StorageSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// Reads the recorded choice. A missing, unreadable or malformed file is not an error worth
    /// interrupting the start for: it means "no choice yet", and the picker asks again.
    /// </summary>
    public static StorageSettings Load(string? path = null)
    {
        var file = path ?? AppPaths.StorageFile;
        try
        {
            if (!File.Exists(file)) { return new StorageSettings(); }
            var json = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(json)) { return new StorageSettings(); }
            return JsonSerializer.Deserialize<StorageSettings>(json, Options) ?? new StorageSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            AppLogger.Warning("storage", $"Could not read the storage choice: {exception.Message}");
            return new StorageSettings();
        }
    }

    /// <summary>
    /// Writes through a temporary file and replaces the real one in a single step, so an interrupted
    /// save leaves either the old answer or the new one, never half of either.
    /// </summary>
    public static bool Save(StorageSettings settings, string? path = null)
    {
        var file = path ?? AppPaths.StorageFile;
        var temporary = file + ".tmp";
        try
        {
            var folder = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            // File.Move with overwrite is the atomic replace on Windows; File.Replace needs a backup
            // file and fails on some volumes.
            File.Move(temporary, file, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            AppLogger.Warning("storage", $"Could not record the storage choice: {exception.Message}");
            TryDelete(temporary);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless; the next save overwrites it.
        }
    }
}
