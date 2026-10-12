using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using NimPad.App.Services;
using NimPad.Core.Storage;

namespace NimPad.App.Shell;

/// <summary>The main window's normal (restored) bounds in screen pixels and whether it was maximized.</summary>
/// <param name="Left">Left edge (workspace coordinates, physical pixels).</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
/// <param name="IsMaximized">Whether the window was maximized.</param>
public sealed record WindowBounds(int Left, int Top, int Right, int Bottom, bool IsMaximized)
{
    /// <summary>The width in pixels.</summary>
    [JsonIgnore]
    public int Width => Right - Left;

    /// <summary>The height in pixels.</summary>
    [JsonIgnore]
    public int Height => Bottom - Top;

    /// <summary>True for a rectangle with a usable size.</summary>
    [JsonIgnore]
    public bool IsValid => Width >= 100 && Height >= 100;
}

/// <summary>
/// Remembers the main window's bounds between runs in <c>window-placement.json</c> next to the database (the Core
/// <c>UiState</c> has no window bounds). Bounds go through <c>GetWindowPlacement</c> / <c>SetWindowPlacement</c>, so
/// they survive DPI changes and Aero Snap; bounds that no longer touch any monitor are ignored (the window opens at its
/// default size). Reading and writing never throw.
/// </summary>
public sealed class WindowPlacementStore
{
    /// <summary>The file name in the profile folder.</summary>
    public const string FileName = "window-placement.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();

    /// <summary>Stores the placement in <paramref name="path"/>.</summary>
    public WindowPlacementStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
    }

    /// <summary>The JSON file.</summary>
    public string FilePath { get; }

    /// <summary>The store of a profile.</summary>
    public static WindowPlacementStore ForProfile(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new WindowPlacementStore(Path.Combine(paths.ProfileDir, FileName));
    }

    /// <summary>The saved bounds, or <see langword="null"/> when none (or unreadable).</summary>
    public WindowBounds? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var bounds = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(FilePath), JsonOptions);
            return bounds is { IsValid: true } ? bounds : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            FileLog.Warn($"The window placement could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes <paramref name="bounds"/> (replacing the file atomically).</summary>
    /// <returns><see langword="true"/> when it was written.</returns>
    public bool Save(WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (!bounds.IsValid)
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(bounds, JsonOptions));
                File.Move(temp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Warn($"The window placement could not be saved: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>The window's current normal bounds and maximized state, or <see langword="null"/> without a handle.</summary>
    public static WindowBounds? Capture(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var placement = new NativeMethods.WindowPlacement { Length = Marshal.SizeOf<NativeMethods.WindowPlacement>() };
        if (!NativeMethods.GetWindowPlacement(hwnd, ref placement))
        {
            return null;
        }

        var rect = placement.NormalPosition;
        var maximized = placement.ShowCmd == NativeMethods.SwShowMaximized
            || (placement.ShowCmd == NativeMethods.SwShowMinimized && (placement.Flags & 0x2) != 0);
        return new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom, maximized);
    }

    /// <summary>
    /// Applies <paramref name="bounds"/> to a window that has a handle but is not shown yet (call it from
    /// <c>SourceInitialized</c>): the normal bounds are set while the window stays hidden, and a maximized window gets
    /// <see cref="WindowState.Maximized"/>. Bounds that touch no monitor are ignored.
    /// </summary>
    /// <returns><see langword="true"/> when the bounds were applied.</returns>
    public static bool Apply(Window window, WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(bounds);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !bounds.IsValid)
        {
            return false;
        }

        var rect = new NativeMethods.Rect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
        if (NativeMethods.MonitorFromRect(ref rect, NativeMethods.MonitorDefaultToNull) == IntPtr.Zero)
        {
            return false;
        }

        var placement = new NativeMethods.WindowPlacement
        {
            Length = Marshal.SizeOf<NativeMethods.WindowPlacement>(),
            ShowCmd = NativeMethods.SwHide,
            NormalPosition = rect,
        };
        if (!NativeMethods.SetWindowPlacement(hwnd, ref placement))
        {
            return false;
        }

        if (bounds.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        return true;
    }
}
