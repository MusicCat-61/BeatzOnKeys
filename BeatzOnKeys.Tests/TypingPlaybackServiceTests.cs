using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Tests;

internal static class TypingPlaybackServiceTests
{
    public static async Task StartRequiresTrackAndLifecycleHasOneWorker()
    {
        var hook = new FakeKeyboardHook();
        var player = new FakeAudioFragmentPlayer();
        var controller = new PlaybackController { StepSeconds = 0.1, Loop = false };
        var service = new TypingPlaybackService(hook, controller, player);
        try
        {
            await AssertEx.ThrowsAsync<InvalidOperationException>(service.StartAsync).ConfigureAwait(false);
            AssertEx.False(service.IsRunning);
            AssertEx.Equal(0, hook.InstallCalls);

            var track = Track("lifecycle", 10);
            controller.UpsertTrack(track);
            AssertEx.True(controller.SelectTrack(track.Id));

            await service.StartAsync().ConfigureAwait(false);
            await service.StartAsync().ConfigureAwait(false);
            AssertEx.True(service.IsRunning);
            AssertEx.True(hook.IsInstalled);
            AssertEx.Equal(1, hook.InstallCalls);
            AssertEx.Equal(1, player.PrepareCalls);

            hook.RaiseKeyDown();
            await AudioTestData.WaitUntilAsync(
                () => player.Steps.Count == 1,
                TimeSpan.FromSeconds(2),
                "The first worker did not process a key.").ConfigureAwait(false);
            AssertEx.NearlyEqual(0.1, player.Steps[0].PositionSeconds);

            await service.StopAsync().ConfigureAwait(false);
            AssertEx.False(service.IsRunning);
            AssertEx.False(hook.IsInstalled);
            var afterFirstStop = player.Steps.Count;
            hook.RaiseKeyDown();
            await Task.Delay(60).ConfigureAwait(false);
            AssertEx.Equal(afterFirstStop, player.Steps.Count, "A stopped worker processed a key.");

            await service.StartAsync().ConfigureAwait(false);
            AssertEx.Equal(2, hook.InstallCalls);
            hook.RaiseKeyDown();
            await AudioTestData.WaitUntilAsync(
                () => player.Steps.Count == afterFirstStop + 1,
                TimeSpan.FromSeconds(2),
                "The restarted worker did not process exactly one key.").ConfigureAwait(false);
            await Task.Delay(60).ConfigureAwait(false);
            AssertEx.Equal(afterFirstStop + 1, player.Steps.Count, "An old worker survived restart and duplicated a key.");
            AssertEx.NearlyEqual(0.2, player.Steps[^1].PositionSeconds);
            await service.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }

        AssertEx.True(hook.IsDisposed);
        AssertEx.True(player.IsDisposed);
    }

    public static async Task StopAwaitsInFlightAndCancelsQueuedKeys()
    {
        var hook = new FakeKeyboardHook();
        var player = new FakeAudioFragmentPlayer { BlockPlay = true };
        var controller = new PlaybackController { StepSeconds = 0.1, Loop = false };
        var track = Track("blocked", 10);
        controller.UpsertTrack(track);
        AssertEx.True(controller.SelectTrack(track.Id));
        var service = new TypingPlaybackService(hook, controller, player);

        try
        {
            await service.StartAsync().ConfigureAwait(false);
            hook.RaiseKeyDown();
            AssertEx.True(player.PlayEntered.Wait(TimeSpan.FromSeconds(2)), "The fake player was never entered.");
            hook.RaiseKeyDown();

            var stopTask = service.StopAsync();
            try
            {
                await Task.Delay(80).ConfigureAwait(false);
                AssertEx.False(stopTask.IsCompleted, "StopAsync returned before the in-flight audio call completed.");
            }
            finally
            {
                player.PlayRelease.Set();
            }

            await stopTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            AssertEx.False(service.IsRunning);
            AssertEx.Equal(1, player.Steps.Count, "A queued key ran after cancellation.");
        }
        finally
        {
            player.PlayRelease.Set();
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static async Task FailedInstallDoesNotLeaveOldWorker()
    {
        var hook = new FakeKeyboardHook { InstallException = new InvalidOperationException("hook failed") };
        var player = new FakeAudioFragmentPlayer();
        var controller = new PlaybackController { StepSeconds = 0.1, Loop = false };
        var track = Track("install", 10);
        controller.UpsertTrack(track);
        AssertEx.True(controller.SelectTrack(track.Id));
        var service = new TypingPlaybackService(hook, controller, player);

        try
        {
            await AssertEx.ThrowsAsync<InvalidOperationException>(service.StartAsync).ConfigureAwait(false);
            AssertEx.False(service.IsRunning);
            AssertEx.False(hook.IsInstalled);

            hook.InstallException = null;
            await service.StartAsync().ConfigureAwait(false);
            hook.RaiseKeyDown();
            await AudioTestData.WaitUntilAsync(
                () => player.Steps.Count == 1,
                TimeSpan.FromSeconds(2),
                "The worker did not recover after a failed hook installation.").ConfigureAwait(false);
            await Task.Delay(60).ConfigureAwait(false);
            AssertEx.Equal(1, player.Steps.Count, "The failed StartAsync left an old worker behind.");
            AssertEx.Equal(2, hook.InstallCalls);
            await service.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static async Task SelectionDurationAndPlaybackFailure()
    {
        var hook = new FakeKeyboardHook();
        var player = new FakeAudioFragmentPlayer { LoadDurationOverride = TimeSpan.FromSeconds(2) };
        var controller = new PlaybackController { StepSeconds = 0.1, Loop = false };
        var track = Track("selection", 1);
        controller.UpsertTrack(track);
        var service = new TypingPlaybackService(hook, controller, player);

        try
        {
            AssertEx.False(service.SelectTrack(Guid.NewGuid()));
            AssertEx.True(service.SelectTrack(track.Id));
            AssertEx.Equal<Guid?>(track.Id, controller.SelectedTrackId);
            AssertEx.NearlyEqual(2, AssertEx.NotNull(controller.CurrentTrack).Duration.TotalSeconds);
            service.Unload();
            AssertEx.Equal(1, player.UnloadCalls);

            var failureCompletion = new TaskCompletionSource<PlaybackFailureEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.PlaybackFailed += value => failureCompletion.TrySetResult(value);
            await service.StartAsync().ConfigureAwait(false);
            var failure = new IOException("decoder failed");
            player.RaiseFailure(failure);
            var observed = await failureCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            AssertEx.Equal<Guid?>(track.Id, observed.TrackId);
            AssertEx.Equal(failure, observed.Exception);
            await AudioTestData.WaitUntilAsync(
                () => !service.IsRunning,
                TimeSpan.FromSeconds(2),
                "The service remained running after an audio failure.").ConfigureAwait(false);
            await service.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static PlaybackTrack Track(string name, double durationSeconds) =>
        new(Guid.NewGuid(), name, $"C:\\audio\\{name}.mp3", TimeSpan.FromSeconds(durationSeconds));
}
