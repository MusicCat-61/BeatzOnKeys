using System.Collections.Concurrent;
using BeatzOnKeys.Core.Models;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Tests;

internal static class SettingsTests
{
    public static Task DefaultsAndNormalization()
    {
        var defaults = new AppSettings();
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, defaults.SchemaVersion);
        AssertEx.NearlyEqual(0.7, defaults.Volume);
        AssertEx.NearlyEqual(AppSettings.DefaultStepSeconds, defaults.StepSeconds);
        AssertEx.False(defaults.AutoAdvance);
        AssertEx.True(defaults.Loop);
        AssertEx.Equal("dark", defaults.Theme);
        AssertEx.Null(defaults.Language);
        AssertEx.Equal(0, defaults.Tracks.Count);

        var retainedId = Guid.NewGuid();
        var invalidSelectedId = Guid.NewGuid();
        var settings = new AppSettings
        {
            SchemaVersion = -10,
            Volume = double.NaN,
            StepSeconds = double.PositiveInfinity,
            AutoAdvance = true,
            Loop = false,
            SelectedTrackId = invalidSelectedId,
            Language = "DE",
            Theme = "unknown",
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
        AssertEx.NearlyEqual(0.7, settings.Volume);
        AssertEx.NearlyEqual(AppSettings.DefaultStepSeconds, settings.StepSeconds);
        AssertEx.True(settings.AutoAdvance);
        AssertEx.False(settings.Loop);
        AssertEx.Equal("dark", settings.Theme);
        AssertEx.Null(settings.Language);
        AssertEx.Null(settings.SelectedTrackId);
        AssertEx.Equal(2, settings.Tracks.Count);
        AssertEx.Equal(retainedId, settings.Tracks[0].Id);
        AssertEx.True(settings.Tracks[1].Id != Guid.Empty);

        var low = new AppSettings { Volume = -1, StepSeconds = -1, Language = "RU", Theme = "LIGHT" };
        low.Normalize();
        AssertEx.NearlyEqual(0, low.Volume);
        AssertEx.NearlyEqual(AppSettings.MinimumStepSeconds, low.StepSeconds);
        AssertEx.Equal("ru", low.Language);
        AssertEx.Equal("light", low.Theme);

        var high = new AppSettings { Volume = 2, StepSeconds = 2, Language = "EN", Theme = "dark" };
        high.Normalize();
        AssertEx.NearlyEqual(1, high.Volume);
        AssertEx.NearlyEqual(AppSettings.MaximumStepSeconds, high.StepSeconds);
        AssertEx.Equal("en", high.Language);
        AssertEx.Equal("dark", high.Theme);

        var nullTracks = new AppSettings { Tracks = null! };
        nullTracks.Normalize();
        AssertEx.Equal(0, nullTracks.Tracks.Count);
        return Task.CompletedTask;
    }

    public static async Task RoundTripAllFieldsAndDelete()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = System.IO.Path.Combine(directory.Path, "nested", "settings.json");
        var service = new SettingsService(settingsPath);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstAdded = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var secondAdded = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.FromHours(3));
        var expected = new AppSettings
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Volume = 0.42,
            StepSeconds = 0.23,
            AutoAdvance = true,
            Loop = false,
            SelectedTrackId = secondId,
            Language = "ru",
            Theme = "light",
            Tracks =
            [
                new TrackState { Id = firstId, SourcePath = "C:\\music\\first.mp3", AddedAt = firstAdded },
                new TrackState { Id = secondId, SourcePath = "C:\\music\\second.wav", AddedAt = secondAdded }
            ]
        };

        await service.SaveAsync(expected).ConfigureAwait(false);
        AssertEx.True(File.Exists(settingsPath));
        var actual = await service.LoadAsync().ConfigureAwait(false);

        AssertEx.Equal(AppSettings.CurrentSchemaVersion, actual.SchemaVersion);
        AssertEx.NearlyEqual(expected.Volume, actual.Volume);
        AssertEx.NearlyEqual(expected.StepSeconds, actual.StepSeconds);
        AssertEx.Equal(expected.AutoAdvance, actual.AutoAdvance);
        AssertEx.Equal(expected.Loop, actual.Loop);
        AssertEx.Equal(expected.SelectedTrackId, actual.SelectedTrackId);
        AssertEx.Equal(expected.Language, actual.Language);
        AssertEx.Equal(expected.Theme, actual.Theme);
        AssertEx.Equal(2, actual.Tracks.Count);
        AssertTrack(expected.Tracks[0], actual.Tracks[0]);
        AssertTrack(expected.Tracks[1], actual.Tracks[1]);

        await service.DeleteAsync().ConfigureAwait(false);
        AssertEx.False(File.Exists(settingsPath));
        var afterDelete = await service.LoadAsync().ConfigureAwait(false);
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, afterDelete.SchemaVersion);
        AssertEx.True(afterDelete.Loop);
        AssertEx.Equal(0, afterDelete.Tracks.Count);
    }

    public static async Task LegacyUnknownPropertiesAreIgnored()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = System.IO.Path.Combine(directory.Path, "legacy.json");
        var id = Guid.NewGuid();
        var json = $$"""
        {
          "SchemaVersion": 1,
          "Volume": 0.61,
          "PlaybackSpeed": 1.8,
          "AutoAdvance": true,
          "SelectedTrackId": "{{id}}",
          "Language": "EN",
          "Theme": "LIGHT",
          "UnknownRoot": { "Nested": true },
          "Tracks": [
            {
              "Id": "{{id}}",
              "SourcePath": "C:\\music\\legacy.mp3",
              "AddedAt": "2026-01-02T03:04:05+00:00",
              "MidiPath": "C:\\cache\\legacy.mid",
              "Status": "Ready",
              "NoteCount": 123,
              "UnknownTrack": "ignored"
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(settingsPath, json).ConfigureAwait(false);

        var service = new SettingsService(settingsPath);
        var settings = await service.LoadAsync().ConfigureAwait(false);
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        AssertEx.NearlyEqual(0.61, settings.Volume);
        AssertEx.NearlyEqual(AppSettings.DefaultStepSeconds, settings.StepSeconds);
        AssertEx.True(settings.AutoAdvance);
        AssertEx.True(settings.Loop, "A missing legacy Loop property must retain the schema-2 default.");
        AssertEx.Equal<Guid?>(id, settings.SelectedTrackId);
        AssertEx.Equal("en", settings.Language);
        AssertEx.Equal("light", settings.Theme);
        AssertEx.Equal(1, settings.Tracks.Count);
        AssertEx.Equal(id, settings.Tracks[0].Id);
        AssertEx.Equal("C:\\music\\legacy.mp3", settings.Tracks[0].SourcePath);
    }

    public static async Task MissingAndCorruptJsonUseDefaults()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = System.IO.Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(settingsPath);

        var missing = await service.LoadAsync().ConfigureAwait(false);
        AssertDefaults(missing);

        await File.WriteAllTextAsync(settingsPath, "{ this is not valid json").ConfigureAwait(false);
        var corrupt = await service.LoadAsync().ConfigureAwait(false);
        AssertDefaults(corrupt);
    }

    public static async Task ConcurrentSavesRemainAtomic()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = System.IO.Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(settingsPath);
        var variants = Enumerable.Range(0, 24).Select(CreateVariant).ToArray();
        await service.SaveAsync(variants[0]).ConfigureAwait(false);

        var observed = new ConcurrentBag<AppSettings>();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = variants.Select(async settings =>
        {
            await start.Task.ConfigureAwait(false);
            await service.SaveAsync(settings).ConfigureAwait(false);
        }).ToArray();
        var readers = Enumerable.Range(0, 8).Select(async _ =>
        {
            await start.Task.ConfigureAwait(false);
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var loaded = await service.LoadAsync().ConfigureAwait(false);
                AssertEx.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
                AssertEx.Equal(1, loaded.Tracks.Count, "A concurrent reader observed a partial/default document.");
                AssertEx.Equal<Guid?>(loaded.Tracks[0].Id, loaded.SelectedTrackId);
                observed.Add(loaded);
                await Task.Yield();
            }
        }).ToArray();

        start.SetResult(true);
        await Task.WhenAll(writers.Cast<Task>().Concat(readers)).ConfigureAwait(false);

        AssertEx.True(observed.Count > 0);
        var final = await service.LoadAsync().ConfigureAwait(false);
        var matching = variants.SingleOrDefault(candidate => candidate.SelectedTrackId == final.SelectedTrackId);
        var expected = AssertEx.NotNull(matching, "The final file did not match any complete submitted settings object.");
        AssertSettingsVariant(expected, final);

        var temporaryFiles = Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories).ToArray();
        AssertEx.Equal(0, temporaryFiles.Length, "Atomic saves left temporary files behind.");
    }

    private static AppSettings CreateVariant(int index)
    {
        var id = Guid.NewGuid();
        return new AppSettings
        {
            Volume = index / 23d,
            StepSeconds = AppSettings.MinimumStepSeconds + (index * 0.01),
            AutoAdvance = index % 2 == 0,
            Loop = index % 3 == 0,
            SelectedTrackId = id,
            Language = index % 2 == 0 ? "en" : "ru",
            Theme = index % 2 == 0 ? "dark" : "light",
            Tracks =
            [
                new TrackState
                {
                    Id = id,
                    SourcePath = $"C:\\music\\variant-{index:D2}.mp3",
                    AddedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index)
                }
            ]
        };
    }

    private static void AssertSettingsVariant(AppSettings expected, AppSettings actual)
    {
        AssertEx.NearlyEqual(expected.Volume, actual.Volume);
        AssertEx.NearlyEqual(expected.StepSeconds, actual.StepSeconds);
        AssertEx.Equal(expected.AutoAdvance, actual.AutoAdvance);
        AssertEx.Equal(expected.Loop, actual.Loop);
        AssertEx.Equal(expected.SelectedTrackId, actual.SelectedTrackId);
        AssertEx.Equal(expected.Language, actual.Language);
        AssertEx.Equal(expected.Theme, actual.Theme);
        AssertEx.Equal(1, actual.Tracks.Count);
        AssertTrack(expected.Tracks[0], actual.Tracks[0]);
    }

    private static void AssertTrack(TrackState expected, TrackState actual)
    {
        AssertEx.Equal(expected.Id, actual.Id);
        AssertEx.Equal(expected.SourcePath, actual.SourcePath);
        AssertEx.Equal(expected.AddedAt, actual.AddedAt);
    }

    private static void AssertDefaults(AppSettings settings)
    {
        AssertEx.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        AssertEx.NearlyEqual(0.7, settings.Volume);
        AssertEx.NearlyEqual(AppSettings.DefaultStepSeconds, settings.StepSeconds);
        AssertEx.False(settings.AutoAdvance);
        AssertEx.True(settings.Loop);
        AssertEx.Null(settings.SelectedTrackId);
        AssertEx.Equal(0, settings.Tracks.Count);
    }
}
