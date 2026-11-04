using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;
using NAudio.Wave;

namespace BeatzOnKeys.Tests;

internal static class AudioFragmentTests
{
    public static Task FragmentUsesExactFramesAndSilenceDoesNotAdvance()
    {
        using var directory = new TemporaryDirectory();
        var path = AudioTestData.CreateConstantPcm16Wave(directory.Path);
        using var reader = new AudioFileReader(path);
        var provider = new FragmentSampleProvider(reader);
        var channels = provider.WaveFormat.Channels;
        var sampleRate = provider.WaveFormat.SampleRate;
        const double durationSeconds = 0.025;
        var fragmentFrames = checked((int)Math.Ceiling(durationSeconds * sampleRate));
        var bufferFrames = fragmentFrames + 160;
        var buffer = new float[bufferFrames * channels];

        provider.BeginFragment(0.2, durationSeconds);
        var read = provider.Read(buffer);
        AssertEx.Equal(buffer.Length, read);
        AssertFramesContainAudio(buffer, channels, 0, fragmentFrames);
        AssertFramesAreSilent(buffer, channels, fragmentFrames, bufferFrames);

        var positionAfterFragment = reader.CurrentTime.TotalSeconds;
        AssertEx.NearlyEqual(0.2 + durationSeconds, positionAfterFragment, 1d / sampleRate + 1e-6);

        Array.Fill(buffer, 1f);
        AssertEx.Equal(buffer.Length, provider.Read(buffer));
        AssertFramesAreSilent(buffer, channels, 0, bufferFrames);
        AssertEx.NearlyEqual(
            positionAfterFragment,
            reader.CurrentTime.TotalSeconds,
            1d / sampleRate + 1e-6,
            "Reading provider silence advanced the decoder.");

        provider.BeginFragment(0.4, 0.1);
        provider.ClearFragment();
        var clearedPosition = reader.CurrentTime.TotalSeconds;
        Array.Fill(buffer, 1f);
        provider.Read(buffer);
        AssertFramesAreSilent(buffer, channels, 0, bufferFrames);
        AssertEx.NearlyEqual(
            clearedPosition,
            reader.CurrentTime.TotalSeconds,
            1d / sampleRate + 1e-6,
            "A cleared fragment advanced while emitting silence.");
        return Task.CompletedTask;
    }

    public static Task FragmentTailVolumeAndValidation()
    {
        using var directory = new TemporaryDirectory();
        var path = AudioTestData.CreateConstantPcm16Wave(directory.Path);
        using var reader = new AudioFileReader(path);
        var provider = new FragmentSampleProvider(reader);
        var channels = provider.WaveFormat.Channels;
        var sampleRate = provider.WaveFormat.SampleRate;

        provider.Volume = -1;
        AssertEx.NearlyEqual(0, provider.Volume);
        provider.Volume = 2;
        AssertEx.NearlyEqual(1, provider.Volume);
        provider.Volume = double.NaN;
        AssertEx.NearlyEqual(0.7, provider.Volume, 1e-6);

        AssertEx.Throws<ArgumentOutOfRangeException>(() => provider.BeginFragment(0, 0));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => provider.BeginFragment(0, -1));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => provider.BeginFragment(double.NaN, 0.1));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => provider.BeginFragment(0, double.NaN));

        provider.Volume = 1;
        const double tailStart = 0.98;
        provider.BeginFragment(tailStart, 0.2);
        var expectedTailFrames = checked((int)Math.Round(
            Math.Max(0, reader.TotalTime.TotalSeconds - reader.CurrentTime.TotalSeconds) * sampleRate,
            MidpointRounding.AwayFromZero));
        var bufferFrames = expectedTailFrames + 80;
        var buffer = new float[bufferFrames * channels];
        provider.Read(buffer);
        AssertFramesContainAudio(buffer, channels, 0, expectedTailFrames);
        AssertFramesAreSilent(buffer, channels, expectedTailFrames, bufferFrames);

        provider.BeginFragment(5, 0.1);
        Array.Fill(buffer, 1f);
        provider.Read(buffer);
        AssertFramesAreSilent(buffer, channels, 0, bufferFrames);
        return Task.CompletedTask;
    }

    public static async Task PlayerLifecycleAndFailureForwarding()
    {
        using var directory = new TemporaryDirectory();
        var path = AudioTestData.CreateConstantPcm16Wave(directory.Path);
        var track = new PlaybackTrack(Guid.NewGuid(), "fixture", path, TimeSpan.FromSeconds(99));
        var output = new FakeWavePlayer();
        var player = new AudioFragmentPlayer(() => output);

        AssertEx.Throws<InvalidOperationException>(player.PrepareOutput);
        var actualDuration = player.LoadTrack(track);
        AssertEx.NearlyEqual(1, actualDuration.TotalSeconds, 1e-6);

        player.Volume = -1;
        AssertEx.NearlyEqual(0, player.Volume);
        player.Volume = 2;
        AssertEx.NearlyEqual(1, player.Volume);
        player.Volume = double.NaN;
        AssertEx.NearlyEqual(0.7, player.Volume);

        player.PrepareOutput();
        player.PrepareOutput();
        AssertEx.Equal(1, output.InitCalls);
        AssertEx.NotNull(output.Provider);

        var failure = new InvalidOperationException("device failed");
        Exception? observedFailure = null;
        player.PlaybackFailed += exception => observedFailure = exception;
        output.RaiseFailure(failure);
        AssertEx.Equal(failure, observedFailure);

        var step = new PlaybackStep(track with { Duration = actualDuration }, 0.2, 0.1, 0.4);
        player.PlayFragment(step);
        AssertEx.True(player.IsPlaying);
        AssertEx.Equal(1, output.PlayCalls);
        AssertEx.NearlyEqual(0.4, player.Volume);

        player.Pause();
        AssertEx.False(player.IsPlaying);
        AssertEx.True(output.StopCalls >= 1);

        player.Unload();
        AssertEx.True(output.IsDisposed);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Unload released the decoder's file handle.
        }

        player.Dispose();
        AssertEx.Throws<ObjectDisposedException>(() => player.LoadTrack(track));
        await Task.CompletedTask;
    }

    public static async Task OldIdleTimerCannotPauseNewFragment()
    {
        using var directory = new TemporaryDirectory();
        var path = AudioTestData.CreateConstantPcm16Wave(directory.Path, durationSeconds: 2);
        var track = new PlaybackTrack(Guid.NewGuid(), "timer", path, TimeSpan.FromSeconds(2));
        var output = new FakeWavePlayer();
        using var player = new AudioFragmentPlayer(() => output);
        player.LoadTrack(track);
        player.PrepareOutput();

        player.PlayFragment(new PlaybackStep(track, 0.1, 0.3, 0.7));
        await Task.Delay(25).ConfigureAwait(false);
        player.PlayFragment(new PlaybackStep(track, 0.2, 0.5, 0.7));

        // The first deadline is about 340 ms from the first key; the new one is about 540 ms
        // from the second. Wait past the old deadline but remain comfortably before the new one.
        await Task.Delay(360).ConfigureAwait(false);
        AssertEx.True(player.IsPlaying, "A stale idle timer paused the newer fragment.");
        AssertEx.Equal(0, output.PauseCalls, "A stale idle timer reached the output device.");

        await AudioTestData.WaitUntilAsync(
            () => !player.IsPlaying,
            TimeSpan.FromSeconds(2),
            "The newest fragment's idle timer did not pause playback.").ConfigureAwait(false);
        AssertEx.Equal(1, output.PauseCalls);
    }

    public static async Task ReplayWaitsForDelayedStopAndOutputInitializesOffContext()
    {
        using var directory = new TemporaryDirectory();
        var path = AudioTestData.CreateConstantPcm16Wave(directory.Path, durationSeconds: 2);
        var track = new PlaybackTrack(Guid.NewGuid(), "delayed-stop", path, TimeSpan.FromSeconds(2));
        var output = new FakeWavePlayer();
        SynchronizationContext? factoryContext = null;
        var previousContext = SynchronizationContext.Current;
        var player = new AudioFragmentPlayer(() =>
        {
            factoryContext = SynchronizationContext.Current;
            return output;
        });
        Task? replay = null;

        try
        {
            player.LoadTrack(track);
            SynchronizationContext.SetSynchronizationContext(new TestUiSynchronizationContext());
            try { player.PrepareOutput(); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

            player.PlayFragment(new PlaybackStep(track, 0.1, 0.02, 0.7));
            await AudioTestData.WaitUntilAsync(
                () => !player.IsPlaying && output.PlaybackState == PlaybackState.Paused,
                TimeSpan.FromSeconds(2),
                "The first short fragment did not reach its idle pause.").ConfigureAwait(false);
            AssertEx.Equal(1, output.PlayCalls);

            output.DelayStopCompletion = true;
            output.StopCalled.Reset();
            replay = Task.Run(() => player.PlayFragment(new PlaybackStep(track, 0.2, 0.2, 0.7)));
            AssertEx.True(output.StopCalled.Wait(TimeSpan.FromSeconds(2)), "Replay never requested a paused-buffer flush.");
            await Task.Delay(80).ConfigureAwait(false);
            AssertEx.False(replay.IsCompleted, "PlayFragment returned before delayed PlaybackStopped.");
            AssertEx.Equal(1, output.PlayCalls, "A new Play started before the old WaveOut worker stopped.");

            output.CompletePendingStop();
            await replay.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            AssertEx.Equal(2, output.PlayCalls);
            AssertEx.True(player.IsPlaying);
            AssertEx.Null(factoryContext, "The output factory captured the caller SynchronizationContext.");
            AssertEx.Null(output.InitSynchronizationContext, "IWavePlayer.Init captured the caller SynchronizationContext.");
        }
        finally
        {
            output.CompletePendingStop();
            output.DelayStopCompletion = false;
            if (replay is not null)
            {
                try { await replay.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* Preserve the primary assertion failure; Dispose still must not hang. */ }
            }
            player.Dispose();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    public static Task TrackLoaderValidatesPathAndFormat()
    {
        using var directory = new TemporaryDirectory();
        var path = AudioTestData.CreateConstantPcm16Wave(directory.Path, "loader.wav");
        var id = Guid.NewGuid();
        var track = AudioTrackLoader.Load(id, path);
        AssertEx.Equal(id, track.Id);
        AssertEx.Equal("loader.wav", track.Name);
        AssertEx.Equal(System.IO.Path.GetFullPath(path), track.SourcePath);
        AssertEx.NearlyEqual(1, track.Duration.TotalSeconds, 1e-6);

        AssertEx.Throws<FileNotFoundException>(() =>
            AudioTrackLoader.Load(Guid.NewGuid(), System.IO.Path.Combine(directory.Path, "missing.mp3")));
        var unsupported = System.IO.Path.Combine(directory.Path, "fixture.txt");
        File.Copy(path, unsupported);
        AssertEx.Throws<NotSupportedException>(() => AudioTrackLoader.Load(Guid.NewGuid(), unsupported));
        return Task.CompletedTask;
    }

    private sealed class TestUiSynchronizationContext : SynchronizationContext
    {
    }

    private static void AssertFramesContainAudio(float[] buffer, int channels, int firstFrame, int endFrame)
    {
        for (var frame = firstFrame; frame < endFrame; frame++)
        {
            var hasAudio = false;
            for (var channel = 0; channel < channels; channel++)
            {
                hasAudio |= Math.Abs(buffer[(frame * channels) + channel]) > 1e-8f;
            }
            AssertEx.True(hasAudio, $"Expected audio at frame {frame}.");
        }
    }

    private static void AssertFramesAreSilent(float[] buffer, int channels, int firstFrame, int endFrame)
    {
        for (var frame = firstFrame; frame < endFrame; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                AssertEx.NearlyEqual(0, buffer[(frame * channels) + channel], 1e-12,
                    $"Expected silence at frame {frame}, channel {channel}.");
            }
        }
    }
}
