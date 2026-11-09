using System.Collections.Concurrent;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Tests;

internal static class SettingsTests
{
    /// <summary>
    /// The window size is remembered, never offered as a setting. An unusable value means "never
    /// set" so the XAML size stands, and a real one is clamped to a size the window can have.
    /// </summary>
    public static Task WindowSizeIsRememberedButBounded()
    {
        var defaults = new AppSettings();
        AssertEx.NearlyEqual(0, defaults.WindowWidth);
        AssertEx.NearlyEqual(0, defaults.WindowHeight);

        var tiny = new AppSettings { WindowWidth = 400, WindowHeight = 200 };
        tiny.Normalize();
        AssertEx.NearlyEqual(AppSettings.MinWindowWidth, tiny.WindowWidth);
        AssertEx.NearlyEqual(AppSettings.MinWindowHeight, tiny.WindowHeight);

        var broken = new AppSettings { WindowWidth = double.NaN, WindowHeight = double.PositiveInfinity };
        broken.Normalize();
        AssertEx.NearlyEqual(0, broken.WindowWidth);
        AssertEx.NearlyEqual(0, broken.WindowHeight);

        var huge = new AppSettings { WindowWidth = 99999, WindowHeight = 99999 };
        huge.Normalize();
        AssertEx.NearlyEqual(16000, huge.WindowWidth);
        AssertEx.NearlyEqual(16000, huge.WindowHeight);
        return Task.CompletedTask;
    }

    public static Task DefaultsAndNormalization()
    {
        AssertDefaults(new AppSettings());
        var retainedId = Guid.NewGuid();
        var settings = new AppSettings
        {
            SchemaVersion = -10, MinVolume = double.NaN, MaxVolume = double.PositiveInfinity,
            IdleTimeout = double.NaN, PauseOnIdle = false, AutoAdvance = true, Loop = false,
            SelectedTrackId = Guid.NewGuid(), Language = "DE", Theme = "unknown",
            Tracks =
            [
                new TrackState { Id = Guid.NewGuid(), SourcePath = " " },
                new TrackState { Id = retainedId, SourcePath = "C:\\music\\one.mp3" },
                new TrackState { Id = Guid.NewGuid(), SourcePath = "c:\\MUSIC\\ONE.MP3" },
                new TrackState { Id = retainedId, SourcePath = "C:\\music\\duplicate-id.mp3" },
                new TrackState { Id = Guid.Empty, SourcePath = "C:\\music\\generated-id.mp3" }
            ]
        };
        settings.Normalize();
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        AssertEx.Equal(7, settings.SchemaVersion);
        AssertEx.NearlyEqual(0.2, settings.MinVolume);
        AssertEx.NearlyEqual(1, settings.MaxVolume);
        AssertEx.NearlyEqual(3, settings.IdleTimeout);
        AssertEx.False(settings.PauseOnIdle);
        AssertEx.True(settings.AutoAdvance);
        AssertEx.False(settings.Loop);
        AssertEx.Equal("dark", settings.Theme);
        AssertEx.Null(settings.Language);
        AssertEx.Null(settings.SelectedTrackId);
        AssertEx.Equal(2, settings.Tracks.Count);
        AssertEx.Equal(retainedId, settings.Tracks[0].Id);
        AssertEx.True(settings.Tracks[1].Id != Guid.Empty);

        var low = new AppSettings { MinVolume = -1, MaxVolume = -1, IdleTimeout = -1, Language = "RU", Theme = "LIGHT" };
        low.Normalize();
        AssertEx.NearlyEqual(0, low.MinVolume);
        AssertEx.NearlyEqual(0, low.MaxVolume);
        AssertEx.NearlyEqual(1, low.IdleTimeout);
        AssertEx.Equal("ru", low.Language);
        AssertEx.Equal("light", low.Theme);
        var high = new AppSettings { MinVolume = 2, MaxVolume = 2, IdleTimeout = 99, Language = "EN", Theme = "dark" };
        high.Normalize();
        AssertEx.NearlyEqual(0.5, high.MinVolume);
        AssertEx.NearlyEqual(1, high.MaxVolume);
        AssertEx.NearlyEqual(10, high.IdleTimeout);
        AssertEx.Equal("en", high.Language);
        var nullTracks = new AppSettings { Tracks = null! };
        nullTracks.Normalize();
        AssertEx.Equal(0, nullTracks.Tracks.Count);
        return Task.CompletedTask;
    }

    public static async Task RoundTripAllFieldsAndDelete()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "nested", "settings.json");
        var service = new SettingsService(path);
        var id = Guid.NewGuid();
        var expected = new AppSettings
        {
            MinVolume = 0.32, MaxVolume = 0.42, IdleTimeout = 6, PauseOnIdle = false,
            AutoAdvance = true, Loop = false, SelectedTrackId = id, Language = "ru", Theme = "light",
            Tracks = [new TrackState { Id = id, SourcePath = "C:\\music\\first.mp3", AddedAt = DateTimeOffset.UnixEpoch }]
        };
        await service.SaveAsync(expected);
        // A new service models restarting the program rather than reading cached values.
        var actual = await new SettingsService(path).LoadAsync();
        AssertSettingsVariant(expected, actual);
        var json = await File.ReadAllTextAsync(path);
        AssertEx.False(json.Contains("\"Volume\"", StringComparison.Ordinal));
        AssertEx.False(json.Contains("\"StepSeconds\"", StringComparison.Ordinal));
        await service.DeleteAsync();
        AssertEx.False(File.Exists(path));
        AssertDefaults(await service.LoadAsync());
        AssertEx.Equal(System.IO.Path.Combine(AppPaths.RootDirectory, "settings.json"), AppPaths.SettingsFile);
    }

    public static async Task LegacyVolumeMigratesAndNewFieldsTakePrecedence()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "legacy.json");
        var id = Guid.NewGuid();
        var json = $$"""
        {
          "SchemaVersion": 2, "Volume": 0.61, "StepSeconds": 0.23,
          "PlaybackSpeed": 1.8, "AutoAdvance": true, "SelectedTrackId": "{{id}}",
          "Language": "EN", "Theme": "LIGHT", "UnknownRoot": { "Nested": true },
          "Tracks": [{ "Id": "{{id}}", "SourcePath": "C:\\music\\legacy.mp3",
                       "AddedAt": "2026-01-02T03:04:05+00:00", "UnknownTrack": "ignored" }]
        }
        """;
        await File.WriteAllTextAsync(path, json);
        var service = new SettingsService(path);
        var settings = await service.LoadAsync();
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        AssertEx.NearlyEqual(0.61, settings.MaxVolume);
        AssertEx.NearlyEqual(0.2, settings.MinVolume);
        AssertEx.NearlyEqual(3, settings.IdleTimeout);
        AssertEx.True(settings.PauseOnIdle);
        AssertEx.True(settings.AutoAdvance);
        AssertEx.True(settings.Loop);
        AssertEx.Equal<Guid?>(id, settings.SelectedTrackId);
        AssertEx.Equal("en", settings.Language);
        AssertEx.Equal("light", settings.Theme);
        AssertEx.Equal(1, settings.Tracks.Count);
        await service.SaveAsync(settings);
        AssertEx.False((await File.ReadAllTextAsync(path)).Contains("\"StepSeconds\"", StringComparison.Ordinal));
        await File.WriteAllTextAsync(path, """{"volume":0.61,"maxVolume":0.8,"minVolume":0.3,"idleTimeout":8,"pauseOnIdle":false}""");
        var explicitFields = await service.LoadAsync();
        AssertEx.NearlyEqual(0.8, explicitFields.MaxVolume);
        AssertEx.NearlyEqual(0.3, explicitFields.MinVolume);
        AssertEx.NearlyEqual(8, explicitFields.IdleTimeout);
        AssertEx.False(explicitFields.PauseOnIdle);
    }

    public static async Task MissingAndCorruptJsonUseDefaults()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(path);
        AssertDefaults(await service.LoadAsync());
        await File.WriteAllTextAsync(path, "{ this is not valid json");
        AssertDefaults(await service.LoadAsync());
        await File.WriteAllTextAsync(path, "null");
        AssertDefaults(await service.LoadAsync());
    }

    public static async Task ConcurrentSavesRemainAtomic()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(path);
        var variants = Enumerable.Range(0, 24).Select(CreateVariant).ToArray();
        await service.SaveAsync(variants[0]);
        var observed = new ConcurrentBag<AppSettings>();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = variants.Select(async settings => { await start.Task; await service.SaveAsync(settings); }).ToArray();
        var readers = Enumerable.Range(0, 8).Select(async _ =>
        {
            await start.Task;
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var loaded = await service.LoadAsync();
                AssertEx.Equal(1, loaded.Tracks.Count, "A reader observed a partial/default document.");
                AssertEx.Equal<Guid?>(loaded.Tracks[0].Id, loaded.SelectedTrackId);
                observed.Add(loaded);
                await Task.Yield();
            }
        }).ToArray();
        start.SetResult(true);
        await Task.WhenAll(writers.Cast<Task>().Concat(readers));
        AssertEx.True(observed.Count > 0);
        var final = await service.LoadAsync();
        AssertSettingsVariant(variants.Single(candidate => candidate.SelectedTrackId == final.SelectedTrackId), final);
        AssertEx.Equal(0, Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories).Count());
    }

    private static AppSettings CreateVariant(int index)
    {
        var id = Guid.NewGuid();
        return new AppSettings
        {
            MinVolume = index / 46d, MaxVolume = index / 23d, IdleTimeout = 1 + index % 10,
            PauseOnIdle = index % 2 == 0, AutoAdvance = index % 2 == 0, Loop = index % 3 == 0,
            SelectedTrackId = id, Language = index % 2 == 0 ? "en" : "ru", Theme = index % 2 == 0 ? "dark" : "light",
            Tracks = [new TrackState { Id = id, SourcePath = $"C:\\music\\variant-{index:D2}.mp3", AddedAt = DateTimeOffset.UnixEpoch.AddMinutes(index) }]
        };
    }

    private static void AssertSettingsVariant(AppSettings expected, AppSettings actual)
    {
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        AssertEx.NearlyEqual(expected.MinVolume, actual.MinVolume);
        AssertEx.NearlyEqual(expected.MaxVolume, actual.MaxVolume);
        AssertEx.NearlyEqual(expected.IdleTimeout, actual.IdleTimeout);
        AssertEx.Equal(expected.PauseOnIdle, actual.PauseOnIdle);
        AssertEx.Equal(expected.AutoAdvance, actual.AutoAdvance);
        AssertEx.Equal(expected.Loop, actual.Loop);
        AssertEx.Equal(expected.SelectedTrackId, actual.SelectedTrackId);
        AssertEx.Equal(expected.Language, actual.Language);
        AssertEx.Equal(expected.Theme, actual.Theme);
        AssertEx.Equal(expected.Tracks.Count, actual.Tracks.Count);
        for (var index = 0; index < expected.Tracks.Count; index++)
        {
            AssertEx.Equal(expected.Tracks[index].Id, actual.Tracks[index].Id);
            AssertEx.Equal(expected.Tracks[index].SourcePath, actual.Tracks[index].SourcePath);
            AssertEx.Equal(expected.Tracks[index].AddedAt, actual.Tracks[index].AddedAt);
        }
    }

    private static void AssertDefaults(AppSettings settings)
    {
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        AssertEx.NearlyEqual(0.2, settings.MinVolume);
        AssertEx.NearlyEqual(1, settings.MaxVolume);
        AssertEx.NearlyEqual(3, settings.IdleTimeout);
        AssertEx.True(settings.PauseOnIdle);
        AssertEx.False(settings.AutoAdvance);
        AssertEx.True(settings.Loop);
        AssertEx.True(settings.PunchResponseEnabled);
        AssertEx.NearlyEqual(AppSettings.DefaultBassReactiveGain, settings.BassReactiveGain);
        AssertEx.False(settings.StereoPulseEnabled);
        AssertEx.True(settings.AdaptiveVolumeEnabled);
        AssertEx.Null(settings.SelectedTrackId);
        AssertEx.Equal(0, settings.Tracks.Count);
    }

    /// <summary>Schema 3 files carry no reactive fields: they must come back with the new defaults.</summary>
    public static async Task ReactiveFieldsMigrateFromSchemaThree()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "SchemaVersion": 3,
              "MinVolume": 0.25,
              "MaxVolume": 0.8,
              "IdleTimeout": 4,
              "PauseOnIdle": false,
              "AutoAdvance": true,
              "Loop": false,
              "Language": "ru",
              "Theme": "light",
              "Tracks": []
            }
            """);
        var service = new SettingsService(path);
        var loaded = await service.LoadAsync();
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        AssertEx.NearlyEqual(0.25, loaded.MinVolume);
        AssertEx.False(loaded.PauseOnIdle);
        AssertEx.True(loaded.PunchResponseEnabled);
        AssertEx.NearlyEqual(AppSettings.DefaultBassReactiveGain, loaded.BassReactiveGain);
        AssertEx.False(loaded.StereoPulseEnabled);
        AssertEx.True(loaded.AdaptiveVolumeEnabled);

        loaded.BassReactiveGain = 0.28;
        loaded.StereoPulseEnabled = true;
        loaded.PunchResponseEnabled = false;
        loaded.AdaptiveVolumeEnabled = false;
        await service.SaveAsync(loaded);
        var reloaded = await new SettingsService(path).LoadAsync();
        AssertEx.NearlyEqual(0.28, reloaded.BassReactiveGain);
        AssertEx.True(reloaded.StereoPulseEnabled);
        AssertEx.False(reloaded.PunchResponseEnabled);
        AssertEx.False(reloaded.AdaptiveVolumeEnabled);

        reloaded.BassReactiveGain = 5;
        reloaded.Normalize();
        AssertEx.NearlyEqual(0.3, reloaded.BassReactiveGain, 1e-9, "Bass gain is clamped to 30%.");
    }
}
