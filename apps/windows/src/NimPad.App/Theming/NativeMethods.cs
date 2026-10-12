using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NimPad.App.Theming;

/// <summary>Win32 entry points used by the theming helpers.</summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1 and later, Windows 11).</summary>
    internal const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>The pre-20H1 value of the same attribute (Windows 10 1809-1909).</summary>
    internal const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
