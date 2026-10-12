using System.Windows;
using NimPad.Presentation.Settings;

namespace NimPad.App.Views.Settings;

/// <summary>
/// Opens the Settings window for the shell's <c>OpenSettingsRequested</c> (Ctrl+,): one window at a time, owned by the
/// main window and modeless. A second request brings the open window forward (restoring it if minimized) instead of
/// opening another.
/// </summary>
public static class SettingsWindowLauncher
{
    // One per UI thread (the App has one; tests create windows on several STA threads).
    [ThreadStatic]
    private static SettingsWindow? _current;

    /// <summary>The open Settings window on this thread, or <see langword="null"/>.</summary>
    public static SettingsWindow? Current => _current;

    /// <summary>Shows the Settings window over <paramref name="owner"/>, or activates the one already open.</summary>
    /// <param name="owner">The main window.</param>
    /// <param name="viewModel">The shell's <c>Settings</c>.</param>
    /// <returns>The window.</returns>
    public static SettingsWindow Show(Window owner, SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_current is { } open)
        {
            if (!ReferenceEquals(open.DataContext, viewModel))
            {
                open.DataContext = viewModel;
            }

            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            open.FocusNavigation();
            return open;
        }

        var window = new SettingsWindow(viewModel) { Owner = owner };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window))
            {
                _current = null;
            }
        };
        _current = window;
        window.Show();
        return window;
    }
}
