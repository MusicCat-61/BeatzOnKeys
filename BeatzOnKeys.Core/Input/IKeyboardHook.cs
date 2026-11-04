namespace BeatzOnKeys.Core.Input;

/// <summary>A signal-only global keyboard hook; no typed text is retained.</summary>
public interface IKeyboardHook : IDisposable
{
    event Action? KeyDown;
    bool IsInstalled { get; }
    void Install();
    void Uninstall();
}
