using NimPad.App.Platform;
using NimPad.App.Theming;

namespace NimPad.App.Shell;

/// <summary>What the composition root hands the main window besides its view model.</summary>
public sealed class MainWindowOptions
{
    /// <summary>The theme service (the window reports the effective theme and recolors the editor); optional.</summary>
    public ThemeService? Themes { get; init; }

    /// <summary>The dialog service whose dialogs appear on this window's modal layer; optional.</summary>
    public DialogService? Dialogs { get; init; }

    /// <summary>Where the window's bounds are remembered; <see langword="null"/> keeps the default bounds.</summary>
    public WindowPlacementStore? Placement { get; init; }

    /// <summary>Shows the Settings window (Ctrl+Comma); <see langword="null"/> when not available.</summary>
    public ISettingsWindowLauncher? SettingsWindow { get; init; }

    /// <summary>How long closing waits for <c>ShellViewModel.ShutdownAsync</c> before the window closes anyway.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(12);
}
