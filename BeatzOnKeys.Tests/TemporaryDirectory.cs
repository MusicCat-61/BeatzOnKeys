namespace BeatzOnKeys.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    private static readonly string TestRoot = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BeatzOnKeys.Tests"));
    private bool _disposed;

    public TemporaryDirectory()
    {
        Directory.CreateDirectory(TestRoot);
        Path = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var safeRoot = TestRoot.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var target = System.IO.Path.GetFullPath(Path);
        if (!target.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete an unsafe test directory: {target}");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        _disposed = true;
    }
}
