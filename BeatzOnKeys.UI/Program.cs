namespace BeatzOnKeys.UI;

/// <summary>The WPF entry point; delegates the headless watcher mode before WPF starts.</summary>
public static class Program
{
    [System.STAThread]
    public static int Main(string[] arguments)
    {
        if (TryRunWatcher(arguments, out var exitCode)) { return exitCode; }
        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }

    private static bool TryRunWatcher(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        var wantsWatcher = arguments.Any(argument =>
            string.Equals(argument, BeatzOnKeys.Core.Input.WatcherProtocol.Switch, StringComparison.OrdinalIgnoreCase));
        if (!wantsWatcher) { return false; }

        var pipeName = BeatzOnKeys.Core.Input.WatcherProtocol.PipeName;
        int? parentProcessId = null;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith(BeatzOnKeys.Core.Input.WatcherProtocol.ParentProcessVariable + "=", StringComparison.OrdinalIgnoreCase))
            {
                parentProcessId = int.TryParse(
                    argument[(BeatzOnKeys.Core.Input.WatcherProtocol.ParentProcessVariable.Length + 1)..],
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            }
        }

        try
        {
            exitCode = BeatzOnKeys.Core.Input.KeyboardWatcherHost.Run(pipeName, parentProcessId);
        }
        catch
        {
            exitCode = 3;
        }
        return true;
    }
}
