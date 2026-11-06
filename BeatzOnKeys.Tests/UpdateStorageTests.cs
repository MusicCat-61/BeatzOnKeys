using System.Diagnostics;
using BeatzOnKeys.Core.Updates;

namespace BeatzOnKeys.Tests;

/// <summary>
/// The update is one executable parked beside the running one. These checks guard the parts that
/// decide whether a download can be trusted and whether the swap is safe to hand over.
/// </summary>
internal static class UpdateStorageTests
{
    public static Task ReleaseAssetsPointAtTheExecutable()
    {
        // The updater replaces the portable executable itself; the zip stays on the release for
        // manual installs but must never be what the updater downloads.
        AssertEx.Equal("BeatzOnKeys.exe", UpdateService.AssetFileName);
        AssertEx.Equal("BeatzOnKeys.exe.sha256", UpdateService.ChecksumFileName);
        AssertEx.Equal("BeatzOnKeys.update.exe", UpdateService.StagedFileName);
        return Task.CompletedTask;
    }

    public static Task StagedFileSitsBesideTheExecutable()
    {
        var expected = Path.Combine(UpdateService.ExecutableDirectory, UpdateService.StagedFileName);
        AssertEx.Equal(expected, UpdateService.StagedFilePath);
        AssertEx.Equal(UpdateService.ExecutableDirectory, Path.GetDirectoryName(UpdateService.StagedFilePath),
            "The staged build must live in the application folder, so the swap is a same-volume rename.");
        return Task.CompletedTask;
    }

    public static Task TinyStagedFileIsDiscarded()
    {
        var staged = UpdateService.StagedFilePath;
        try
        {
            File.WriteAllBytes(staged, new byte[64]);
            AssertEx.Null(UpdateService.FindPreparedUpdate(),
                "A file far below any real build size must not be offered as an update.");
            AssertEx.False(File.Exists(staged), "The rejected file must be deleted, not offered again.");
        }
        finally
        {
            TryDelete(staged);
        }

        return Task.CompletedTask;
    }

    public static Task StagedBuildNotNewerIsDiscarded()
    {
        var staged = UpdateService.StagedFilePath;
        try
        {
            // The running test host carries this build's version, so it stands in for "a leftover of
            // an already installed release". Padding keeps it above the size floor so the version
            // guard, not the size guard, is what rejects it.
            WritePaddedCopy(Environment.ProcessPath ?? throw new TestFailureException("No process path."), staged, 2 * 1024 * 1024);
            var fileVersion = FileVersionInfo.GetVersionInfo(staged).FileVersion;
            AssertEx.True(!string.IsNullOrWhiteSpace(fileVersion), "The fixture must carry a file version.");

            AssertEx.Null(UpdateService.FindPreparedUpdate(),
                $"A staged build of {fileVersion} must not be offered to {UpdateService.CurrentVersion.Display}.");
            AssertEx.False(File.Exists(staged), "Applying it would downgrade the app, so it must be deleted.");
        }
        finally
        {
            TryDelete(staged);
        }

        return Task.CompletedTask;
    }

    public static Task ApplyScriptIsAsciiAndTakesPathsFromTheEnvironment()
    {
        var script = UpdateService.ApplyScriptText;
        AssertEx.True(script.All(character => character is '\n' or '\r' || character < 128),
            "The apply script must stay pure ASCII: cmd.exe reads a .cmd file in the OEM code page.");
        AssertEx.True(script.Contains("%BOK_STAGED%", StringComparison.Ordinal) &&
            script.Contains("%BOK_TARGET%", StringComparison.Ordinal) &&
            script.Contains("%BOK_LOG%", StringComparison.Ordinal) &&
            script.Contains("%BOK_PID%", StringComparison.Ordinal),
            "Every path must arrive through an environment variable.");
        AssertEx.False(script.Contains(":\\", StringComparison.Ordinal),
            "The script must not embed a literal path; a Cyrillic profile folder would be mangled.");
        AssertEx.True(script.Contains("move /Y", StringComparison.Ordinal),
            "The swap must be a rename first.");
        AssertEx.True(script.Contains("copy /Y /B", StringComparison.Ordinal),
            "A rename can fail for reasons no retry fixes, so a copy fallback must exist.");
        AssertEx.True(script.Contains("taskkill", StringComparison.Ordinal),
            "A process that never exits on its own has to be ended before the retry.");
        AssertEx.True(script.Contains("RESULT=OK", StringComparison.Ordinal) &&
            script.Contains("RESULT=FAILED", StringComparison.Ordinal),
            "The script must report its outcome, so a failed update is not silent.");
        return Task.CompletedTask;
    }

    public static Task WriteProbeUsesTheApplicationFolder()
    {
        AssertEx.True(UpdateService.CanWriteToInstallFolder(),
            $"The test host folder must be writable: {UpdateService.ExecutableDirectory}");
        AssertEx.Equal(0, Directory.GetFiles(UpdateService.ExecutableDirectory, ".beatzonkeys-write-*").Length,
            "The writability probe must not leave files behind.");
        return Task.CompletedTask;
    }

    private static void WritePaddedCopy(string source, string destination, int size)
    {
        var bytes = File.ReadAllBytes(source);
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.SetLength(Math.Max(size, bytes.Length));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch
        {
            // Cleanup is best effort.
        }
    }
}