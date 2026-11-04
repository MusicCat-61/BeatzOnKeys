using System.Text;

namespace BeatzOnKeys.Core.Services;

/// <summary>Small dependency-free daily file logger suitable for a portable desktop app.</summary>
public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static bool _initialized;

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
        try
        {
            Initialize();
            var now = DateTimeOffset.Now;
            var path = Path.Combine(AppPaths.LogDirectory, $"beatzonkeys-{now:yyyyMMdd}.log");
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{category}] {message}{Environment.NewLine}";

            lock (Gate)
            {
                File.AppendAllText(path, line, Utf8);
            }
        }
        catch
        {
            // Logging must never take down audio playback or the keyboard hook.
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
