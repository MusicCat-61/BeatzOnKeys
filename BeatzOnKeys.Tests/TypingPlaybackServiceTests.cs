using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Tests;

internal static class TypingPlaybackServiceTests
{
    public static async Task StartRequiresTrackAndLifecycleHasOneWorker()
    {
        var hook = new FakeKeyboardHook();
        var player = new FakeContinuousAudioPlayer();
        var controller = new PlaybackController { Loop = false };
        var service = new TypingPlaybackService(hook, controller, player);
        try
        {
            await AssertEx.ThrowsAsync<InvalidOperationException>(service.StartAsync);
            AssertEx.False(service.IsRunning);
            AssertEx.Equal(0, hook.InstallCalls);
            var track = Track("lifecycle");
            controller.UpsertTrack(track);
            controller.SelectTrack(track.Id);
            await service.StartAsync();
            await service.StartAsync();
            AssertEx.True(service.IsRunning);
            AssertEx.True(player.IsPlaying, "Start must play immediately, without a key.");
            AssertEx.Equal(1, hook.InstallCalls);
            AssertEx.Equal(1, player.StartCalls);
            player.Position = TimeSpan.FromSeconds(0.3);
            hook.RaiseKeyDown();
            await Wait(() => player.KeyPressCalls == 1);
            AssertEx.NearlyEqual(0.3, player.Position.TotalSeconds, message: "Typing moved the audio position.");
            await service.StopAsync();
            AssertEx.False(hook.IsInstalled);
            AssertEx.False(service.IsRunning);
            hook.RaiseKeyDown();
            await Task.Delay(40);
            AssertEx.Equal(1, player.KeyPressCalls);
            await service.StartAsync();
            AssertEx.Equal(2, hook.InstallCalls);
            AssertEx.Equal(TimeSpan.Zero, player.Position);
            hook.RaiseKeyDown();
            await Wait(() => player.KeyPressCalls == 2);
            await Task.Delay(40);
            AssertEx.Equal(2, player.KeyPressCalls, "An old worker duplicated a key.");
        }
        finally { await service.DisposeAsync(); }
        AssertEx.True(hook.IsDisposed);
        AssertEx.True(player.IsDisposed);
    }

    public static async Task StopAwaitsInFlightAndCancelsQueuedKeys()
    {
        var hook = new FakeKeyboardHook();
        var player = new FakeContinuousAudioPlayer { BlockNotify = true };
        var controller = Controller("blocked");
        var service = new TypingPlaybackService(hook, controller, player);
        try
        {
            await service.StartAsync();
            hook.RaiseKeyDown();
            AssertEx.True(player.PlayEntered.Wait(TimeSpan.FromSeconds(2)));
            hook.RaiseKeyDown();
            var stop = service.StopAsync();
            try { await Task.Delay(60); AssertEx.False(stop.IsCompleted); }
            finally { player.PlayRelease.Set(); }
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            AssertEx.False(service.IsRunning);
            AssertEx.Equal(1, player.KeyPressCalls, "A queued key ran after Stop.");
        }
        finally { player.PlayRelease.Set(); await service.DisposeAsync(); }
    }

    public static async Task FailedInstallAndStartRecoverWithoutOldWorker()
    {
        var hook = new FakeKeyboardHook { InstallException = new InvalidOperationException("hook failed") };
        var player = new FakeContinuousAudioPlayer();
        await using var service = new TypingPlaybackService(hook, Controller("install"), player);
        await AssertEx.ThrowsAsync<InvalidOperationException>(service.StartAsync);
        AssertEx.False(service.IsRunning);
        AssertEx.False(hook.IsInstalled);
        hook.InstallException = null;
        player.ThrowOnStart = true;
        await AssertEx.ThrowsAsync<IOException>(service.StartAsync);
        AssertEx.False(service.IsRunning);
        AssertEx.False(hook.IsInstalled);
        player.ThrowOnStart = false;
        await service.StartAsync();
        hook.RaiseKeyDown();
        await Wait(() => player.KeyPressCalls == 1);
        await Task.Delay(40);
        AssertEx.Equal(1, player.KeyPressCalls);
        AssertEx.Equal(3, hook.InstallCalls);
    }

    public static async Task SelectionDurationAndPlaybackFailure()
    {
        var hook = new FakeKeyboardHook();
        var player = new FakeContinuousAudioPlayer { LoadDurationOverride = TimeSpan.FromSeconds(2) };
        var controller = new PlaybackController { Loop = false };
        var first = Track("first");
        var second = Track("second");
        controller.UpsertTrack(first);
        controller.UpsertTrack(second);
        await using var service = new TypingPlaybackService(hook, controller, player);
        AssertEx.False(service.SelectTrack(Guid.NewGuid()));
        AssertEx.True(service.SelectTrack(first.Id));
        AssertEx.NearlyEqual(2, AssertEx.NotNull(controller.CurrentTrack).Duration.TotalSeconds);
        await service.StartAsync();
        player.Position = TimeSpan.FromSeconds(1);
        AssertEx.True(service.SelectTrack(second.Id));
        AssertEx.Equal<Guid?>(second.Id, controller.SelectedTrackId);
        AssertEx.Equal(TimeSpan.Zero, player.Position);
        AssertEx.True(player.IsPlaying);
        AssertEx.Equal(2, player.StartCalls);
        var completed = new TaskCompletionSource<PlaybackFailureEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PlaybackFailed += failure => completed.TrySetResult(failure);
        var error = new IOException("decoder failed");
        player.RaiseFailure(error);
        var observed = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AssertEx.Equal<Guid?>(second.Id, observed.TrackId);
        AssertEx.Equal(error, observed.Exception);
        AssertEx.False(service.IsRunning);
        await service.StopAsync();
        service.Deselect();
        AssertEx.Null(controller.CurrentTrack);
    }

    public static async Task NaturalEndAdvancesWithoutTypingAndLoopWraps()
    {
        var hook = new FakeKeyboardHook();
        var player = new FakeContinuousAudioPlayer();
        var controller = new PlaybackController { AutoAdvance = true, Loop = false };
        var first = Track("first");
        var second = Track("second");
        controller.UpsertTrack(first);
        controller.UpsertTrack(second);
        controller.SelectTrack(first.Id);
        await using var service = new TypingPlaybackService(hook, controller, player);
        await service.StartAsync();
        player.RaiseTrackEnded();
        await Wait(() => player.StartCalls == 2);
        AssertEx.Equal<Guid?>(second.Id, controller.SelectedTrackId);
        AssertEx.Equal(0, player.KeyPressCalls);
        player.RaiseTrackEnded();
        await Task.Delay(60);
        AssertEx.Equal(2, player.StartCalls);
        AssertEx.False(player.IsPlaying);
        hook.RaiseKeyDown();
        await Task.Delay(40);
        AssertEx.Equal(2, player.StartCalls, "A key must not reload an ended track.");
        controller.Loop = true;
        service.SelectTrack(second.Id);
        player.RaiseTrackEnded();
        await Wait(() => player.StartCalls == 4);
        AssertEx.Equal<Guid?>(first.Id, controller.SelectedTrackId);
    }

    public static async Task LateTrackEndAfterUnloadDoesNotAdvance()
    {
        var hook = new FakeKeyboardHook();
        var player = new FakeContinuousAudioPlayer();
        var controller = Controller("late-end");
        controller.AutoAdvance = true;
        controller.UpsertTrack(Track("next"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        player.TrackEnded += _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("End delivery was not released."); }
        };
        await using var service = new TypingPlaybackService(hook, controller, player);
        await service.StartAsync();
        var starts = player.StartCalls;
        var loads = player.LoadCalls;
        var delivery = Task.Run(() => player.RaiseTrackEnded());
        try
        {
            AssertEx.True(entered.Wait(TimeSpan.FromSeconds(2)));
            service.Unload();
            AssertEx.Equal(Guid.Empty, player.PlaybackId);
        }
        finally { release.Set(); }
        await delivery.WaitAsync(TimeSpan.FromSeconds(2));
        hook.RaiseKeyDown();
        await Wait(() => player.NotifyCalls == 1);
        AssertEx.Equal(starts, player.StartCalls, "A late end restarted playback after Unload.");
        AssertEx.Equal(loads, player.LoadCalls, "A late end loaded the next track after Unload.");
        AssertEx.False(player.IsPlaying);
    }

    public static async Task LateFailureCannotStopReplacementOrNewSession()
    {
        var hook = new FakeKeyboardHook();
        var player = new FakeContinuousAudioPlayer();
        var controller = Controller("old");
        var oldTrack = AssertEx.NotNull(controller.CurrentTrack);
        var replacement = Track("replacement");
        controller.UpsertTrack(replacement);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        player.PlaybackFailed += _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Failure delivery was not released."); }
        };
        await using var service = new TypingPlaybackService(hook, controller, player);
        await service.StartAsync();
        var oldId = player.PlaybackId;
        var oldSession = service.SessionId;
        var delivered = new TaskCompletionSource<PlaybackFailureEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PlaybackFailed += args => delivered.TrySetResult(args);
        var delivery = Task.Run(() => player.RaiseFailure(new IOException("old output failed")));
        try
        {
            AssertEx.True(entered.Wait(TimeSpan.FromSeconds(2)));
            AssertEx.True(service.SelectTrack(replacement.Id));
            AssertEx.True(oldId != player.PlaybackId);
        }
        finally { release.Set(); }
        await delivery.WaitAsync(TimeSpan.FromSeconds(2));
        hook.RaiseKeyDown();
        await Wait(() => player.KeyPressCalls == 1);
        AssertEx.True(service.IsRunning && player.IsPlaying);
        AssertEx.False(delivered.Task.IsCompleted, "An old output failure reached the replacement session.");
        var replacementId = player.PlaybackId;
        await service.StopAsync();
        await service.StartAsync();
        AssertEx.True(service.SessionId > oldSession && replacementId != player.PlaybackId);
        player.RaiseFailure(new IOException("stale output failed"), replacementId, oldTrack.Id);
        hook.RaiseKeyDown();
        await Wait(() => player.KeyPressCalls == 2);
        AssertEx.True(service.IsRunning && player.IsPlaying);
        AssertEx.False(delivered.Task.IsCompleted);
        var currentId = player.PlaybackId;
        player.RaiseFailure(new IOException("current output failed"));
        var failure = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AssertEx.Equal(currentId, failure.PlaybackId);
        AssertEx.Equal(service.SessionId, failure.SessionId);
        AssertEx.Equal<Guid?>(replacement.Id, failure.TrackId);
    }

    private static Task Wait(Func<bool> condition) => AudioTestData.WaitUntilAsync(condition, TimeSpan.FromSeconds(2), "The playback worker did not respond.");
    private static PlaybackController Controller(string name)
    {
        var controller = new PlaybackController { Loop = false };
        var track = Track(name);
        controller.UpsertTrack(track);
        controller.SelectTrack(track.Id);
        return controller;
    }
    private static PlaybackTrack Track(string name) => new(Guid.NewGuid(), name, $"C:\\audio\\{name}.mp3", TimeSpan.FromSeconds(10));
}
