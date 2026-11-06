using System.Reflection;
using System.Reflection.Emit;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Tests;

/// <summary>
/// The update download must survive a per-user folder it cannot write to, which is what happens when
/// the app runs under a restricted token or the profile folder lost its permissions.
/// </summary>
internal static class UpdateStorageTests
{
    private delegate ref string StaticStringRef();

    public static Task PrimaryDirectoryIsUsedWhenWritable()
    {
        using var directory = new TemporaryDirectory();
        using var scope = new RedirectedPaths(directory.Path);
        var resolved = AppPaths.ResolveUpdateDirectory()
            ?? throw new TestFailureException("A writable per-user folder must resolve.");
        AssertEx.False(resolved.IsFallback, "A writable per-user folder must be preferred.");
        AssertEx.Equal(AppPaths.UpdateDirectory, resolved.Path);
        AssertEx.Null(resolved.PrimaryFailure);
        AssertEx.True(Directory.Exists(AppPaths.UpdateDirectory), "Resolving must create the folder it returns.");
        AssertEx.Equal(0, Directory.GetFiles(AppPaths.UpdateDirectory, ".write-probe-*").Length,
            "The writability probe must not leave files behind.");
        return Task.CompletedTask;
    }

    public static Task BlockedPrimaryFallsBackToTemp()
    {
        using var directory = new TemporaryDirectory();
        using var scope = new RedirectedPaths(directory.Path);
        // A file where the folder belongs makes every attempt to create it fail, without ACL surgery.
        Directory.CreateDirectory(AppPaths.RootDirectory);
        File.WriteAllText(AppPaths.UpdateDirectory, "not a directory");

        var resolved = AppPaths.ResolveUpdateDirectory()
            ?? throw new TestFailureException("A blocked per-user folder must fall back, not fail.");
        AssertEx.True(resolved.IsFallback, "A blocked per-user folder must fall back to the temp folder.");
        AssertEx.Equal(AppPaths.FallbackUpdateDirectory, resolved.Path);
        AssertEx.True(resolved.PrimaryFailure?.Contains(AppPaths.UpdateDirectory, StringComparison.OrdinalIgnoreCase) == true,
            $"The failure must name the blocked folder, got '{resolved.PrimaryFailure}'.");
        AssertEx.True(Directory.Exists(AppPaths.FallbackUpdateDirectory), "The fallback folder must exist.");
        return Task.CompletedTask;
    }

    public static Task NoWritableFolderReportsAPermissionProblem()
    {
        using var directory = new TemporaryDirectory();
        using var scope = new RedirectedPaths(directory.Path);
        Directory.CreateDirectory(AppPaths.RootDirectory);
        File.WriteAllText(AppPaths.UpdateDirectory, "not a directory");
        Directory.CreateDirectory(AppPaths.FallbackUpdateDirectory);
        File.WriteAllText(Path.Combine(AppPaths.FallbackUpdateDirectory, "blocked"), "not a directory");
        // Replace the fallback folder itself with a file so nothing is writable.
        Directory.Delete(AppPaths.FallbackUpdateDirectory, recursive: true);
        File.WriteAllText(AppPaths.FallbackUpdateDirectory, "not a directory");

        AssertEx.Null(AppPaths.ResolveUpdateDirectory(),
            "When no folder is writable the resolver must report failure instead of throwing.");
        return Task.CompletedTask;
    }

    public static Task StagedPathsAreRecognisedUnderBothRoots()
    {
        using var directory = new TemporaryDirectory();
        using var scope = new RedirectedPaths(directory.Path);
        var primary = Path.Combine(AppPaths.UpdateDirectory, "v1.2.2", "payload", "BeatzOnKeys.exe");
        var fallback = Path.Combine(AppPaths.FallbackUpdateDirectory, "v1.2.2", "payload", "BeatzOnKeys.exe");
        var elsewhere = Path.Combine(directory.Path, "elsewhere", "BeatzOnKeys.exe");
        AssertEx.True(AppPaths.IsInsideUpdateDirectory(primary), "A staged payload in the per-user folder is valid.");
        AssertEx.True(AppPaths.IsInsideUpdateDirectory(fallback), "A staged payload in the temp fallback is valid.");
        AssertEx.False(AppPaths.IsInsideUpdateDirectory(elsewhere), "A payload outside both roots must be rejected.");
        return Task.CompletedTask;
    }

    /// <summary>Points every app-data path at a throwaway folder for the duration of a test.</summary>
    private sealed class RedirectedPaths : IDisposable
    {
        private readonly (string Name, string Original)[] _originals;
        private readonly string _root;

        public RedirectedPaths(string root)
        {
            _root = root;
            _originals =
            [
                ("RootDirectory", AppPaths.RootDirectory),
                ("SettingsFile", AppPaths.SettingsFile),
                ("LogDirectory", AppPaths.LogDirectory),
                ("UpdateDirectory", AppPaths.UpdateDirectory),
                ("FallbackUpdateDirectory", AppPaths.FallbackUpdateDirectory)
            ];
            Set("RootDirectory", Path.Combine(root, "root"));
            Set("SettingsFile", Path.Combine(root, "root", "settings.json"));
            Set("LogDirectory", Path.Combine(root, "root", "logs"));
            Set("UpdateDirectory", Path.Combine(root, "root", "updates"));
            Set("FallbackUpdateDirectory", Path.Combine(root, "temp", "updates"));
        }

        public void Dispose()
        {
            foreach (var (name, original) in _originals) { Set(name, original); }
        }

        private static void Set(string property, string value)
        {
            var field = typeof(AppPaths).GetField($"<{property}>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(AppPaths).FullName, property);
            _ = typeof(AppPaths).GetProperty(property)!.GetValue(null); // Initialize before taking the reference.
            var method = new DynamicMethod("TestsPath_" + property, typeof(string).MakeByRefType(), Type.EmptyTypes,
                typeof(AppPaths).Module, skipVisibility: true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldsflda, field);
            il.Emit(OpCodes.Ret);
            var accessor = (StaticStringRef)method.CreateDelegate(typeof(StaticStringRef));
            accessor() = value;
        }
    }
}