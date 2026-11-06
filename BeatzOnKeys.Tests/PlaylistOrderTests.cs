using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Tests;

/// <summary>Playlist order is playback order: moving rows must move the auto-advance sequence too.</summary>
internal static class PlaylistOrderTests
{
    public static Task MoveTrackStepsAndRespectsEdges()
    {
        var controller = new PlaybackController();
        var first = Track("first");
        var second = Track("second");
        var third = Track("third");
        foreach (var track in new[] { first, second, third }) { controller.UpsertTrack(track); }

        AssertEx.Equal(1, controller.IndexOf(second.Id));
        AssertEx.True(controller.CanMove(second.Id, -1));
        AssertEx.Equal(0, controller.MoveTrack(second.Id, -1));
        AssertEx.Equal(1, controller.IndexOf(first.Id));
        AssertEx.False(controller.CanMove(second.Id, -1));
        AssertEx.Equal(-1, controller.MoveTrack(second.Id, -1));

        AssertEx.Equal(1, controller.MoveTrack(second.Id, 1));
        AssertEx.True(controller.CanMove(second.Id, 1));
        AssertEx.Equal(2, controller.MoveTrack(second.Id, 1));
        AssertEx.False(controller.CanMove(second.Id, 1));
        AssertEx.Equal(-1, controller.MoveTrack(second.Id, 1));

        var unknown = Guid.NewGuid();
        AssertEx.Equal(-1, controller.IndexOf(unknown));
        AssertEx.False(controller.CanMove(unknown, 1));
        AssertEx.Equal(-1, controller.MoveTrack(unknown, 1));
        return Task.CompletedTask;
    }

    public static Task ReorderFollowsVisualOrderAndKeepsSelection()
    {
        var controller = new PlaybackController();
        var first = Track("first");
        var second = Track("second");
        var third = Track("third");
        foreach (var track in new[] { first, second, third }) { controller.UpsertTrack(track); }
        AssertEx.True(controller.SelectTrack(first.Id));
        var events = 0;
        controller.CurrentTrackChanged += _ => events++;

        controller.Reorder([third.Id, first.Id, second.Id]);
        AssertEx.Equal(0, controller.IndexOf(third.Id));
        AssertEx.Equal(1, controller.IndexOf(first.Id));
        AssertEx.Equal(2, controller.IndexOf(second.Id));
        AssertEx.Equal<Guid?>(first.Id, controller.SelectedTrackId);
        AssertEx.Equal(0, events, "Reordering must not raise a track-changed event: playback would restart.");

        // Ids the caller omits keep their relative order at the end; unknown ids are ignored.
        controller.Reorder([Guid.NewGuid(), second.Id]);
        AssertEx.Equal(0, controller.IndexOf(second.Id));
        AssertEx.Equal(1, controller.IndexOf(third.Id));
        AssertEx.Equal(2, controller.IndexOf(first.Id));
        return Task.CompletedTask;
    }

    public static Task NaturalEndFollowsReorderedList()
    {
        var controller = new PlaybackController { AutoAdvance = true, Loop = false };
        var first = Track("first");
        var second = Track("second");
        var third = Track("third");
        foreach (var track in new[] { first, second, third }) { controller.UpsertTrack(track); }
        AssertEx.True(controller.SelectTrack(first.Id));
        controller.Reorder([third.Id, first.Id, second.Id]);

        AssertEx.True(controller.TryAdvanceAtEnd(out var next));
        AssertEx.Equal(second.Id, AssertEx.NotNull(next).Id, "Auto-advance must follow the visual order, not the import order.");
        AssertEx.False(controller.TryAdvanceAtEnd(out var afterLast));
        AssertEx.Null(afterLast);
        return Task.CompletedTask;
    }

    public static Task RemoveCurrentTrackLeavesNoSelection()
    {
        var controller = new PlaybackController();
        var first = Track("first");
        var second = Track("second");
        controller.UpsertTrack(first);
        controller.UpsertTrack(second);
        AssertEx.True(controller.SelectTrack(first.Id));
        controller.RemoveTrack(first.Id);
        AssertEx.Null(controller.SelectedTrackId);
        AssertEx.NotNull(controller.GetTrack(second.Id));
        controller.Clear();
        AssertEx.Equal(-1, controller.IndexOf(second.Id));
        AssertEx.False(controller.CanMove(second.Id, 1));
        AssertEx.Equal(-1, controller.MoveTrack(second.Id, 1));
        return Task.CompletedTask;
    }

    private static PlaybackTrack Track(string name) =>
        new(Guid.NewGuid(), name, $"C:\\audio\\{name}.mp3", TimeSpan.FromSeconds(1));
}