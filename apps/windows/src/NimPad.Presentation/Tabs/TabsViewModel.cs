using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimPad.Core.Domain;
using NimPad.Core.Files;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Workspace;

namespace NimPad.Presentation.Tabs;

/// <summary>
/// The tab strip: open tabs in order (pinned tabs always first), the selected tab, the reopen-closed-tab stack and the
/// session that remembers all of it.
/// </summary>
/// <remarks>
/// Commands take an optional <see cref="TabViewModel"/> parameter (the tab of a context menu); without one they act on
/// <see cref="SelectedTab"/>. Any change of the tabs, the selection or a tab's view state saves the session 500 ms
/// later (<see cref="SessionSaveDelay"/>).
/// </remarks>
public sealed partial class TabsViewModel : ObservableObject
{
    /// <summary>The quiet period before a changed session is saved.</summary>
    public static readonly TimeSpan SessionSaveDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The toast group of "Closed …" toasts (a newer one replaces an older one, so Undo is unambiguous).</summary>
    public const string ClosedToastGroup = "tab-closed";

    private readonly ViewModelContext _context;
    private readonly Debouncer _sessionSaver;
    private readonly Dictionary<NodeId, SessionTab> _closedByTrash = [];
    private readonly Dictionary<string, RecoveryBuffer> _recoveredFiles = new(StringComparer.OrdinalIgnoreCase);
    private Task _lastSessionSave = Task.CompletedTask;
    private int _closedCount;
    private bool _restoring;
    private bool _attached;

    internal TabsViewModel(ViewModelContext context)
    {
        _context = context;
        _sessionSaver = new Debouncer(context.Clock, context.Dispatcher, SessionSaveDelay, () => _context.Work.Track(SaveSessionAsync(), "save the session"));
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(PinnedCount));
            RefreshCommandStates();
        };
    }

    /// <summary>The open tabs in strip order; pinned tabs come first.</summary>
    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    /// <summary>The selected (active) tab, or <see langword="null"/> when no tab is open.</summary>
    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    /// <summary>True when at least one tab is open (otherwise the window shows "No notes open").</summary>
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>How many tabs are pinned (they occupy indexes 0 to <c>PinnedCount - 1</c>).</summary>
    public int PinnedCount => Tabs.Count(tab => tab.IsPinned);

    /// <summary>True when the reopen stack has entries (Ctrl+Shift+T, the empty state's "Reopen closed tab").</summary>
    [ObservableProperty]
    public partial bool CanReopenClosedTab { get; private set; }

    /// <summary>Raised after a tab left the strip; the App releases its document.</summary>
    public event EventHandler<TabViewModel>? TabClosed;

    // ------------------------------------------------------------------ open

    /// <summary>Activates the note's tab, or opens the note in a new tab at the end.</summary>
    /// <param name="nodeId">The note.</param>
    /// <param name="activate">Whether to select the tab.</param>
    /// <returns>The tab, or <see langword="null"/> when the note is not a live document.</returns>
    public async Task<TabViewModel?> OpenAsync(NodeId nodeId, bool activate = true)
    {
        if (FindByNode(nodeId) is { } existing)
        {
            if (activate)
            {
                SelectedTab = existing;
            }

            return existing;
        }

        var node = _context.Model.Get(nodeId) ?? await _context.Core.Nodes.GetNodeAsync(nodeId);
        if (node is null || node.IsTrashed || !node.IsDocument)
        {
            _context.Toasts.ShowError("This note no longer exists.");
            return null;
        }

        if (FindByNode(nodeId) is { } opened)
        {
            // Opened while the node was read.
            if (activate)
            {
                SelectedTab = opened;
            }

            return opened;
        }

        var tab = CreateManagedTab(Guid.CreateVersion7(), node, createdThisSession: false, discardsWhenPristine: false);
        Tabs.Add(tab);
        if (activate)
        {
            SelectedTab = tab;
        }

        ScheduleSessionSave();
        return tab;
    }

    /// <summary>Activates the file's tab (the same path is the same tab), or opens the file in a new tab at the end.</summary>
    /// <param name="path">The file.</param>
    /// <param name="activate">Whether to select the tab.</param>
    /// <returns>The tab.</returns>
    public TabViewModel OpenExternal(string path, bool activate = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (FindByPath(fullPath) is { } existing)
        {
            if (activate)
            {
                SelectedTab = existing;
            }

            return existing;
        }

        var tab = CreateExternalTab(Guid.CreateVersion7(), fullPath);
        Tabs.Add(tab);
        if (activate)
        {
            SelectedTab = tab;
        }

        ScheduleSessionSave();
        return tab;
    }

    /// <summary>Creates the next <c>Untitled N</c> note at the workspace root and opens it at the end (Ctrl+N).</summary>
    /// <returns>The new tab.</returns>
    public async Task<TabViewModel> NewUntitledAsync()
    {
        var node = await _context.Core.Nodes.CreateDocumentAsync(_context.Workspace.Id, null);
        _context.Model.Upsert(node, NodeChangeKind.Created, string.Empty);
        var tab = CreateManagedTab(Guid.CreateVersion7(), node, createdThisSession: true, discardsWhenPristine: true);
        Tabs.Add(tab);
        SelectedTab = tab;
        ScheduleSessionSave();
        return tab;
    }

    /// <summary>Opens a note created by another command (explorer "New note") as a new tab.</summary>
    internal TabViewModel OpenCreated(NodeInfo node)
    {
        if (FindByNode(node.Id) is { } existing)
        {
            SelectedTab = existing;
            return existing;
        }

        var tab = CreateManagedTab(Guid.CreateVersion7(), node, createdThisSession: true, discardsWhenPristine: true);
        Tabs.Add(tab);
        SelectedTab = tab;
        ScheduleSessionSave();
        return tab;
    }

    // ------------------------------------------------------------------ close

    /// <summary>
    /// Closes a tab: a note's pending autosave is flushed first (a note created in this session and never touched is
    /// deleted for good, so it never litters the Trash); a file with unsaved changes asks Save / Don't save / Cancel.
    /// The tab goes onto the reopen stack and a "Closed “X”" toast offers Undo.
    /// </summary>
    /// <returns><see langword="true"/> when the tab closed.</returns>
    public Task<bool> CloseAsync(TabViewModel tab) => CloseCoreAsync(tab, showToast: true);

    /// <summary>Closes every tab except <paramref name="keep"/> and the pinned tabs.</summary>
    /// <returns>How many tabs closed.</returns>
    public Task<int> CloseOthersAsync(TabViewModel keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        return CloseManyAsync(Tabs.Where(tab => tab != keep && !tab.IsPinned).ToList());
    }

    /// <summary>Closes the unpinned tabs to the right of <paramref name="tab"/>.</summary>
    /// <returns>How many tabs closed.</returns>
    public Task<int> CloseToTheRightAsync(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        return index < 0 ? Task.FromResult(0) : CloseManyAsync(Tabs.Skip(index + 1).Where(other => !other.IsPinned).ToList());
    }

    /// <summary>Reopens the most recently closed tab at its old place with its caret and scroll position (Ctrl+Shift+T).</summary>
    /// <returns>The reopened (or already open) tab, or <see langword="null"/> when the stack is empty.</returns>
    public async Task<TabViewModel?> ReopenClosedAsync()
    {
        // A visible "Closed …" toast's Undo would now reopen a different, older tab.
        _context.Toasts.DismissGroup(ClosedToastGroup);
        while (true)
        {
            var entry = await _context.Core.Sessions.PopRecentlyClosedAsync();
            if (entry is null)
            {
                _closedCount = 0;
                CanReopenClosedTab = false;
                return null;
            }

            _closedCount = Math.Max(0, _closedCount - 1);
            CanReopenClosedTab = _closedCount > 0;
            var saved = entry.Tab;
            TabViewModel? tab = null;
            if (saved.Kind == SessionTabKind.Managed && saved.NodeId is { } nodeId)
            {
                if (FindByNode(nodeId) is { } open)
                {
                    SelectedTab = open;
                    return open;
                }

                if (_context.Model.Get(nodeId) is { IsDocument: true } node)
                {
                    tab = CreateManagedTab(saved.TabId, node, createdThisSession: false, discardsWhenPristine: false);
                }
            }
            else if (saved.FilePath is { } path)
            {
                if (FindByPath(path) is { } open)
                {
                    SelectedTab = open;
                    return open;
                }

                tab = CreateExternalTab(saved.TabId, path);
            }

            if (tab is null)
            {
                continue;
            }

            if (FindById(tab.TabId) is not null)
            {
                tab = Recreate(tab);
            }

            tab.IsPinned = saved.IsPinned;
            tab.RestoreViewState(saved);
            InsertAt(tab, saved.Order);
            SelectedTab = tab;
            ScheduleSessionSave();
            return tab;
        }
    }

    // ------------------------------------------------------------------ order

    /// <summary>Pins a tab: it moves to the end of the pinned group at the left edge.</summary>
    public void Pin(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.IsPinned || !Tabs.Contains(tab))
        {
            return;
        }

        var target = PinnedCount;
        tab.IsPinned = true;
        Tabs.Move(Tabs.IndexOf(tab), target);
        RefreshCommandStates();
        ScheduleSessionSave();
    }

    /// <summary>Unpins a tab: it moves to the first place after the pinned group.</summary>
    public void Unpin(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!tab.IsPinned || !Tabs.Contains(tab))
        {
            return;
        }

        tab.IsPinned = false;
        Tabs.Move(Tabs.IndexOf(tab), PinnedCount);
        RefreshCommandStates();
        ScheduleSessionSave();
    }

    /// <summary>Moves a tab to <paramref name="newIndex"/>, clamped so it never crosses the pinned boundary.</summary>
    /// <returns>The index the tab ended at.</returns>
    public int Move(TabViewModel tab, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return -1;
        }

        var pinned = PinnedCount;
        var (min, max) = tab.IsPinned ? (0, pinned - 1) : (pinned, Tabs.Count - 1);
        var target = Math.Clamp(newIndex, min, max);
        if (target != index)
        {
            Tabs.Move(index, target);
            ScheduleSessionSave();
        }

        return target;
    }

    /// <summary>Selects the next tab, wrapping around (Ctrl+Tab).</summary>
    [RelayCommand]
    public void NextTab() => SelectRelative(1);

    /// <summary>Selects the previous tab, wrapping around (Ctrl+Shift+Tab).</summary>
    [RelayCommand]
    public void PreviousTab() => SelectRelative(-1);

    /// <summary>Selects tab <paramref name="number"/> (1-based, Ctrl+1…8); 9 always selects the last tab (Ctrl+9).</summary>
    /// <returns><see langword="true"/> when a tab was selected.</returns>
    public bool GoTo(int number)
    {
        if (Tabs.Count == 0 || number < 1 || number > 9)
        {
            return false;
        }

        if (number == 9)
        {
            SelectedTab = Tabs[^1];
            return true;
        }

        if (number > Tabs.Count)
        {
            return false;
        }

        SelectedTab = Tabs[number - 1];
        return true;
    }

    /// <summary>Copies a managed note (stored text, after flushing) next to itself and opens the copy beside its tab (Ctrl+Shift+D).</summary>
    /// <returns>The copy's tab, or <see langword="null"/> for an external file.</returns>
    public async Task<TabViewModel?> DuplicateAsync(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!tab.IsManaged || tab.NodeId is not { } nodeId)
        {
            return null;
        }

        await _context.Core.Autosave.FlushAsync(nodeId);
        var copy = await _context.Core.Nodes.DuplicateAsync(nodeId);
        _context.Model.Upsert(copy, NodeChangeKind.Created, _context.Model.GetPreview(nodeId));
        var copyTab = CreateManagedTab(Guid.CreateVersion7(), copy, createdThisSession: false, discardsWhenPristine: false);
        var index = Tabs.IndexOf(tab);
        InsertAt(copyTab, index < 0 ? Tabs.Count : index + 1);
        SelectedTab = copyTab;
        ScheduleSessionSave();
        return copyTab;
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Closes the tab (Ctrl+W).</summary>
    [RelayCommand]
    private Task CloseTabAsync(TabViewModel? tab) => _context.GuardAsync(async () =>
    {
        if ((tab ?? SelectedTab) is { } target)
        {
            await CloseAsync(target);
        }
    });

    /// <summary>Closes the other unpinned tabs (disabled when there are none).</summary>
    [RelayCommand(CanExecute = nameof(CanCloseOtherTabs))]
    private Task CloseOtherTabsAsync(TabViewModel? tab) => _context.GuardAsync(async () =>
    {
        if ((tab ?? SelectedTab) is { } target)
        {
            await CloseOthersAsync(target);
        }
    });

    /// <summary>Closes the unpinned tabs to the right (disabled when there are none).</summary>
    [RelayCommand(CanExecute = nameof(CanCloseTabsToTheRight))]
    private Task CloseTabsToTheRightAsync(TabViewModel? tab) => _context.GuardAsync(async () =>
    {
        if ((tab ?? SelectedTab) is { } target)
        {
            await CloseToTheRightAsync(target);
        }
    });

    /// <summary>Reopens the last closed tab (Ctrl+Shift+T).</summary>
    [RelayCommand]
    private Task ReopenClosedTabAsync() => _context.GuardAsync(ReopenClosedAsync);

    /// <summary>Pins or unpins the tab.</summary>
    [RelayCommand]
    private void TogglePin(TabViewModel? tab)
    {
        if ((tab ?? SelectedTab) is not { } target)
        {
            return;
        }

        if (target.IsPinned)
        {
            Unpin(target);
        }
        else
        {
            Pin(target);
        }
    }

    /// <summary>Starts renaming the tab's note (F2 in the tab strip; disabled for files).</summary>
    [RelayCommand(CanExecute = nameof(IsManagedTab))]
    private void RenameTab(TabViewModel? tab) => (tab ?? SelectedTab)?.BeginRename();

    /// <summary>Duplicates the tab's note (Ctrl+Shift+D; disabled for files).</summary>
    [RelayCommand(CanExecute = nameof(IsManagedTab))]
    private Task DuplicateTabAsync(TabViewModel? tab) => _context.GuardAsync(async () =>
    {
        if ((tab ?? SelectedTab) is { } target)
        {
            await DuplicateAsync(target);
        }
    });

    /// <summary>Moves the tab one place left (Ctrl+Shift+Left).</summary>
    [RelayCommand]
    private void MoveTabLeft(TabViewModel? tab)
    {
        if ((tab ?? SelectedTab) is { } target)
        {
            Move(target, Tabs.IndexOf(target) - 1);
        }
    }

    /// <summary>Moves the tab one place right (Ctrl+Shift+Right).</summary>
    [RelayCommand]
    private void MoveTabRight(TabViewModel? tab)
    {
        if ((tab ?? SelectedTab) is { } target)
        {
            Move(target, Tabs.IndexOf(target) + 1);
        }
    }

    /// <summary>Selects tab N (parameter 1–9 as a number or text; Ctrl+1…9).</summary>
    [RelayCommand]
    private void GoToTab(object? number)
    {
        var value = number switch
        {
            int n => n,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => 0,
        };
        GoTo(value);
    }

    /// <summary>Imports the external file into the workspace (disabled for notes).</summary>
    [RelayCommand(CanExecute = nameof(IsExternalTab))]
    private Task ImportTabAsync(TabViewModel? tab) => _context.GuardAsync(async () =>
    {
        if ((tab ?? SelectedTab) is { IsExternal: true } target)
        {
            await target.ImportAsync();
        }
    });

    private bool CanCloseOtherTabs(TabViewModel? tab) =>
        (tab ?? SelectedTab) is { } target && Tabs.Any(other => other != target && !other.IsPinned);

    private bool CanCloseTabsToTheRight(TabViewModel? tab)
    {
        var index = (tab ?? SelectedTab) is { } target ? Tabs.IndexOf(target) : -1;
        if (index < 0)
        {
            return false;
        }

        for (var i = index + 1; i < Tabs.Count; i++)
        {
            if (!Tabs[i].IsPinned)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsManagedTab(TabViewModel? tab) => (tab ?? SelectedTab) is { IsManaged: true };

    private bool IsExternalTab(TabViewModel? tab) => (tab ?? SelectedTab) is { IsExternal: true };

    // The context menu's enabled states depend on the tabs, the pins and the selection.
    private void RefreshCommandStates()
    {
        CloseOtherTabsCommand.NotifyCanExecuteChanged();
        CloseTabsToTheRightCommand.NotifyCanExecuteChanged();
        RenameTabCommand.NotifyCanExecuteChanged();
        DuplicateTabCommand.NotifyCanExecuteChanged();
        ImportTabCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ session

    /// <summary>Saves the session now (tabs, order, pins, selection, view state), after any save already running.</summary>
    public Task SaveSessionAsync()
    {
        if (!_context.IsReady)
        {
            return Task.CompletedTask;
        }

        _sessionSaver.Cancel();
        var tabs = BuildSessionTabs();
        var sessions = _context.Core.Sessions;
        var previous = _lastSessionSave;
        _lastSessionSave = SaveAfterAsync(previous, sessions, tabs);
        return _lastSessionSave;

        static async Task SaveAfterAsync(Task previous, SessionService sessions, IReadOnlyList<SessionTab> tabs)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reported by that save.
            }

            await sessions.SaveTabsAsync(tabs).ConfigureAwait(false);
        }
    }

    /// <summary>The session as it would be saved now.</summary>
    public IReadOnlyList<SessionTab> BuildSessionTabs()
    {
        var tabs = new List<SessionTab>(Tabs.Count);
        for (var i = 0; i < Tabs.Count; i++)
        {
            var tab = Tabs[i];
            if (tab.IsExternal && tab.FilePath is null)
            {
                continue;
            }

            tabs.Add(tab.ToSessionTab(i, tab == SelectedTab));
        }

        return tabs;
    }

    internal void ScheduleSessionSave()
    {
        if (!_restoring && _context.IsReady)
        {
            _sessionSaver.Schedule();
        }
    }

    /// <summary>Recreates the saved tabs (titles known at once; only the active tab's text is loaded).</summary>
    internal void Restore(IReadOnlyList<SessionTab> saved)
    {
        _restoring = true;
        try
        {
            TabViewModel? active = null;
            var restored = new List<TabViewModel>();
            foreach (var entry in saved.OrderBy(tab => tab.Order))
            {
                TabViewModel? tab = null;
                if (entry.Kind == SessionTabKind.Managed && entry.NodeId is { } nodeId)
                {
                    if (FindByNode(nodeId) is null && restored.All(other => other.NodeId != nodeId)
                        && _context.Model.Get(nodeId) is { IsDocument: true } node)
                    {
                        // A tab carried over from the last session: an untouched untitled note in it is still litter.
                        tab = CreateManagedTab(entry.TabId, node, createdThisSession: false, discardsWhenPristine: true);
                    }
                }
                else if (entry.FilePath is { } path && restored.All(other => !SamePath(other.FilePath, path)))
                {
                    tab = CreateExternalTab(entry.TabId, path, entry.Title);
                }

                if (tab is null)
                {
                    continue;
                }

                tab.IsPinned = entry.IsPinned;
                tab.RestoreViewState(entry);
                restored.Add(tab);
                if (entry.IsActive)
                {
                    active = tab;
                }
            }

            // Pinned tabs always sit at the left edge, in their saved order.
            foreach (var tab in restored.Where(tab => tab.IsPinned).Concat(restored.Where(tab => !tab.IsPinned)))
            {
                Tabs.Add(tab);
            }

            SelectedTab = active ?? Tabs.FirstOrDefault();
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>
    /// Remembers the unsaved text of external files found at startup (recovery buffers). Call it before
    /// <see cref="Restore"/>: a tab opened for one of these files, then or later, starts from its recovered text.
    /// </summary>
    internal void SetRecoveredFiles(IReadOnlyList<RecoveryBuffer> buffers)
    {
        _recoveredFiles.Clear();
        foreach (var buffer in buffers)
        {
            _recoveredFiles[Path.GetFullPath(buffer.Path)] = buffer;
        }
    }

    /// <summary>Starts following autosave results and workspace changes, and reads the reopen stack.</summary>
    internal async Task AttachAsync()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        var core = _context.Core;
        core.Autosave.SaveSucceeded += OnAutosaveSucceeded;
        core.Autosave.SaveFailed += OnAutosaveFailed;
        _context.Model.Changed += OnModelChanged;
        _closedCount = (await core.Sessions.GetRecentlyClosedAsync()).Count;
        CanReopenClosedTab = _closedCount > 0;
    }

    /// <summary>Stops following Core (shutdown).</summary>
    internal void Detach()
    {
        if (!_attached || !_context.IsReady)
        {
            return;
        }

        _attached = false;
        _sessionSaver.Cancel();
        _context.Core.Autosave.SaveSucceeded -= OnAutosaveSucceeded;
        _context.Core.Autosave.SaveFailed -= OnAutosaveFailed;
        _context.Model.Changed -= OnModelChanged;
    }

    /// <summary>Writes every dirty external file's recovery buffer now.</summary>
    internal Task FlushRecoveryBuffersAsync() =>
        Task.WhenAll(Tabs.Where(tab => tab.IsExternal).Select(tab => tab.FlushRecoveryBufferAsync()));

    // ------------------------------------------------------------------ internals

    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
            if (oldValue.IsRenaming)
            {
                oldValue.CancelRename();
            }
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
            if (newValue.LoadState == TabLoadState.NotLoaded && _context.IsReady)
            {
                // Prefetch: the App awaits the same task when it shows the tab.
                _context.Work.Track(LoadQuietlyAsync(newValue), "load the active tab");
            }
        }

        RefreshCommandStates();
        ScheduleSessionSave();
    }

    private static async Task LoadQuietlyAsync(TabViewModel tab)
    {
        try
        {
            await tab.LoadAsync();
        }
        catch (Exception)
        {
            // Shown through LoadState / LoadError, or the tab closed because the load was cancelled.
        }
    }

    /// <summary>Raised when a tab's live document changed (the shell refreshes find results of the active tab).</summary>
    internal event EventHandler<TabViewModel>? BufferChanged;

    internal void OnTabBufferChanged(TabViewModel tab) => BufferChanged?.Invoke(this, tab);

    internal void OnLoadCancelled(TabViewModel tab)
    {
        if (Tabs.Contains(tab))
        {
            RemoveTab(tab);
            ScheduleSessionSave();
        }
    }

    private async Task<bool> CloseCoreAsync(TabViewModel tab, bool showToast)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!Tabs.Contains(tab) || tab.IsClosing)
        {
            return false;
        }

        tab.IsClosing = true;
        try
        {
            var discarded = false;
            if (tab.IsExternal && tab.IsDirty)
            {
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
                        break;
                    default:
                        return false;
                }
            }
            else if (tab.IsManaged && tab.NodeId is { } nodeId)
            {
                if (!await FlushNoteAsync(tab, nodeId))
                {
                    return false;
                }

                if (tab.DiscardsWhenPristine && tab.TextLength == 0)
                {
                    discarded = await _context.Core.Nodes.DiscardIfPristineAsync(nodeId);
                }

                _context.Core.Autosave.Remove(nodeId);
            }

            if (!Tabs.Contains(tab))
            {
                return false;
            }

            var index = Tabs.IndexOf(tab);
            var record = tab.ToSessionTab(index, isActive: false);
            RemoveTab(tab);
            if (!discarded)
            {
                _closedCount = Math.Min(_closedCount + 1, SessionService.RecentlyClosedCapacity);
                CanReopenClosedTab = true;
                _context.Work.Track(_context.Core.Sessions.PushRecentlyClosedAsync(record), "push a closed tab");
                if (showToast)
                {
                    _context.Toasts.Show(
                        "Closed " + DisplayText.Quote(tab.Title),
                        ToastKind.Neutral,
                        "Undo",
                        "Ctrl+Shift+T",
                        async () => await ReopenClosedAsync(),
                        ClosedToastGroup);
                }
            }

            ScheduleSessionSave();
            return true;
        }
        finally
        {
            tab.IsClosing = false;
        }
    }

    private async Task<bool> FlushNoteAsync(TabViewModel tab, NodeId nodeId)
    {
        try
        {
            await _context.Core.Autosave.FlushAsync(nodeId);
            return true;
        }
        catch (NodeNotFoundException)
        {
            // Deleted forever elsewhere: there is nothing left to save into.
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Closing {nodeId}: the final save failed: {ex.Message}");
            _context.Toasts.ShowError($"{DisplayText.Quote(tab.Title)} couldn't be saved, so its tab stays open.");
            return false;
        }
    }

    private async Task<int> CloseManyAsync(IReadOnlyList<TabViewModel> tabs)
    {
        var closed = 0;
        foreach (var tab in tabs)
        {
            if (!Tabs.Contains(tab))
            {
                continue;
            }

            if (!await CloseCoreAsync(tab, showToast: false))
            {
                if (tab.IsExternal && Tabs.Contains(tab))
                {
                    // The user cancelled a save prompt: stop here.
                    break;
                }

                continue;
            }

            closed++;
        }

        if (closed > 0)
        {
            var count = closed;
            // No shortcut on this toast: Ctrl+Shift+T reopens one tab, while Undo reopens all of them.
            _context.Toasts.Show(
                closed == 1 ? "Closed 1 tab" : $"Closed {closed.ToString(CultureInfo.CurrentCulture)} tabs",
                ToastKind.Neutral,
                "Undo",
                null,
                async () =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (await ReopenClosedAsync() is null)
                        {
                            break;
                        }
                    }
                },
                ClosedToastGroup);
        }

        return closed;
    }

    /// <summary>Closes a tab without saving, prompting or remembering it (its note was deleted forever, or a load was replaced).</summary>
    internal Task CloseWithoutSavingAsync(TabViewModel tab)
    {
        if (tab.NodeId is { } nodeId && _context.IsReady)
        {
            _context.Core.Autosave.Remove(nodeId);
        }

        if (Tabs.Contains(tab))
        {
            RemoveTab(tab);
            ScheduleSessionSave();
        }

        return Task.CompletedTask;
    }

    // A note went to the Trash: flush its last edits (saves to trashed notes are accepted), close the tab and remember
    // it, so restoring the note (Undo, or Restore in the Trash) reopens it where it was.
    private async Task CloseForTrashAsync(TabViewModel tab, NodeId nodeId)
    {
        if (tab.IsClosing)
        {
            return;
        }

        tab.IsClosing = true;
        try
        {
            try
            {
                await _context.Core.Autosave.FlushAsync(nodeId);
            }
            catch (NodeNotFoundException)
            {
                // Deleted forever meanwhile: there is nothing left to save into.
            }
            catch (Exception ex)
            {
                // The edits are only in this tab: keep it open (autosave keeps trying, and its banner offers the way
                // out) rather than lose them.
                Trace.TraceWarning($"Trashed note {nodeId}: the final save failed: {ex.Message}");
                _context.Toasts.ShowError($"{DisplayText.Quote(tab.Title)} is in the Trash, but its last changes couldn't be saved, so its tab stays open.");
                return;
            }

            _context.Core.Autosave.Remove(nodeId);
            if (Tabs.Contains(tab))
            {
                _closedByTrash[nodeId] = tab.ToSessionTab(Tabs.IndexOf(tab), tab == SelectedTab);
                RemoveTab(tab);
                ScheduleSessionSave();
            }
        }
        finally
        {
            tab.IsClosing = false;
        }
    }

    private void RemoveTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasSelected = tab == SelectedTab;
        if (wasSelected)
        {
            // The neighbor to the right takes over, or the one to the left at the end of the strip.
            SelectedTab = Tabs.Count > 1 ? Tabs[index + 1 < Tabs.Count ? index + 1 : index - 1] : null;
        }

        Tabs.RemoveAt(index);
        tab.IsSelected = false;
        tab.OnClosed();
        TabClosed?.Invoke(this, tab);
    }

    private void InsertAt(TabViewModel tab, int index)
    {
        var pinned = PinnedCount;
        var target = tab.IsPinned ? Math.Clamp(index, 0, pinned) : Math.Clamp(index, pinned, Tabs.Count);
        Tabs.Insert(target, tab);
    }

    private void SelectRelative(int step)
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        var index = SelectedTab is { } selected ? Tabs.IndexOf(selected) : -1;
        var next = index < 0 ? 0 : ((index + step) % Tabs.Count + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[next];
    }

    private TabViewModel CreateManagedTab(Guid tabId, NodeInfo node, bool createdThisSession, bool discardsWhenPristine)
    {
        var tab = new TabViewModel(this, _context, tabId, TabKind.Managed, node.Id, null, node.Name)
        {
            IsCreatedThisSession = createdThisSession,
            DiscardsWhenPristine = discardsWhenPristine,
        };
        tab.Format = node.Format ?? DocumentFormat.Plain;
        tab.ToolTip = BuildToolTip(node.Id, node.Name);
        return tab;
    }

    private TabViewModel CreateExternalTab(Guid tabId, string path, string? title = null)
    {
        var fileName = Path.GetFileName(path);
        var tab = new TabViewModel(this, _context, tabId, TabKind.External, null, path, string.IsNullOrEmpty(fileName) ? title ?? path : fileName);
        if (_recoveredFiles.Remove(Path.GetFullPath(path), out var recovery))
        {
            // Unsaved text from a crash or an earlier session: the tab opens with it (dirty), not with the disk text.
            tab.StartFromRecovery(recovery);
        }

        return tab;
    }

    private TabViewModel Recreate(TabViewModel tab) =>
        tab.IsManaged && _context.Model.Get(tab.NodeId!.Value) is { } node
            ? CreateManagedTab(Guid.CreateVersion7(), node, createdThisSession: false, discardsWhenPristine: false)
            : CreateExternalTab(Guid.CreateVersion7(), tab.FilePath!);

    private string BuildToolTip(NodeId nodeId, string fallback)
    {
        var path = _context.Model.GetPath(nodeId);
        if (path.Count == 0)
        {
            return fallback;
        }

        var names = path.Select(node => node.Name);
        return _context.Model.Workspace is { } workspace ? string.Join(" / ", names.Prepend(workspace.Name)) : string.Join(" / ", names);
    }

    private TabViewModel? FindByNode(NodeId nodeId) => Tabs.FirstOrDefault(tab => tab.NodeId == nodeId);

    private TabViewModel? FindByPath(string path) => Tabs.FirstOrDefault(tab => SamePath(tab.FilePath, path));

    private TabViewModel? FindById(Guid tabId) => Tabs.FirstOrDefault(tab => tab.TabId == tabId);

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private void OnAutosaveSucceeded(object? sender, AutosaveSucceededEventArgs args) =>
        _context.Dispatcher.Post(() => FindByNode(args.NodeId)?.OnAutosaveSucceeded(args));

    private void OnAutosaveFailed(object? sender, AutosaveFailedEventArgs args) =>
        _context.Dispatcher.Post(() => FindByNode(args.NodeId)?.OnAutosaveFailed(args));

    private void OnModelChanged(object? sender, WorkspaceModelChangedEventArgs args)
    {
        foreach (var change in args.Changes)
        {
            var tab = FindByNode(change.NodeId);
            if (change.After is { } after)
            {
                if (tab is not null)
                {
                    tab.Title = after.Name;
                    if (after.Format is { } format)
                    {
                        tab.Format = format;
                    }
                }
                else if (change.Kind == NodeChangeKind.Restored && _closedByTrash.Remove(change.NodeId, out var saved) && after.IsDocument)
                {
                    var reopened = CreateManagedTab(saved.TabId, after, createdThisSession: false, discardsWhenPristine: false);
                    if (FindById(reopened.TabId) is not null)
                    {
                        reopened = CreateManagedTab(Guid.CreateVersion7(), after, createdThisSession: false, discardsWhenPristine: false);
                    }

                    reopened.IsPinned = saved.IsPinned;
                    reopened.RestoreViewState(saved);
                    InsertAt(reopened, saved.Order);
                    SelectedTab = reopened;
                    ScheduleSessionSave();
                }
            }
            else if (tab is not null)
            {
                // The note is no longer live.
                if (change.Kind == NodeChangeKind.Deleted)
                {
                    _ = CloseWithoutSavingAsync(tab);
                }
                else
                {
                    _context.Work.Track(CloseForTrashAsync(tab, change.NodeId), "close the tab of a trashed note");
                }
            }
            else if (change.Kind == NodeChangeKind.Deleted)
            {
                _closedByTrash.Remove(change.NodeId);
            }
        }

        if (args.IsReset)
        {
            foreach (var tab in Tabs.Where(tab => tab.IsManaged && !_context.Model.Contains(tab.NodeId!.Value)).ToList())
            {
                _context.Work.Track(CloseForTrashAsync(tab, tab.NodeId!.Value), "close the tab of a missing note");
            }
        }

        foreach (var tab in Tabs.Where(tab => tab.IsManaged))
        {
            if (_context.Model.Get(tab.NodeId!.Value) is { } node)
            {
                tab.Title = node.Name;
                tab.ToolTip = BuildToolTip(node.Id, node.Name);
            }
        }
    }
}
