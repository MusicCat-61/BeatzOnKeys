using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;
using NAudio.Wave;

namespace BeatzOnKeys.Tests;

internal static class ContinuousAudioTests
{
    public static Task SequentialProviderAdvancesDuringQuietAndEndsNormally()
    {
        using var directory = new TemporaryDirectory();
        var path = AudioTestData.CreateConstantPcm16Wave(directory.Path);
        using var reader = new AudioFileReader(path);
        var envelope = new VolumeEnvelope(new ManualTimeProvider());
        var provider = new ContinuousSampleProvider(reader, envelope);
        var packet = new float[800];
        for (var index = 0; index < 20; index++)
        {
            AssertEx.Equal(packet.Length, provider.Read(packet));
            AssertEx.NearlyEqual((index + 1) * 0.05, provider.Position.TotalSeconds, 1e-6);
            AssertEx.True(packet.All(sample => sample > 0), "Quiet playback emitted a silent packet.");
        }
        AssertEx.Equal(0, provider.Read(packet));
        return Task.CompletedTask;
    }

    public static Task IdlePauseResumeKeepsDecoderAndPosition()
    {
        using var directory = new TemporaryDirectory();
        var track = LoadWave(directory.Path);
        var clock = new ManualTimeProvider();
        var output = new FakeWavePlayer();
        using var player = new ContinuousAudioPlayer(() => output, clock);
        player.LoadTrack(track);
        player.Start();
        var playbackId = player.PlaybackId;
        AssertEx.True(playbackId != Guid.Empty);
        AssertEx.True(player.IsPlaying);
        AssertEx.NearlyEqual(0.2, player.CurrentGain);
        AssertEx.Equal(1, output.PlayCalls);
        AssertEx.Equal(TimeSpan.Zero, player.Position);
        ReadAudio(output, 0.1);
        clock.Advance(0.1);
        var position = player.Position;
        player.NotifyKeyPress();
        AssertEx.Equal(position, player.Position, "A key moved the decoder.");
        AssertEx.Equal(1, output.PlayCalls, "Typing restarted the device.");
        clock.Advance(0.05);
        AssertEx.NearlyEqual(1, player.CurrentGain);
        clock.Advance(0.3);
        AssertEx.NearlyEqual(0.2, player.CurrentGain);
        AssertEx.True(player.IsPlaying, "The release incorrectly stopped audio.");
        clock.Advance(2.649);
        AssertEx.True(player.IsPlaying);
        clock.Advance(0.001);
        AssertEx.True(player.IsIdlePaused);
        AssertEx.False(player.IsPlaying);
        AssertEx.Equal(1, output.PauseCalls);
        AssertEx.Equal(0, output.StopCalls);
        clock.Advance(1);
        AssertEx.Equal(position, player.Position);
        player.NotifyKeyPress();
        AssertEx.True(player.IsPlaying);
        AssertEx.False(player.IsIdlePaused);
        AssertEx.NearlyEqual(1, player.CurrentGain);
        AssertEx.Equal(position, player.Position);
        AssertEx.Equal(1, output.InitCalls, "Resume recreated the audio stream.");
        AssertEx.Equal(2, output.PlayCalls);
        AssertEx.Equal(playbackId, player.PlaybackId, "Idle resume replaced the playback generation.");
        AssertEx.Equal(0, output.StopCalls);
        return Task.CompletedTask;
    }

    public static Task DisabledPauseAndLiveSettings()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider();
        var output = new FakeWavePlayer();
        using var player = new ContinuousAudioPlayer(() => output, clock) { PauseOnIdle = false };
        player.LoadTrack(LoadWave(directory.Path));
        player.Start();
        clock.Advance(10);
        AssertEx.True(player.IsPlaying);
        AssertEx.Equal(0, output.PauseCalls);
        player.PauseOnIdle = true;
        clock.Advance(0);
        AssertEx.True(player.IsIdlePaused);
        player.PauseOnIdle = false;
        AssertEx.True(player.IsPlaying);
        AssertEx.NearlyEqual(0.2, player.CurrentGain);
        player.MinVolume = 2;
        player.MaxVolume = -1;
        player.IdleTimeout = 99;
        AssertEx.NearlyEqual(0.5, player.MinVolume);
        AssertEx.NearlyEqual(0, player.MaxVolume);
        AssertEx.NearlyEqual(10, player.IdleTimeout);
        clock.Advance(0.2);
        AssertEx.NearlyEqual(0, player.CurrentGain);
        player.MinVolume = double.NaN;
        player.MaxVolume = double.NaN;
        player.IdleTimeout = double.NaN;
        AssertEx.NearlyEqual(0.2, player.MinVolume);
        AssertEx.NearlyEqual(1, player.MaxVolume);
        AssertEx.NearlyEqual(3, player.IdleTimeout);
        return Task.CompletedTask;
    }

    public static Task StaleIdleCallbacksCannotPauseNewActivityOrRun()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider();
        var outputs = new List<FakeWavePlayer>();
        using var player = new ContinuousAudioPlayer(() => { var output = new FakeWavePlayer(); outputs.Add(output); return output; }, clock);
        player.LoadTrack(LoadWave(directory.Path));
        player.Start();
        clock.Advance(2.9);
        player.NotifyKeyPress();
        clock.FireStaleCallbacks();
        AssertEx.True(player.IsPlaying);
        AssertEx.Equal(0, outputs[0].PauseCalls);
        clock.Advance(2.9);
        AssertEx.True(player.IsPlaying);
        clock.Advance(0.1);
        AssertEx.True(player.IsIdlePaused);
        player.Stop();
        player.Start();
        clock.FireStaleCallbacks();
        AssertEx.True(player.IsPlaying);
        AssertEx.Equal(0, outputs[1].PauseCalls);
        player.Dispose();
        clock.FireStaleCallbacks();
        return Task.CompletedTask;
    }

    public static Task StopStartResetsToZeroAndUnloadReleasesFile()
    {
        using var directory = new TemporaryDirectory();
        var track = LoadWave(directory.Path);
        var outputs = new List<FakeWavePlayer>();
        var player = new ContinuousAudioPlayer(() => { var output = new FakeWavePlayer(); outputs.Add(output); return output; });
        AssertEx.Throws<InvalidOperationException>(player.Start);
        AssertEx.NearlyEqual(2, player.LoadTrack(track).TotalSeconds);
        player.Start();
        ReadAudio(outputs[0], 0.2);
        AssertEx.True(player.Position > TimeSpan.Zero);
        player.Stop();
        player.Start();
        AssertEx.Equal(TimeSpan.Zero, player.Position);
        AssertEx.NearlyEqual(0.2, player.CurrentGain);
        AssertEx.True(outputs[0].IsDisposed);
        AssertEx.Equal(2, outputs.Count);
        player.Unload();
        AssertEx.True(outputs[1].IsDisposed);
        using (new FileStream(track.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        player.Dispose();
        AssertEx.Throws<ObjectDisposedException>(() => player.LoadTrack(track));
        return Task.CompletedTask;
    }

    public static async Task NaturalEndFailureAndExplicitStopAreDistinct()
    {
        using var directory = new TemporaryDirectory();
        var output = new FakeWavePlayer();
        using var player = new ContinuousAudioPlayer(() => output);
        player.LoadTrack(LoadWave(directory.Path));
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.TrackEnded += _ => ended.TrySetResult(true);
        player.Start();
        output.CompleteTrack();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AssertEx.False(player.IsPlaying);
        player.NotifyKeyPress();
        AssertEx.Equal(1, output.PlayCalls);

        var failingOutput = new FakeWavePlayer();
        using var failingPlayer = new ContinuousAudioPlayer(() => failingOutput);
        failingPlayer.LoadTrack(LoadWave(directory.Path, "failure.wav"));
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var endEvents = 0;
        failingPlayer.PlaybackFailed += failure => failed.TrySetResult(failure.Exception);
        failingPlayer.TrackEnded += _ => Interlocked.Increment(ref endEvents);
        failingPlayer.Start();
        var exception = new IOException("output failed");
        failingOutput.RaiseFailure(exception);
        AssertEx.Equal(exception, await failed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        AssertEx.False(failingPlayer.IsPlaying);
        AssertEx.Equal(0, endEvents);

        var stopOutput = new FakeWavePlayer();
        using var stoppedPlayer = new ContinuousAudioPlayer(() => stopOutput);
        stoppedPlayer.LoadTrack(LoadWave(directory.Path, "stop.wav"));
        stoppedPlayer.TrackEnded += _ => Interlocked.Increment(ref endEvents);
        stoppedPlayer.Start();
        stoppedPlayer.Stop();
        await Task.Delay(40);
        AssertEx.Equal(0, endEvents, "Explicit Stop emitted a natural-end event.");
    }

    public static async Task StopWaitsForOutputAndInitializationAvoidsUiContext()
    {
        using var directory = new TemporaryDirectory();
        var first = new FakeWavePlayer();
        var calls = 0;
        SynchronizationContext? factoryContext = new SynchronizationContext();
        using var player = new ContinuousAudioPlayer(() =>
        {
            factoryContext = SynchronizationContext.Current;
            return calls++ == 0 ? first : new FakeWavePlayer();
        });
        player.LoadTrack(LoadWave(directory.Path));
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try { player.Start(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        AssertEx.Null(factoryContext);
        AssertEx.Null(first.InitSynchronizationContext);
        first.DelayStopCompletion = true;
        var stop = Task.Run(player.Stop);
        try
        {
            AssertEx.True(first.StopCalled.Wait(TimeSpan.FromSeconds(2)));
            await Task.Delay(60);
            AssertEx.False(stop.IsCompleted, "Stop returned while the old device worker was active.");
            first.CompletePendingStop();
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            player.Start();
            AssertEx.True(player.IsPlaying);
            AssertEx.Equal(TimeSpan.Zero, player.Position);
        }
        finally { first.CompletePendingStop(); first.DelayStopCompletion = false; }
    }

    public static Task InvalidReplacementPreservesWorkingTrackAndLoaderValidatesFormat()
    {
        using var directory = new TemporaryDirectory();
        var track = LoadWave(directory.Path);
        var output = new FakeWavePlayer();
        using var player = new ContinuousAudioPlayer(() => output);
        player.LoadTrack(track);
        player.Start();
        AssertEx.Throws<FileNotFoundException>(() => player.LoadTrack(track with { Id = Guid.NewGuid(), SourcePath = System.IO.Path.Combine(directory.Path, "missing.mp3") }));
        AssertEx.True(player.IsPlaying);
        AssertEx.False(output.IsDisposed);
        var unsupported = System.IO.Path.Combine(directory.Path, "fixture.txt");
        File.Copy(track.SourcePath, unsupported);
        AssertEx.Throws<NotSupportedException>(() => AudioTrackLoader.Load(Guid.NewGuid(), unsupported));
        AssertEx.Equal(track.Id, AudioTrackLoader.Load(track.Id, track.SourcePath).Id);
        return Task.CompletedTask;
    }

    public static Task PauseKeepsPositionAndResumeContinuesFromIt()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider();
        var output = new FakeWavePlayer();
        using var player = new ContinuousAudioPlayer(() => output, clock);
        player.LoadTrack(LoadWave(directory.Path));
        player.Start();
        ReadAudio(output, 0.5);
        clock.Advance(0.5);
        var position = player.Position;
        AssertEx.True(position > TimeSpan.Zero, "The decoder must have advanced before the pause.");

        player.Pause();
        AssertEx.True(player.IsPaused);
        AssertEx.False(player.IsPlaying, "A paused player is not playing.");
        AssertEx.Equal(1, output.PauseCalls, "Pause must silence the device, not tear it down.");
        AssertEx.Equal(1, output.InitCalls, "Pause must not recreate the stream.");
        AssertEx.Equal(0, output.StopCalls, "Pause is not a stop.");

        clock.Advance(3);
        AssertEx.Equal(position, player.Position, "A pause must not move the decoder.");

        // Typing is the whole point of the app, so an explicit pause has to survive it.
        player.NotifyKeyPress();
        AssertEx.True(player.IsPaused, "A key press must not quietly undo an explicit pause.");
        AssertEx.Equal(position, player.Position, "A key press during a pause must not move the decoder.");

        player.Resume();
        AssertEx.True(player.IsPlaying);
        AssertEx.False(player.IsPaused);
        AssertEx.Equal(1, output.InitCalls, "Resume must reuse the paused stream.");
        AssertEx.Equal(2, output.PlayCalls);
        AssertEx.Equal(position, player.Position, "Resume must continue from the paused sample, not from zero.");

        ReadAudio(output, 0.5);
        clock.Advance(0.5);
        AssertEx.True(player.Position > position, "Audio must keep moving after a resume.");
        return Task.CompletedTask;
    }

    public static Task PausedPlayerIgnoresTheIdleTimerAndStopResetsIt()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider();
        var outputs = new List<FakeWavePlayer>();
        using var player = new ContinuousAudioPlayer(
            () => { var created = new FakeWavePlayer(); outputs.Add(created); return created; }, clock);
        player.LoadTrack(LoadWave(directory.Path));
        player.Start();
        var output = outputs[0];
        ReadAudio(output, 0.2);
        clock.Advance(0.2);
        var position = player.Position;

        player.Pause();
        clock.Advance(10);
        AssertEx.True(player.IsPaused, "The idle timer must not take over an explicit pause.");
        AssertEx.False(player.IsIdlePaused);
        AssertEx.Equal(1, output.PauseCalls, "Only the user pause may touch the device here.");
        AssertEx.Equal(position, player.Position);

        player.Stop();
        AssertEx.False(player.IsPaused, "Stop clears the pause.");

        // Start still means "from the beginning"; only Resume continues in place.
        player.Start();
        AssertEx.Equal(TimeSpan.Zero, player.Position);
        AssertEx.False(player.IsPaused);
        return Task.CompletedTask;
    }

    public static Task ResumeLiftsTheIdlePauseFromTheSamePosition()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider();
        var output = new FakeWavePlayer();
        using var player = new ContinuousAudioPlayer(() => output, clock);
        player.IdleTimeout = 1;
        player.PauseOnIdle = true;
        player.LoadTrack(LoadWave(directory.Path));
        player.Start();
        ReadAudio(output, 0.4);
        clock.Advance(0.4);

        clock.Advance(2);
        AssertEx.True(player.IsIdlePaused, "The idle timer pauses the run.");
        AssertEx.False(player.IsPaused, "An idle pause is not an explicit one.");
        var frozen = player.Position;

        // The transport button calls Resume for an idle pause too, so it must lift that pause.
        player.Resume();
        AssertEx.True(player.IsPlaying, "Resume starts the audio again.");
        AssertEx.False(player.IsIdlePaused, "Resume clears the idle pause.");
        AssertEx.False(player.IsPaused, "Resume does not leave an explicit pause behind.");
        AssertEx.Equal(1, output.InitCalls, "Resume must reuse the paused stream.");
        AssertEx.Equal(frozen, player.Position, "Resume continues from the idle-paused sample.");

        ReadAudio(output, 0.3);
        clock.Advance(0.3);
        AssertEx.True(player.Position > frozen, "Audio keeps moving after resuming from idle.");

        // Resuming again is harmless: there is nothing left to resume.
        var playCalls = output.PlayCalls;
        player.Resume();
        AssertEx.Equal(playCalls, output.PlayCalls, "Resume on a running player must not restart it.");
        return Task.CompletedTask;
    }

    private static PlaybackTrack LoadWave(string directory, string fileName = "fixture.wav") =>
        AudioTrackLoader.Load(Guid.NewGuid(), AudioTestData.CreateConstantPcm16Wave(directory, fileName, durationSeconds: 2));

    private static void ReadAudio(FakeWavePlayer output, double seconds)
    {
        var provider = AssertEx.NotNull(output.Provider);
        var samples = (int)Math.Round(provider.WaveFormat.SampleRate * seconds) * provider.WaveFormat.Channels;
        var buffer = new byte[samples * provider.WaveFormat.BitsPerSample / 8];
        AssertEx.Equal(buffer.Length, provider.Read(buffer));
    }
}
