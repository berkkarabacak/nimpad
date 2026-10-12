using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Interop;
using System.Windows.Threading;
using NimPad.Presentation.Integration;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Settings;

namespace NimPad.App.Integration;

/// <summary>
/// The system-wide shortcut (default Ctrl+Alt+N) that brings NimPad forward: <c>RegisterHotKey</c> on a message-only
/// window owned by this service, whose hook raises <see cref="Pressed"/> on <c>WM_HOTKEY</c>.
/// </summary>
/// <remarks>
/// <para>The hotkey lives on its own message-only window (not the main window's HWND) because the shell registers the
/// saved shortcut during <c>ShellViewModel.InitializeAsync</c>, which may run before the main window has a handle, and
/// so the registration survives the main window recreating its handle. The window is created on first use on the
/// thread that created the service (the UI thread); calls from other threads are marshaled to it.</para>
/// <para>A new registration replaces the previous one only once it succeeded, so a conflict keeps the old shortcut
/// working. With <c>--no-hotkey</c> (<see cref="RegistersWithWindows"/> false) the gesture is remembered and reported
/// as registered, but nothing is registered with Windows, so test runs never take the user's shortcut.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    // Application hotkey IDs must be in 0x0000-0xBFFF.
    private const int FirstId = 0x4E50;
    private const int LastId = 0xBFFF;

    private readonly Dispatcher _dispatcher;
    private HwndSource? _window;
    private int _registeredId;
    private int _nextId = FirstId;
    private bool _disposed;

    /// <summary>Creates the service on the UI thread.</summary>
    /// <param name="registerWithWindows">False for <c>--no-hotkey</c>: remember gestures without registering them.</param>
    public GlobalHotkeyService(bool registerWithWindows = true)
    {
        RegistersWithWindows = registerWithWindows;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Whether gestures are registered with Windows (false with <c>--no-hotkey</c>).</summary>
    public bool RegistersWithWindows { get; }

    /// <summary>The gesture in effect (normalized, for example <c>Ctrl+Alt+N</c>), or <see langword="null"/>.</summary>
    public string? Gesture { get; private set; }

    /// <summary>Raised on the UI thread when the user presses the shortcut anywhere in Windows.</summary>
    public event EventHandler? Pressed;

    /// <summary>The message window's handle, once created (tests send it WM_HOTKEY).</summary>
    internal IntPtr WindowHandle => _window?.Handle ?? IntPtr.Zero;

    /// <summary>The ID of the current registration, or 0 (tests).</summary>
    internal int RegisteredId => _registeredId;

    /// <inheritdoc/>
    public bool TryRegister(string gesture, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gesture);
        if (!_dispatcher.CheckAccess())
        {
            var (registered, message) = _dispatcher.Invoke(() => (TryRegister(gesture, out var inner), inner));
            error = message;
            return registered;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HotkeyGesture.TryParse(gesture, out var parsed, out var parseError))
        {
            error = parseError;
            return false;
        }

        var text = parsed.ToString();
        if (!RegistersWithWindows)
        {
            Trace.TraceInformation($"--no-hotkey: {text} is not registered with Windows.");
            Gesture = text;
            error = null;
            return true;
        }

        if (_registeredId != 0 && text == Gesture)
        {
            error = null;
            return true;
        }

        var (modifiers, virtualKey) = HotkeyVirtualKeys.ToWin32(parsed);
        var handle = EnsureWindow();
        var id = _nextId;
        _nextId = _nextId >= LastId ? FirstId : _nextId + 1;
        if (!NativeMethods.RegisterHotKey(handle, id, (uint)(modifiers | HotkeyVirtualKeys.ModNoRepeat), (uint)virtualKey))
        {
            var code = Marshal.GetLastPInvokeError();
            Trace.TraceWarning($"RegisterHotKey({text}) failed with error {code}.");
            error = code == NativeMethods.ErrorHotkeyAlreadyRegistered
                ? $"Another app already uses {text}. Try a different shortcut."
                : $"{text} couldn't be registered. Try a different shortcut.";
            return false;
        }

        if (_registeredId != 0)
        {
            NativeMethods.UnregisterHotKey(handle, _registeredId);
        }

        _registeredId = id;
        Gesture = text;
        error = null;
        return true;
    }

    /// <inheritdoc/>
    public void Unregister()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Unregister);
            return;
        }

        if (_registeredId != 0 && _window is { } window)
        {
            NativeMethods.UnregisterHotKey(window.Handle, _registeredId);
        }

        _registeredId = 0;
        Gesture = null;
    }

    /// <summary>Unregisters the shortcut and destroys the message window.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Dispose);
            return;
        }

        Unregister();
        _disposed = true;
        if (_window is { } window)
        {
            window.RemoveHook(WndProc);
            window.Dispose();
            _window = null;
        }
    }

    private IntPtr EnsureWindow()
    {
        if (_window is null)
        {
            var parameters = new HwndSourceParameters("NimPad global shortcut")
            {
                ParentWindow = NativeMethods.HwndMessage,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            };
            _window = new HwndSource(parameters);
            _window.AddHook(WndProc);
        }

        return _window.Handle;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey && _registeredId != 0 && wParam.ToInt64() == _registeredId)
        {
            handled = true;
            try
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // A failing handler must not take the message loop down.
                Trace.TraceError($"Handling the global shortcut failed: {ex}");
            }
        }

        return IntPtr.Zero;
    }
}
