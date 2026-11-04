using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Tests;

internal static class PlaybackControllerTests
{
    public static Task EmptyPlaylistAndDefaults()
    {
        var controller = new PlaybackController();
        AssertEx.False(controller.AutoAdvance);
        AssertEx.True(controller.Loop);
        AssertEx.False(controller.SelectTrack(Guid.NewGuid()));
        AssertEx.False(controller.TryAdvanceAtEnd(out var next));
        AssertEx.Null(next);
        AssertEx.Null(controller.CurrentTrack);
        return Task.CompletedTask;
    }

    public static Task NaturalEndModeMatrix()
    {
        foreach (var count in new[] { 1, 3 })
        foreach (var autoAdvance in new[] { false, true })
        foreach (var loop in new[] { false, true })
        {
            var controller = new PlaybackController { AutoAdvance = autoAdvance, Loop = loop };
            var tracks = Enumerable.Range(0, count).Select(index => Track($"track-{index}")).ToArray();
            foreach (var track in tracks) { controller.UpsertTrack(track); }
            controller.SelectTrack(tracks[0].Id);
            for (var index = 0; index < count + 1; index++)
            {
                var current = Array.FindIndex(tracks, track => track.Id == controller.SelectedTrackId);
                var expectedIndex = autoAdvance ? (current + 1 < count ? current + 1 : loop ? 0 : -1) : loop ? current : -1;
                var advanced = controller.TryAdvanceAtEnd(out var next);
                AssertEx.Equal(expectedIndex >= 0, advanced);
                if (expectedIndex < 0) { AssertEx.Null(next); AssertEx.Equal<Guid?>(tracks[current].Id, controller.SelectedTrackId); }
                else { AssertEx.Equal(tracks[expectedIndex], next); AssertEx.Equal<Guid?>(tracks[expectedIndex].Id, controller.SelectedTrackId); }
            }
        }
        return Task.CompletedTask;
    }

    public static Task SelectionUpsertRemoveAndClear()
    {
        var controller = new PlaybackController();
        AssertEx.Throws<ArgumentException>(() => controller.UpsertTrack(Track("invalid") with { Id = Guid.Empty }));
        AssertEx.Throws<ArgumentException>(() => controller.UpsertTrack(Track("invalid") with { SourcePath = " " }));
        AssertEx.Throws<ArgumentException>(() => controller.UpsertTrack(Track("invalid") with { Duration = TimeSpan.Zero }));
        var first = Track("first");
        var second = Track("second");
        controller.UpsertTrack(first);
        controller.UpsertTrack(second);
        AssertEx.True(controller.SelectTrack(first.Id));
        var replacement = first with { Name = "updated", Duration = TimeSpan.FromSeconds(3) };
        controller.UpsertTrack(replacement);
        AssertEx.Equal(replacement, controller.CurrentTrack);
        AssertEx.Equal(replacement, controller.GetTrack(first.Id));
        controller.RemoveTrack(second.Id);
        AssertEx.Equal<Guid?>(first.Id, controller.SelectedTrackId);
        controller.RemoveTrack(first.Id);
        AssertEx.Null(controller.CurrentTrack);
        AssertEx.Null(controller.SelectedTrackId);
        controller.UpsertTrack(second);
        controller.SelectTrack(second.Id);
        controller.Deselect();
        AssertEx.Null(controller.CurrentTrack);
        controller.SelectTrack(second.Id);
        controller.Clear();
        AssertEx.Null(controller.GetTrack(second.Id));
        AssertEx.False(controller.TryAdvanceAtEnd(out _));
        return Task.CompletedTask;
    }

    public static Task EventsRunOutsidePlaylistLock()
    {
        var controller = new PlaybackController { AutoAdvance = true };
        var first = Track("first");
        var second = Track("second");
        controller.UpsertTrack(first);
        controller.UpsertTrack(second);
        var events = new List<PlaybackTrackChangedEventArgs>();
        controller.CurrentTrackChanged += value =>
        {
            var entered = Task.Run(() => { _ = controller.CurrentTrack; controller.Loop = controller.Loop; });
            AssertEx.True(entered.Wait(TimeSpan.FromSeconds(2)), "Track event was raised under the playlist lock.");
            entered.GetAwaiter().GetResult();
            events.Add(value);
        };
        controller.SelectTrack(first.Id);
        controller.TryAdvanceAtEnd(out _);
        AssertEx.Equal(2, events.Count);
        AssertEx.False(events[0].AutoAdvanced);
        AssertEx.True(events[1].AutoAdvanced);
        AssertEx.Equal(second.Id, events[1].TrackId);
        return Task.CompletedTask;
    }

    private static PlaybackTrack Track(string name) =>
        new(Guid.NewGuid(), name, $"C:\\audio\\{name}.mp3", TimeSpan.FromSeconds(1));
}
