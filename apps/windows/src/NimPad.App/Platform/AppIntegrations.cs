using System.Windows;
using NimPad.App.Services;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Shell;

namespace NimPad.App.Platform;

/// <summary>
/// Single instance and activation: forwards a second launch's arguments to the running NimPad and brings the window
/// forward (global shortcut, forwarded launch). Implemented outside the shell; plugged in through
/// <see cref="AppIntegrations.Activation"/>.
/// </summary>
public interface IAppActivation
{
    /// <summary>
    /// Runs first, before any window or database work. Returns <see langword="false"/> when another NimPad already runs
    /// and took over <paramref name="args"/> (this process then exits at once). Not called with
    /// <c>--no-single-instance</c>.
    /// </summary>
    bool TryBecomePrimaryInstance(StartupArgs args);

    /// <summary>
    /// The main window and its view model exist (the data may still be loading). Hook activation here: show and
    /// activate <paramref name="window"/>, and pass forwarded files to <see cref="ShellViewModel.OpenFiles"/>.
    /// </summary>
    void Attach(Window window, ShellViewModel shell);

    /// <summary>The app is exiting: release the instance lock and listeners.</summary>
    void Detach();
}

/// <summary>Shows the Settings window (implemented with the Settings window; see <see cref="AppIntegrations.SettingsWindow"/>).</summary>
public interface ISettingsWindowLauncher
{
    /// <summary>Shows (or activates) the Settings window for <paramref name="shell"/>'s <see cref="ShellViewModel.Settings"/>.</summary>
    void Show(Window owner, ShellViewModel shell);
}

/// <summary>
/// The features that plug into the shell without editing it: single instance and activation, the global shortcut,
/// start with Windows and the Settings window. <c>App</c> fills an instance through its <c>ConfigureIntegrations</c>
/// partial method (implement it in a separate <c>partial class App</c> file); anything left unset falls back to the
/// shell's defaults: no single-instance handling, no global shortcut, start with Windows unavailable, and no Settings
/// window (the request is logged).
/// </summary>
public sealed class AppIntegrations
{
    /// <summary>Single instance and activation, or <see langword="null"/>.</summary>
    public IAppActivation? Activation { get; set; }

    /// <summary>Creates the global shortcut service (not called with <c>--no-hotkey</c>), or <see langword="null"/>.</summary>
    public Func<StartupArgs, IGlobalHotkeyService>? GlobalHotkey { get; set; }

    /// <summary>Creates the start-with-Windows service, or <see langword="null"/>.</summary>
    public Func<IAutostartService>? Autostart { get; set; }

    /// <summary>The Settings window, or <see langword="null"/>.</summary>
    public ISettingsWindowLauncher? SettingsWindow { get; set; }
}

/// <summary>
/// The global shortcut when it is turned off for this process (<c>--no-hotkey</c>, or no shortcut service is
/// configured): nothing is registered with Windows, and registering reports success because the shortcut was
/// deliberately not requested, not refused by another app. The intent is logged once.
/// </summary>
public sealed class DisabledGlobalHotkeyService : IGlobalHotkeyService
{
    private readonly string _reason;
    private bool _logged;

    /// <summary>Creates the service; <paramref name="reason"/> goes to the log ("--no-hotkey was passed").</summary>
    public DisabledGlobalHotkeyService(string reason)
    {
        _reason = reason;
    }

    /// <inheritdoc/>
    public bool TryRegister(string gesture, out string? error)
    {
        error = null;
        if (!_logged)
        {
            _logged = true;
            FileLog.Info($"The global shortcut {gesture} is not registered: {_reason}.");
        }

        return true;
    }

    /// <inheritdoc/>
    public void Unregister()
    {
    }
}

/// <summary>
/// Start with Windows when no implementation is configured: reports "off", accepts turning it off and refuses to turn
/// it on (the Settings view model then reverts the switch and shows an error toast).
/// </summary>
public sealed class UnavailableAutostartService : IAutostartService
{
    /// <inheritdoc/>
    public bool IsEnabled => false;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException"><paramref name="enabled"/> is true.</exception>
    public void Set(bool enabled)
    {
        if (enabled)
        {
            throw new NotSupportedException("Starting with Windows is not available in this build.");
        }
    }
}
