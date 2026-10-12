using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NimPad.App.Controls;

namespace NimPad.App.Shell;

/// <summary>
/// The custom title bar (design pages 4, 24 and 28): app mark, tab strip, new-note button, account slot and window
/// controls. <see cref="MainWindow"/> drives the maximize button's hover/pressed look for Snap Layouts and opens
/// <see cref="ShowTabBarMenu"/> for right-clicks on the empty tab-bar space.
/// </summary>
public partial class TitleBarView : UserControl
{
    private Window? _window;

    /// <summary>Creates the title bar.</summary>
    public TitleBarView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The tab strip.</summary>
    public TabStrip TabStrip => Tabs;

    /// <summary>The maximize / restore button (Snap Layouts hit-testing).</summary>
    public Button MaximizeWindowButton => MaximizeButton;

    /// <summary>The area of the tabs and the new-note button, up to the window controls ("empty tab-bar space").</summary>
    public FrameworkElement TabBarArea => TabArea;

    /// <summary>Opens the tab-bar menu (New note, Reopen closed tab) at the mouse pointer.</summary>
    public void ShowTabBarMenu()
    {
        if (Resources["TabBarMenu"] is not ContextMenu menu)
        {
            return;
        }

        menu.DataContext = DataContext;
        menu.PlacementTarget = TabArea;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Shows the maximize or the restore glyph for <paramref name="state"/>.</summary>
    public void UpdateMaximizeGlyph(WindowState state)
    {
        var maximized = state == WindowState.Maximized;
        MaximizeButton.SetResourceReference(Ui.IconProperty, maximized ? "RestoreWindow" : "Maximize");
        var label = maximized ? "Restore down" : "Maximize";
        MaximizeButton.ToolTip = label;
        AutomationProperties.SetName(MaximizeButton, label);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += OnWindowStateChanged;
            UpdateMaximizeGlyph(_window.WindowState);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window is not null)
        {
            UpdateMaximizeGlyph(_window.WindowState);
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            SystemCommands.MinimizeWindow(window);
        }
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            SystemCommands.CloseWindow(window);
        }
    }

    /// <summary>Maximizes the window, or restores it when maximized.</summary>
    public void ToggleMaximize()
    {
        if (Window.GetWindow(this) is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(window);
        }
        else
        {
            SystemCommands.MaximizeWindow(window);
        }
    }
}
