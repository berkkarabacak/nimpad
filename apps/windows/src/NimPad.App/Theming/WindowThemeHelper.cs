using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;

namespace NimPad.App.Theming;

/// <summary>Native window-frame theming.</summary>
[SupportedOSPlatform("windows")]
public static class WindowThemeHelper
{
    /// <summary>
    /// Asks DWM to draw the window's standard frame (title bar, borders) dark or light via
    /// DWMWA_USE_IMMERSIVE_DARK_MODE. Use it for standard-chrome windows such as Settings; it also tints the
    /// Windows 11 border of custom-chrome windows. If the window has no handle yet, the call is deferred to
    /// <see cref="Window.SourceInitialized"/>. A safe no-op where the attribute or dwmapi is unavailable.
    /// </summary>
    /// <returns>True when DWM accepted the attribute (or it was deferred).</returns>
    public static bool ApplyImmersiveDarkMode(Window window, bool dark)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            void OnSourceInitialized(object? sender, EventArgs e)
            {
                window.SourceInitialized -= OnSourceInitialized;
                ApplyImmersiveDarkMode(window, dark);
            }

            window.SourceInitialized += OnSourceInitialized;
            return true;
        }

        return ApplyImmersiveDarkMode(handle, dark);
    }

    /// <summary>Same as <see cref="ApplyImmersiveDarkMode(Window, bool)"/> for a raw HWND.</summary>
    public static bool ApplyImmersiveDarkMode(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return false;
        }

        try
        {
            var value = dark ? 1 : 0;
            if (NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) >= 0)
            {
                return true;
            }

            return NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int)) >= 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
