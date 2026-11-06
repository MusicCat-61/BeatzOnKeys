using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;
using NAudio.Wave;

namespace BeatzOnKeys.Tests;

/// <summary>The four music-reactive features: punch attack, bass boost, stereo pulse, adaptive level.</summary>
internal static class ReactiveAudioTests
{
    public static Task PunchAttackIsFasterThanTheDefault()
    {
        var clock = new ManualTimeProvider();
        var envelope = new VolumeEnvelope(clock);
        envelope.Configure(0.2, 1);

        envelope.Trigger();
        clock.Advance(0.02);
        var defaultGain = envelope.GetGain();

        envelope.Reset();
        envelope.Trigger(attackSeconds: VolumeEnvelope.PunchAttackSeconds);
        clock.Advance(0.02);
        var punchGain = envelope.GetGain();

        AssertEx.True(punchGain > 0.95, $"A punch attack must reach full volume in 15 ms, got {punchGain:F3}.");
        AssertEx.True(defaultGain < 0.7, $"The default 50 ms attack must still be mid-ramp after 20 ms, got {defaultGain:F3}.");
        return Task.CompletedTask;
    }

    public static Task AnalyzerReportsOnlyRealTransients()
    {
        var clock = new ManualTimeProvider();
        var analyzer = new AudioAnalyzer(clock);
        var window = TimeSpan.FromMilliseconds(200);

        analyzer.Process(Buffer(0.05f), channels: 2, sampleRate: 8_000);
        AssertEx.False(analyzer.HitWithin(window), "A steady quiet buffer is not a hit.");
        AssertEx.NearlyEqual(0.05, analyzer.Peak, 1e-6);

        analyzer.Process(Buffer(0.5f), channels: 2, sampleRate: 8_000);
        AssertEx.True(analyzer.HitWithin(window), "A 10x peak jump is a hit.");

        clock.Advance(0.5);
        AssertEx.False(analyzer.HitWithin(window), "The hit expires after its window.");
        return Task.CompletedTask;
    }

    public static Task BassBoostRaisesGainButNeverAboveMaximum()
    {
        var clock = new ManualTimeProvider();
        var envelope = new VolumeEnvelope(clock);
        envelope.Configure(0.2, 0.7);
        for (var buffer = 0; buffer < 20; buffer++) { envelope.SetBassBoost(0.15); }
        AssertEx.NearlyEqual(0.35, envelope.GetGain(), 0.01, $"Quiet level plus bass boost should be 0.35, got {envelope.GetGain():F3}.");

        envelope.Configure(0.6, 0.7);
        clock.Advance(0.3);
        for (var buffer = 0; buffer < 20; buffer++) { envelope.SetBassBoost(0.3); }
        AssertEx.NearlyEqual(0.7, envelope.GetGain(), 0.001, "The boost must never pass MaxVolume.");

        for (var buffer = 0; buffer < 40; buffer++) { envelope.SetBassBoost(0); }
        AssertEx.NearlyEqual(0.5, envelope.GetGain(), 0.01, "Without bass the boost decays to zero (MinVolume is capped at 0.5).");
        return Task.CompletedTask;
    }

    public static Task StereoPulseAlternatesForFiftyMilliseconds()
    {
        var clock = new ManualTimeProvider();
        var envelope = new VolumeEnvelope(clock) { StereoPulseEnabled = true };
        envelope.Configure(0.2, 1);

        envelope.Trigger();
        var first = Render(envelope, 0.5f);
        AssertEx.True(Math.Abs(first.Left - first.Right) > 0.005, $"The first pulse must shift the balance, got {first}.");

        clock.Advance(0.06);
        var afterPulse = Render(envelope, 0.5f);
        AssertEx.NearlyEqual(afterPulse.Left, afterPulse.Right, 1e-6, "After 50 ms the balance returns to normal.");

        envelope.Trigger();
        var second = Render(envelope, 0.5f);
        AssertEx.True(second.Left < second.Right, $"The second pulse must shift the other way, got {second}.");
        AssertEx.True(second.Left < afterPulse.Left, "The second pulse pans left, the first panned right.");

        envelope.StereoPulseEnabled = false;
        envelope.Trigger();
        var disabled = Render(envelope, 0.5f);
        AssertEx.NearlyEqual(disabled.Left, disabled.Right, 1e-6, "Disabling the pulse removes the panning.");
        return Task.CompletedTask;
    }

    public static Task AdaptiveThresholdFollowsTrackLoudness()
    {
        using var directory = new TemporaryDirectory();
        var quiet = Load(directory.Path, "quiet.wav", sample: 800);
        var loud = Load(directory.Path, "loud.wav", sample: 30_000);
        var medium = Load(directory.Path, "medium.wav", sample: 12_000);

        var clock = new ManualTimeProvider();
        using var player = new ContinuousAudioPlayer(() => new FakeWavePlayer(), clock) { AdaptiveVolumeEnabled = true };

        player.LoadTrack(quiet);
        AssertEx.NearlyEqual(0.1, player.EffectiveMinVolume, 1e-6, "A quiet track lowers the quiet level to 10%.");
        player.LoadTrack(loud);
        AssertEx.NearlyEqual(0.3, player.EffectiveMinVolume, 1e-6, "A loud track raises the quiet level to 30%.");
        player.LoadTrack(medium);
        AssertEx.NearlyEqual(0.2, player.EffectiveMinVolume, 1e-6, "An average track keeps the user value.");

        player.AdaptiveVolumeEnabled = false;
        player.LoadTrack(quiet);
        AssertEx.NearlyEqual(0.2, player.EffectiveMinVolume, 1e-6, "Disabling the feature restores the user value.");
        AssertEx.NearlyEqual(0.2, player.MinVolume, 1e-6, "The user slider keeps its own value.");
        return Task.CompletedTask;
    }

    private static PlaybackTrack Load(string directory, string fileName, short sample) =>
        AudioTrackLoader.Load(Guid.NewGuid(),
            AudioTestData.CreateConstantPcm16Wave(directory, fileName, durationSeconds: 2, sample: sample));

    private static float[] Buffer(float value)
    {
        var buffer = new float[64];
        Array.Fill(buffer, value);
        return buffer;
    }

    private static (double Left, double Right) Render(VolumeEnvelope envelope, float value)
    {
        var samples = new[] { value, value, value, value };
        envelope.Apply(samples, channels: 2, sampleRate: 8_000);
        return (samples[0], samples[1]);
    }
}