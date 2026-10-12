using System.Windows;
using System.Windows.Threading;
using NimPad.App.Platform;
using NimPad.App.Shell;
using NimPad.App.Theming;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;
using AppThemeMode = NimPad.App.Theming.ThemeMode;

namespace NimPad.App.Tests.Shell;

/// <summary>
/// A real <see cref="MainWindow"/> over a real <see cref="ShellViewModel"/> and the App's WPF platform services, on a
/// temporary data folder: the design system is merged into the window's resources (no Application in tests), the
/// window is shown off-screen without activation, and the App's startup path (<see cref="ShellStartup"/>) runs.
/// </summary>
internal sealed class ShellTestWindow
{
    private readonly SystemThemeWatcher _watcher;
    private bool _closed;

    private ShellTestWindow(MainWindow window, ShellViewModel shell, ThemeService themes, SystemThemeWatcher watcher, DialogService dialogs, bool started)
    {
        Window = window;
        Shell = shell;
        Themes = themes;
        _watcher = watcher;
        Dialogs = dialogs;
        Started = started;
        window.Closed += (_, _) => _closed = true;
    }

    public MainWindow Window { get; }

    public ShellViewModel Shell { get; }

    public ThemeService Themes { get; }

    public DialogService Dialogs { get; }

    /// <summary>Whether the startup path opened the data.</summary>
    public bool Started { get; }

    public static PlatformServices CreatePlatform(DialogService dialogs, SystemThemeWatcher watcher)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        return new PlatformServices
        {
            Dispatcher = new WpfUiDispatcher(dispatcher),
            Clipboard = new ClipboardService(dispatcher),
            FileDialogs = new FileDialogService(),
            Dialogs = dialogs,
            SystemTheme = new SystemThemeProvider(watcher),
            Autostart = new UnavailableAutostartService(),
            GlobalHotkey = new DisabledGlobalHotkeyService("UI tests"),
            Fonts = new FontCatalog(),
            Shell = new ShellLauncher(),
        };
    }

    /// <summary>Builds, shows and starts a window; waits until the active tab's text is in the editor.</summary>
    public static async Task<ShellTestWindow> OpenAsync(string dataDirectory, bool dark = false, double width = 1120, bool waitForEditor = true)
    {
        var watcher = new SystemThemeWatcher(() => dark, listen: false);
        var dialogs = new DialogService();
        var shell = new ShellViewModel(CreatePlatform(dialogs, watcher));
        var window = new MainWindow(shell, new MainWindowOptions { Dialogs = dialogs, ShutdownTimeout = TimeSpan.FromSeconds(30) })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = width,
            Height = 720,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        ThemeService.InstallDesignSystem(window.Resources, dark);
        var themes = new ThemeService(window.Resources, watcher) { UpdateWindowFrames = false };
        themes.Apply(dark ? AppThemeMode.Dark : AppThemeMode.Light);
        window.AttachThemeService(themes);

        var started = await ShellStartup.RunAsync(window, new StartupOptions { DataDirectory = dataDirectory }, show: true, showDeadline: TimeSpan.FromSeconds(5));
        var test = new ShellTestWindow(window, shell, themes, watcher, dialogs, started);
        if (started && waitForEditor)
        {
            await test.WaitForEditorAsync();
        }

        await UiThread.SettleAsync();
        return test;
    }

    /// <summary>Waits until the selected tab's document is shown in the editor (or no tab is open).</summary>
    public async Task WaitForEditorAsync()
    {
        await UiThread.WaitUntilAsync(
            () => Shell.IsInitialized
                && (Shell.Tabs.SelectedTab is null || Window.EditorView.Host is { IsShowingActiveDocument: true }),
            "the active tab's document is shown");
        await UiThread.SettleAsync();
    }

    /// <summary>The container of the tab at <paramref name="index"/>.</summary>
    public TabStripItem TabContainer(int index)
    {
        var strip = Window.TitleBar.TabStrip;
        strip.UpdateLayout();
        var container = strip.ItemContainerGenerator.ContainerFromIndex(index) as TabStripItem;
        Xunit.Assert.NotNull(container);
        return container!;
    }

    /// <summary>The selected tab.</summary>
    public TabViewModel SelectedTab
    {
        get
        {
            Xunit.Assert.NotNull(Shell.Tabs.SelectedTab);
            return Shell.Tabs.SelectedTab!;
        }
    }

    /// <summary>Resizes the window and waits for the layout mode to follow.</summary>
    public async Task ResizeAsync(double width)
    {
        Window.Width = width;
        await UiThread.WaitUntilAsync(() => Math.Abs(Shell.WindowWidth - Window.ActualWidth) < 1 && Math.Abs(Window.ActualWidth - width) < 2, $"the window is {width} px wide");
        await UiThread.SettleAsync();
    }

    /// <summary>Closes the window through its normal path (save, shut down) and waits until it is gone.</summary>
    public async Task CloseAsync()
    {
        if (!_closed)
        {
            Window.Close();
            await UiThread.WaitUntilAsync(() => _closed, "the window closed", TimeSpan.FromSeconds(40));
        }

        Themes.Dispose();
        _watcher.Dispose();
    }
}
