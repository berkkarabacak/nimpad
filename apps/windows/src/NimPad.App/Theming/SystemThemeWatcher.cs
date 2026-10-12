using System.Runtime.Versioning;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NimPad.App.Theming;

/// <summary>
/// Reports whether Windows asks apps to use dark mode (HKCU\...\Themes\Personalize\AppsUseLightTheme = 0) and
/// raises <see cref="Changed"/> when the user flips it. Events from <see cref="SystemEvents"/> arrive on a system
/// thread and are marshalled to the dispatcher this watcher was created on.
/// Its members match NimPad.Presentation's <c>ISystemThemeProvider</c> (IsDarkMode + Changed), so the
/// composition root can expose it as that interface without an adapter.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemThemeWatcher : IDisposable
{
    public const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    private readonly Func<bool> _readIsDark;
    private readonly Dispatcher _dispatcher;
    private readonly bool _listening;
    private bool _disposed;

    /// <summary>Reads the registry and listens to <see cref="SystemEvents.UserPreferenceChanged"/>.</summary>
    public SystemThemeWatcher()
        : this(ReadSystemIsDark, listen: true)
    {
    }

    /// <summary>For tests: a custom reader and no system subscription (call <see cref="Refresh"/>).</summary>
    internal SystemThemeWatcher(Func<bool> readIsDark, bool listen)
    {
        _readIsDark = readIsDark;
        _dispatcher = Dispatcher.CurrentDispatcher;
        IsDarkMode = SafeRead();
        if (listen)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
    }

    /// <summary>True when Windows' app mode is Dark.</summary>
    public bool IsDarkMode { get; private set; }

    /// <summary>Raised on the owning dispatcher after <see cref="IsDarkMode"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Reads AppsUseLightTheme: 0 means dark; 1, a missing key or any failure means light.
    /// </summary>
    public static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            return key?.GetValue(AppsUseLightThemeValueName) is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                       or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Re-reads the setting and raises <see cref="Changed"/> if it differs. Call on the owning thread.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var isDark = SafeRead();
        if (isDark == IsDarkMode)
        {
            return;
        }

        IsDarkMode = isDark;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_listening)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        Changed = null;
    }

    private bool SafeRead()
    {
        try
        {
            return _readIsDark();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // The app-mode switch arrives as WM_SETTINGCHANGE("ImmersiveColorSet"), surfaced as General.
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Refresh));
    }
}
