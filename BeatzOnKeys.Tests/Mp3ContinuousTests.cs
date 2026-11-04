using BeatzOnKeys.Core.Audio;
using NAudio.Wave;

namespace BeatzOnKeys.Tests;

internal static class Mp3ContinuousTests
{
    private static string FixturePath => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "neutral-1s.mp3");

    public static Task SequentialPacketsMatchOriginalRecording()
    {
        using var raw = new AudioFileReader(FixturePath);
        using var reader = new AudioFileReader(FixturePath);
        var envelope = new VolumeEnvelope(new ManualTimeProvider());
        var provider = new ContinuousSampleProvider(reader, envelope);
        var packet = new float[(reader.WaveFormat.SampleRate / 50) * reader.WaveFormat.Channels];
        var original = new float[packet.Length];
        var packets = 0;
        var total = 0;
        while (true)
        {
            var expected = ReadFull(raw, original);
            var actual = provider.Read(packet);
            AssertEx.Equal(expected, actual, "Sequential MP3 packets changed length.");
            if (actual == 0) { break; }
            for (var sample = 0; sample < actual; sample++)
            {
                AssertEx.NearlyEqual(original[sample] * 0.2, packet[sample], 1e-6, "MP3 samples were skipped, repeated, or silenced.");
            }
            total += actual;
            packets++;
        }
        AssertEx.True(packets > 20);
        AssertEx.True(total > 0);
        AssertEx.True(provider.Position > TimeSpan.FromSeconds(0.9));
        Console.WriteLine($"INFO MP3 continuous: {packets} packets, {total} samples, no intermediate gaps.");
        return Task.CompletedTask;
    }

    public static Task RapidVolumePulsesNeverInterruptMp3Packets()
    {
        using var raw = new AudioFileReader(FixturePath);
        using var reader = new AudioFileReader(FixturePath);
        var clock = new ManualTimeProvider();
        var envelope = new VolumeEnvelope(clock);
        var provider = new ContinuousSampleProvider(reader, envelope);
        var packet = new float[(reader.WaveFormat.SampleRate / 50) * reader.WaveFormat.Channels];
        var original = new float[packet.Length];
        var packets = 0;
        var audible = 0;
        while (true)
        {
            if (packets % 2 == 0) { envelope.Trigger(); }
            var expected = ReadFull(raw, original);
            var actual = provider.Read(packet);
            AssertEx.Equal(expected, actual);
            if (actual == 0) { break; }
            for (var sample = 0; sample < actual; sample++)
            {
                if (Math.Abs(original[sample]) < 1e-6) { continue; }
                var ratio = packet[sample] / original[sample];
                AssertEx.True(ratio >= 0.199 && ratio <= 1.001, "Typing replaced an original MP3 sample or produced silence.");
                audible++;
            }
            packets++;
            clock.Advance(0.02);
        }
        AssertEx.True(audible > 1000);
        AssertEx.NearlyEqual(raw.CurrentTime.TotalSeconds, provider.Position.TotalSeconds, 1e-6);
        return Task.CompletedTask;
    }

    public static Task Mp3StartAndIdleResumeKeepOneStream()
    {
        var track = AudioTrackLoader.Load(Guid.NewGuid(), FixturePath);
        var clock = new ManualTimeProvider();
        var output = new FakeWavePlayer();
        using var player = new ContinuousAudioPlayer(() => output, clock);
        player.LoadTrack(track);
        player.Start();
        AssertEx.True(player.IsPlaying);
        AssertEx.Equal(TimeSpan.Zero, player.Position);
        var provider = AssertEx.NotNull(output.Provider);
        var buffer = new byte[provider.WaveFormat.AverageBytesPerSecond / 10];
        AssertEx.Equal(buffer.Length, provider.Read(buffer));
        clock.Advance(0.1);
        var position = player.Position;
        player.NotifyKeyPress();
        AssertEx.Equal(position, player.Position);
        clock.Advance(3);
        AssertEx.True(player.IsIdlePaused);
        player.NotifyKeyPress();
        AssertEx.Equal(position, player.Position);
        AssertEx.NearlyEqual(1, player.CurrentGain);
        AssertEx.Equal(1, output.InitCalls);
        AssertEx.Equal(0, output.StopCalls);
        AssertEx.Equal(2, output.PlayCalls);
        return Task.CompletedTask;
    }

    private static int ReadFull(AudioFileReader reader, float[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = reader.Read(buffer.AsSpan(read));
            if (count == 0) { break; }
            read += count;
        }
        return read;
    }
}
