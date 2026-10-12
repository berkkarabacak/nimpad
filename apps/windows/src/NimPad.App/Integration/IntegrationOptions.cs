using System.Windows;
using NimPad.Presentation.Integration;

namespace NimPad.App.Integration;

/// <summary>What <see cref="IntegrationBootstrapper"/> turns on. <see cref="FromCommandLine"/> gives the App's defaults.</summary>
public sealed record IntegrationOptions
{
    /// <summary>Forward to a running NimPad (false with <c>--no-single-instance</c>).</summary>
    public bool SingleInstance { get; init; } = true;

    /// <summary>Register the global shortcut with Windows (false with <c>--no-hotkey</c>: remembered, never registered).</summary>
    public bool RegisterGlobalHotkey { get; init; } = true;

    /// <summary>Ask Windows to restart NimPad after a crash, hang or update (<c>RegisterApplicationRestart</c>).</summary>
    public bool RegisterRestart { get; init; } = true;

    /// <summary>Watch open external files for changes made by other programs.</summary>
    public bool WatchExternalFiles { get; init; } = true;

    /// <summary>The pipe and mutex name, or <see langword="null"/> for <see cref="Integration.SingleInstance.GetDefaultName"/> (tests pass a unique one).</summary>
    public string? InstanceName { get; init; }

    /// <summary>How long a second launch waits for the running NimPad to take its files.</summary>
    public TimeSpan ForwardTimeout { get; init; } = Integration.SingleInstance.DefaultForwardTimeout;

    /// <summary>
    /// Moves focus to the editor after the window was brought forward; by default the element with AutomationId
    /// <c>Editor</c> (<see cref="WindowActivator.FocusEditor"/>).
    /// </summary>
    public Action<Window>? FocusEditor { get; init; }

    /// <summary>The options the command line asks for (<c>--no-single-instance</c>, <c>--no-hotkey</c>).</summary>
    public static IntegrationOptions FromCommandLine(AppCommandLine commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        return new IntegrationOptions
        {
            SingleInstance = !commandLine.NoSingleInstance,
            RegisterGlobalHotkey = !commandLine.NoHotkey,
        };
    }
}
