using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Tests;

internal static class PlaybackControllerTests
{
    public static Task ZeroTrackMatrix()
    {
        foreach (var autoAdvance in new[] { false, true })
        {
            foreach (var loop in new[] { false, true })
            {
                var controller = new PlaybackController
                {
                    AutoAdvance = autoAdvance,
                    Loop = loop
                };

                AssertEx.False(controller.TryTakeNext(out var step));
                AssertEx.Null(step);
                AssertEx.Null(controller.SelectedTrackId);
                AssertEx.Null(controller.CurrentTrack);
                AssertEx.NearlyEqual(0, controller.CurrentPosition);
                AssertEx.False(controller.SelectTrack(Guid.NewGuid()));
            }
        }

        var defaults = new PlaybackController();
        AssertEx.NearlyEqual(0.7, defaults.Volume);
        AssertEx.NearlyEqual(AppSettings.DefaultStepSeconds, defaults.StepSeconds);
        AssertEx.False(defaults.AutoAdvance);
        AssertEx.True(defaults.Loop);
        return Task.CompletedTask;
    }

    public static async Task StepAndIdleCursor()
    {
        var controller = new PlaybackController();
        var first = Track("first", 1);
        var second = Track("second", 1);
        controller.UpsertTrack(first);
        controller.UpsertTrack(second);

        AssertEx.True(controller.SelectTrack(first.Id));
        AssertEx.NearlyEqual(0, controller.CurrentPosition);
        await Task.Delay(40).ConfigureAwait(false);
        AssertEx.NearlyEqual(0, controller.CurrentPosition, message: "Wall-clock time moved the cursor without a key.");

        var step = Take(controller);
        AssertEx.Equal(first, step.Track);
        AssertEx.NearlyEqual(0.15, step.PositionSeconds);
        AssertEx.NearlyEqual(0.15, step.FragmentSeconds);
        AssertEx.NearlyEqual(0.7, step.Volume);

        await Task.Delay(40).ConfigureAwait(false);
        AssertEx.NearlyEqual(0.15, controller.CurrentPosition, message: "An idle pause changed the cursor.");

        AssertEx.True(controller.SelectTrack(first.Id, restart: false));
        AssertEx.NearlyEqual(0.15, controller.CurrentPosition);
        AssertEx.True(controller.SelectTrack(first.Id, restart: true));
        AssertEx.NearlyEqual(0, controller.CurrentPosition);

        Take(controller);
        AssertEx.True(controller.SelectTrack(second.Id, restart: false));
        AssertEx.NearlyEqual(0, controller.CurrentPosition, message: "Changing tracks must start the new track at zero.");
    }

    public static Task TailAndExactBoundary()
    {
        var controller = new PlaybackController
        {
            StepSeconds = 0.2,
            Volume = 0.33,
            AutoAdvance = false,
            Loop = false
        };
        var track = Track("tail", 0.55);
        controller.UpsertTrack(track);
        AssertEx.True(controller.SelectTrack(track.Id));

        PlaybackPositionEventArgs? lastPosition = null;
        controller.PositionChanged += value => lastPosition = value;

        var first = Take(controller);
        AssertEx.NearlyEqual(0.2, first.PositionSeconds);
        AssertEx.NearlyEqual(0.2, first.FragmentSeconds);
        AssertEx.NearlyEqual(0.33, first.Volume);

        var second = Take(controller);
        AssertEx.NearlyEqual(0.4, second.PositionSeconds);
        AssertEx.NearlyEqual(0.15, second.FragmentSeconds);

        AssertEx.False(controller.TryTakeNext(out var endStep));
        AssertEx.Null(endStep);
        AssertEx.NearlyEqual(0.55, controller.CurrentPosition);
        var end = AssertEx.NotNull(lastPosition);
        AssertEx.True(end.ReachedEnd);
        AssertEx.NearlyEqual(0.55, end.PositionSeconds);

        AssertEx.False(controller.TryTakeNext(out _));
        AssertEx.NearlyEqual(0.55, controller.CurrentPosition);

        var exact = new PlaybackController
        {
            StepSeconds = 0.2,
            AutoAdvance = false,
            Loop = false
        };
        var exactTrack = Track("exact", 0.4);
        exact.UpsertTrack(exactTrack);
        AssertEx.True(exact.SelectTrack(exactTrack.Id));
        AssertEx.NearlyEqual(0.2, Take(exact).PositionSeconds);
        AssertEx.False(exact.TryTakeNext(out var exactEnd));
        AssertEx.Null(exactEnd);
        AssertEx.NearlyEqual(0.4, exact.CurrentPosition);
        return Task.CompletedTask;
    }

    public static Task SingleTrackModeMatrix()
    {
        foreach (var autoAdvance in new[] { false, true })
        {
            foreach (var loop in new[] { false, true })
            {
                var controller = new PlaybackController
                {
                    StepSeconds = 0.2,
                    AutoAdvance = autoAdvance,
                    Loop = loop
                };
                var track = Track($"single-{autoAdvance}-{loop}", 0.3);
                controller.UpsertTrack(track);
                AssertEx.True(controller.SelectTrack(track.Id));

                var automaticChanges = new List<PlaybackTrackChangedEventArgs>();
                controller.CurrentTrackChanged += value => automaticChanges.Add(value);

                AssertEx.NearlyEqual(0.2, Take(controller).PositionSeconds);
                var playedAtBoundary = controller.TryTakeNext(out var boundaryStep);

                if (loop)
                {
                    AssertEx.True(playedAtBoundary);
                    var wrapped = AssertEx.NotNull(boundaryStep);
                    AssertEx.Equal(track.Id, wrapped.Track.Id);
                    AssertEx.NearlyEqual(0, wrapped.PositionSeconds);
                    AssertEx.NearlyEqual(0.2, wrapped.FragmentSeconds);
                    AssertEx.NearlyEqual(0, controller.CurrentPosition);
                    AssertEx.Equal(autoAdvance ? 1 : 0, automaticChanges.Count);
                    if (autoAdvance)
                    {
                        AssertEx.True(automaticChanges[0].AutoAdvanced);
                    }
                }
                else
                {
                    AssertEx.False(playedAtBoundary);
                    AssertEx.Null(boundaryStep);
                    AssertEx.NearlyEqual(0.3, controller.CurrentPosition);
                    AssertEx.Equal(0, automaticChanges.Count);
                }

                AssertEx.Equal(track.Id, controller.SelectedTrackId);
            }
        }

        return Task.CompletedTask;
    }

    public static Task MultipleTrackModeMatrix()
    {
        VerifyMultipleTracks(autoAdvance: false, loop: false);
        VerifyMultipleTracks(autoAdvance: false, loop: true);
        VerifyMultipleTracks(autoAdvance: true, loop: false);
        VerifyMultipleTracks(autoAdvance: true, loop: true);
        return Task.CompletedTask;
    }

    public static Task SelectionUpsertRemoveAndClear()
    {
        var controller = new PlaybackController();
        AssertEx.Throws<ArgumentException>(() => controller.UpsertTrack(
            new PlaybackTrack(Guid.Empty, "bad", "bad.mp3", TimeSpan.FromSeconds(1))));
        AssertEx.Throws<ArgumentException>(() => controller.UpsertTrack(
            new PlaybackTrack(Guid.NewGuid(), "bad", " ", TimeSpan.FromSeconds(1))));
        AssertEx.Throws<ArgumentException>(() => controller.UpsertTrack(
            new PlaybackTrack(Guid.NewGuid(), "bad", "bad.mp3", TimeSpan.Zero)));

        var first = Track("first", 1);
        var second = Track("second", 1);
        controller.UpsertTrack(first);
        controller.UpsertTrack(second);
        AssertEx.Null(controller.CurrentTrack);
        AssertEx.False(controller.SelectTrack(Guid.NewGuid()));
        AssertEx.True(controller.SelectTrack(first.Id));

        controller.StepSeconds = 0.2;
        Take(controller);
        Take(controller);
        AssertEx.NearlyEqual(0.4, controller.CurrentPosition);

        var replacement = first with { Name = "first-updated", Duration = TimeSpan.FromSeconds(0.25) };
        controller.UpsertTrack(replacement);
        AssertEx.Equal(replacement, controller.CurrentTrack);
        AssertEx.Equal(replacement, controller.GetTrack(first.Id));
        AssertEx.NearlyEqual(0.25, controller.CurrentPosition);

        controller.RemoveTrack(second.Id);
        AssertEx.Equal(first.Id, controller.SelectedTrackId);

        PlaybackPositionEventArgs? removalPosition = null;
        controller.PositionChanged += value => removalPosition = value;
        controller.RemoveTrack(first.Id);
        AssertEx.Null(controller.SelectedTrackId);
        AssertEx.Null(controller.CurrentTrack);
        AssertEx.Null(controller.GetTrack(first.Id));
        AssertEx.NearlyEqual(0, controller.CurrentPosition);
        AssertEx.Null(AssertEx.NotNull(removalPosition).TrackId);
        AssertEx.False(controller.TryTakeNext(out _));

        controller.UpsertTrack(second);
        AssertEx.True(controller.SelectTrack(second.Id));
        controller.Clear();
        AssertEx.Null(controller.SelectedTrackId);
        AssertEx.Null(controller.CurrentTrack);
        AssertEx.Null(controller.GetTrack(second.Id));
        AssertEx.NearlyEqual(0, controller.CurrentPosition);
        return Task.CompletedTask;
    }

    public static Task VolumeAndStepClamping()
    {
        var controller = new PlaybackController();

        controller.Volume = -5;
        AssertEx.NearlyEqual(0, controller.Volume);
        controller.Volume = 5;
        AssertEx.NearlyEqual(1, controller.Volume);
        controller.Volume = double.NaN;
        AssertEx.NearlyEqual(0.7, controller.Volume);
        controller.Volume = double.PositiveInfinity;
        AssertEx.NearlyEqual(0.7, controller.Volume);

        controller.StepSeconds = -5;
        AssertEx.NearlyEqual(AppSettings.MinimumStepSeconds, controller.StepSeconds);
        controller.StepSeconds = 5;
        AssertEx.NearlyEqual(AppSettings.MaximumStepSeconds, controller.StepSeconds);
        controller.StepSeconds = double.NaN;
        AssertEx.NearlyEqual(AppSettings.DefaultStepSeconds, controller.StepSeconds);
        controller.StepSeconds = double.NegativeInfinity;
        AssertEx.NearlyEqual(AppSettings.DefaultStepSeconds, controller.StepSeconds);

        controller.Volume = 1;
        controller.StepSeconds = AppSettings.MinimumStepSeconds;
        var track = Track("clamped", 1);
        controller.UpsertTrack(track);
        AssertEx.True(controller.SelectTrack(track.Id));
        var step = Take(controller);
        AssertEx.NearlyEqual(AppSettings.MinimumStepSeconds, step.PositionSeconds);
        AssertEx.NearlyEqual(AppSettings.MinimumStepSeconds, step.FragmentSeconds);
        AssertEx.NearlyEqual(1, step.Volume);
        return Task.CompletedTask;
    }

    public static Task EventsRunOutsideControllerLock()
    {
        var controller = new PlaybackController
        {
            StepSeconds = 0.2,
            AutoAdvance = true,
            Loop = false
        };
        var first = Track("first", 0.3);
        var second = Track("second", 0.3);
        controller.UpsertTrack(first);
        controller.UpsertTrack(second);

        var trackEvents = 0;
        var positionEvents = 0;
        controller.CurrentTrackChanged += _ =>
        {
            trackEvents++;
            AssertOtherThreadCanEnter(controller);
        };
        controller.PositionChanged += _ =>
        {
            positionEvents++;
            AssertOtherThreadCanEnter(controller);
        };

        AssertEx.True(controller.SelectTrack(first.Id));
        Take(controller);
        var advanced = Take(controller);
        AssertEx.Equal(second.Id, advanced.Track.Id);
        AssertEx.True(trackEvents >= 2, "Expected selection and auto-advance track events.");
        AssertEx.True(positionEvents >= 3, "Expected selection and key position events.");
        return Task.CompletedTask;
    }

    private static void VerifyMultipleTracks(bool autoAdvance, bool loop)
    {
        var controller = new PlaybackController
        {
            StepSeconds = 0.2,
            AutoAdvance = autoAdvance,
            Loop = loop
        };
        var first = Track($"first-{autoAdvance}-{loop}", 0.3);
        var second = Track($"second-{autoAdvance}-{loop}", 0.3);
        controller.UpsertTrack(first);
        controller.UpsertTrack(second);
        AssertEx.True(controller.SelectTrack(first.Id));
        AssertEx.NearlyEqual(0.2, Take(controller).PositionSeconds);

        var secondPress = controller.TryTakeNext(out var boundary);
        if (!autoAdvance)
        {
            if (loop)
            {
                AssertEx.True(secondPress);
                var wrapped = AssertEx.NotNull(boundary);
                AssertEx.Equal(first.Id, wrapped.Track.Id);
                AssertEx.NearlyEqual(0, wrapped.PositionSeconds);
            }
            else
            {
                AssertEx.False(secondPress);
                AssertEx.Null(boundary);
                AssertEx.Equal(first.Id, controller.SelectedTrackId);
                AssertEx.NearlyEqual(0.3, controller.CurrentPosition);
            }

            return;
        }

        AssertEx.True(secondPress);
        var nextTrack = AssertEx.NotNull(boundary);
        AssertEx.Equal(second.Id, nextTrack.Track.Id);
        AssertEx.NearlyEqual(0, nextTrack.PositionSeconds);
        AssertEx.Equal(second.Id, controller.SelectedTrackId);

        AssertEx.NearlyEqual(0.2, Take(controller).PositionSeconds);
        var afterLast = controller.TryTakeNext(out var afterLastStep);
        if (loop)
        {
            AssertEx.True(afterLast);
            var playlistWrapped = AssertEx.NotNull(afterLastStep);
            AssertEx.Equal(first.Id, playlistWrapped.Track.Id);
            AssertEx.NearlyEqual(0, playlistWrapped.PositionSeconds);
        }
        else
        {
            AssertEx.False(afterLast);
            AssertEx.Null(afterLastStep);
            AssertEx.Equal(second.Id, controller.SelectedTrackId);
            AssertEx.NearlyEqual(0.3, controller.CurrentPosition);
        }
    }

    private static PlaybackStep Take(PlaybackController controller)
    {
        AssertEx.True(controller.TryTakeNext(out var step), "Expected the controller to produce a playback step.");
        return AssertEx.NotNull(step);
    }

    private static PlaybackTrack Track(string name, double durationSeconds) =>
        new(Guid.NewGuid(), name, $"C:\\audio\\{name}.mp3", TimeSpan.FromSeconds(durationSeconds));

    private static void AssertOtherThreadCanEnter(PlaybackController controller)
    {
        var entered = Task.Run(() =>
        {
            _ = controller.CurrentPosition;
            controller.Volume = controller.Volume;
        });
        AssertEx.True(
            entered.Wait(TimeSpan.FromSeconds(2)),
            "An event subscriber could not enter PlaybackController from another thread; the event was likely raised under its lock.");
        entered.GetAwaiter().GetResult();
    }
}
