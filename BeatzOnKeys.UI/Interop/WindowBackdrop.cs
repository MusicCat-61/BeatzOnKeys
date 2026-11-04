using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BeatzOnKeys.UI.Interop;

/// <summary>Windows 11 Mica integration adapted from MusicCatRGB.</summary>
internal static class WindowBackdrop
{
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmncrpDisabled = 1;
    private const int DwmwcpRound = 2;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    private const int DwmsbtMainWindow = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    public static bool TryApplyMica(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var source = handle == nint.Zero ? null : HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null)
        {
            return false;
        }

        SetWindowChromeAttributes(handle, dark);
        if (Environment.OSVersion.Version.Build < 22621 || SystemParameters.HighContrast)
        {
            return false;
        }

        var previousColor = source.CompositionTarget.BackgroundColor;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        var backdrop = DwmsbtMainWindow;
        if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
        {
            source.CompositionTarget.BackgroundColor = previousColor;
            return false;
        }

        window.Background = System.Windows.Media.Brushes.Transparent;
        return true;
    }

    public static void SetDarkMode(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != nint.Zero)
        {
            SetWindowChromeAttributes(handle, dark);
        }
    }

    private static void SetWindowChromeAttributes(nint handle, bool dark)
    {
        var framePolicy = DwmncrpDisabled;
        DwmSetWindowAttribute(handle, DwmwaNcRenderingPolicy, ref framePolicy, sizeof(int));
        var darkFlag = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkFlag, sizeof(int));
        if (Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        var corners = DwmwcpRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
        var noBorder = DwmwaColorNone;
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref noBorder, sizeof(int));
    }
}
