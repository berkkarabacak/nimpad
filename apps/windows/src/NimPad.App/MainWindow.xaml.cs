using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using NimPad.App.Platform;
using NimPad.App.Services;
using NimPad.App.Shell;
using NimPad.App.Theming;
using NimPad.Core.Domain;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Settings;
using NimPad.Presentation.Shell;
using AppThemeMode = NimPad.App.Theming.ThemeMode;

namespace NimPad.App;

/// <summary>
/// The main window (design pages 4, 5, 24 and 28): custom WindowChrome title bar with the tab strip, banners, the
/// notes sidebar beside (wide) or over (medium, narrow) the editor, the status bar, toasts and the modal layer.
/// It binds every <see cref="ShellViewModel"/> command gesture, cycles focus regions (F6), opens files dropped from
/// Windows Explorer, keeps <see cref="ShellViewModel.WindowWidth"/> current, remembers its bounds, reports the effective
/// theme as its UI Automation ItemStatus (<c>theme:light</c> / <c>theme:dark</c>) and, on close, asks about unsaved files
/// and waits (bounded) for <see cref="ShellViewModel.ShutdownAsync"/>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The banner key of a failed startup.</summary>
    public const string StartupFailedBannerKey = "startup-failed";

    /// <summary>The banner key of an unexpected error the app recovered from.</summary>
    public const string UnexpectedErrorBannerKey = "unexpected-error";

    private readonly ShellViewModel _shell;
    private readonly MainWindowOptions _options;
    private readonly WindowChromeHook _chromeHook;
    private readonly DispatcherTimer _placementSaver;
    private readonly WindowBounds? _initialBounds;
    private ThemeService? _themes;
    private bool _closeConfirmed;
    private bool _closing;
    private bool _focusedOnce;

    /// <summary>Creates the window over <paramref name="shell"/> (bindable at once; data arrives with its initialization).</summary>
    /// <param name="shell">The window's view model.</param>
    /// <param name="options">Theme, dialogs, placement and settings window; all optional.</param>
    public MainWindow(ShellViewModel shell, MainWindowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _options = options ?? new MainWindowOptions();
        InitializeComponent();
        DataContext = shell;
        _initialBounds = _options.Placement?.Load();
        _chromeHook = new WindowChromeHook(
            this,
            () => TitleBar.MaximizeWindowButton,
            () => TitleBar.TabBarArea,
            () => !Modal.IsOpen,
            TitleBar.ToggleMaximize,
            TitleBar.ShowTabBarMenu);
        _placementSaver = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _placementSaver.Tick += (_, _) =>
        {
            _placementSaver.Stop();
            SavePlacement(synchronously: false);
        };

        _options.Dialogs?.Attach(Modal);
        Modal.FocusFallback = () => FocusRegionNow(FocusRegion.Editor);
        Modal.IsWindowShortcut = e => InputBindings.OfType<InputBinding>().Any(binding => binding.Gesture?.Matches(this, e) == true);
        RegisterInputBindings();

        shell.PropertyChanged += OnShellPropertyChanged;
        shell.FocusRequested += OnFocusRequested;
        shell.ShowShortcutsRequested += OnShowShortcutsRequested;
        shell.OpenSettingsRequested += OnOpenSettingsRequested;
        shell.SettingsStore.Changed += OnSettingsChanged;
        TitleBar.TabStrip.EditorFocusRequested += (_, _) => FocusRegionLater(FocusRegion.Editor);
        if (EditorView.Host is { } host)
        {
            host.DocumentShown += OnDocumentShown;
        }

        SizeChanged += OnSizeChanged;
        LocationChanged += (_, _) => SchedulePlacementSave();
        StateChanged += (_, _) => SchedulePlacementSave();
        Activated += (_, _) => FocusEditorIfIdle();
        ContentArea.SizeChanged += (_, _) =>
        {
            if (_shell.IsSidebarOverlay)
            {
                UpdateSidebarLayout();
            }
        };

        if (_options.Themes is { } themes)
        {
            AttachThemeService(themes);
        }

        UpdateTheme();
        UpdateSidebarLayout();
    }

    /// <summary>The window's view model.</summary>
    public ShellViewModel Shell => _shell;

    /// <summary>True once closing finished its shutdown work (the window is about to close for good).</summary>
    public bool IsCloseConfirmed => _closeConfirmed;

    /// <summary>The effective theme as reported to UI Automation: <c>theme:light</c> or <c>theme:dark</c>.</summary>
    public string ThemeStatus => AutomationProperties.GetItemStatus(this);

    /// <summary>
    /// Uses <paramref name="themes"/> for the theme setting and the effective-theme report (instead of
    /// <see cref="MainWindowOptions.Themes"/>; for hosts that create the service after the window, such as tests that
    /// install the design system into the window's own resources).
    /// </summary>
    public void AttachThemeService(ThemeService themes)
    {
        ArgumentNullException.ThrowIfNull(themes);
        if (_themes is not null)
        {
            _themes.ThemeChanged -= OnThemeChanged;
        }

        _themes = themes;
        themes.ThemeChanged += OnThemeChanged;
        themes.ApplyToWindow(this);
        UpdateTheme();
    }

    /// <summary>Applies the theme setting (Light, Dark, System) through the theme service, if any.</summary>
    public void ApplyThemeFromSettings()
    {
        if (_themes is not { } themes)
        {
            UpdateTheme();
            return;
        }

        var mode = _shell.SettingsStore.Current.Theme switch
        {
            ThemePreference.Light => AppThemeMode.Light,
            ThemePreference.Dark => AppThemeMode.Dark,
            _ => AppThemeMode.System,
        };
        if (themes.Mode != mode)
        {
            themes.Apply(mode);
        }

        UpdateTheme();
    }

    /// <summary>Shows the error banner of a startup that could not open the data (the window stays usable).</summary>
    public void ShowStartupFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var detail = error is IOException or UnauthorizedAccessException
            ? " Another copy of NimPad may be using them; close it and start NimPad again."
            : " Details are in the log.";
        _shell.Banners.Show(
            StartupFailedBannerKey,
            BannerKind.Error,
            "NimPad couldn't open your notes." + detail,
            FileLog.Current is null ? null : "Show log",
            FileLog.Current is null ? null : OpenLogFolderAsync,
            canDismiss: false,
            removeOnAction: false);
    }

    /// <summary>Shows the banner of an unexpected error NimPad recovered from (after its notes were flushed).</summary>
    public void ShowUnexpectedError()
    {
        _shell.Banners.Show(
            UnexpectedErrorBannerKey,
            BannerKind.Error,
            "Something went wrong, but your notes are saved. If it keeps happening, restart NimPad.",
            FileLog.Current is null ? null : "Show log",
            FileLog.Current is null ? null : OpenLogFolderAsync,
            canDismiss: true,
            removeOnAction: false);
    }

    /// <summary>Moves keyboard focus to a region now (F6 cycling, Ctrl+B, Enter in the tab strip).</summary>
    /// <returns><see langword="true"/> when something in the region took focus.</returns>
    public bool FocusRegionNow(FocusRegion region)
    {
        if (Modal.IsOpen)
        {
            return false;
        }

        switch (region)
        {
            case FocusRegion.TabStrip:
                return TitleBar.TabStrip.FocusSelectedTab() || TitleBar.NewTabButton.Focus();
            case FocusRegion.Sidebar:
                return Explorer.FocusSearch();
            case FocusRegion.StatusBar:
                return StatusBar.FocusFirstItem();
            default:
                return _shell.IsEmptyStateVisible ? EmptyState.FocusNewNote() : EditorView.FocusEditor();
        }
    }

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            // Added after WindowChrome's hook, so it sees the messages first.
            _chromeHook.Attach();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Warn("The window chrome hook could not be installed.", ex);
        }

        if (_initialBounds is { } bounds)
        {
            try
            {
                WindowPlacementStore.Apply(this, bounds);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FileLog.Warn("The window bounds could not be restored.", ex);
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeConfirmed)
        {
            return;
        }

        // Unsaved files are asked about and the data is saved before the window really closes.
        e.Cancel = true;
        if (!_closing)
        {
            _closing = true;
            _ = CloseGracefullyAsync();
        }
    }

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        _placementSaver.Stop();
        _chromeHook.Detach();
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell.FocusRequested -= OnFocusRequested;
        _shell.ShowShortcutsRequested -= OnShowShortcutsRequested;
        _shell.OpenSettingsRequested -= OnOpenSettingsRequested;
        _shell.SettingsStore.Changed -= OnSettingsChanged;
        if (_themes is { } themes)
        {
            themes.ThemeChanged -= OnThemeChanged;
        }

        EditorView.Host?.Dispose();
        base.OnClosed(e);
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || Modal.IsOpen)
        {
            return;
        }

        // Ctrl+Tab must switch tabs even in the editor, which takes Tab for itself.
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            var command = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? _shell.PreviousTabCommand : _shell.NextTabCommand;
            command.Execute(null);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && _shell.IsSidebarOverlay && _shell.IsSidebarOpen)
        {
            _shell.DismissSidebarOverlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnPreviewGotKeyboardFocus(e);
        if (e.NewFocus is DependencyObject focused && RegionOf(focused) is { } region)
        {
            _shell.ReportFocus(region);
        }
    }

    /// <inheritdoc/>
    protected override void OnPreviewDragEnter(DragEventArgs e)
    {
        base.OnPreviewDragEnter(e);
        AcceptFileDrag(e);
    }

    /// <inheritdoc/>
    protected override void OnPreviewDragOver(DragEventArgs e)
    {
        base.OnPreviewDragOver(e);
        AcceptFileDrag(e);
    }

    /// <inheritdoc/>
    protected override void OnPreviewDrop(DragEventArgs e)
    {
        base.OnPreviewDrop(e);
        if (Modal.IsOpen || !e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        e.Handled = true;
        var files = paths.Where(File.Exists).ToList();
        if (files.Count > 0)
        {
            _shell.OpenFiles(files);
            Activate();
            FocusRegionLater(FocusRegion.Editor);
        }
    }

    private static bool IsWithin(DependencyObject element, DependencyObject? ancestor) =>
        ancestor is not null && (ReferenceEquals(element, ancestor) || (ancestor is Visual visual && element is Visual && visual.IsAncestorOf(element)));

    private void AcceptFileDrag(DragEventArgs e)
    {
        if (!Modal.IsOpen && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private FocusRegion? RegionOf(DependencyObject element)
    {
        if (IsWithin(element, TitleBar.TabStrip) || IsWithin(element, TitleBar))
        {
            return FocusRegion.TabStrip;
        }

        if (IsWithin(element, EditorView) || IsWithin(element, EmptyState))
        {
            return FocusRegion.Editor;
        }

        if (IsWithin(element, SidebarHost))
        {
            return FocusRegion.Sidebar;
        }

        if (IsWithin(element, StatusBar))
        {
            return FocusRegion.StatusBar;
        }

        return null;
    }

    private void RegisterInputBindings()
    {
        var shell = _shell;
        Bind(shell.NewNoteCommand, Key.N, ModifierKeys.Control);
        Bind(shell.OpenFileCommand, Key.O, ModifierKeys.Control);
        Bind(shell.SaveCommand, Key.S, ModifierKeys.Control);
        Bind(shell.SaveAsCommand, Key.S, ModifierKeys.Control | ModifierKeys.Shift);
        Bind(shell.CloseTabCommand, Key.W, ModifierKeys.Control);
        Bind(shell.ReopenClosedTabCommand, Key.T, ModifierKeys.Control | ModifierKeys.Shift);
        Bind(shell.MoveTabLeftCommand, Key.Left, ModifierKeys.Control | ModifierKeys.Shift);
        Bind(shell.MoveTabRightCommand, Key.Right, ModifierKeys.Control | ModifierKeys.Shift);
        Bind(shell.DuplicateNoteCommand, Key.D, ModifierKeys.Control | ModifierKeys.Shift);
        Bind(shell.ToggleSidebarCommand, Key.B, ModifierKeys.Control);
        Bind(shell.ZoomInCommand, Key.OemPlus, ModifierKeys.Control);
        Bind(shell.ZoomInCommand, Key.Add, ModifierKeys.Control);
        Bind(shell.ZoomOutCommand, Key.OemMinus, ModifierKeys.Control);
        Bind(shell.ZoomOutCommand, Key.Subtract, ModifierKeys.Control);
        Bind(shell.ResetZoomCommand, Key.D0, ModifierKeys.Control);
        Bind(shell.ResetZoomCommand, Key.NumPad0, ModifierKeys.Control);
        Bind(new RelayCommand(() => shell.FindCommand.Execute(EditorView.SelectedText)), Key.F, ModifierKeys.Control);
        Bind(new RelayCommand(() => shell.ReplaceCommand.Execute(EditorView.SelectedText)), Key.H, ModifierKeys.Control);
        Bind(shell.OpenSettingsCommand, Key.OemComma, ModifierKeys.Control);
        Bind(shell.ShowShortcutsCommand, Key.OemQuestion, ModifierKeys.Control);
        Bind(shell.ShowShortcutsCommand, Key.Divide, ModifierKeys.Control);
        Bind(shell.NextRegionCommand, Key.F6, ModifierKeys.None);
        Bind(shell.PreviousRegionCommand, Key.F6, ModifierKeys.Shift);
        for (var number = 1; number <= 9; number++)
        {
            var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Bind(shell.GoToTabCommand, Key.D0 + number, ModifierKeys.Control, text);
            Bind(shell.GoToTabCommand, Key.NumPad0 + number, ModifierKeys.Control, text);
        }

        void Bind(ICommand command, Key key, ModifierKeys modifiers, object? parameter = null) =>
            InputBindings.Add(new KeyBinding(command, key, modifiers) { CommandParameter = parameter });
    }

    private async Task CloseGracefullyAsync()
    {
        try
        {
            Modal.CancelCurrent();
            if (!_shell.IsShuttingDown && !await _shell.ConfirmCloseAsync())
            {
                _closing = false;
                return;
            }

            SavePlacement(synchronously: true);
            var shutdown = _shell.ShutdownAsync();
            if (await Task.WhenAny(shutdown, Task.Delay(_options.ShutdownTimeout)) == shutdown)
            {
                await shutdown;
            }
            else
            {
                FileLog.Warn($"Saving at exit took longer than {_options.ShutdownTimeout.TotalSeconds:0} s; closing anyway.");
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Closing the window failed; saving what can be saved.", ex);
            _shell.EmergencyFlush();
        }

        _closeConfirmed = true;
        Close();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && e.NewSize.Width > 0)
        {
            _shell.WindowWidth = e.NewSize.Width;
        }

        SchedulePlacementSave();
    }

    private void SchedulePlacementSave()
    {
        if (_options.Placement is not null && IsLoaded && !_closeConfirmed)
        {
            _placementSaver.Stop();
            _placementSaver.Start();
        }
    }

    private void SavePlacement(bool synchronously)
    {
        if (_options.Placement is not { } store || WindowPlacementStore.Capture(this) is not { } bounds)
        {
            return;
        }

        if (synchronously)
        {
            store.Save(bounds);
        }
        else
        {
            _ = Task.Run(() => store.Save(bounds));
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.IsSidebarOpen):
            case nameof(ShellViewModel.IsSidebarOverlay):
            case nameof(ShellViewModel.SidebarWidth):
                UpdateSidebarLayout();
                break;
            case nameof(ShellViewModel.IsInitialized):
                FocusEditorIfIdle();
                break;
        }
    }

    // Docked (wide): the sidebar column has the remembered width and a splitter; overlaid (medium, narrow): the sidebar
    // floats over the editor with a flyout shadow and a dimmed layer that closes it on click; closed: no column.
    private void UpdateSidebarLayout()
    {
        var open = _shell.IsSidebarOpen;
        var overlay = _shell.IsSidebarOverlay;
        if (!open)
        {
            SidebarHost.Visibility = Visibility.Collapsed;
            SidebarShadow.Visibility = Visibility.Collapsed;
            OverlayDismissLayer.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.MaxWidth = double.PositiveInfinity;
            SidebarColumn.Width = new GridLength(0);
            return;
        }

        SidebarHost.Visibility = Visibility.Visible;
        if (overlay)
        {
            SidebarColumn.MinWidth = 0;
            SidebarColumn.MaxWidth = double.PositiveInfinity;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitter.Visibility = Visibility.Collapsed;
            var available = ContentArea.ActualWidth > 0 ? ContentArea.ActualWidth : _shell.WindowWidth;
            var width = Math.Max(Math.Min(ShellViewModel.MinSidebarWidth, available), Math.Min(_shell.SidebarWidth, available - 48));
            Grid.SetColumnSpan(SidebarHost, 3);
            SidebarHost.HorizontalAlignment = HorizontalAlignment.Left;
            SidebarHost.Width = width;
            SidebarShadow.Width = width;
            SidebarShadow.Visibility = Visibility.Visible;
            OverlayDismissLayer.Visibility = Visibility.Visible;
        }
        else
        {
            Grid.SetColumnSpan(SidebarHost, 1);
            SidebarHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidebarHost.Width = double.NaN;
            SidebarShadow.Visibility = Visibility.Collapsed;
            OverlayDismissLayer.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Visible;
            SidebarColumn.MinWidth = ShellViewModel.MinSidebarWidth;
            SidebarColumn.MaxWidth = ShellViewModel.MaxSidebarWidth;
            SidebarColumn.Width = new GridLength(_shell.SidebarWidth);
        }
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (SidebarColumn.ActualWidth > 0)
        {
            _shell.SidebarWidth = SidebarColumn.ActualWidth;
        }
    }

    private void OnOverlayDismissMouseDown(object sender, MouseButtonEventArgs e)
    {
        _shell.DismissSidebarOverlayCommand.Execute(null);
        e.Handled = true;
    }

    private void OnFocusRequested(object? sender, FocusRequestedEventArgs e) => FocusRegionLater(e.Region);

    // A region that just appeared (the sidebar after Ctrl+B) needs a layout pass before it can take focus.
    private void FocusRegionLater(FocusRegion region) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusRegionNow(region)));

    private void OnDocumentShown(object? sender, EventArgs e) => FocusEditorIfIdle();

    // Startup and new documents: the editor gets focus unless the user is somewhere else on purpose.
    private void FocusEditorIfIdle()
    {
        if (!IsActive || Modal.IsOpen || !_shell.IsInitialized)
        {
            return;
        }

        var focused = Keyboard.FocusedElement as DependencyObject;
        var elsewhere = focused is not null && !ReferenceEquals(focused, this) && RegionOf(focused) is { } region && region != FocusRegion.Editor;
        if (elsewhere && _focusedOnce)
        {
            return;
        }

        if (_shell.FocusedRegion == FocusRegion.Editor || !_focusedOnce)
        {
            _focusedOnce |= FocusRegionNow(FocusRegion.Editor);
        }
    }

    private void OnShowShortcutsRequested(object? sender, EventArgs e)
    {
        if (!Modal.IsOpen)
        {
            _ = ShowDialogAsync(new ShortcutsDialog(_shell.Shortcuts));
        }
    }

    private async Task ShowDialogAsync(FrameworkElement dialog)
    {
        try
        {
            await Modal.ShowAsync(dialog);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Error("A dialog failed.", ex);
        }
    }

    private void OnOpenSettingsRequested(object? sender, EventArgs e)
    {
        if (_options.SettingsWindow is { } launcher)
        {
            try
            {
                launcher.Show(this, _shell);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FileLog.Error("The Settings window could not be shown.", ex);
                _shell.Toasts.ShowError("Settings couldn't be opened.");
            }
        }
        else
        {
            FileLog.Info("Settings were requested, but no Settings window is configured in this build.");
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.Theme != e.NewSettings.Theme)
        {
            ApplyThemeFromSettings();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateTheme();

    private void UpdateTheme()
    {
        var dark = _themes?.IsDark ?? string.Equals(TryFindResource("ThemeName") as string, "Dark", StringComparison.Ordinal);
        var status = dark ? "theme:dark" : "theme:light";
        var previous = AutomationProperties.GetItemStatus(this);
        EditorView.IsDarkTheme = dark;
        if (string.Equals(previous, status, StringComparison.Ordinal))
        {
            return;
        }

        AutomationProperties.SetItemStatus(this, status);
        UIElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, previous ?? string.Empty, status);
    }

    private static Task OpenLogFolderAsync()
    {
        if (FileLog.Current is { } log)
        {
            new ShellLauncher().OpenUri(new Uri(log.Directory));
        }

        return Task.CompletedTask;
    }
}
