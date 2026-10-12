using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using NimPad.App.Controls;

namespace NimPad.App.Shell;

/// <summary>
/// The window-procedure part of the custom chrome (design page 24):
/// <list type="bullet">
/// <item><b>Snap Layouts.</b> <c>WM_NCHITTEST</c> answers <c>HTMAXBUTTON</c> over the maximize button, so Windows 11
/// shows the Snap Layouts flyout. The button then gets non-client messages instead of WPF mouse events, so its hover
/// and pressed looks come from <c>WM_NCMOUSEMOVE</c> / <c>WM_NCMOUSELEAVE</c> / <c>WM_NCLBUTTONDOWN</c>
/// (<see cref="Ui.ForceHover"/>, <see cref="Ui.ForcePressed"/>) and the click is performed on <c>WM_NCLBUTTONUP</c>.</item>
/// <item><b>Maximized size.</b> <c>WM_GETMINMAXINFO</c> fits a maximized window to the monitor's work area (no 8 px
/// overhang) and leaves 1 px for an auto-hide taskbar so it can still be revealed; it also applies the minimum size.</item>
/// <item><b>Tab-bar menu.</b> A right-click on the caption over the empty tab-bar space opens the tab-bar menu; elsewhere
/// on the caption WindowChrome shows the system menu.</item>
/// </list>
/// </summary>
internal sealed class WindowChromeHook
{
    private readonly Window _window;
    private readonly Func<Button?> _maximizeButton;
    private readonly Func<FrameworkElement?> _tabBarArea;
    private readonly Func<bool> _isInteractive;
    private readonly Action _toggleMaximize;
    private readonly Action _showTabBarMenu;
    private HwndSource? _source;
    private bool _maximizePressed;

    /// <summary>Creates the hook; call <see cref="Attach"/> once the window has a handle.</summary>
    /// <param name="window">The window.</param>
    /// <param name="maximizeButton">The maximize button.</param>
    /// <param name="tabBarArea">The empty tab-bar space (right-clicks there open <paramref name="showTabBarMenu"/>).</param>
    /// <param name="isInteractive">False while a modal dialog covers the window.</param>
    /// <param name="toggleMaximize">Maximizes or restores the window.</param>
    /// <param name="showTabBarMenu">Opens the tab-bar menu at the pointer.</param>
    public WindowChromeHook(
        Window window,
        Func<Button?> maximizeButton,
        Func<FrameworkElement?> tabBarArea,
        Func<bool> isInteractive,
        Action toggleMaximize,
        Action showTabBarMenu)
    {
        _window = window;
        _maximizeButton = maximizeButton;
        _tabBarArea = tabBarArea;
        _isInteractive = isInteractive;
        _toggleMaximize = toggleMaximize;
        _showTabBarMenu = showTabBarMenu;
    }

    /// <summary>Adds the hook to the window's <see cref="HwndSource"/> (after WindowChrome's own, so it runs first).</summary>
    public void Attach()
    {
        _source = PresentationSource.FromVisual(_window) as HwndSource;
        _source?.AddHook(WndProc);
    }

    /// <summary>Removes the hook.</summary>
    public void Detach()
    {
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    /// <summary>True when the screen point (physical pixels) is over <paramref name="element"/>.</summary>
    /// <param name="element">The element.</param>
    /// <param name="x">Screen x in physical pixels.</param>
    /// <param name="y">Screen y in physical pixels.</param>
    /// <param name="topExclusion">A band at the element's top (DIPs) that does not count (the window's resize edge).</param>
    internal static bool ContainsScreenPoint(FrameworkElement? element, int x, int y, double topExclusion = 0)
    {
        if (element is not { IsVisible: true } || PresentationSource.FromVisual(element) is null)
        {
            return false;
        }

        try
        {
            var point = element.PointFromScreen(new Point(x, y));
            return point.X >= 0 && point.Y >= topExclusion && point.X < element.ActualWidth && point.Y < element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            // The element left the tree.
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case NativeMethods.WmNcHitTest:
                if (_isInteractive() && _window.WindowState != WindowState.Minimized
                    && _maximizeButton() is { IsEnabled: true } button
                    && ContainsScreenPoint(button, NativeMethods.LowWordSigned(lParam), NativeMethods.HighWordSigned(lParam), ResizeEdge()))
                {
                    handled = true;
                    return NativeMethods.HtMaxButton;
                }

                break;
            case NativeMethods.WmNcMouseMove:
                if (wParam == NativeMethods.HtMaxButton)
                {
                    SetMaximizeHover(true);
                    TrackNonClientLeave(hwnd);
                }
                else
                {
                    ClearMaximizeState();
                }

                break;
            case NativeMethods.WmNcMouseLeave:
            case NativeMethods.WmMouseMove:
                ClearMaximizeState();
                break;
            case NativeMethods.WmNcLButtonDown:
            case NativeMethods.WmNcLButtonDblClk:
                if (wParam == NativeMethods.HtMaxButton)
                {
                    _maximizePressed = true;
                    SetMaximizePressed(true);
                    handled = true;
                    return IntPtr.Zero;
                }

                break;
            case NativeMethods.WmNcLButtonUp:
                if (wParam == NativeMethods.HtMaxButton)
                {
                    var click = _maximizePressed;
                    _maximizePressed = false;
                    SetMaximizePressed(false);
                    handled = true;
                    if (click)
                    {
                        _toggleMaximize();
                    }

                    return IntPtr.Zero;
                }

                _maximizePressed = false;
                SetMaximizePressed(false);
                break;
            case NativeMethods.WmNcRButtonUp:
                if (wParam == NativeMethods.HtCaption && _isInteractive()
                    && ContainsScreenPoint(_tabBarArea(), NativeMethods.LowWordSigned(lParam), NativeMethods.HighWordSigned(lParam)))
                {
                    handled = true;
                    _showTabBarMenu();
                    return IntPtr.Zero;
                }

                break;
            case NativeMethods.WmGetMinMaxInfo:
                handled = ApplyMinMaxInfo(hwnd, lParam);
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // The top resize edge stays a resize edge over the maximize button, except when maximized (no resizing then).
    private double ResizeEdge() =>
        _window.WindowState == WindowState.Maximized ? 0 : SystemParameters.WindowResizeBorderThickness.Top;

    private void SetMaximizeHover(bool hover)
    {
        if (_maximizeButton() is { } button && Ui.GetForceHover(button) != hover)
        {
            Ui.SetForceHover(button, hover);
        }
    }

    private void SetMaximizePressed(bool pressed)
    {
        if (_maximizeButton() is { } button && Ui.GetForcePressed(button) != pressed)
        {
            Ui.SetForcePressed(button, pressed);
        }
    }

    private void ClearMaximizeState()
    {
        SetMaximizeHover(false);
        if (!_maximizePressed)
        {
            SetMaximizePressed(false);
        }
    }

    private static void TrackNonClientLeave(IntPtr hwnd)
    {
        var info = new NativeMethods.TrackMouseEventInfo
        {
            Size = Marshal.SizeOf<NativeMethods.TrackMouseEventInfo>(),
            Flags = NativeMethods.TmeLeave | NativeMethods.TmeNonClient,
            Window = hwnd,
        };
        NativeMethods.TrackMouseEvent(ref info);
    }

    private bool ApplyMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var work = info.Work;
        var bounds = info.Monitor;
        LeaveRoomForAutoHideTaskbar(ref work, bounds);
        var minMax = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
        minMax.MaxPosition = new NativeMethods.Point { X = work.Left - bounds.Left, Y = work.Top - bounds.Top };
        minMax.MaxSize = new NativeMethods.Point { X = work.Width, Y = work.Height };
        var dpi = VisualTreeHelper.GetDpi(_window);
        minMax.MinTrackSize = new NativeMethods.Point
        {
            X = (int)Math.Ceiling(_window.MinWidth * dpi.DpiScaleX),
            Y = (int)Math.Ceiling(_window.MinHeight * dpi.DpiScaleY),
        };
        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: false);
        return true;
    }

    // An auto-hide taskbar shows when the pointer touches its screen edge; a window covering the whole edge would
    // hide it for good, so the maximized window stops 1 px short on that edge.
    private static void LeaveRoomForAutoHideTaskbar(ref NativeMethods.Rect work, NativeMethods.Rect monitor)
    {
        if (work.Left != monitor.Left || work.Top != monitor.Top || work.Right != monitor.Right || work.Bottom != monitor.Bottom)
        {
            // A visible taskbar already takes its edge.
            return;
        }

        foreach (var edge in new[] { NativeMethods.AbeBottom, NativeMethods.AbeTop, NativeMethods.AbeLeft, NativeMethods.AbeRight })
        {
            var data = new NativeMethods.AppBarData
            {
                Size = Marshal.SizeOf<NativeMethods.AppBarData>(),
                Edge = edge,
                Rect = monitor,
            };
            if (NativeMethods.SHAppBarMessage(NativeMethods.AbmGetAutoHideBarEx, ref data) == IntPtr.Zero)
            {
                continue;
            }

            switch (edge)
            {
                case NativeMethods.AbeBottom:
                    work.Bottom -= 1;
                    break;
                case NativeMethods.AbeTop:
                    work.Top += 1;
                    break;
                case NativeMethods.AbeLeft:
                    work.Left += 1;
                    break;
                default:
                    work.Right -= 1;
                    break;
            }

            return;
        }
    }
}
