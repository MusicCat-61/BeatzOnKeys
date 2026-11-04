using BeatzOnKeys.Core.Audio;
using NAudio.Wave;

namespace BeatzOnKeys.Tests;

internal static class Mp3ShortReadRegressionTests
{
    private static string FixturePath => System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "neutral-1s.mp3");

    public static Task RawAudioFileReaderSeekReadProfile()
    {
        AssertEx.True(File.Exists(FixturePath), $"MP3 fixture missing: {FixturePath}");
        using var reader = new AudioFileReader(FixturePath);
        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;
        var requests = new[]
        {
            (sampleRate / 50) * channels,
            checked((int)Math.Ceiling(0.15 * sampleRate) * channels),
            checked((sampleRate / 2) * channels)
        };
        var positions = new[] { 0.10, 0.15, 0.31, 0.47, 0.63 };
        var positiveShortReads = 0;

        foreach (var requested in requests)
        {
            var counts = new List<int>();
            foreach (var position in positions)
            {
                reader.CurrentTime = TimeSpan.FromSeconds(position);
                var buffer = new float[requested];
                var count = reader.Read(buffer);
                counts.Add(count);
                AssertEx.True(count > 0, $"MP3 reader returned EOS in the middle of the fixture at {position:F2}s.");
                AssertEx.True(count <= requested);
                if (count < requested) { positiveShortReads++; }
            }
            Console.WriteLine(
                $"INFO MP3 raw Read requested={requested} samples ({requested / (double)(sampleRate * channels):F3}s), " +
                $"returned=[{string.Join(',', counts)}]");
        }

        Console.WriteLine($"INFO MP3 positive short reads after seek={positiveShortReads}");

        var packetSamples = (sampleRate / 50) * channels;
        foreach (var position in positions)
        {
            reader.CurrentTime = TimeSpan.FromSeconds(position);
            var packetRms = new List<string>();
            for (var packetIndex = 0; packetIndex < 12; packetIndex++)
            {
                var packet = new float[packetSamples];
                var count = reader.Read(packet);
                packetRms.Add($"{Rms(packet.AsSpan(0, count)):E3}");
            }
            Console.WriteLine($"INFO MP3 seek={position:F2}s raw 20ms RMS=[{string.Join(',', packetRms)}]");
        }
        return Task.CompletedTask;
    }

    public static Task SequentialReadAndPrerollAvoidSeekPriming()
    {
        AssertEx.True(File.Exists(FixturePath), $"MP3 fixture missing: {FixturePath}");
        const double start = 0.15;
        const double fragment = 0.15;

        using var continuousReader = new AudioFileReader(FixturePath);
        var channels = continuousReader.WaveFormat.Channels;
        var sampleRate = continuousReader.WaveFormat.SampleRate;
        var fragmentSamples = checked((int)Math.Ceiling(fragment * sampleRate) * channels);
        var packetSamples = (sampleRate / 50) * channels;
        continuousReader.CurrentTime = TimeSpan.FromSeconds(start);
        _ = continuousReader.Read(new float[fragmentSamples]);
        var continuousPacket = new float[packetSamples];
        _ = continuousReader.Read(continuousPacket);
        var continuousRms = Rms(continuousPacket);

        using var reseekReader = new AudioFileReader(FixturePath);
        reseekReader.CurrentTime = TimeSpan.FromSeconds(start);
        _ = reseekReader.Read(new float[fragmentSamples]);
        var sameLogicalPosition = reseekReader.CurrentTime;
        reseekReader.CurrentTime = sameLogicalPosition;
        var reseekPacket = new float[packetSamples];
        _ = reseekReader.Read(reseekPacket);
        var reseekRms = Rms(reseekPacket);

        using var prerollReader = new AudioFileReader(FixturePath);
        var target = 0.63;
        const double prerollSeconds = 0.10;
        prerollReader.CurrentTime = TimeSpan.FromSeconds(target - prerollSeconds);
        _ = prerollReader.Read(new float[checked((int)Math.Ceiling(prerollSeconds * sampleRate) * channels)]);
        var prerollPacket = new float[packetSamples];
        _ = prerollReader.Read(prerollPacket);
        var prerollRms = Rms(prerollPacket);

        Console.WriteLine(
            $"INFO MP3 A/B continuousRms={continuousRms:E6}, same-position-reseekRms={reseekRms:E6}, " +
            $"100ms-prerollRms={prerollRms:E6}");
        AssertEx.True(continuousRms > 1e-2, "Sequential decoding unexpectedly went silent.");
        AssertEx.True(prerollRms > 1e-2, "100 ms pre-roll did not produce audible MP3 data.");
        return Task.CompletedTask;
    }

    public static Task ProviderFillsEveryPacketUntilFragmentBudget()
    {
        AssertEx.True(File.Exists(FixturePath), $"MP3 fixture missing: {FixturePath}");
        using var reader = new AudioFileReader(FixturePath);
        var provider = new FragmentSampleProvider(reader);
        var channels = provider.WaveFormat.Channels;
        var sampleRate = provider.WaveFormat.SampleRate;
        var packetFrames = sampleRate / 50; // 20 ms, matching the WaveOut buffer target.
        var packet = new float[packetFrames * channels];
        const double fragmentSeconds = 0.15;
        var fragmentFrames = checked((int)Math.Ceiling(fragmentSeconds * sampleRate));
        var activePackets = checked((int)Math.Ceiling(fragmentFrames / (double)packetFrames));

        provider.BeginFragment(0.15, fragmentSeconds);
        var actualStart = reader.CurrentTime.TotalSeconds;
        for (var index = 0; index < activePackets; index++)
        {
            Array.Fill(packet, 0f);
            AssertEx.Equal(packet.Length, provider.Read(packet));
            var rms = Rms(packet);
            Console.WriteLine($"INFO MP3 provider packet={index + 1}/{activePackets} rms={rms:E6}");
            AssertEx.True(rms > 1e-5, $"Silent packet {index + 1} appeared before the fragment budget ended.");
        }

        var endPosition = reader.CurrentTime.TotalSeconds;
        AssertEx.NearlyEqual(
            fragmentSeconds,
            endPosition - actualStart,
            2d / sampleRate,
            "The decoder did not advance by the complete fragment budget.");

        Array.Fill(packet, 1f);
        provider.Read(packet);
        AssertEx.True(packet.All(sample => sample == 0), "Provider emitted samples after its fragment budget.");
        var frozenPosition = reader.CurrentTime.TotalSeconds;
        provider.Read(packet);
        AssertEx.NearlyEqual(
            frozenPosition,
            reader.CurrentTime.TotalSeconds,
            1d / sampleRate,
            "Silent provider reads advanced the MP3 decoder.");
        return Task.CompletedTask;
    }

    public static Task RapidMp3SeeksHaveNoIntermediateSilentPacket()
    {
        AssertEx.True(File.Exists(FixturePath), $"MP3 fixture missing: {FixturePath}");
        using var reader = new AudioFileReader(FixturePath);
        var provider = new FragmentSampleProvider(reader);
        var channels = provider.WaveFormat.Channels;
        var sampleRate = provider.WaveFormat.SampleRate;
        var packet = new float[(sampleRate / 50) * channels];

        for (var key = 0; key < 16; key++)
        {
            var position = 0.10 + (key * 0.04);
            provider.BeginFragment(position, 0.15);
            for (var packetIndex = 0; packetIndex < 2; packetIndex++)
            {
                Array.Fill(packet, 0f);
                provider.Read(packet);
                var rms = Rms(packet);
                AssertEx.True(
                    rms > 1e-5,
                    $"Rapid seek {key + 1}, packet {packetIndex + 1} was silent at {position:F2}s.");
            }
        }

        return Task.CompletedTask;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double power = 0;
        foreach (var sample in samples) { power += sample * sample; }
        return samples.Length == 0 ? 0 : Math.Sqrt(power / samples.Length);
    }
}
