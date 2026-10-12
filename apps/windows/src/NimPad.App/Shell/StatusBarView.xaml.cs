using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NimPad.Presentation.Shell;

namespace NimPad.App.Shell;

/// <summary>
/// The status bar (design pages 4, 24 and 28). The mode button opens the list of every format; in the narrow layout a
/// ⋯ button holds encoding, mode and zoom. The encoding is shown as text: changing a file's save encoding needs a
/// view-model operation that does not exist yet.
/// </summary>
public partial class StatusBarView : UserControl
{
    /// <summary>Creates the status bar.</summary>
    public StatusBarView()
    {
        InitializeComponent();
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    /// <summary>Moves keyboard focus into the status bar (F6), on the sidebar toggle.</summary>
    /// <returns><see langword="true"/> when an item took focus.</returns>
    public bool FocusFirstItem() => SidebarToggle.Focus();

    // The mode picker (every format, the current one checked) opens above the button.
    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (Shell is not { } shell)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = Mode,
            Placement = PlacementMode.Top,
            VerticalOffset = 14,
            DataContext = shell,
        };
        AutomationProperties.SetAutomationId(menu, "ModeMenu");
        foreach (var item in ModeItems(shell))
        {
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private static IEnumerable<MenuItem> ModeItems(ShellViewModel shell)
    {
        foreach (var mode in shell.StatusBar.AvailableModes)
        {
            var item = new MenuItem
            {
                Header = mode.DisplayName,
                Command = shell.StatusBar.ChangeModeCommand,
                CommandParameter = mode,
                IsChecked = mode.Format == shell.Editor.Format,
            };
            AutomationProperties.SetAutomationId(item, "Mode_" + mode.Format);
            yield return item;
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Shell is not { } shell)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = More,
            Placement = PlacementMode.Top,
            VerticalOffset = 14,
            DataContext = shell,
        };
        AutomationProperties.SetAutomationId(menu, "StatusMoreMenu");

        var encoding = new MenuItem { Header = "Encoding: " + shell.StatusBar.EncodingText, IsEnabled = false };
        AutomationProperties.SetAutomationId(encoding, "StatusMoreEncoding");
        menu.Items.Add(encoding);

        var modes = new MenuItem { Header = "Mode: " + shell.StatusBar.ModeText };
        AutomationProperties.SetAutomationId(modes, "StatusMoreMode");
        foreach (var item in ModeItems(shell))
        {
            modes.Items.Add(item);
        }

        menu.Items.Add(modes);

        var zoom = new MenuItem
        {
            Header = "Zoom: " + shell.StatusBar.ZoomText,
            InputGestureText = "Ctrl+0",
            Command = shell.ResetZoomCommand,
        };
        AutomationProperties.SetAutomationId(zoom, "StatusMoreZoom");
        AutomationProperties.SetHelpText(zoom, "Reset zoom to 100%");
        menu.Items.Add(zoom);
        menu.IsOpen = true;
    }
}
