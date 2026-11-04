using System.Runtime.InteropServices;

namespace BeatzOnKeys.Core.Input;

/// <summary>
/// Minimal WH_KEYBOARD_LL hook adapted from MusicCatRGB. It reports only that a
/// non-modifier key went down; it never records text, window titles, or key codes.
/// </summary>
public sealed class KeyboardHook : IKeyboardHook
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const uint LlkfInjected = 0x10;

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    private readonly LowLevelKeyboardProc _callback;
    private nint _hook;
    private bool _disposed;

    public KeyboardHook() => _callback = HandleKey;

    public event Action? KeyDown;

    public bool IsInstalled => _hook != nint.Zero;

    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != nint.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
        if (_hook == nint.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx failed with Windows error {Marshal.GetLastWin32Error()}.");
        }
    }

    public void Uninstall()
    {
        if (_hook == nint.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hook);
        _hook = nint.Zero;
    }

    private nint HandleKey(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((data.Flags & LlkfInjected) == 0 && !IsBareModifier(data.VirtualKey))
            {
                try
                {
                    KeyDown?.Invoke();
                }
                catch
                {
                    // A hook must always return promptly; consumers handle their own errors.
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsBareModifier(uint virtualKey) => virtualKey switch
    {
        0x10 or 0x11 or 0x12 or // Shift, Control, Alt
        0x14 or                 // Caps Lock
        0x5B or 0x5C or        // Windows keys
        >= 0xA0 and <= 0xA5 => true, // left/right Shift, Control, Alt
        _ => false
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Uninstall();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        nint moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hookHandle, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
