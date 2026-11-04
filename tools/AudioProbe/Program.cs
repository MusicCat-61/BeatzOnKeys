using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// This developer-only probe captures THIS process, never the microphone or other applications.
// Only numeric signal levels are retained; no captured audio is written to disk.
if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
{
    Console.Error.WriteLine("Process-specific audio diagnostics require Windows 10 build 19041 or later.");
    return 2;
}
var sourcePath = args.Length > 0 ? Path.GetFullPath(args[0]) :
    Path.GetFullPath("artifacts/ui-smoke/fixtures/Afterglow - generated fixture.mp3");
var reportPath = Path.GetFullPath("artifacts/audio-probe/report.json");
var track = AudioTrackLoader.Load(Guid.NewGuid(), sourcePath);
using (var readerProbe = new AudioFileReader(sourcePath))
{
    Console.WriteLine($"Decoder format: {readerProbe.WaveFormat}; length: {readerProbe.TotalTime.TotalSeconds:F4}");
    readerProbe.CurrentTime = TimeSpan.FromSeconds(0.15);
    var block = new float[readerProbe.WaveFormat.SampleRate / 50 * readerProbe.WaveFormat.Channels];
    for (var index = 0; index < 10; index++)
    {
        var read = readerProbe.Read(block);
        Console.WriteLine($"Decode {index}: requested={block.Length}, read={read}, time={readerProbe.CurrentTime.TotalSeconds:F6}, peak={block.Take(read).Select(value => Math.Abs(value)).DefaultIfEmpty().Max():F6}");
    }
}
using var player = new AudioFragmentPlayer();
player.LoadTrack(track);
player.PrepareOutput();
await using var capture = await new WasapiRecorderBuilder()
    .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2))
    .WithBufferLength(20)
    .BuildAsync();
var packets = new List<LevelPacket>();
var gate = new object();
var clock = new Stopwatch();
Exception? captureError = null;
capture.RecordingStopped += (_, eventArgs) => captureError = eventArgs.Exception;
capture.DataAvailable += (buffer, flags, _, _) =>
{
    var values = MemoryMarshal.Cast<byte, float>(buffer);
    double peak = 0;
    double power = 0;
    if ((flags & AudioClientBufferFlags.Silent) == 0)
    {
        foreach (var value in values)
        {
            peak = Math.Max(peak, Math.Abs(value));
            power += value * value;
        }
    }
    var rms = values.Length == 0 ? 0 : Math.Sqrt(power / values.Length);
    lock (gate) { packets.Add(new LevelPacket(clock.Elapsed.TotalMilliseconds, peak, rms)); }
};
capture.StartRecording();
clock.Start();
await Task.Delay(300);
var singleStart = clock.Elapsed.TotalMilliseconds;
player.PlayFragment(new PlaybackStep(track, 0.15, 0.15, 0.15));
await Task.Delay(450);
var rapidStart = clock.Elapsed.TotalMilliseconds;
for (var index = 0; index < 16; index++)
{
    player.PlayFragment(new PlaybackStep(track, 0.45 + index * 0.15, 0.15, 0.15));
    await Task.Delay(40);
}
var rapidEnd = clock.Elapsed.TotalMilliseconds;
await Task.Delay(450);
capture.StopRecording();
await Task.Delay(80);
if (captureError is not null) { throw captureError; }
LevelPacket[] snapshot;
lock (gate) { snapshot = packets.ToArray(); }
Region Summarize(double start, double end)
{
    var region = snapshot.Where(packet => packet.TimeMs >= start && packet.TimeMs < end).ToArray();
    return new Region(region.Length, region.Length == 0 ? 0 : region.Max(packet => packet.Peak),
        region.Length == 0 ? 0 : region.Count(packet => packet.Rms > 1e-5) / (double)region.Length);
}
var idle = Summarize(0, singleStart - 40);
var single = Summarize(singleStart + 30, singleStart + 210);
var paused = Summarize(singleStart + 280, rapidStart - 20);
var rapid = Summarize(rapidStart + 60, rapidEnd - 20);
var finalIdle = Summarize(rapidEnd + 280, clock.Elapsed.TotalMilliseconds);
var passed = idle.Peak < 1e-5 && single.Peak > 1e-4 && single.AudibleFraction > 0.3 &&
             paused.Peak < 1e-5 && rapid.Peak > 1e-4 && rapid.AudibleFraction > 0.85 &&
             finalIdle.Peak < 1e-5 && !player.IsPlaying;
var report = new
{
    passed,
    capture = "Windows process-specific loopback (only this diagnostic process)",
    source = Path.GetFileName(sourcePath),
    packets = snapshot.Length,
    idle,
    single,
    paused,
    rapid,
    finalIdle,
    privacy = "No microphone/other-process capture. No raw captured audio saved."
};
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(reportPath, json);
Console.WriteLine(json);
return passed ? 0 : 1;

internal sealed record LevelPacket(double TimeMs, double Peak, double Rms);
internal sealed record Region(int Packets, double Peak, double AudibleFraction);
