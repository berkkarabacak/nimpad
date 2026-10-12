using System.Runtime.InteropServices;

namespace NimPad.App.Shell;

/// <summary>Win32 messages, structures and entry points used by the custom window chrome and placement.</summary>
internal static class NativeMethods
{
    internal const int WmMouseMove = 0x0200;
    internal const int WmGetMinMaxInfo = 0x0024;
    internal const int WmNcHitTest = 0x0084;
    internal const int WmNcMouseMove = 0x00A0;
    internal const int WmNcLButtonDown = 0x00A1;
    internal const int WmNcLButtonUp = 0x00A2;
    internal const int WmNcLButtonDblClk = 0x00A3;
    internal const int WmNcRButtonUp = 0x00A5;
    internal const int WmNcMouseLeave = 0x02A2;

    internal const int HtCaption = 2;
    internal const int HtMaxButton = 9;

    internal const uint TmeLeave = 0x00000002;
    internal const uint TmeNonClient = 0x00000010;

    internal const uint MonitorDefaultToNull = 0;
    internal const uint MonitorDefaultToNearest = 2;

    internal const uint AbmGetAutoHideBarEx = 0x0000000b;
    internal const uint AbeLeft = 0;
    internal const uint AbeTop = 1;
    internal const uint AbeRight = 2;
    internal const uint AbeBottom = 3;

    internal const int SwHide = 0;
    internal const int SwShowMinimized = 2;
    internal const int SwShowMaximized = 3;

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shell32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TrackMouseEvent(ref TrackMouseEventInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    /// <summary>Signed x of a mouse-message LPARAM (screen coordinates can be negative on multi-monitor setups).</summary>
    internal static int LowWordSigned(IntPtr value) => unchecked((short)(long)value);

    /// <summary>Signed y of a mouse-message LPARAM.</summary>
    internal static int HighWordSigned(IntPtr value) => unchecked((short)((long)value >> 16));

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AppBarData
    {
        public int Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Rect;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrackMouseEventInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }
}
