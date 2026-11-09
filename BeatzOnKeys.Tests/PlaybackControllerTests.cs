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

    /// <summary>
    /// The queue is the list of one page. Switching pages must not restart what is being heard, and
    /// "next" must come from that page: this is the defect where a TikTok track ended and the player
    /// walked into the local playlist.
    /// </summary>
    public static Task ReplaceMakesThePageTheQueue()
    {
        var controller = new PlaybackController { AutoAdvance = true, Loop = true };
        var local = Track("local");
        var first = Track("first");
        var second = Track("second");
        controller.UpsertTrack(local);
        controller.SelectTrack(local.Id);

        // The page does not hold the playing track, so it is kept in front instead of being lost.
        controller.Replace([first, second], local);
        AssertEx.Equal(0, controller.IndexOf(local.Id));
        AssertEx.Equal<Guid?>(local.Id, controller.SelectedTrackId, "A page switch restarted or forgot the playing track.");
        AssertEx.True(controller.TryAdvanceAtEnd(out var next));
        AssertEx.Equal(first, next, "The next track came from another page instead of this one.");
        AssertEx.Equal(2, controller.IndexOf(second.Id), "The page order must survive behind the kept track.");

        // A selection that is still in the new list survives, so reordering a page never restarts it.
        controller.SelectTrack(second.Id);
        controller.Replace([first, second], null);
        AssertEx.Equal<Guid?>(second.Id, controller.SelectedTrackId);
        controller.Replace([second]);
        AssertEx.Equal(0, controller.IndexOf(second.Id));
        AssertEx.Null(controller.GetTrack(first.Id), "Replacing the queue left tracks of the previous page behind.");

        // A selection that the new list no longer holds is dropped: keeping it would make "next"
        // point at a track that is not in the queue any more.
        controller.Replace([Track("other")]);
        AssertEx.Null(controller.SelectedTrackId);
        controller.Replace([]);
        AssertEx.Null(controller.CurrentTrack);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A preview is heard once. It belongs to no list, so continuing from it would pick an unrelated
    /// row — and with Loop on, that row is simply the first downloaded track. Reported by a user:
    /// "the preview finished and something from the downloads started playing".
    /// </summary>
    public static Task AStandaloneTrackStopsInsteadOfContinuing()
    {
        foreach (var loop in new[] { false, true })
        foreach (var autoAdvance in new[] { false, true })
        {
            var controller = new PlaybackController { AutoAdvance = autoAdvance, Loop = loop };
            var downloaded = Track("downloaded");
            var preview = Track("preview");
            controller.UpsertTrack(downloaded);
            controller.UpsertTrack(preview);
            controller.SetStandalone(preview.Id);
            AssertEx.True(controller.IsStandalone(preview.Id), "The mark has to stick.");

            controller.SelectTrack(preview.Id);
            AssertEx.False(controller.TryAdvanceAtEnd(out var next),
                $"A preview advanced the queue (autoAdvance={autoAdvance}, loop={loop}).");
            AssertEx.Null(next);
            AssertEx.Equal<Guid?>(preview.Id, controller.SelectedTrackId,
                "A finished preview must not move the selection onto another track.");

            // An ordinary track is untouched by the rule and still advances as configured.
            controller.SelectTrack(downloaded.Id);
            var advanced = controller.TryAdvanceAtEnd(out var afterDownloaded);
            var expected = autoAdvance || loop;
            AssertEx.Equal(expected, advanced, $"A downloaded track stopped advancing (autoAdvance={autoAdvance}, loop={loop}).");
            if (expected) { AssertEx.NotNull(afterDownloaded); }
        }

        // The mark can be taken back, so a file that is later played as a real track behaves normally.
        var plain = new PlaybackController { AutoAdvance = true, Loop = false };
        var first = Track("first");
        var second = Track("second");
        plain.UpsertTrack(first);
        plain.UpsertTrack(second);
        plain.SetStandalone(first.Id);
        plain.SetStandalone(first.Id, standalone: false);
        AssertEx.False(plain.IsStandalone(first.Id));
        plain.SelectTrack(first.Id);
        AssertEx.True(plain.TryAdvanceAtEnd(out var nextAfterClear), "Clearing the mark restores normal behaviour.");
        AssertEx.Equal(second, nextAfterClear);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A track kept from another page is parked at the front of this page's queue. Loop must wrap
    /// onto the page's own first track, never onto that parked one — that is how the local playlist
    /// "moved on" into the downloaded YouTube list and then into TikTok.
    /// </summary>
    public static Task LoopWrapsOntoThePageNotTheParkedTrack()
    {
        foreach (var autoAdvance in new[] { false, true })
        {
            var controller = new PlaybackController { AutoAdvance = autoAdvance, Loop = true };
            var foreign = Track("from-youtube");
            var first = Track("local-1");
            var second = Track("local-2");
            controller.UpsertTrack(foreign);
            controller.SelectTrack(foreign.Id);
            // The page does not hold the playing track, so it is parked in front and marked.
            controller.Replace([first, second], foreign);
            controller.SetStandalone(foreign.Id);
            AssertEx.Equal(0, controller.IndexOf(foreign.Id));

            // The parked track ends: it stops instead of continuing into this page.
            AssertEx.False(controller.TryAdvanceAtEnd(out var afterForeign),
                $"The parked foreign track continued (autoAdvance={autoAdvance}).");
            AssertEx.Null(afterForeign);

            // The page's own last track wraps onto the page's first, not onto the parked one. With
            // auto-advance off, Loop repeats the current track instead — that is its own rule and
            // is asserted below.
            controller.SelectTrack(second.Id);
            var advanced = controller.TryAdvanceAtEnd(out var wrapped);
            AssertEx.True(advanced, $"The page did not continue (autoAdvance={autoAdvance}).");
            var expected = autoAdvance ? first : second;
            AssertEx.Equal(expected, wrapped,
                autoAdvance
                    ? "Loop landed on the parked foreign track instead of the page."
                    : "Without auto-advance the current track has to repeat.");
            if (autoAdvance) { AssertEx.Equal(1, controller.IndexOf(first.Id)); }
        }

        // With auto-advance off, Loop repeats the selected track — the documented behaviour.
        var repeating = new PlaybackController { AutoAdvance = false, Loop = true };
        var only = Track("only");
        repeating.UpsertTrack(only);
        repeating.SelectTrack(only.Id);
        AssertEx.True(repeating.TryAdvanceAtEnd(out var repeated));
        AssertEx.Equal(only, repeated, "Without auto-advance the current track repeats.");
        return Task.CompletedTask;
    }

    private static PlaybackTrack Track(string name) =>
        new(Guid.NewGuid(), name, $"C:\\audio\\{name}.mp3", TimeSpan.FromSeconds(1));
}
