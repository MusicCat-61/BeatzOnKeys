using System.Text;

namespace BeatzOnKeys.Core.Services;

/// <summary>Small dependency-free daily file logger suitable for a portable desktop app.</summary>
public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static bool _initialized;
    private static string? _lastFailure;

    /// <summary>Diagnostics must never take the app down, but failures must stay visible.</summary>
    public static string? LastFailure { get { lock (Gate) { return _lastFailure; } } }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            AppPaths.EnsureCreated();
            _initialized = true;
            CleanupOldLogs();
        }
    }

    public static void Info(string category, string message) => Write("INF", category, message);

    public static void Warning(string category, string message) => Write("WRN", category, message);

    public static void Error(string category, string message, Exception? exception = null)
    {
        var details = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERR", category, details);
    }

    private static void Write(string level, string category, string message)
    {
        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{category}] {message}{Environment.NewLine}";
        string primaryPath;
        try
        {
            Initialize();
            primaryPath = Path.Combine(AppPaths.LogDirectory, $"beatzonkeys-{now:yyyyMMdd}.log");
            lock (Gate)
            {
                File.AppendAllText(primaryPath, line, Utf8);
            }
            return;
        }
        catch (Exception exception)
        {
            // Keep the reason so the app can show it instead of silently losing diagnostics.
            lock (Gate) { _lastFailure = exception.ToString(); }
        }

        // Fallback keeps the reason visible when the per-user folder is not writable.
        try
        {
            var fallbackDirectory = Path.Combine(Path.GetTempPath(), AppPaths.ProductName);
            Directory.CreateDirectory(fallbackDirectory);
            var fallbackPath = Path.Combine(fallbackDirectory, $"beatzonkeys-fallback-{now:yyyyMMdd}.log");
            var reason = _lastFailure ?? "unknown logging failure";
            File.AppendAllText(fallbackPath,
                $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [WRN] [log] Primary log unavailable: {reason}{Environment.NewLine}{line}",
                Utf8);
        }
        catch
        {
            // Nothing else can be done without risking the caller.
        }

        // Last resort: next to the executable, where a portable copy is usually writable.
        try
        {
            var besideExe = Path.Combine(AppContext.BaseDirectory, $"beatzonkeys-crash-{now:yyyyMMdd}.log");
            File.AppendAllText(besideExe,
                $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [WRN] [log] Primary log unavailable: {_lastFailure}{Environment.NewLine}{line}",
                Utf8);
        }
        catch
        {
            // Diagnostics are best effort; they must never take the app down.
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var threshold = DateTime.UtcNow.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "beatzonkeys-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < threshold)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Retention cleanup is best effort.
        }
    }
}
