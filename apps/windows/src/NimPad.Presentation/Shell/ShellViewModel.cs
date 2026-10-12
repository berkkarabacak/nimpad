using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimPad.Core.Domain;
using NimPad.Core.Services;
using NimPad.Core.Storage;
using NimPad.Presentation.Editor;
using NimPad.Presentation.Explorer;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Find;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Settings;
using NimPad.Presentation.Shortcuts;
using NimPad.Presentation.Tabs;

namespace NimPad.Presentation.Shell;

/// <summary>The three window layouts (design page 28).</summary>
public enum LayoutMode
{
    /// <summary>720 px and up: docked 288 px sidebar, app name, character count, editor padding 32.</summary>
    Wide,

    /// <summary>520–719 px: the sidebar overlays the editor, the app name and the character count hide, padding 24.</summary>
    Medium,

    /// <summary>Under 520 px (the minimum is 400): like Medium, and the status bar keeps only position and sync.</summary>
    Narrow,
}

/// <summary>The four keyboard regions F6 / Shift+F6 cycle through (design page 27).</summary>
public enum FocusRegion
{
    /// <summary>The tab strip (one tab stop; arrows move inside).</summary>
    TabStrip,

    /// <summary>The editor.</summary>
    Editor,

    /// <summary>The notes sidebar; focus lands in its search field.</summary>
    Sidebar,

    /// <summary>The status bar.</summary>
    StatusBar,
}

/// <summary>A request to move keyboard focus to a region.</summary>
public sealed class FocusRequestedEventArgs(FocusRegion region) : EventArgs
{
    /// <summary>Where focus goes.</summary>
    public FocusRegion Region { get; } = region;
}

/// <summary>How NimPad was started.</summary>
public sealed record StartupOptions
{
    /// <summary>The <c>--data-dir</c> override, or <see langword="null"/> for the default (or portable) location.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Files named on the command line; they open as external tabs.</summary>
    public IReadOnlyList<string> FilesToOpen { get; init; } = [];
}

/// <summary>
/// The main window's root view model: the explorer, the tabs, the editor state, the status bar, find and replace,
/// toasts, banners, settings and shortcuts; the window layout; every app command; startup and shutdown.
/// </summary>
/// <remarks>
/// <para>Create it on the UI thread, call <see cref="InitializeAsync"/> (it opens the database, restores the session
/// and returns quickly; only the active tab's text loads), and on exit call <see cref="ConfirmCloseAsync"/> (unsaved
/// external files) and then <see cref="ShutdownAsync"/>. Crash and session-end handlers call
/// <see cref="EmergencyFlush"/>.</para>
/// <para>Keyboard gestures are listed on each command; the App binds them with InputBindings.</para>
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>The default (and initial) sidebar width.</summary>
    public const double DefaultSidebarWidth = UiState.DefaultSidebarWidth;

    /// <summary>The narrowest sidebar.</summary>
    public const double MinSidebarWidth = 200;

    /// <summary>The widest sidebar.</summary>
    public const double MaxSidebarWidth = 480;

    /// <summary>The narrowest wide layout.</summary>
    public const double WideMinWidth = 720;

    /// <summary>The narrowest medium layout.</summary>
    public const double MediumMinWidth = 520;

    /// <summary>How long shutdown waits for pending saves.</summary>
    public static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The banner key of the database recovery notice.</summary>
    public const string DatabaseRecoveryBannerKey = "database-recovery";

    /// <summary>The banner key of the recovered unsaved external files.</summary>
    public const string RecoveredFilesBannerKey = "recovered-files";

    /// <summary>The banner key of a global shortcut that could not be registered.</summary>
    public const string HotkeyBannerKey = "global-hotkey";

    private static readonly FocusRegion[] RegionOrder = [FocusRegion.TabStrip, FocusRegion.Editor, FocusRegion.Sidebar, FocusRegion.StatusBar];

    private readonly ViewModelContext _context;
    private readonly Debouncer _uiStateSaver;
    private LocalDatabase? _database;
    private CoreServices? _core;
    private UiState _uiState = UiState.Default;
    private bool _sidebarPreferredOpen = true;
    private bool _shutDown;
    private bool _initializing;
    private Task _lastUiStateSave = Task.CompletedTask;

    /// <summary>Creates the shell and its children (bindable at once; commands work after <see cref="InitializeAsync"/>).</summary>
    /// <param name="platform">The App's platform services.</param>
    /// <param name="clock">The clock (tests pass a fake one); <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="culture">The culture for timestamps and counts; the current culture by default.</param>
    public ShellViewModel(PlatformServices platform, TimeProvider? clock = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        _context = new ViewModelContext(platform, clock ?? TimeProvider.System, culture);
        _uiStateSaver = new Debouncer(_context.Clock, _context.Dispatcher, TabsViewModel.SessionSaveDelay, () => _context.Work.Track(SaveUiStateAsync(), "save the UI state"));
        Tabs = new TabsViewModel(_context);
        Editor = new EditorViewModel(_context, Tabs);
        StatusBar = new StatusBarViewModel(_context, Editor);
        FindReplace = new FindReplaceViewModel(_context);
        Explorer = new ExplorerViewModel(_context, Tabs);
        Settings = new SettingsViewModel(_context);
        Shortcuts = new ShortcutsViewModel(_context.Settings);

        Tabs.PropertyChanged += OnTabsPropertyChanged;
        Tabs.BufferChanged += (_, tab) =>
        {
            if (tab == Tabs.SelectedTab)
            {
                FindReplace.OnDocumentChanged();
            }
        };
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.ActiveTab))
            {
                FindReplace.SetActiveTab(Editor.ActiveTab);
            }
        };
        Editor.RevealRequested += (_, e) => RevealInExplorer(e.NodeId);
        StatusBar.ToggleSidebarRequested += (_, _) => ToggleSidebar();
        Explorer.StateChanged += (_, _) => ScheduleUiStateSave();
        Explorer.FocusEditorRequested += (_, _) =>
        {
            // An overlaid sidebar gets out of the way, as with Esc.
            if (IsSidebarOverlay && IsSidebarOpen)
            {
                SetSidebarOpen(false);
            }

            RequestFocus(FocusRegion.Editor);
        };
        Banners.ActionFailed += (_, message) => Toasts.ShowError(message);
        _context.Settings.SaveFailed += (_, _) => _context.Dispatcher.Post(() => Toasts.ShowError("Your settings couldn't be saved."));
        _context.Work.Failed += (_, error) => _context.Dispatcher.Post(() => ReportBackgroundFailure(error));
        ApplyLayout();
    }

    // ------------------------------------------------------------------ children

    /// <summary>The notes sidebar.</summary>
    public ExplorerViewModel Explorer { get; }

    /// <summary>The tab strip.</summary>
    public TabsViewModel Tabs { get; }

    /// <summary>The active tab's editor state.</summary>
    public EditorViewModel Editor { get; }

    /// <summary>The status bar.</summary>
    public StatusBarViewModel StatusBar { get; }

    /// <summary>The find and replace panel.</summary>
    public FindReplaceViewModel FindReplace { get; }

    /// <summary>The toast stack.</summary>
    public ToastHostViewModel Toasts => _context.Toasts;

    /// <summary>The banners under the tab strip.</summary>
    public BannerHostViewModel Banners => _context.Banners;

    /// <summary>The Settings window.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The keyboard shortcuts dialog.</summary>
    public ShortcutsViewModel Shortcuts { get; }

    /// <summary>The current settings (shared by every view model).</summary>
    public SettingsStore SettingsStore => _context.Settings;

    /// <summary>Fire-and-forget work in flight (debounced saves, refreshes).</summary>
    public BackgroundWork BackgroundWork => _context.Work;

    /// <summary>Where the data lives (after <see cref="InitializeAsync"/>), for example for the log folder.</summary>
    [ObservableProperty]
    public partial AppPaths? Paths { get; private set; }

    /// <summary>
    /// True from the start of <see cref="ShutdownAsync"/>: bind the editors' <c>IsReadOnly</c> to it (or hide the
    /// window), because text typed after the final flush is not saved.
    /// </summary>
    [ObservableProperty]
    public partial bool IsShuttingDown { get; private set; }

    /// <summary>True once <see cref="InitializeAsync"/> completed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyStateVisible))]
    public partial bool IsInitialized { get; private set; }

    /// <summary>True when every tab is closed: the window shows "No notes open" with New note and Reopen closed tab.</summary>
    public bool IsEmptyStateVisible => IsInitialized && !Tabs.HasTabs;

    /// <summary>The empty window's title.</summary>
    public string EmptyStateTitle => "No notes open";

    internal CoreServices Core => _context.Core;

    internal ViewModelContext Context => _context;

    // ------------------------------------------------------------------ layout

    /// <summary>The window width in device-independent pixels (the view sets it on resize).</summary>
    [ObservableProperty]
    public partial double WindowWidth { get; set; } = 1120;

    /// <summary>The layout for <see cref="WindowWidth"/>.</summary>
    [ObservableProperty]
    public partial LayoutMode LayoutMode { get; private set; }

    /// <summary>Whether the sidebar is shown.</summary>
    [ObservableProperty]
    public partial bool IsSidebarOpen { get; private set; } = true;

    /// <summary>The docked sidebar width (200–480, remembered).</summary>
    [ObservableProperty]
    public partial double SidebarWidth { get; set; } = DefaultSidebarWidth;

    /// <summary>True in Medium and Narrow: the sidebar overlays the editor; Esc or a click outside closes it.</summary>
    [ObservableProperty]
    public partial bool IsSidebarOverlay { get; private set; }

    /// <summary>True in Wide: the title bar shows "NimPad".</summary>
    [ObservableProperty]
    public partial bool ShowAppName { get; private set; } = true;

    /// <summary>True in Wide: the status bar shows the character count.</summary>
    [ObservableProperty]
    public partial bool IsCharacterCountVisible { get; private set; } = true;

    /// <summary>True in Narrow: encoding, mode and zoom move into the status bar's "···" menu.</summary>
    [ObservableProperty]
    public partial bool IsStatusBarCompact { get; private set; }

    /// <summary>The editor's left and right padding: 32 in Wide, 24 otherwise.</summary>
    [ObservableProperty]
    public partial double EditorHorizontalPadding { get; private set; } = 32;

    /// <summary>The region with keyboard focus, as last reported by the view.</summary>
    [ObservableProperty]
    public partial FocusRegion FocusedRegion { get; private set; } = FocusRegion.Editor;

    partial void OnWindowWidthChanged(double value) => ApplyLayout();

    partial void OnSidebarWidthChanged(double value)
    {
        var clamped = Math.Clamp(double.IsFinite(value) ? value : DefaultSidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        if (!clamped.Equals(value))
        {
            SidebarWidth = clamped;
            return;
        }

        ScheduleUiStateSave();
    }

    private void ApplyLayout()
    {
        var width = WindowWidth;
        var mode = width >= WideMinWidth ? LayoutMode.Wide : width >= MediumMinWidth ? LayoutMode.Medium : LayoutMode.Narrow;
        var wasOverlay = IsSidebarOverlay;
        LayoutMode = mode;
        IsSidebarOverlay = mode != LayoutMode.Wide;
        ShowAppName = mode == LayoutMode.Wide;
        IsCharacterCountVisible = mode == LayoutMode.Wide;
        IsStatusBarCompact = mode == LayoutMode.Narrow;
        EditorHorizontalPadding = mode == LayoutMode.Wide ? 32 : 24;
        Editor.HorizontalPadding = EditorHorizontalPadding;
        if (IsSidebarOverlay && !wasOverlay)
        {
            // The overlay starts closed; the docked preference is kept for the wide layout.
            IsSidebarOpen = false;
        }
        else if (!IsSidebarOverlay && wasOverlay)
        {
            IsSidebarOpen = _sidebarPreferredOpen;
        }
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Opens the database, ensures the default workspace, loads the settings and every node's metadata, restores
    /// the session (tabs appear at once in order; only the active tab loads its text), opens command-line files,
    /// shows recovery banners, and starts the trash purge and the daily backup in the background. A brand-new profile
    /// starts with one new untitled note.
    /// </summary>
    public async Task InitializeAsync(StartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsInitialized || _initializing)
        {
            throw new InvalidOperationException("The shell is already initialized.");
        }

        _initializing = true;
        try
        {
            var paths = AppPaths.ResolveForCurrentProcess(options.DataDirectory);
            Paths = paths;
            var clock = _context.Clock;
            var (database, brandNew) = await Task.Run(() =>
            {
                paths.EnsureDirectories();
                var isNew = !File.Exists(paths.DatabasePath);
                return (LocalDatabase.Open(paths, clock), isNew);
            });
            _database = database;
            _core = CoreServices.Create(database, clock);
            _context.Attach(_core);

            var workspace = await _core.Workspaces.EnsureDefaultWorkspaceAsync();
            await _context.Settings.LoadAsync(_core.Settings);
            _uiState = await _core.Sessions.LoadUiStateAsync();
            await _context.Model.LoadAsync(_core, workspace);

            SidebarWidth = Math.Clamp(_uiState.SidebarWidth, MinSidebarWidth, MaxSidebarWidth);
            _sidebarPreferredOpen = !_uiState.IsSidebarCollapsed;
            IsSidebarOpen = !IsSidebarOverlay && _sidebarPreferredOpen;
            Explorer.Load(_uiState);

            await Tabs.AttachAsync();

            // Unsaved text of external files from a crash or an earlier session: their tabs (restored or opened
            // later) start from it, so nothing typed afterwards can overwrite it with the disk text.
            var recoveredFiles = await _core.ExternalFiles.GetRecoveryBuffersAsync();
            Tabs.SetRecoveredFiles(recoveredFiles);
            if (_context.Settings.Current.RestoreTabs)
            {
                Tabs.Restore(await _core.Sessions.LoadTabsAsync());
            }

            foreach (var file in options.FilesToOpen.Where(file => !string.IsNullOrWhiteSpace(file)))
            {
                Tabs.OpenExternal(file);
            }

            ShowDatabaseRecovery(database.Recovery);
            ShowRecoveredFiles(recoveredFiles);
            RegisterGlobalHotkey();

            _context.Work.Track(_core.Nodes.PurgeExpiredTrashAsync(), "purge expired Trash items");
            _context.Work.Track(_core.Backups.RunDailyBackupAsync(), "run the daily backup");

            if (brandNew && !Tabs.HasTabs)
            {
                // Design: a brand-new profile gets an empty note, no tour.
                await Tabs.NewUntitledAsync();
            }

            IsInitialized = true;
            if (Tabs.SelectedTab is { LoadState: TabLoadState.NotLoaded } active)
            {
                _context.Work.Track(LoadQuietlyAsync(active), "load the active tab");
            }

            // The restore itself is not a change; save once so the session reflects what was actually opened.
            Tabs.ScheduleSessionSave();
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// Asks about every external file with unsaved changes (Save / Don't save / Cancel) before the window closes.
    /// Notes need no question: they are saved automatically.
    /// </summary>
    /// <returns><see langword="false"/> when the user cancelled closing.</returns>
    public async Task<bool> ConfirmCloseAsync()
    {
        foreach (var tab in Tabs.Tabs.Where(tab => tab.IsExternal && tab.IsDirty).ToList())
        {
            Tabs.SelectedTab = tab;
            var choice = await _context.Platform.Dialogs.ChooseAsync(new ConfirmRequest(
                $"Save changes to {DisplayText.Quote(tab.Title)}?",
                "Your changes will be lost if you don't save them.",
                "Save",
                SecondaryText: "Don't save"));
            switch (choice)
            {
                case DialogChoice.Primary:
                    if (!await tab.SaveAsync())
                    {
                        return false;
                    }

                    break;
                case DialogChoice.Secondary:
                    await tab.DiscardRecoveryBufferAsync();
                    tab.MarkDiscarded();
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Saves everything and closes the database: pending autosaves are flushed first (bounded wait), then unsaved
    /// external text goes to its recovery buffer, the session and the UI state are saved, and the database is
    /// checkpointed. Each step runs even when an earlier one failed. Make the editors read-only while it runs
    /// (<see cref="IsShuttingDown"/>): text typed after the flush is not saved.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_shutDown || _core is not { } core || _database is not { } database)
        {
            return;
        }

        _shutDown = true;
        IsShuttingDown = true;
        try
        {
            // Notes first: they hold the most important text.
            await RunShutdownStepAsync("flush the autosaves", () => core.Autosave.FlushAllAsync(ShutdownFlushTimeout));
            await RunShutdownStepAsync("write the recovery buffers", Tabs.FlushRecoveryBuffersAsync);
            await RunShutdownStepAsync("save the session", Tabs.SaveSessionAsync);
            await RunShutdownStepAsync("save the window layout", SaveUiStateAsync);
            await RunShutdownStepAsync("save the settings", _context.Settings.WhenSavedAsync);
            await RunShutdownStepAsync("finish background work", () => _context.Work.WhenIdleAsync(ShutdownFlushTimeout));
        }
        finally
        {
            Tabs.Detach();
            _context.Model.Dispose();
            try
            {
                _context.Platform.GlobalHotkey.Unregister();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unregistering the global shortcut failed: {ex.Message}");
            }

            try
            {
                await core.Backups.CheckpointAndOptimizeAsync();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"The shutdown checkpoint failed: {ex.Message}");
            }

            _context.Settings.Detach();
            _context.Detach();
            core.Dispose();
            database.Dispose();
            _core = null;
            _database = null;
        }
    }

    /// <summary>
    /// Best-effort synchronous save for crash and session-end handlers: flushes pending autosaves, writes unsaved
    /// external text to recovery buffers and saves the session, waiting at most <paramref name="timeout"/> (3 s by
    /// default). Each part runs even when another fails. Never throws.
    /// </summary>
    /// <returns><see langword="true"/> when everything was saved in time.</returns>
    public bool EmergencyFlush(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(3);
        if (_core is not { } core || _shutDown)
        {
            return true;
        }

        // Notes first; every part starts even when another throws right away.
        var flush = StartFlush(() => core.Autosave.FlushAllAsync(limit));
        var recovery = Start(Tabs.FlushRecoveryBuffersAsync);
        var saveSession = Start(() => core.Sessions.SaveTabsAsync(Tabs.BuildSessionTabs()));
        try
        {
            var finished = Task.WhenAll(flush, recovery, saveSession).Wait(limit);
            return finished && flush.Result;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Emergency flush failed: {ex}");
            return false;
        }

        static Task<bool> StartFlush(Func<Task<bool>> part)
        {
            try
            {
                return part();
            }
            catch (Exception ex)
            {
                return Task.FromException<bool>(ex);
            }
        }

        static Task Start(Func<Task> part)
        {
            try
            {
                return part();
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }
    }

    private static async Task RunShutdownStepAsync(string description, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Shutdown: could not {description}: {ex}");
        }
    }

    /// <summary>Opens files as external tabs (single-instance forwarding, files dropped from Windows Explorer).</summary>
    public void OpenFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!_context.IsReady)
        {
            return;
        }

        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            Tabs.OpenExternal(path);
        }
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Raised to move keyboard focus to a region.</summary>
    public event EventHandler<FocusRequestedEventArgs>? FocusRequested;

    /// <summary>Raised by <see cref="OpenSettingsCommand"/>; the App shows the Settings window.</summary>
    public event EventHandler? OpenSettingsRequested;

    /// <summary>Raised by <see cref="ShowShortcutsCommand"/>; the App shows the shortcuts dialog.</summary>
    public event EventHandler? ShowShortcutsRequested;

    /// <summary>New untitled note at the end of the strip (Ctrl+N).</summary>
    [RelayCommand]
    private Task NewNoteAsync() => _context.GuardAsync(async () =>
    {
        await Tabs.NewUntitledAsync();
        RequestFocus(FocusRegion.Editor);
    });

    /// <summary>Opens files from disk (Ctrl+O).</summary>
    [RelayCommand]
    private void OpenFile()
    {
        var files = _context.Platform.FileDialogs.PickFilesToOpen();
        if (files.Count > 0)
        {
            OpenFiles(files);
            RequestFocus(FocusRegion.Editor);
        }
    }

    /// <summary>Ctrl+S: saves an external file in place; exports a note to a file (notes save themselves).</summary>
    [RelayCommand]
    private Task SaveAsync() => _context.GuardAsync(async () =>
    {
        switch (Tabs.SelectedTab)
        {
            case { IsExternal: true } file:
                await file.SaveAsync();
                break;
            case { IsManaged: true, NodeId: { } nodeId }:
                await NoteExport.ExportAsync(_context, nodeId);
                break;
        }
    });

    /// <summary>Ctrl+Shift+S: saves an external file under a new name (the tab follows it); exports a note.</summary>
    [RelayCommand]
    private Task SaveAsAsync() => _context.GuardAsync(async () =>
    {
        switch (Tabs.SelectedTab)
        {
            case { IsExternal: true, FilePath: { } path } file:
                var target = _context.Platform.FileDialogs.PickSaveFile(Path.GetFileName(path), Path.GetDirectoryName(path));
                if (!string.IsNullOrWhiteSpace(target))
                {
                    await file.SaveAsAsync(target);
                }

                break;
            case { IsManaged: true, NodeId: { } nodeId }:
                await NoteExport.ExportAsync(_context, nodeId);
                break;
        }
    });

    /// <summary>Closes the active tab (Ctrl+W).</summary>
    [RelayCommand]
    private Task CloseTabAsync() => _context.GuardAsync(async () =>
    {
        if (Tabs.SelectedTab is { } tab)
        {
            await Tabs.CloseAsync(tab);
        }
    });

    /// <summary>Reopens the last closed tab (Ctrl+Shift+T).</summary>
    [RelayCommand]
    private Task ReopenClosedTabAsync() => _context.GuardAsync(async () =>
    {
        if (await Tabs.ReopenClosedAsync() is not null)
        {
            RequestFocus(FocusRegion.Editor);
        }
    });

    /// <summary>Next tab, wrapping (Ctrl+Tab).</summary>
    [RelayCommand]
    private void NextTab() => Tabs.NextTab();

    /// <summary>Previous tab, wrapping (Ctrl+Shift+Tab).</summary>
    [RelayCommand]
    private void PreviousTab() => Tabs.PreviousTab();

    /// <summary>Tab N (Ctrl+1…8); the last tab (Ctrl+9). The parameter is the number, as a number or text.</summary>
    [RelayCommand]
    private void GoToTab(object? number) => Tabs.GoToTabCommand.Execute(number);

    /// <summary>Moves the active tab left (Ctrl+Shift+Left).</summary>
    [RelayCommand]
    private void MoveTabLeft() => Tabs.MoveTabLeftCommand.Execute(null);

    /// <summary>Moves the active tab right (Ctrl+Shift+Right).</summary>
    [RelayCommand]
    private void MoveTabRight() => Tabs.MoveTabRightCommand.Execute(null);

    /// <summary>Renames the active tab's note (F2 while the tab strip has focus).</summary>
    [RelayCommand]
    private void RenameTab() => Tabs.SelectedTab?.BeginRename();

    /// <summary>Duplicates the active note (Ctrl+Shift+D).</summary>
    [RelayCommand]
    private Task DuplicateNoteAsync() => _context.GuardAsync(async () =>
    {
        if (Tabs.SelectedTab is { IsManaged: true } tab)
        {
            await Tabs.DuplicateAsync(tab);
        }
    });

    /// <summary>Shows or hides the notes sidebar (Ctrl+B); opening it puts focus in its search field.</summary>
    [RelayCommand]
    private void ToggleSidebar()
    {
        if (IsSidebarOpen)
        {
            SetSidebarOpen(false);
            RequestFocus(FocusRegion.Editor);
        }
        else
        {
            SetSidebarOpen(true);
            RequestFocus(FocusRegion.Sidebar);
        }
    }

    /// <summary>Closes an overlaid sidebar (Esc or a click outside it in the medium and narrow layouts).</summary>
    [RelayCommand]
    private void DismissSidebarOverlay()
    {
        if (IsSidebarOverlay && IsSidebarOpen)
        {
            SetSidebarOpen(false);
            RequestFocus(FocusRegion.Editor);
        }
    }

    /// <summary>Opens the sidebar (if needed) and focuses its search field.</summary>
    [RelayCommand]
    private void FocusSearch()
    {
        SetSidebarOpen(true);
        RequestFocus(FocusRegion.Sidebar);
    }

    /// <summary>Zoom in (Ctrl+Plus).</summary>
    [RelayCommand]
    private void ZoomIn() => Editor.ZoomIn();

    /// <summary>Zoom out (Ctrl+Minus).</summary>
    [RelayCommand]
    private void ZoomOut() => Editor.ZoomOut();

    /// <summary>Zoom back to 100 % (Ctrl+0).</summary>
    [RelayCommand]
    private void ResetZoom() => Editor.ResetZoom();

    /// <summary>Opens Find (Ctrl+F); the parameter is the editor's selected text.</summary>
    [RelayCommand]
    private void Find(string? selectedText)
    {
        if (Tabs.SelectedTab is not null)
        {
            FindReplace.Open(selectedText);
        }
    }

    /// <summary>Opens Replace (Ctrl+H); the parameter is the editor's selected text.</summary>
    [RelayCommand]
    private void Replace(string? selectedText)
    {
        if (Tabs.SelectedTab is not null)
        {
            FindReplace.OpenReplace(selectedText);
        }
    }

    /// <summary>Opens the Settings window (Ctrl+Comma).</summary>
    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows the keyboard shortcuts (Ctrl+Slash).</summary>
    [RelayCommand]
    private void ShowShortcuts()
    {
        Shortcuts.Filter = string.Empty;
        ShowShortcutsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves focus to the next region (F6): tab strip, editor, sidebar (when open), status bar.</summary>
    [RelayCommand]
    private void NextRegion() => CycleRegion(1);

    /// <summary>Moves focus to the previous region (Shift+F6).</summary>
    [RelayCommand]
    private void PreviousRegion() => CycleRegion(-1);

    /// <summary>Imports the active external file into the workspace.</summary>
    [RelayCommand]
    private Task ImportFileAsync() => _context.GuardAsync(async () =>
    {
        if (Tabs.SelectedTab is { IsExternal: true } tab)
        {
            await tab.ImportAsync();
        }
    });

    /// <summary>Records where keyboard focus is (the view calls it when a region gets focus).</summary>
    public void ReportFocus(FocusRegion region) => FocusedRegion = region;

    /// <summary>Moves focus one region forward (<paramref name="step"/> = 1) or back (-1).</summary>
    public FocusRegion CycleRegion(int step)
    {
        var regions = RegionOrder.Where(region => region != FocusRegion.Sidebar || IsSidebarOpen).ToList();
        var index = regions.IndexOf(FocusedRegion);
        var next = regions[index < 0 ? 0 : ((index + Math.Sign(step == 0 ? 1 : step)) % regions.Count + regions.Count) % regions.Count];
        RequestFocus(next);
        return next;
    }

    private void RequestFocus(FocusRegion region)
    {
        FocusedRegion = region;
        FocusRequested?.Invoke(this, new FocusRequestedEventArgs(region));
    }

    private void SetSidebarOpen(bool open)
    {
        IsSidebarOpen = open;
        if (!IsSidebarOverlay && _sidebarPreferredOpen != open)
        {
            _sidebarPreferredOpen = open;
            ScheduleUiStateSave();
        }
    }

    private void RevealInExplorer(Core.Ids.NodeId nodeId)
    {
        SetSidebarOpen(true);
        if (Explorer.Reveal(nodeId))
        {
            RequestFocus(FocusRegion.Sidebar);
        }
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabsViewModel.HasTabs))
        {
            OnPropertyChanged(nameof(IsEmptyStateVisible));
            if (!Tabs.HasTabs)
            {
                FindReplace.Close();
            }
        }
    }

    // ------------------------------------------------------------------ UI state

    /// <summary>The window layout as it would be saved now.</summary>
    public UiState BuildUiState() => _uiState with
    {
        ExpandedFolderIds = Explorer.ExpandedFolderIds,
        SidebarWidth = SidebarWidth,
        IsSidebarCollapsed = !_sidebarPreferredOpen,
        SortOrder = Explorer.SortOrder,
        SelectedNodeId = Explorer.SelectedNodeId,
    };

    /// <summary>Saves the layout now (after any save already running).</summary>
    public Task SaveUiStateAsync()
    {
        if (!_context.IsReady)
        {
            return Task.CompletedTask;
        }

        _uiStateSaver.Cancel();
        var state = BuildUiState();
        _uiState = state;
        var sessions = _context.Core.Sessions;
        var previous = _lastUiStateSave;
        _lastUiStateSave = SaveAfterAsync(previous, sessions, state);
        return _lastUiStateSave;

        static async Task SaveAfterAsync(Task previous, SessionService sessions, UiState state)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reported by that save.
            }

            await sessions.SaveUiStateAsync(state).ConfigureAwait(false);
        }
    }

    private void ScheduleUiStateSave()
    {
        if (_context.IsReady && !_shutDown)
        {
            _uiStateSaver.Schedule();
        }
    }

    // ------------------------------------------------------------------ startup notices

    private void ShowDatabaseRecovery(StartupRecovery recovery)
    {
        if (!recovery.Recovered)
        {
            return;
        }

        var folder = recovery.CorruptDatabasePath is { } corrupt ? Path.GetDirectoryName(corrupt) : null;
        Func<Task>? showFiles = folder is null
            ? null
            : () =>
            {
                _context.Platform.Shell.OpenUri(new Uri(folder));
                return Task.CompletedTask;
            };
        var message = recovery.Kind == StartupRecoveryKind.RestoredFromBackup
            ? "NimPad found damage in its database and restored the latest backup. Changes made after that backup may be missing."
            : "NimPad found damage in its database and had no backup to restore, so it started a new one. The damaged file was kept.";
        Banners.Show(
            DatabaseRecoveryBannerKey,
            recovery.Kind == StartupRecoveryKind.RestoredFromBackup ? BannerKind.Warning : BannerKind.Error,
            message,
            "Show files",
            showFiles,
            canDismiss: true,
            removeOnAction: false);
    }

    // The recovered files' tabs start from their recovered text (TabsViewModel.SetRecoveredFiles); Review opens the
    // files that are not open yet and shows the last one.
    private void ShowRecoveredFiles(IReadOnlyList<Core.Files.RecoveryBuffer> buffers)
    {
        if (buffers.Count == 0)
        {
            return;
        }

        var message = buffers.Count == 1
            ? "NimPad recovered unsaved changes to 1 file"
            : $"NimPad recovered unsaved changes to {buffers.Count.ToString(_context.Culture)} files";
        Banners.Show(
            RecoveredFilesBannerKey,
            BannerKind.Info,
            message,
            "Review",
            () =>
            {
                TabViewModel? last = null;
                foreach (var buffer in buffers)
                {
                    last = Tabs.OpenExternal(buffer.Path, activate: false);
                }

                if (last is not null)
                {
                    Tabs.SelectedTab = last;
                }

                return Task.CompletedTask;
            });
    }

    private void RegisterGlobalHotkey()
    {
        var hotkey = _context.Settings.Current.GlobalHotkey;
        if (string.IsNullOrWhiteSpace(hotkey) || !HotkeyGesture.TryParse(hotkey, out var gesture, out _))
        {
            return;
        }

        string? error;
        bool registered;
        try
        {
            registered = _context.Platform.GlobalHotkey.TryRegister(gesture.ToString(), out error);
        }
        catch (Exception ex)
        {
            registered = false;
            error = ex.Message;
        }

        if (!registered)
        {
            Trace.TraceWarning($"The global shortcut {gesture} could not be registered: {error}");
            Banners.Show(
                HotkeyBannerKey,
                BannerKind.Info,
                $"{gesture} is used by another app, so it doesn't open NimPad. You can pick another shortcut in Settings.",
                "Open settings",
                () =>
                {
                    Settings.SelectedPage = SettingsPage.General;
                    OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                });
        }
    }

    private static async Task LoadQuietlyAsync(TabViewModel tab)
    {
        try
        {
            await tab.LoadAsync();
        }
        catch (Exception)
        {
            // Shown through the tab's LoadState and LoadError.
        }
    }

    private void ReportBackgroundFailure(Exception error)
    {
        if (_shutDown)
        {
            return;
        }

        // Saves of the session or layout are retried by the next change; only tell the user about lasting problems.
        if (error is IOException or UnauthorizedAccessException)
        {
            Toasts.ShowError("Something couldn't be saved on this computer. " + error.Message);
        }
    }
}
