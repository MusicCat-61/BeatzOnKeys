using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.Json;
using BeatzOnKeys.Core.Audio;
using BeatzOnKeys.Core.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// Capture only this process, never a microphone or other applications.
// Retain numeric levels/timing only: raw captured samples never leave the callback.
if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
{
    Console.Error.WriteLine("Process-specific audio diagnostics require Windows build 20348 or later.");
    return 2;
}
var sourcePath = args.Length > 0 ? Path.GetFullPath(args[0]) :
    Path.GetFullPath("artifacts/ui-smoke/fixtures/Afterglow - generated fixture.mp3");
var reportPath = Path.GetFullPath("artifacts/audio-probe/report.json");
try
{
    return await RunProbeAsync(sourcePath, reportPath);
}
catch (Exception error)
{
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    var failure = JsonSerializer.Serialize(new { passed = false, utc = DateTimeOffset.UtcNow,
        error = error.ToString(), privacy = "Only this process; no microphone, other-process audio, or raw recording." },
        new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(reportPath, failure);
    Console.Error.WriteLine(failure);
    return 2;
}

static async Task<int> RunProbeAsync(string sourcePath, string reportPath)
{
    var artifacts = Path.GetDirectoryName(reportPath)!;
    Directory.CreateDirectory(artifacts);
    SetProcessPath("RootDirectory", Path.Combine(artifacts, "localappdata"));
    SetProcessPath("SettingsFile", Path.Combine(artifacts, "unused-default-settings.json"));
    SetProcessPath("LogDirectory", Path.Combine(artifacts, "logs"));
    SetProcessPath("UpdateDirectory", Path.Combine(artifacts, "updates"));
    var track = AudioTrackLoader.Load(Guid.NewGuid(), sourcePath);
    using var player = new ContinuousAudioPlayer
    {
        MinVolume = 0.2, MaxVolume = 1.0, IdleTimeout = 3, PauseOnIdle = true
    };
    var duration = player.LoadTrack(track);
    if (duration < TimeSpan.FromSeconds(15))
        throw new ArgumentException("Use a fixture at least 15 seconds long so natural completion cannot interrupt the probe.");
    var format = WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);
    await using var capture = await new WasapiRecorderBuilder()
        .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
        .WithFormat(format)
        .WithBufferLength(20)
        .BuildAsync();
    var packets = new List<LevelPacket>();
    var checks = new List<ProbeAssertion>();
    var gate = new object();
    var clock = Stopwatch.StartNew();
    Exception? captureError = null;
    capture.RecordingStopped += (_, eventArgs) => { lock (gate) captureError ??= eventArgs.Exception; };
    capture.DataAvailable += (buffer, flags, _, _) =>
    {
        var values = MemoryMarshal.Cast<byte, float>(buffer);
        double peak = 0;
        double power = 0;
        var nonSilentSamples = 0;
        if ((flags & AudioClientBufferFlags.Silent) == 0)
        {
            foreach (var value in values)
            {
                peak = Math.Max(peak, Math.Abs(value));
                power += value * value;
                if (Math.Abs(value) > 1e-7) { nonSilentSamples++; }
            }
        }
        var rms = values.Length == 0 ? 0 : Math.Sqrt(power / values.Length);
        var nonSilentFraction = values.Length == 0 ? 0 : nonSilentSamples / (double)values.Length;
        var packetDuration = values.Length * 1000.0 / (format.SampleRate * format.Channels);
        lock (gate) packets.Add(new LevelPacket(clock.Elapsed.TotalMilliseconds, packetDuration, peak, rms, nonSilentFraction));
    };
    void Check(string name, bool passed, string detail)
    {
        checks.Add(new ProbeAssertion(name, passed, detail));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
    }
    capture.StartRecording();
    await Task.Delay(300);
    var start = clock.Elapsed.TotalMilliseconds;
    player.Start();
    var startPosition = player.Position;
    Check("start.playsQuietFromBeginning", player.IsPlaying && !player.IsIdlePaused &&
        Math.Abs(player.CurrentGain - 0.2) < 0.01 && startPosition.TotalSeconds < 0.75,
        $"Playing={player.IsPlaying}, gain={player.CurrentGain:F3}, position={startPosition.TotalSeconds:F3}s.");
    await Task.Delay(1200);
    var quietEnd = clock.Elapsed.TotalMilliseconds;
    Check("start.positionAdvancesWithoutKeys", player.IsPlaying && player.Position > startPosition + TimeSpan.FromMilliseconds(600),
        $"No keys: {startPosition.TotalSeconds:F3}s -> {player.Position.TotalSeconds:F3}s.");

    var typingStart = clock.Elapsed.TotalMilliseconds;
    var lastKey = typingStart;
    var typingClock = Stopwatch.StartNew();
    while (typingClock.ElapsedMilliseconds < 1500)
    {
        lastKey = clock.Elapsed.TotalMilliseconds;
        player.NotifyKeyPress();
        await Task.Delay(40);
    }
    var typingEnd = clock.Elapsed.TotalMilliseconds;
    Check("typing.reachesMaximum", player.IsPlaying && Math.Abs(player.CurrentGain - 1.0) < 0.01,
        $"Playing={player.IsPlaying}, gain={player.CurrentGain:F3}.");
    await Task.Delay(1400);
    var quietAfterEnd = clock.Elapsed.TotalMilliseconds;
    Check("release.quietButStillPlaying", player.IsPlaying && !player.IsIdlePaused && Math.Abs(player.CurrentGain - 0.2) < 0.01,
        $"After 150ms hold + 200ms release: Playing={player.IsPlaying}, gain={player.CurrentGain:F3}.");

    await UntilAsync(() => player.IsIdlePaused, 4000, "3-second idle pause");
    var pauseObserved = clock.Elapsed.TotalMilliseconds;
    var sinceKey = pauseObserved - lastKey;
    await Task.Delay(200);
    var pausedPosition = player.Position;
    await Task.Delay(500);
    var pausedEnd = clock.Elapsed.TotalMilliseconds;
    Check("idle.pausesAfterTimeout", !player.IsPlaying && player.IsIdlePaused && sinceKey >= 2950 && sinceKey <= 3400,
        $"Idle pause observed {sinceKey:F1}ms after last key; Playing={player.IsPlaying}.");
    Check("idle.positionFrozen", player.Position == pausedPosition,
        $"Paused position remains {pausedPosition.TotalSeconds:F4}s.");

    var resumeStart = clock.Elapsed.TotalMilliseconds;
    player.NotifyKeyPress();
    var resumedPosition = player.Position;
    Check("resume.instantMaximumSamePosition", player.IsPlaying && !player.IsIdlePaused &&
        Math.Abs(player.CurrentGain - 1.0) < 0.01 && resumedPosition >= pausedPosition &&
        resumedPosition - pausedPosition < TimeSpan.FromMilliseconds(750),
        $"First key: gain={player.CurrentGain:F3}, {pausedPosition.TotalSeconds:F4}s -> {resumedPosition.TotalSeconds:F4}s (normal output buffering allowed).");
    var resumeClock = Stopwatch.StartNew();
    while (resumeClock.ElapsedMilliseconds < 800)
    {
        await Task.Delay(40);
        player.NotifyKeyPress();
    }
    var resumeEnd = clock.Elapsed.TotalMilliseconds;

    player.PauseOnIdle = false;
    var continuousStart = clock.Elapsed.TotalMilliseconds;
    var continuousPosition = player.Position;
    var idleClock = Stopwatch.StartNew();
    var stateSamples = 0;
    var stateGaps = 0;
    while (idleClock.Elapsed.TotalSeconds < player.IdleTimeout + 0.8)
    {
        await Task.Delay(20);
        stateSamples++;
        if (!player.IsPlaying || player.IsIdlePaused) stateGaps++;
    }
    var continuousEnd = clock.Elapsed.TotalMilliseconds;
    Check("idle.disabledRemainsContinuous", stateGaps == 0 && player.IsPlaying && !player.IsIdlePaused &&
        Math.Abs(player.CurrentGain - 0.2) < 0.01 && player.Position > continuousPosition + TimeSpan.FromSeconds(3),
        $"{stateSamples} state samples over {idleClock.Elapsed.TotalSeconds:F2}s, gaps={stateGaps}, gain={player.CurrentGain:F3}.");
    player.Stop();
    var stoppedStart = clock.Elapsed.TotalMilliseconds;
    await Task.Delay(500);
    var stoppedEnd = clock.Elapsed.TotalMilliseconds;
    Check("stop.staysStopped", !player.IsPlaying && !player.IsIdlePaused, "Stop leaves no playing or idle-paused session.");
    capture.StopRecording();
    await Task.Delay(120);
    LevelPacket[] snapshot;
    lock (gate)
    {
        if (captureError is not null) throw captureError;
        snapshot = packets.ToArray();
    }
    Region Summarize(double from, double to)
    {
        var region = snapshot.Where(packet => packet.TimeMs >= from && packet.TimeMs < to).ToArray();
        var recordedDuration = region.Sum(packet => packet.DurationMs);
        var rms = recordedDuration == 0 ? 0 : Math.Sqrt(region.Sum(packet => packet.Rms * packet.Rms * packet.DurationMs) / recordedDuration);
        var maxGap = region.Length < 2 ? 0 : region.Zip(region.Skip(1)).Max(pair => pair.Second.TimeMs - pair.First.TimeMs);
        return new Region(region.Length, region.Length == 0 ? 0 : region.Max(packet => packet.Peak), rms,
            region.Length == 0 ? 0 : region.Count(packet => packet.Rms > 1e-5) / (double)region.Length,
            to <= from ? 0 : Math.Min(1, recordedDuration / (to - from)), maxGap,
            recordedDuration == 0 ? 0 : region.Sum(packet => packet.NonSilentSampleFraction * packet.DurationMs) / recordedDuration);
    }
    var preStart = Summarize(0, start - 30);
    var initialQuiet = Summarize(start + 300, quietEnd - 30);
    var typing = Summarize(typingStart + 300, typingEnd - 30);
    var quietAfterTyping = Summarize(lastKey + 600, quietAfterEnd - 30);
    var idlePaused = Summarize(pauseObserved + 200, pausedEnd - 30);
    var resumed = Summarize(resumeStart + 200, resumeEnd - 30);
    var continuousIdle = Summarize(continuousStart + 600, continuousEnd - 30);
    var stopped = Summarize(stoppedStart + 200, stoppedEnd - 30);
    bool Continuous(Region region) => region.Packets >= 10 && region.AudibleFraction >= 0.9 &&
        region.PacketCoverage >= 0.9 && region.NonSilentSampleFraction >= 0.98;
    Check("loopback.processSilentBeforeStart", preStart.Peak < 1e-5, $"Peak={preStart.Peak:F7}.");
    Check("loopback.typingContinuity90Percent", Continuous(typing), JsonSerializer.Serialize(typing));
    Check("loopback.quietContinuity90Percent", Continuous(initialQuiet) && Continuous(quietAfterTyping) && Continuous(continuousIdle),
        JsonSerializer.Serialize(new { initialQuiet, quietAfterTyping, continuousIdle }));
    Check("levels.maximumLouderThanMinimum", initialQuiet.Rms > 1e-5 && typing.Rms > 1e-4 && typing.Rms > initialQuiet.Rms * 3,
        $"Min RMS={initialQuiet.Rms:F6}, Max RMS={typing.Rms:F6}, ratio={typing.Rms / Math.Max(initialQuiet.Rms, 1e-12):F2}.");
    Check("levels.releaseAndContinuousIdleAreQuiet", quietAfterTyping.Rms > 1e-5 && continuousIdle.Rms > 1e-5 &&
        quietAfterTyping.Rms < typing.Rms * 0.5 && continuousIdle.Rms < typing.Rms * 0.5,
        $"After release RMS={quietAfterTyping.Rms:F6}; disabled-pause idle RMS={continuousIdle.Rms:F6}.");
    Check("loopback.idlePauseSilent", idlePaused.Peak < 1e-5, $"Paused peak={idlePaused.Peak:F7}.");
    Check("loopback.resumeMaximum", Continuous(resumed) && resumed.Rms > quietAfterTyping.Rms * 3,
        JsonSerializer.Serialize(resumed));
    Check("loopback.stopSilent", stopped.Peak < 1e-5, $"Stopped peak={stopped.Peak:F7}.");
    Check("isolation.noDefaultSettings", !File.Exists(AppPaths.SettingsFile), "App-data/log paths redirected to diagnostic artifacts; no real settings touched.");
    var passed = checks.All(check => check.Passed);
    var report = new
    {
        passed, utc = DateTimeOffset.UtcNow, version = "1.3.0.8", source = Path.GetFileName(sourcePath),
        durationSeconds = duration.TotalSeconds, processId = Environment.ProcessId,
        capture = "Windows process-specific loopback (only this diagnostic process tree)",
        defaults = new { MinVolume = 0.2, MaxVolume = 1.0, IdleTimeout = 3, PauseOnIdle = true },
        packets = snapshot.Length, assertions = checks,
        regions = new { preStart, initialQuiet, typing, quietAfterTyping, idlePaused, resumed, continuousIdle, stopped },
        continuity = "At least 90% audible packets, 90% time coverage, and 98% non-silent samples in typing and unpaused quiet regions.",
        privacy = "No microphone or other-process capture. No raw captured audio saved; numeric levels/timing only."
    };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(reportPath, json);
    Console.WriteLine(json);
    return passed ? 0 : 1;
}

static async Task UntilAsync(Func<bool> predicate, int timeoutMs, string reason)
{
    var clock = Stopwatch.StartNew();
    while (!predicate())
    {
        if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for " + reason);
        await Task.Delay(10);
    }
}

static void SetProcessPath(string property, string value)
{
    var field = typeof(AppPaths).GetField($"<{property}>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(AppPaths).FullName, property);
    _ = typeof(AppPaths).GetProperty(property)!.GetValue(null);
    var method = new DynamicMethod("AudioProbePath_" + property, typeof(string).MakeByRefType(), Type.EmptyTypes,
        typeof(AppPaths).Module, skipVisibility: true);
    var il = method.GetILGenerator();
    il.Emit(OpCodes.Ldsflda, field);
    il.Emit(OpCodes.Ret);
    var accessor = (StaticStringRef)method.CreateDelegate(typeof(StaticStringRef));
    accessor() = value;
}

internal delegate ref string StaticStringRef();
internal sealed record ProbeAssertion(string Name, bool Passed, string Detail);
internal sealed record LevelPacket(double TimeMs, double DurationMs, double Peak, double Rms, double NonSilentSampleFraction);
internal sealed record Region(int Packets, double Peak, double Rms, double AudibleFraction, double PacketCoverage, double MaximumGapMs,
    double NonSilentSampleFraction);
