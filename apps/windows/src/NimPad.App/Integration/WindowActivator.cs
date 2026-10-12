using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace NimPad.App.Integration;

/// <summary>Brings a window to the front from the background (single-instance forwarding, the global shortcut).</summary>
[SupportedOSPlatform("windows")]
public static class WindowActivator
{
    /// <summary>The editor's AutomationId (UI automation contract); focused after activation.</summary>
    public const string EditorAutomationId = "Editor";

    /// <summary>
    /// Shows the window if hidden, restores it if minimized (back to maximized when it was) and makes it the
    /// foreground window. Windows only lets a process take the foreground in some cases (it received the last input,
    /// the global shortcut, or <c>AllowSetForegroundWindow</c> from the launch that forwarded to it); when
    /// <c>Activate</c> is refused, the <c>AttachThreadInput</c> fallback joins the foreground thread's input state for
    /// the call.
    /// </summary>
    /// <returns><see langword="true"/> when the window ended up in the foreground.</returns>
    public static bool BringToFront(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsVisible)
        {
            window.Show();
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return window.Activate();
        }

        if (window.WindowState == WindowState.Minimized || NativeMethods.IsIconic(handle))
        {
            NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
        }

        window.Activate();
        if (NativeMethods.GetForegroundWindow() == handle)
        {
            return true;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(handle);
            NativeMethods.SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        window.Activate();
        return NativeMethods.GetForegroundWindow() == handle;
    }

    /// <summary>Moves keyboard focus to the element with AutomationId <see cref="EditorAutomationId"/>, if there is one.</summary>
    /// <returns><see langword="true"/> when the editor took focus.</returns>
    public static bool FocusEditor(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (FindByAutomationId(window, EditorAutomationId) is not UIElement editor || !editor.IsVisible)
        {
            return false;
        }

        editor.Focus();
        Keyboard.Focus(editor);
        return editor.IsKeyboardFocusWithin;
    }

    /// <summary>The first element under <paramref name="root"/> (visual tree, depth first) with the AutomationId.</summary>
    public static DependencyObject? FindByAutomationId(DependencyObject root, string automationId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (AutomationProperties.GetAutomationId(current) == automationId)
            {
                return current;
            }

            if (current is not Visual and not System.Windows.Media.Media3D.Visual3D)
            {
                continue;
            }

            for (var i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--)
            {
                pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        return null;
    }
}
