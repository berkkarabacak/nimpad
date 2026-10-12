using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Text;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Workspace;

namespace NimPad.Presentation.Explorer;

/// <summary>A request to scroll a row into view (after Reveal or a new folder).</summary>
public sealed class ScrollIntoViewRequestedEventArgs(ExplorerRowViewModel row) : EventArgs
{
    /// <summary>The row.</summary>
    public ExplorerRowViewModel Row { get; } = row;
}

/// <summary>
/// The notes sidebar (tree-first, see docs/design/README.md): one flattened row list for a single virtualized
/// ListBox, with section headers, the Pinned and Recent sections and the workspace tree; a search mode and a Trash mode.
/// </summary>
/// <remarks>
/// <para>Rows change one at a time (insert, remove, move), never as a range, because WPF's CollectionView rejects range
/// changes; existing row objects are reused and only the rows that really moved are moved (one move per moved row), so
/// scroll position and selection survive live updates, in the tree, the search results and the Trash alike.</para>
/// <para>Commands take an optional row parameter (the row of a context menu, or the row with keyboard focus); without
/// one they act on <see cref="SelectedRow"/>.</para>
/// </remarks>
public sealed partial class ExplorerViewModel : ObservableObject
{
    /// <summary>How many notes the Recent section lists.</summary>
    public const int RecentCount = 8;

    /// <summary>The debounce of the search field.</summary>
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>The Trash notice under its header.</summary>
    public const string TrashNotice = "Notes are deleted forever after 30 days.";

    /// <summary>The Undo shortcut of the "Moved to Trash" toast (while the sidebar has keyboard focus).</summary>
    public const string UndoTrashShortcut = "Ctrl+Z";

    // Above this many moves a reconcile replaces the list instead (a real reorder, such as a new sort order).
    private const int MaxMovesPerReconcile = 128;

    private readonly ViewModelContext _context;
    private readonly TabsViewModel _tabs;
    private readonly Debouncer _searchDebouncer;
    private readonly HashSet<NodeId> _expanded = [];
    private readonly Dictionary<NodeId, ExplorerNodeRowViewModel>[] _nodeRows = [[], [], []];
    private readonly ExplorerHeaderRowViewModel?[] _headers = new ExplorerHeaderRowViewModel?[3];
    private readonly Dictionary<NodeId, ExplorerSearchResultRowViewModel> _searchRows = [];
    private readonly Dictionary<NodeId, ExplorerTrashRowViewModel> _trashRows = [];
    private long _buildGeneration;
    private DateOnly _today;
    private CancellationTokenSource? _searchCancellation;
    private long _trashGeneration;
    private long _trashCountGeneration;
    private LastTrash? _lastTrash;
    private NodeId? _selectedNodeId;
    private int _lastSelectedIndex = -1;
    private ExplorerSection _selectedSection = ExplorerSection.Workspace;
    private bool _clearingSearch;

    internal ExplorerViewModel(ViewModelContext context, TabsViewModel tabs)
    {
        _context = context;
        _tabs = tabs;
        _searchDebouncer = new Debouncer(context.Clock, context.Dispatcher, SearchDelay, () => _context.Work.Track(RunSearchAsync(), "search notes"));
        context.Model.Changed += OnModelChanged;
    }

    /// <summary>The rows of the current mode, top to bottom.</summary>
    public ObservableCollection<ExplorerRowViewModel> Rows { get; } = [];

    /// <summary>The selected row (bind the ListBox's SelectedItem two-way).</summary>
    [ObservableProperty]
    public partial ExplorerRowViewModel? SelectedRow { get; set; }

    /// <summary>The selected node (kept across modes and restarts).</summary>
    public NodeId? SelectedNodeId => _selectedNodeId;

    /// <summary>What the list shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrashMode), nameof(IsSearchMode), nameof(IsTreeMode))]
    public partial ExplorerMode Mode { get; private set; }

    /// <summary>True in the tree mode.</summary>
    public bool IsTreeMode => Mode == ExplorerMode.Tree;

    /// <summary>True while search results show.</summary>
    public bool IsSearchMode => Mode == ExplorerMode.Search;

    /// <summary>True while the Trash shows (header "Trash", Empty trash, the 30-day notice).</summary>
    public bool IsTrashMode => Mode == ExplorerMode.Trash;

    /// <summary>The search field ("Search all notes"); non-empty text switches to search results (120 ms debounce).</summary>
    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    /// <summary>True while a search runs.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>The line above the results: <c>3 notes match “beta”</c> (empty when there are none).</summary>
    [ObservableProperty]
    public partial string SearchSummaryText { get; private set; } = string.Empty;

    /// <summary>True when there is something to search (the field is disabled in an empty workspace).</summary>
    [ObservableProperty]
    public partial bool IsSearchEnabled { get; private set; }

    /// <summary>The tree's sort order (Manual, Last edited, Name A to Z; remembered).</summary>
    [ObservableProperty]
    public partial ExplorerSortOrder SortOrder { get; private set; } = ExplorerSortOrder.Manual;

    /// <summary>The sort menu.</summary>
    public IReadOnlyList<ExplorerSortOption> SortOptions { get; } =
    [
        new(ExplorerSortOrder.Manual, "Manual"),
        new(ExplorerSortOrder.LastEdited, "Last edited"),
        new(ExplorerSortOrder.Name, "Name, A to Z"),
    ];

    /// <summary>The empty state to show instead of rows, or <see langword="null"/>.</summary>
    [ObservableProperty]
    public partial ExplorerEmptyState? EmptyState { get; private set; }

    /// <summary>
    /// How many items the Trash lists (the sidebar's Trash entry shows it, for example "Trash 3"). Kept current in
    /// every mode.
    /// </summary>
    [ObservableProperty]
    public partial int TrashCount { get; private set; }

    /// <summary>True while the Trash has items (Empty trash is enabled); kept current in every mode.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EmptyTrashCommand))]
    public partial bool HasTrashItems { get; private set; }

    /// <summary>True until the tree is loaded (rows keep their final height while loading).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>The folders currently expanded (for the UI state).</summary>
    public IReadOnlyList<NodeId> ExpandedFolderIds =>
        _expanded.Where(id => _context.Model.Get(id) is { IsFolder: true }).OrderBy(id => id.ToString(), StringComparer.Ordinal).ToList();

    /// <summary>Raised to scroll a row into view.</summary>
    public event EventHandler<ScrollIntoViewRequestedEventArgs>? ScrollIntoViewRequested;

    /// <summary>
    /// Raised when focus should go to the editor: a second Esc in the search field, or a new note at the root (the
    /// user types in it next, as after Ctrl+N).
    /// </summary>
    public event EventHandler? FocusEditorRequested;

    /// <summary>Raised when an inline rename starts; the view focuses the row's text box.</summary>
    public event EventHandler<ExplorerNodeRowViewModel>? RenameStarted;

    /// <summary>Raised when the expansion, the selection or the sort order changed (the shell saves the UI state).</summary>
    public event EventHandler? StateChanged;

    // ------------------------------------------------------------------ modes

    /// <summary>Shows the Trash.</summary>
    [RelayCommand]
    public void ShowTrash()
    {
        _searchDebouncer.Cancel();
        _searchCancellation?.Cancel();
        if (SearchQuery.Length > 0)
        {
            _clearingSearch = true;
            SearchQuery = string.Empty;
            _clearingSearch = false;
        }

        SearchSummaryText = string.Empty;
        IsSearching = false;
        EmptyState = null;
        Mode = ExplorerMode.Trash;
        _searchRows.Clear();
        ReplaceRows([]);
        _context.Work.Track(LoadTrashAsync(), "load the Trash");
    }

    /// <summary>Shows the tree (leaves search and the Trash).</summary>
    [RelayCommand]
    public void ShowTree()
    {
        _searchDebouncer.Cancel();
        _searchCancellation?.Cancel();
        if (SearchQuery.Length > 0)
        {
            _clearingSearch = true;
            SearchQuery = string.Empty;
            _clearingSearch = false;
        }

        SearchSummaryText = string.Empty;
        IsSearching = false;
        var wasTree = Mode == ExplorerMode.Tree;
        Mode = ExplorerMode.Tree;
        if (!wasTree)
        {
            _searchRows.Clear();
            _trashRows.Clear();
            ReplaceRows([]);
        }

        RebuildTree();
    }

    /// <summary>Esc in the search field: clears the search; when it is already empty, asks to focus the editor.</summary>
    [RelayCommand]
    public void ClearSearch()
    {
        if (SearchQuery.Length == 0 && Mode != ExplorerMode.Search)
        {
            FocusEditorRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ShowTree();
    }

    /// <summary>Changes the sort order of the tree.</summary>
    [RelayCommand]
    public void SetSortOrder(ExplorerSortOrder order)
    {
        if (!Enum.IsDefined(order) || order == SortOrder)
        {
            return;
        }

        SortOrder = order;
        if (Mode == ExplorerMode.Tree)
        {
            RebuildTree();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (_clearingSearch)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            _searchDebouncer.Cancel();
            _searchCancellation?.Cancel();
            if (Mode == ExplorerMode.Search)
            {
                ShowTree();
            }

            return;
        }

        _searchDebouncer.Schedule();
    }

    // ------------------------------------------------------------------ tree building

    /// <summary>Applies the remembered UI state (expansion, selection, sort order) and builds the rows.</summary>
    internal void Load(UiState state)
    {
        _expanded.Clear();
        foreach (var id in state.ExpandedFolderIds)
        {
            _expanded.Add(id);
        }

        SortOrder = Enum.IsDefined(state.SortOrder) ? state.SortOrder : ExplorerSortOrder.Manual;
        _selectedNodeId = state.SelectedNodeId;
        IsLoading = false;
        RebuildTree();
        if (_context.IsReady)
        {
            _context.Work.Track(RefreshTrashCountAsync(), "count the Trash");
        }
        if (_selectedNodeId is { } selected && FindTreeRow(selected) is { } row)
        {
            SelectedRow = row;
        }
    }

    /// <summary>Rebuilds the tree rows from the workspace model (reusing row objects).</summary>
    internal void RebuildTree()
    {
        if (Mode != ExplorerMode.Tree)
        {
            return;
        }

        var desired = BuildTreeRows();
        Reconcile(desired);
        IsSearchEnabled = _context.Model.Count > 0;
        EmptyState = _context.Model.Count == 0 && !IsLoading
            ? new ExplorerEmptyState(
                ExplorerEmptyStateKind.NoNotes,
                "No notes yet",
                "Start typing in any tab. It is saved automatically on this computer.",
                "New note",
                "Ctrl+N",
                NewNoteCommand)
            : null;
        RestoreSelection();
    }

    /// <summary>The rows of the tree mode as they should be now (headers, Pinned, Recent, the expanded tree).</summary>
    internal List<ExplorerRowViewModel> BuildTreeRows()
    {
        var model = _context.Model;
        var rows = new List<ExplorerRowViewModel>(model.Count + 24);
        _buildGeneration++;
        _today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_context.Clock.GetUtcNow(), _context.Clock.LocalTimeZone).DateTime);
        if (model.Count == 0)
        {
            PruneRows();
            return rows;
        }

        if (model.Pinned.Count > 0)
        {
            rows.Add(Header(ExplorerSection.Pinned, "PINNED"));
            foreach (var node in model.Pinned)
            {
                rows.Add(NodeRow(ExplorerSection.Pinned, node, 0));
            }
        }

        var recent = MostRecentlyEdited(model.All, RecentCount);
        if (recent.Count > 0)
        {
            rows.Add(Header(ExplorerSection.Recent, "RECENT"));
            foreach (var node in recent)
            {
                rows.Add(NodeRow(ExplorerSection.Recent, node, 0));
            }
        }

        var workspaceName = model.Workspace?.Name ?? "My Workspace";
        rows.Add(Header(ExplorerSection.Workspace, workspaceName.ToUpper(_context.Culture)));

        // Depth-first over expanded folders, without recursion (trees can be deep).
        var stack = new Stack<(NodeInfo Node, int Depth)>();
        PushChildren(stack, null, 0);
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            rows.Add(NodeRow(ExplorerSection.Workspace, node, depth));
            if (node.IsFolder && _expanded.Contains(node.Id))
            {
                PushChildren(stack, node.Id, depth + 1);
            }
        }

        PruneRows();
        return rows;
    }

    // The `count` documents edited last (ties: lower ID first), in one pass instead of sorting every note.
    private static List<NodeInfo> MostRecentlyEdited(IEnumerable<NodeInfo> nodes, int count)
    {
        var top = new List<NodeInfo>(count + 1);
        foreach (var node in nodes)
        {
            if (!node.IsDocument)
            {
                continue;
            }

            var index = top.Count;
            while (index > 0 && IsMoreRecent(node, top[index - 1]))
            {
                index--;
            }

            if (index < count)
            {
                top.Insert(index, node);
                if (top.Count > count)
                {
                    top.RemoveAt(count);
                }
            }
        }

        return top;

        static bool IsMoreRecent(NodeInfo a, NodeInfo b)
        {
            var byTime = (a.ContentUpdatedAt ?? a.UpdatedAt).CompareTo(b.ContentUpdatedAt ?? b.UpdatedAt);
            return byTime != 0 ? byTime > 0 : a.Id.Value.CompareTo(b.Id.Value) < 0;
        }
    }

    private void PushChildren(Stack<(NodeInfo Node, int Depth)> stack, NodeId? parentId, int depth)
    {
        var children = SortChildren(_context.Model.GetChildren(parentId));
        for (var i = children.Count - 1; i >= 0; i--)
        {
            stack.Push((children[i], depth));
        }
    }

    /// <summary>Siblings in the current sort order (folders first in the non-manual orders).</summary>
    private IReadOnlyList<NodeInfo> SortChildren(IReadOnlyList<NodeInfo> children)
    {
        if (SortOrder == ExplorerSortOrder.Manual || children.Count < 2)
        {
            return children;
        }

        var culture = _context.Culture;
        IOrderedEnumerable<NodeInfo> ordered = children.OrderBy(node => node.IsFolder ? 0 : 1);
        ordered = SortOrder switch
        {
            ExplorerSortOrder.LastEdited => ordered.ThenByDescending(node => node.ContentUpdatedAt ?? node.UpdatedAt),
            ExplorerSortOrder.DateCreated => ordered.ThenByDescending(node => node.CreatedAt),
            _ => ordered.ThenBy(node => node.Name, Comparer<string>.Create((a, b) =>
                string.Compare(a, b, culture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering))),
        };
        return ordered.ThenBy(node => node.Id.ToString(), StringComparer.Ordinal).ToList();
    }

    private ExplorerHeaderRowViewModel Header(ExplorerSection section, string title)
    {
        var header = _headers[(int)section] ??= new ExplorerHeaderRowViewModel(section, title);
        header.Title = title;
        return header;
    }

    private ExplorerNodeRowViewModel NodeRow(ExplorerSection section, NodeInfo node, int depth)
    {
        var rows = _nodeRows[(int)section];
        if (!rows.TryGetValue(node.Id, out var row) || row.Kind != node.Kind)
        {
            row = new ExplorerNodeRowViewModel(section, node);
            rows[node.Id] = row;
        }

        row.Generation = _buildGeneration;

        var model = _context.Model;
        var isFolder = node.IsFolder;
        var timestamp = isFolder ? string.Empty : TimestampFor(row, node.ContentUpdatedAt ?? node.UpdatedAt);
        row.Update(
            node,
            depth,
            isFolder && _expanded.Contains(node.Id),
            isFolder && model.HasChildren(node.Id),
            timestamp,
            isFolder ? string.Empty : model.GetPreview(node.Id));
        return row;
    }

    // Formatting a timestamp is the costliest part of a row update; reuse the row's text while the time and the
    // current local day are unchanged.
    private string TimestampFor(ExplorerNodeRowViewModel row, DateTimeOffset when)
    {
        if (row.TimestampSource == when && row.TimestampDay == _today)
        {
            return row.TimestampText;
        }

        row.TimestampSource = when;
        row.TimestampDay = _today;
        return _context.FormatTimestamp(when);
    }

    // Forgets the rows of nodes that are no longer shown.
    private void PruneRows()
    {
        foreach (var rows in _nodeRows)
        {
            List<NodeId>? stale = null;
            foreach (var (id, row) in rows)
            {
                if (row.Generation != _buildGeneration)
                {
                    (stale ??= []).Add(id);
                }
            }

            if (stale is not null)
            {
                foreach (var id in stale)
                {
                    rows.Remove(id);
                }
            }
        }
    }

    // Brings Rows to `desired` with single-item changes, reusing row objects and moving only the rows that moved; a
    // real reorder (more than MaxMovesPerReconcile moves) replaces the list instead.
    private void Reconcile(IReadOnlyList<ExplorerRowViewModel> desired)
    {
        if (!CollectionReconciler.TryReconcile(Rows, desired, MaxMovesPerReconcile))
        {
            ReplaceRows(desired);
        }
    }

    private void ReplaceRows(IReadOnlyList<ExplorerRowViewModel> rows)
    {
        var selected = SelectedRow;
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        if (selected is not null && rows.Contains(selected))
        {
            SelectedRow = selected;
        }
    }

    private void RestoreSelection()
    {
        if (SelectedRow is { } selected && Rows.Contains(selected))
        {
            return;
        }

        if (_selectedNodeId is not { } nodeId)
        {
            SelectedRow = null;
            return;
        }

        // The selected node's row went away. Collapsed or moved: its row in the same section, else its nearest visible
        // row in the tree (itself or an ancestor), else any of its rows.
        if (FindRow(_selectedSection, nodeId) is { } same)
        {
            SelectedRow = same;
            return;
        }

        for (var node = _context.Model.Get(nodeId); node is not null; node = node.ParentId is { } parentId ? _context.Model.Get(parentId) : null)
        {
            if (FindRow(ExplorerSection.Workspace, node.Id) is { } visible)
            {
                SelectedRow = visible;
                return;
            }
        }

        if (FindTreeRow(nodeId) is { } elsewhere)
        {
            SelectedRow = elsewhere;
            return;
        }

        if (!_context.Model.Contains(nodeId) && _lastSelectedIndex >= 0 && Rows.Count > 0)
        {
            // Trashed or deleted: the row that took its place keeps the keyboard where it was.
            var index = Math.Min(_lastSelectedIndex, Rows.Count - 1);
            var next = Rows[index] is ExplorerHeaderRowViewModel && index > 0 ? Rows[index - 1] : Rows[index];
            SelectedRow = next is ExplorerHeaderRowViewModel ? null : next;
            return;
        }

        SelectedRow = null;
    }

    // A row of the current tree build (rows of the latest build are exactly the rows shown in the tree mode).
    private ExplorerNodeRowViewModel? FindRow(ExplorerSection section, NodeId nodeId) =>
        Mode == ExplorerMode.Tree && _nodeRows[(int)section].TryGetValue(nodeId, out var row) && row.Generation == _buildGeneration
            ? row
            : null;

    private ExplorerNodeRowViewModel? FindTreeRow(NodeId nodeId) =>
        FindRow(ExplorerSection.Workspace, nodeId) ?? FindRow(ExplorerSection.Pinned, nodeId) ?? FindRow(ExplorerSection.Recent, nodeId);

    partial void OnSelectedRowChanged(ExplorerRowViewModel? oldValue, ExplorerRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        ExportCommand.NotifyCanExecuteChanged();
        if (newValue is null && oldValue is not null && !Rows.Contains(oldValue))
        {
            // The row left the list (the ListBox clears its selection); keep remembering the node so the selection
            // can follow it (RestoreSelection).
            return;
        }

        if (newValue is not null)
        {
            _lastSelectedIndex = Rows.IndexOf(newValue);
            _selectedSection = newValue is ExplorerNodeRowViewModel node ? node.Section : ExplorerSection.Workspace;
        }

        var nodeId = newValue?.NodeId;
        if (nodeId != _selectedNodeId)
        {
            _selectedNodeId = nodeId;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnModelChanged(object? sender, WorkspaceModelChangedEventArgs e)
    {
        var trashChanged = e.IsReset || e.Changes.Any(change => change.Kind is NodeChangeKind.Trashed or NodeChangeKind.Restored or NodeChangeKind.Deleted);
        switch (Mode)
        {
            case ExplorerMode.Tree:
                RebuildTree();
                break;
            case ExplorerMode.Search:
                // Any edit can make a note match or stop matching; the results are reconciled row by row.
                _searchDebouncer.Schedule();
                break;
            case ExplorerMode.Trash when trashChanged || e.Changes.Any(change => _trashRows.ContainsKey(change.NodeId)):
                // Edits of live notes (autosaves) cannot change the Trash.
                _context.Work.Track(LoadTrashAsync(), "reload the Trash");
                break;
        }

        if (trashChanged && Mode != ExplorerMode.Trash)
        {
            _context.Work.Track(RefreshTrashCountAsync(), "count the Trash");
        }
    }

    // ------------------------------------------------------------------ expand and collapse

    /// <summary>Expands or collapses a folder (chevron click, Enter on a folder).</summary>
    [RelayCommand]
    public void ToggleExpand(ExplorerRowViewModel? row)
    {
        if ((row ?? SelectedRow) is ExplorerNodeRowViewModel { IsFolder: true } folder)
        {
            SetExpanded(folder.Id, !_expanded.Contains(folder.Id));
        }
    }

    /// <summary>Right arrow: expands a collapsed folder, or moves to the first child of an expanded one.</summary>
    [RelayCommand]
    public void ExpandRow(ExplorerRowViewModel? row)
    {
        if ((row ?? SelectedRow) is not ExplorerNodeRowViewModel { IsFolder: true, Section: ExplorerSection.Workspace } folder)
        {
            return;
        }

        if (!_expanded.Contains(folder.Id))
        {
            if (_context.Model.HasChildren(folder.Id))
            {
                SetExpanded(folder.Id, true);
            }

            return;
        }

        var index = Rows.IndexOf(folder);
        if (index >= 0 && index + 1 < Rows.Count && Rows[index + 1] is ExplorerNodeRowViewModel child && child.Depth > folder.Depth)
        {
            SelectedRow = child;
            ScrollIntoViewRequested?.Invoke(this, new ScrollIntoViewRequestedEventArgs(child));
        }
    }

    /// <summary>Left arrow: collapses an expanded folder, or moves to the parent folder.</summary>
    [RelayCommand]
    public void CollapseRow(ExplorerRowViewModel? row)
    {
        if ((row ?? SelectedRow) is not ExplorerNodeRowViewModel { Section: ExplorerSection.Workspace } node)
        {
            return;
        }

        if (node.IsFolder && _expanded.Contains(node.Id))
        {
            SetExpanded(node.Id, false);
            return;
        }

        if (node.Node.ParentId is { } parentId && FindTreeRow(parentId) is { Section: ExplorerSection.Workspace } parent)
        {
            SelectedRow = parent;
            ScrollIntoViewRequested?.Invoke(this, new ScrollIntoViewRequestedEventArgs(parent));
        }
    }

    /// <summary>Expands or collapses a folder; rows are inserted or removed one at a time.</summary>
    public void SetExpanded(NodeId folderId, bool expanded)
    {
        var changed = expanded ? _expanded.Add(folderId) : _expanded.Remove(folderId);
        if (!changed)
        {
            return;
        }

        if (Mode == ExplorerMode.Tree)
        {
            RebuildTree();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows a node in the tree: leaves search or the Trash, expands its ancestors, selects it and scrolls to it.</summary>
    /// <returns><see langword="true"/> when the node is live and now selected.</returns>
    public bool Reveal(NodeId nodeId)
    {
        if (!_context.Model.Contains(nodeId))
        {
            return false;
        }

        if (Mode != ExplorerMode.Tree)
        {
            ShowTree();
        }

        var changed = false;
        foreach (var ancestor in _context.Model.GetPath(nodeId).SkipLast(1))
        {
            changed |= _expanded.Add(ancestor.Id);
        }

        if (changed)
        {
            RebuildTree();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        if (FindRow(ExplorerSection.Workspace, nodeId) is { } row)
        {
            SelectedRow = row;
            ScrollIntoViewRequested?.Invoke(this, new ScrollIntoViewRequestedEventArgs(row));
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ commands on nodes

    /// <summary>
    /// Enter: opens a note (or switches to its tab), toggles a folder, opens a search result, restores a Trash item.
    /// </summary>
    [RelayCommand]
    public Task OpenAsync(ExplorerRowViewModel? row) => _context.GuardAsync(async () =>
    {
        switch (row ?? SelectedRow)
        {
            case ExplorerNodeRowViewModel { IsFolder: true } folder:
                if (folder.Section == ExplorerSection.Workspace)
                {
                    ToggleExpand(folder);
                }
                else
                {
                    Reveal(folder.Id);
                }

                break;
            case ExplorerNodeRowViewModel note:
                await _tabs.OpenAsync(note.Id);
                break;
            case ExplorerSearchResultRowViewModel { Kind: NodeKind.Folder } folderResult:
                Reveal(folderResult.Id);
                break;
            case ExplorerSearchResultRowViewModel result:
                await _tabs.OpenAsync(result.Id);
                break;
            case ExplorerTrashRowViewModel trashed:
                await RestoreAsync(trashed);
                break;
        }
    });

    /// <summary>Creates a note inside the folder of the row (the root without one), opens it and starts renaming it.</summary>
    [RelayCommand]
    public Task NewNoteAsync(object? parent) => _context.GuardAsync(async () =>
    {
        var parentId = ResolveParent(parent);
        var node = await _context.Core.Nodes.CreateDocumentAsync(_context.Workspace.Id, parentId);
        _context.Model.Upsert(node, NodeChangeKind.Created, string.Empty);
        _tabs.OpenCreated(node);
        if (parentId is not null)
        {
            RevealAndRename(node.Id);
        }
        else
        {
            // Like Ctrl+N (and the empty state's "New note Ctrl+N"): the user types in the new note next.
            Reveal(node.Id);
            FocusEditorRequested?.Invoke(this, EventArgs.Empty);
        }
    });

    /// <summary>Creates a folder inside the folder of the row (the root without one) and starts renaming it.</summary>
    [RelayCommand]
    public Task NewFolderAsync(object? parent) => _context.GuardAsync(async () =>
    {
        var parentId = ResolveParent(parent);
        var siblings = _context.Model.GetChildren(parentId).Select(node => node.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = "New folder";
        for (var n = 2; siblings.Contains(name); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"New folder {n}");
        }

        var node = await _context.Core.Nodes.CreateFolderAsync(_context.Workspace.Id, parentId, name);
        _context.Model.Upsert(node, NodeChangeKind.Created);
        RevealAndRename(node.Id);
    });

    /// <summary>Starts the inline rename of a node row (F2).</summary>
    [RelayCommand]
    public void Rename(ExplorerRowViewModel? row)
    {
        if ((row ?? SelectedRow) is not ExplorerNodeRowViewModel node)
        {
            return;
        }

        foreach (var other in Rows.OfType<ExplorerNodeRowViewModel>().Where(other => other.IsRenaming && other != node))
        {
            other.IsRenaming = false;
        }

        node.RenameText = node.Name;
        node.RenameError = null;
        node.IsRenaming = true;
        SelectedRow = node;
        RenameStarted?.Invoke(this, node);
    }

    /// <summary>Renames the node to the row's text (Enter); a rejected name keeps the box open with its reason.</summary>
    [RelayCommand]
    public Task CommitRenameAsync(ExplorerRowViewModel? row) => _context.GuardAsync(async () =>
    {
        if ((row ?? SelectedRow) is not ExplorerNodeRowViewModel { IsRenaming: true } node)
        {
            return;
        }

        if (string.Equals(node.RenameText.Trim(), node.Name, StringComparison.Ordinal))
        {
            node.IsRenaming = false;
            return;
        }

        try
        {
            var renamed = await _context.Core.Nodes.RenameAsync(node.Id, node.RenameText);
            node.IsRenaming = false;
            node.RenameError = null;
            _context.Model.Upsert(renamed, NodeChangeKind.Renamed);
        }
        catch (InvalidNodeNameException ex)
        {
            node.RenameError = ex.Reason;
        }
        catch (NodeOperationException ex)
        {
            node.RenameError = ex.Message;
        }
    });

    /// <summary>Leaves the inline rename (Esc).</summary>
    [RelayCommand]
    public void CancelRename(ExplorerRowViewModel? row)
    {
        if ((row ?? SelectedRow) is ExplorerNodeRowViewModel node)
        {
            node.IsRenaming = false;
            node.RenameError = null;
        }
    }

    /// <summary>
    /// Delete: moves a node to the Trash (a toast offers Undo); in the Trash, deletes the item forever after a
    /// confirmation.
    /// </summary>
    [RelayCommand]
    public Task DeleteAsync(ExplorerRowViewModel? row) => _context.GuardAsync(async () =>
    {
        switch (row ?? SelectedRow)
        {
            case ExplorerTrashRowViewModel trashed:
                await DeleteForeverAsync(trashed);
                break;
            case { NodeId: { } nodeId } target and not ExplorerHeaderRowViewModel:
                await MoveToTrashAsync(nodeId, target);
                break;
        }
    });

    private async Task MoveToTrashAsync(NodeId nodeId, ExplorerRowViewModel row)
    {
        var node = _context.Model.Get(nodeId);
        if (node is null)
        {
            return;
        }

        if (SelectedRow == row)
        {
            _lastSelectedIndex = Rows.IndexOf(row);
        }

        var batches = await _context.Core.Nodes.TrashAsync([nodeId]);
        var trash = new LastTrash(batches);
        var toast = _context.Toasts.Show(
            "Moved " + DisplayText.Quote(node.Name) + " to Trash",
            ToastKind.Neutral,
            "Undo",
            UndoTrashShortcut,
            () => UndoTrashCoreAsync(trash));
        trash.Toast = toast;
        _lastTrash = trash;
        toast.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToastViewModel.IsDismissed) && ReferenceEquals(_lastTrash, trash))
            {
                // The keyboard Undo lasts as long as its toast.
                _lastTrash = null;
                UndoTrashCommand.NotifyCanExecuteChanged();
            }
        };
        UndoTrashCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Undoes the last "Moved … to Trash" while its toast shows (Ctrl+Z while the sidebar has keyboard focus; the
    /// toast's Undo does the same).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoTrash))]
    public Task UndoTrashAsync() => _context.GuardAsync(async () =>
    {
        if (_lastTrash is { } trash)
        {
            await UndoTrashCoreAsync(trash);
        }
    });

    private bool CanUndoTrash() => _lastTrash is not null;

    private async Task UndoTrashCoreAsync(LastTrash trash)
    {
        if (trash.IsUndone)
        {
            return;
        }

        trash.IsUndone = true;
        if (ReferenceEquals(_lastTrash, trash))
        {
            _lastTrash = null;
            UndoTrashCommand.NotifyCanExecuteChanged();
        }

        if (trash.Toast is { } toast)
        {
            _context.Toasts.Dismiss(toast);
        }

        await _context.Core.Nodes.RestoreBatchAsync(trash.Batches);
    }

    private sealed class LastTrash(IReadOnlyList<TrashBatchId> batches)
    {
        public IReadOnlyList<TrashBatchId> Batches { get; } = batches;

        public ToastViewModel? Toast { get; set; }

        public bool IsUndone { get; set; }
    }

    /// <summary>Restores a Trash item (with everything trashed together with it) and opens a restored note in a tab.</summary>
    [RelayCommand]
    public Task RestoreAsync(ExplorerRowViewModel? row) => _context.GuardAsync(async () =>
    {
        if ((row ?? SelectedRow) is not ExplorerTrashRowViewModel trashed)
        {
            return;
        }

        var restored = await _context.Core.Nodes.RestoreAsync(trashed.Id);
        _context.Model.Upsert(restored, NodeChangeKind.Restored);
        if (restored.IsDocument)
        {
            await _tabs.OpenAsync(restored.Id);
        }

        _context.Toasts.Show("Restored " + DisplayText.Quote(restored.Name), ToastKind.Success);
    });

    /// <summary>Deletes a Trash item forever, after a confirmation.</summary>
    [RelayCommand]
    public Task DeleteForeverAsync(ExplorerRowViewModel? row) => _context.GuardAsync(async () =>
    {
        if ((row ?? SelectedRow) is not ExplorerTrashRowViewModel trashed)
        {
            return;
        }

        var confirmed = await _context.Platform.Dialogs.ConfirmAsync(new ConfirmRequest(
            $"Delete {DisplayText.Quote(trashed.Title)} forever?",
            trashed.DescendantCount > 0
                ? "It and everything in it will be gone for good. This can't be undone."
                : "It will be gone for good. This can't be undone.",
            "Delete forever",
            IsDestructive: true));
        if (!confirmed)
        {
            return;
        }

        await _context.Core.Nodes.DeleteForeverAsync([trashed.Id]);
        await LoadTrashAsync();
    });

    /// <summary>Deletes everything in the Trash forever, after a confirmation (disabled when the Trash is empty).</summary>
    [RelayCommand(CanExecute = nameof(HasTrashItems))]
    public Task EmptyTrashAsync() => _context.GuardAsync(async () =>
    {
        var count = Rows.OfType<ExplorerTrashRowViewModel>().Count();
        var confirmed = await _context.Platform.Dialogs.ConfirmAsync(new ConfirmRequest(
            "Empty the Trash?",
            count == 1
                ? "The item in the Trash will be deleted forever. This can't be undone."
                : $"All {count.ToString(_context.Culture)} items in the Trash will be deleted forever. This can't be undone.",
            "Empty trash",
            IsDestructive: true));
        if (!confirmed)
        {
            return;
        }

        await _context.Core.Nodes.EmptyTrashAsync(_context.Workspace.Id);
        await LoadTrashAsync();
    });

    /// <summary>Pins or unpins the node.</summary>
    [RelayCommand]
    public Task TogglePinAsync(ExplorerRowViewModel? row) => SetPinnedAsync(row, pin: null);

    /// <summary>Pins the node (it appears in the Pinned section).</summary>
    [RelayCommand]
    public Task PinAsync(ExplorerRowViewModel? row) => SetPinnedAsync(row, pin: true);

    /// <summary>Unpins the node.</summary>
    [RelayCommand]
    public Task UnpinAsync(ExplorerRowViewModel? row) => SetPinnedAsync(row, pin: false);

    private Task SetPinnedAsync(ExplorerRowViewModel? row, bool? pin) => _context.GuardAsync(async () =>
    {
        if ((row ?? SelectedRow)?.NodeId is not { } nodeId || _context.Model.Get(nodeId) is not { } node)
        {
            return;
        }

        var target = pin ?? !node.IsPinned;
        if (target == node.IsPinned)
        {
            return;
        }

        var updated = target ? await _context.Core.Nodes.PinAsync(nodeId) : await _context.Core.Nodes.UnpinAsync(nodeId);
        _context.Model.Upsert(updated, NodeChangeKind.PinChanged);
    });

    /// <summary>Copies the node (with its contents) next to itself and selects the copy.</summary>
    [RelayCommand]
    public Task DuplicateAsync(ExplorerRowViewModel? row) => _context.GuardAsync(async () =>
    {
        if ((row ?? SelectedRow)?.NodeId is not { } nodeId || !_context.Model.Contains(nodeId))
        {
            return;
        }

        if (_context.Core.Autosave.IsDirty(nodeId))
        {
            await _context.Core.Autosave.FlushAsync(nodeId);
        }

        var copy = await _context.Core.Nodes.DuplicateAsync(nodeId);
        _context.Model.Upsert(copy, NodeChangeKind.Created, _context.Model.GetPreview(nodeId));
        Reveal(copy.Id);
    });

    /// <summary>Copies the node's link (<c>nimpad://note/&lt;id&gt;</c>) to the clipboard.</summary>
    [RelayCommand]
    public void CopyLink(ExplorerRowViewModel? row)
    {
        if ((row ?? SelectedRow)?.NodeId is not { } nodeId)
        {
            return;
        }

        try
        {
            _context.Platform.Clipboard.SetText(LinkParser.CreateUri(nodeId));
        }
        catch (Exception ex)
        {
            // Windows' clipboard can be held by another app (CLIPBRD_E_CANT_OPEN); a command must not throw.
            System.Diagnostics.Trace.TraceWarning($"Copying a link failed: {ex}");
            _context.Toasts.ShowError("Couldn't copy the link. Try again.");
            return;
        }

        _context.Toasts.Show("Link copied");
    }

    /// <summary>Exports a note to a file the user picks (disabled for folders and headers).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    public Task ExportAsync(ExplorerRowViewModel? row) => _context.GuardAsync(async () =>
    {
        if ((row ?? SelectedRow)?.NodeId is { } nodeId && _context.Model.Get(nodeId) is { IsDocument: true })
        {
            await NoteExport.ExportAsync(_context, nodeId);
        }
    });

    private bool CanExport(ExplorerRowViewModel? row) =>
        (row ?? SelectedRow)?.NodeId is { } nodeId && _context.Model.Get(nodeId) is { IsDocument: true };

    private NodeId? ResolveParent(object? parent) => parent switch
    {
        NodeId id when _context.Model.Get(id) is { } node => node.IsFolder ? node.Id : node.ParentId,
        ExplorerNodeRowViewModel row when _context.Model.Get(row.Id) is { } node => node.IsFolder ? node.Id : node.ParentId,
        _ => null,
    };

    private void RevealAndRename(NodeId nodeId)
    {
        if (Reveal(nodeId) && SelectedRow is ExplorerNodeRowViewModel row)
        {
            Rename(row);
        }
    }

    // ------------------------------------------------------------------ drag and drop

    /// <summary>
    /// Whether <paramref name="dragged"/> can be dropped at <paramref name="position"/> of <paramref name="target"/>:
    /// not into itself or a descendant, not inside a note, not onto headers other than the workspace header (the
    /// root), and reordering within a folder only in the Manual sort order (moving into another folder always works).
    /// Pinned rows can be reordered among themselves.
    /// </summary>
    public bool CanDrop(IReadOnlyList<NodeId> dragged, ExplorerRowViewModel? target, DropPosition position) =>
        ResolveDrop(dragged, target, position) is not null;

    /// <summary>Moves (or reorders pinned) <paramref name="dragged"/> as <see cref="CanDrop"/> allowed.</summary>
    /// <returns><see langword="true"/> when something moved.</returns>
    public async Task<bool> DropAsync(IReadOnlyList<NodeId> dragged, ExplorerRowViewModel? target, DropPosition position)
    {
        if (ResolveDrop(dragged, target, position) is not { } drop)
        {
            return false;
        }

        try
        {
            if (drop.ReorderPinned)
            {
                var anchor = drop.Position;
                foreach (var id in drop.Nodes)
                {
                    var moved = await _context.Core.Nodes.ReorderPinnedAsync(id, anchor);
                    _context.Model.Upsert(moved, NodeChangeKind.PinChanged);
                    anchor = InsertPosition.After(id);
                }

                return true;
            }

            await _context.Core.Nodes.MoveAsync(drop.Nodes, drop.ParentId, drop.Position);
            if (drop.ParentId is { } parentId && !_expanded.Contains(parentId))
            {
                SetExpanded(parentId, true);
            }

            return true;
        }
        catch (NodeOperationException ex)
        {
            _context.Toasts.ShowError(ex.Message);
            return false;
        }
    }

    private sealed record DropPlan(IReadOnlyList<NodeId> Nodes, NodeId? ParentId, InsertPosition Position, bool ReorderPinned);

    private DropPlan? ResolveDrop(IReadOnlyList<NodeId> dragged, ExplorerRowViewModel? target, DropPosition position)
    {
        if (dragged is null || dragged.Count == 0 || target is null || Mode != ExplorerMode.Tree)
        {
            return null;
        }

        var model = _context.Model;
        var nodes = dragged.Distinct().Select(model.Get).ToList();
        if (nodes.Any(node => node is null))
        {
            return null;
        }

        var ids = nodes.Select(node => node!.Id).ToList();
        NodeId? parentId;
        InsertPosition insert;
        switch (target)
        {
            case ExplorerHeaderRowViewModel { IsWorkspaceRoot: true }:
                parentId = null;
                insert = position == DropPosition.Before ? InsertPosition.Start : InsertPosition.End;
                break;
            case ExplorerNodeRowViewModel { Section: ExplorerSection.Pinned } pinned:
                if (position == DropPosition.Inside || ids.Contains(pinned.Id) || nodes.Any(node => !node!.IsPinned))
                {
                    return null;
                }

                return new DropPlan(ids, null, position == DropPosition.Before ? InsertPosition.Before(pinned.Id) : InsertPosition.After(pinned.Id), true);
            case ExplorerNodeRowViewModel { Section: ExplorerSection.Workspace } row:
                if (ids.Contains(row.Id))
                {
                    return null;
                }

                if (position == DropPosition.Inside)
                {
                    if (!row.IsFolder)
                    {
                        return null;
                    }

                    parentId = row.Id;
                    insert = InsertPosition.End;
                }
                else if (position == DropPosition.After && row.IsFolder && row.IsExpanded && row.HasChildren)
                {
                    // Just below an open folder is its first child.
                    parentId = row.Id;
                    insert = InsertPosition.Start;
                }
                else
                {
                    parentId = row.Node.ParentId;
                    insert = position == DropPosition.Before ? InsertPosition.Before(row.Id) : InsertPosition.After(row.Id);
                }

                break;
            default:
                return null;
        }

        if (parentId is { } destination && nodes.Any(node => node!.IsFolder && model.IsSelfOrAncestor(node.Id, destination)))
        {
            return null;
        }

        var isReorder = nodes.All(node => node!.ParentId == parentId);
        if (isReorder && SortOrder != ExplorerSortOrder.Manual)
        {
            return null;
        }

        return new DropPlan(ids, parentId, SortOrder == ExplorerSortOrder.Manual ? insert : InsertPosition.End, false);
    }

    // ------------------------------------------------------------------ search and trash

    private async Task RunSearchAsync()
    {
        var query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            return;
        }

        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        IsSearching = true;
        try
        {
            var results = await _context.Core.Search.SearchAsync(_context.Workspace.Id, query, SearchService.DefaultLimit, cancellation.Token);
            if (cancellation.IsCancellationRequested || !string.Equals(SearchQuery.Trim(), query, StringComparison.Ordinal))
            {
                return;
            }

            var refresh = Mode == ExplorerMode.Search;
            Mode = ExplorerMode.Search;
            var rows = new List<ExplorerRowViewModel>(results.Count);
            var shown = new HashSet<NodeId>();
            foreach (var result in results)
            {
                if (!shown.Add(result.NodeId))
                {
                    continue;
                }

                var timestamp = result.ContentUpdatedAt is { } updated ? _context.FormatTimestamp(updated) : string.Empty;
                if (_searchRows.TryGetValue(result.NodeId, out var existing))
                {
                    existing.Update(result, timestamp);
                }
                else
                {
                    existing = new ExplorerSearchResultRowViewModel(result, timestamp);
                    _searchRows[result.NodeId] = existing;
                }

                rows.Add(existing);
            }

            foreach (var stale in _searchRows.Keys.Where(id => !shown.Contains(id)).ToList())
            {
                _searchRows.Remove(stale);
            }

            // Refreshed results (an edit, a rename) change row by row; leaving the tree replaces the list once.
            if (refresh)
            {
                Reconcile(rows);
            }
            else
            {
                ReplaceRows(rows);
            }
            var quoted = DisplayText.Quote(query);
            SearchSummaryText = rows.Count switch
            {
                0 => string.Empty,
                1 => $"1 note matches {quoted}",
                _ => $"{rows.Count.ToString(_context.Culture)} notes match {quoted}",
            };
            EmptyState = rows.Count == 0
                ? new ExplorerEmptyState(
                    ExplorerEmptyStateKind.NoResults,
                    $"No notes match {quoted}",
                    "Check the spelling or try fewer words.",
                    "Clear search",
                    null,
                    ClearSearchCommand)
                : null;
            SelectedRow = rows.FirstOrDefault(row => row.NodeId == _selectedNodeId);
        }
        catch (OperationCanceledException)
        {
            // A newer query replaced this one.
        }
        finally
        {
            if (_searchCancellation == cancellation)
            {
                IsSearching = false;
            }
        }
    }

    private async Task LoadTrashAsync()
    {
        var generation = ++_trashGeneration;
        var items = await _context.Core.Nodes.GetTrashAsync(_context.Workspace.Id);
        if (generation != _trashGeneration || Mode != ExplorerMode.Trash)
        {
            return;
        }

        var zone = _context.Clock.LocalTimeZone;
        var today = TimeZoneInfo.ConvertTime(_context.Clock.GetUtcNow(), zone).Date;
        var previews = await _context.Core.Documents.GetPreviewsAsync(items.Where(item => item.Node.IsDocument).Select(item => item.Node.Id).ToList());
        if (generation != _trashGeneration || Mode != ExplorerMode.Trash)
        {
            return;
        }

        var selectedId = SelectedRow?.NodeId ?? _selectedNodeId;
        var rows = new List<ExplorerRowViewModel>(items.Count);
        var shown = new HashSet<NodeId>();
        foreach (var item in items)
        {
            if (!shown.Add(item.Node.Id))
            {
                continue;
            }

            var preview = item.Node.IsFolder ? DescribeFolder(item.DescendantCount) : previews.GetValueOrDefault(item.Node.Id) ?? string.Empty;
            var status = TimeZoneInfo.ConvertTime(item.DeletedAt, zone).Date == today
                ? "Deleted today"
                : item.DaysLeft == 1 ? "1 day left" : $"{item.DaysLeft.ToString(_context.Culture)} days left";
            if (_trashRows.TryGetValue(item.Node.Id, out var existing))
            {
                existing.Update(item, preview, status);
            }
            else
            {
                existing = new ExplorerTrashRowViewModel(item, preview, status);
                _trashRows[item.Node.Id] = existing;
            }

            rows.Add(existing);
        }

        foreach (var stale in _trashRows.Keys.Where(id => !shown.Contains(id)).ToList())
        {
            _trashRows.Remove(stale);
        }

        // The first load after ShowTrash fills the empty list at once; reloads change row by row.
        if (Rows.Count == 0)
        {
            ReplaceRows(rows);
        }
        else
        {
            Reconcile(rows);
        }

        _trashCountGeneration++;
        TrashCount = rows.Count;
        EmptyState = rows.Count == 0
            ? new ExplorerEmptyState(
                ExplorerEmptyStateKind.TrashEmpty,
                "Trash is empty",
                "Deleted notes wait here for 30 days, so nothing is lost by accident.",
                null,
                null,
                null)
            : null;
        SelectedRow = rows.FirstOrDefault(row => row.NodeId == selectedId);
    }

    private async Task RefreshTrashCountAsync()
    {
        var generation = ++_trashCountGeneration;
        var items = await _context.Core.Nodes.GetTrashAsync(_context.Workspace.Id);
        if (generation == _trashCountGeneration)
        {
            TrashCount = items.Count;
        }
    }

    partial void OnTrashCountChanged(int value) => HasTrashItems = value > 0;

    private string DescribeFolder(int descendants) => descendants switch
    {
        0 => "Empty folder",
        1 => "Folder with 1 item",
        _ => $"Folder with {descendants.ToString(_context.Culture)} items",
    };
}
