using BeatzOnKeys.Core.Audio;

namespace BeatzOnKeys.Tests;

internal static class VolumeEnvelopeTests
{
    public static Task AttackHoldAndRelease()
    {
        var clock = new ManualTimeProvider();
        var envelope = new VolumeEnvelope(clock);
        AssertEx.NearlyEqual(0.2, envelope.GetGain());
        envelope.Trigger();
        AssertEx.NearlyEqual(0.2, envelope.GetGain());
        clock.Advance(0.025);
        AssertEx.NearlyEqual(0.6, envelope.GetGain());
        clock.Advance(0.025);
        AssertEx.NearlyEqual(1, envelope.GetGain());
        clock.Advance(0.1);
        AssertEx.NearlyEqual(1, envelope.GetGain());
        clock.Advance(0.1);
        AssertEx.NearlyEqual(0.6, envelope.GetGain());
        clock.Advance(0.1);
        AssertEx.NearlyEqual(0.2, envelope.GetGain());
        return Task.CompletedTask;
    }

    public static Task RetriggerAndRapidKeysStaySmooth()
    {
        var clock = new ManualTimeProvider();
        var envelope = new VolumeEnvelope(clock);
        envelope.Trigger();
        clock.Advance(0.02);
        envelope.Trigger();
        clock.Advance(0.03);
        AssertEx.NearlyEqual(1, envelope.GetGain(), message: "Rapid keys must not keep restarting the attack.");
        clock.Advance(0.22);
        AssertEx.NearlyEqual(0.6, envelope.GetGain());
        envelope.Trigger();
        AssertEx.NearlyEqual(0.6, envelope.GetGain(), message: "A key during release must not jump in volume.");
        clock.Advance(0.025);
        AssertEx.NearlyEqual(0.8, envelope.GetGain());
        clock.Advance(0.025);
        AssertEx.NearlyEqual(1, envelope.GetGain());
        return Task.CompletedTask;
    }

    public static Task InstantResumeAndMasterVolumeCeiling()
    {
        var clock = new ManualTimeProvider();
        var envelope = new VolumeEnvelope(clock);
        envelope.Trigger(instant: true);
        AssertEx.NearlyEqual(1, envelope.GetGain());
        clock.Advance(0.35);
        AssertEx.NearlyEqual(0.2, envelope.GetGain());
        envelope.Configure(0.5, 0.1);
        envelope.Reset();
        AssertEx.NearlyEqual(0.1, envelope.GetGain());
        envelope.Trigger(instant: true);
        AssertEx.NearlyEqual(0.1, envelope.GetGain());
        envelope.Configure(double.NaN, double.PositiveInfinity);
        envelope.Reset();
        AssertEx.NearlyEqual(0.2, envelope.GetGain());
        envelope.Configure(0.2, 0);
        envelope.Reset();
        AssertEx.NearlyEqual(0, envelope.GetGain());
        return Task.CompletedTask;
    }

    public static Task ConsecutiveBuffersAreSampleSmoothAndStereoLinked()
    {
        var envelope = new VolumeEnvelope(new ManualTimeProvider());
        envelope.Trigger();
        var first = Enumerable.Repeat(1f, 40).ToArray();
        var second = Enumerable.Repeat(1f, 40).ToArray();
        envelope.Apply(first, 2, 1000);
        envelope.Apply(second, 2, 1000);
        var samples = first.Concat(second).ToArray();
        for (var frame = 0; frame < 40; frame++)
        {
            AssertEx.NearlyEqual(0.2 + frame * 0.016, samples[frame * 2], 1e-6);
            AssertEx.Equal(samples[frame * 2], samples[frame * 2 + 1]);
        }
        AssertEx.Throws<ArgumentOutOfRangeException>(() => envelope.Apply(first, 0, 1000));
        AssertEx.Throws<ArgumentException>(() => envelope.Apply(new float[3], 2, 1000));
        return Task.CompletedTask;
    }

    public static Task RenderingAheadDoesNotPrematurelyReleaseLogicalGain()
    {
        var clock = new ManualTimeProvider();
        var envelope = new VolumeEnvelope(clock);
        envelope.Trigger();
        envelope.Apply(Enumerable.Repeat(1f, 400).ToArray(), 1, 1000);
        AssertEx.NearlyEqual(0.2, envelope.GetGain());
        clock.Advance(0.025);
        AssertEx.NearlyEqual(0.6, envelope.GetGain(), message: "Rendering later samples mutated the earlier envelope state.");
        return Task.CompletedTask;
    }
}
