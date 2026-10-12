using System.Diagnostics;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Presentation.Platform;

namespace NimPad.Presentation.Workspace;

/// <summary>One node change as the model applied it.</summary>
/// <param name="NodeId">The node.</param>
/// <param name="Kind">What Core reported.</param>
/// <param name="Before">The live node before the change, or <see langword="null"/> when it was not live.</param>
/// <param name="After">The live node after the change, or <see langword="null"/> when it is no longer live (trashed or deleted).</param>
public readonly record struct WorkspaceModelChange(NodeId NodeId, NodeChangeKind Kind, NodeInfo? Before, NodeInfo? After);

/// <summary>A batch of applied changes, raised on the UI thread.</summary>
public sealed class WorkspaceModelChangedEventArgs(IReadOnlyList<WorkspaceModelChange> changes, bool isReset) : EventArgs
{
    /// <summary>The changes, in order (a node can appear more than once).</summary>
    public IReadOnlyList<WorkspaceModelChange> Changes { get; } = changes;

    /// <summary>True when the whole model was reloaded; views rebuild from scratch.</summary>
    public bool IsReset { get; } = isReset;
}

/// <summary>
/// The in-memory copy of the workspace tree (metadata and preview lines of every live node) that the explorer, the
/// tabs and the breadcrumbs read. It is loaded once at startup and then kept current from Core's change
/// notifications: each notification's nodes are re-read in one query off the UI thread and applied on the UI thread,
/// in commit order. View models may also apply the result of their own writes right away with <see cref="Upsert"/>.
/// </summary>
public sealed class WorkspaceModel : IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<NodeId, NodeInfo> _live = [];
    private readonly Dictionary<NodeId, List<NodeInfo>> _children = [];
    private readonly List<NodeInfo> _rootChildren = [];
    private readonly List<NodeInfo> _pinned = [];
    private readonly Dictionary<NodeId, string> _previews = [];
    private readonly Queue<WorkspaceChangedEventArgs> _pending = [];
    private CoreServices? _core;
    private bool _processing;
    private bool _disposed;
    private TaskCompletionSource? _idle;

    /// <summary>Creates an empty model.</summary>
    public WorkspaceModel(IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <summary>The workspace shown (Phase 1 has one).</summary>
    public Core.Domain.Workspace? Workspace { get; private set; }

    /// <summary>How many live nodes the workspace has.</summary>
    public int Count => _live.Count;

    /// <summary>Raised on the UI thread after changes were applied.</summary>
    public event EventHandler<WorkspaceModelChangedEventArgs>? Changed;

    /// <summary>True while notifications are queued or being applied.</summary>
    internal bool IsProcessing => _processing || _pending.Count > 0;

    /// <summary>Completes when every notification received so far has been applied (tests and shutdown).</summary>
    internal Task WhenAppliedAsync() => _idle?.Task ?? Task.CompletedTask;

    /// <summary>Loads every live node and preview of <paramref name="workspace"/> and starts following changes.</summary>
    internal async Task LoadAsync(CoreServices core, Core.Domain.Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(workspace);

        // Follow changes before reading, so nothing committed during the load is missed: notifications that arrive
        // meanwhile wait in the queue and are applied (re-read) once the load is in.
        _core = core;
        core.Notifier.Changed += OnNotified;
        var nodesTask = core.Nodes.GetAllNodesAsync(workspace.Id, cancellationToken);
        var previewsTask = core.Documents.GetPreviewsAsync(workspace.Id, cancellationToken);
        var nodes = await nodesTask;
        var previews = await previewsTask;
        Workspace = workspace;
        Load(nodes, previews);
        StartProcessing();
    }

    /// <summary>Replaces the whole model (startup, or a reload after an error).</summary>
    internal void Load(IReadOnlyList<NodeInfo> nodes, IReadOnlyDictionary<NodeId, string> previews)
    {
        _live.Clear();
        _children.Clear();
        _rootChildren.Clear();
        _pinned.Clear();
        _previews.Clear();
        foreach (var node in nodes)
        {
            if (!node.IsTrashed)
            {
                _live[node.Id] = node;
            }
        }

        foreach (var node in _live.Values)
        {
            SiblingsOf(node.ParentId, create: true)!.Add(node);
            if (node.IsPinned)
            {
                _pinned.Add(node);
            }
        }

        _rootChildren.Sort(SiblingComparer.Instance);
        foreach (var list in _children.Values)
        {
            list.Sort(SiblingComparer.Instance);
        }

        _pinned.Sort(PinComparer.Instance);
        foreach (var (id, preview) in previews)
        {
            _previews[id] = preview;
        }
    }

    /// <summary>The live node, or <see langword="null"/>.</summary>
    public NodeInfo? Get(NodeId id) => _live.GetValueOrDefault(id);

    /// <summary>True when the node is live (not trashed, not deleted).</summary>
    public bool Contains(NodeId id) => _live.ContainsKey(id);

    /// <summary>The live children of a folder (or of the root) in manual (sibling) order.</summary>
    public IReadOnlyList<NodeInfo> GetChildren(NodeId? parentId) => SiblingsOf(parentId, create: false) ?? (IReadOnlyList<NodeInfo>)[];

    /// <summary>True when the folder has live children.</summary>
    public bool HasChildren(NodeId folderId) => _children.TryGetValue(folderId, out var list) && list.Count > 0;

    /// <summary>The live pinned nodes in Pinned-section order.</summary>
    public IReadOnlyList<NodeInfo> Pinned => _pinned;

    /// <summary>Every live node (unordered).</summary>
    public IEnumerable<NodeInfo> All => _live.Values;

    /// <summary>The preview line of a document, or an empty string.</summary>
    public string GetPreview(NodeId id) => _previews.GetValueOrDefault(id) ?? string.Empty;

    /// <summary>The node and its ancestors, from the workspace root down to the node; empty when it is not live.</summary>
    public IReadOnlyList<NodeInfo> GetPath(NodeId id)
    {
        var path = new List<NodeInfo>();
        var current = Get(id);
        while (current is not null && path.Count <= _live.Count)
        {
            path.Add(current);
            current = current.ParentId is { } parent ? Get(parent) : null;
        }

        path.Reverse();
        return path;
    }

    /// <summary>True when <paramref name="ancestorId"/> is <paramref name="nodeId"/> or one of its live ancestors.</summary>
    public bool IsSelfOrAncestor(NodeId ancestorId, NodeId nodeId)
    {
        var current = Get(nodeId);
        var steps = 0;
        while (current is not null && steps++ <= _live.Count)
        {
            if (current.Id == ancestorId)
            {
                return true;
            }

            current = current.ParentId is { } parent ? Get(parent) : null;
        }

        return false;
    }

    /// <summary>
    /// Applies the result of a write made by a view model right away (Core's notification follows and re-applies
    /// the same state). Raises <see cref="Changed"/>.
    /// </summary>
    /// <param name="node">The node as the write returned it.</param>
    /// <param name="kind">What happened.</param>
    /// <param name="preview">The document's preview line, when known.</param>
    public void Upsert(NodeInfo node, NodeChangeKind kind, string? preview = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var change = Apply(node.Id, kind, node, preview);
        Changed?.Invoke(this, new WorkspaceModelChangedEventArgs([change], false));
    }

    /// <summary>Stops following changes.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (_core is { } core)
        {
            core.Notifier.Changed -= OnNotified;
        }
    }

    // ------------------------------------------------------------------ notifications

    private void OnNotified(object? sender, WorkspaceChangedEventArgs args)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed || (Workspace is not null && args.WorkspaceId != Workspace.Id))
            {
                return;
            }

            _pending.Enqueue(args);
            StartProcessing();
        });
    }

    // Starts applying queued notifications (once the model is loaded).
    private void StartProcessing()
    {
        if (Workspace is not { } workspace || _processing || _disposed)
        {
            return;
        }

        // Drop what was queued for other workspaces while the model was loading.
        var queued = _pending.Where(args => args.WorkspaceId == workspace.Id).ToList();
        _pending.Clear();
        foreach (var args in queued)
        {
            _pending.Enqueue(args);
        }

        if (_pending.Count == 0)
        {
            return;
        }

        _processing = true;
        _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ProcessAsync();
    }

    // Runs on the UI thread; applies queued notifications one at a time, in order.
    private async Task ProcessAsync()
    {
        try
        {
            while (_pending.Count > 0 && !_disposed && _core is { } core)
            {
                var args = _pending.Dequeue();
                try
                {
                    var ids = args.Changes.Select(change => change.NodeId).Distinct().ToList();
                    var previewIds = args.Changes
                        .Where(change => change.Kind is NodeChangeKind.ContentChanged or NodeChangeKind.Created or NodeChangeKind.Restored)
                        .Select(change => change.NodeId)
                        .Distinct()
                        .ToList();
                    var nodesTask = core.Nodes.GetNodesAsync(ids);
                    var previewsTask = core.Documents.GetPreviewsAsync(previewIds);
                    var nodes = (await nodesTask).ToDictionary(node => node.Id);
                    var previews = await previewsTask;
                    if (_disposed)
                    {
                        return;
                    }

                    var applied = new List<WorkspaceModelChange>(args.Changes.Count);
                    var seen = new HashSet<NodeId>();
                    foreach (var change in args.Changes)
                    {
                        // The re-read state is the newest; apply it once per node, but report every change.
                        var current = nodes.GetValueOrDefault(change.NodeId);
                        if (seen.Add(change.NodeId))
                        {
                            applied.Add(Apply(change.NodeId, change.Kind, current, previews.GetValueOrDefault(change.NodeId)));
                        }
                        else
                        {
                            var node = Get(change.NodeId);
                            applied.Add(new WorkspaceModelChange(change.NodeId, change.Kind, node, node));
                        }
                    }

                    Changed?.Invoke(this, new WorkspaceModelChangedEventArgs(applied, false));
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"Applying a workspace change failed; reloading the tree: {ex}");
                    await ReloadAsync(core);
                }
            }
        }
        finally
        {
            _processing = false;
            if (_pending.Count == 0)
            {
                var idle = _idle;
                _idle = null;
                idle?.TrySetResult();
            }
            else if (!_disposed)
            {
                _processing = true;
                _ = ProcessAsync();
            }
        }
    }

    private async Task ReloadAsync(CoreServices core)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        try
        {
            var nodes = await core.Nodes.GetAllNodesAsync(workspace.Id);
            var previews = await core.Documents.GetPreviewsAsync(workspace.Id);
            Load(nodes, previews);
            Changed?.Invoke(this, new WorkspaceModelChangedEventArgs([], true));
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Reloading the workspace tree failed: {ex}");
        }
    }

    // ------------------------------------------------------------------ mutation

    // Brings one node to its current state: live (inserted or updated) or gone (trashed, deleted or missing).
    private WorkspaceModelChange Apply(NodeId id, NodeChangeKind kind, NodeInfo? current, string? preview)
    {
        var before = Get(id);
        if (preview is not null)
        {
            _previews[id] = preview;
        }

        if (current is null || current.IsTrashed || (Workspace is { } workspace && current.WorkspaceId != workspace.Id))
        {
            if (before is not null)
            {
                RemoveSubtree(before);
            }

            if (current is null)
            {
                _previews.Remove(id);
            }

            return new WorkspaceModelChange(id, kind, before, null);
        }

        if (before is not null)
        {
            if (before.ParentId != current.ParentId || before.Position != current.Position || before.Id != current.Id)
            {
                RemoveFromList(SiblingsOf(before.ParentId, create: false), id);
                InsertSorted(SiblingsOf(current.ParentId, create: true)!, current, SiblingComparer.Instance);
            }
            else
            {
                ReplaceInList(SiblingsOf(current.ParentId, create: false), current);
            }

            if (before.IsPinned)
            {
                RemoveFromList(_pinned, id);
            }
        }
        else
        {
            InsertSorted(SiblingsOf(current.ParentId, create: true)!, current, SiblingComparer.Instance);
        }

        if (current.IsPinned)
        {
            InsertSorted(_pinned, current, PinComparer.Instance);
        }

        _live[id] = current;
        return new WorkspaceModelChange(id, kind, before, current);
    }

    private void RemoveSubtree(NodeInfo node)
    {
        var stack = new Stack<NodeInfo>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!_live.Remove(current.Id))
            {
                continue;
            }

            RemoveFromList(SiblingsOf(current.ParentId, create: false), current.Id);
            if (current.IsPinned)
            {
                RemoveFromList(_pinned, current.Id);
            }

            if (_children.Remove(current.Id, out var children))
            {
                foreach (var child in children)
                {
                    stack.Push(child);
                }
            }
        }
    }

    private List<NodeInfo>? SiblingsOf(NodeId? parentId, bool create)
    {
        if (parentId is not { } parent)
        {
            return _rootChildren;
        }

        if (_children.TryGetValue(parent, out var list))
        {
            return list;
        }

        if (!create)
        {
            return null;
        }

        list = [];
        _children[parent] = list;
        return list;
    }

    private static void InsertSorted(List<NodeInfo> list, NodeInfo node, IComparer<NodeInfo> comparer)
    {
        var index = list.BinarySearch(node, comparer);
        list.Insert(index >= 0 ? index : ~index, node);
    }

    private static void RemoveFromList(List<NodeInfo>? list, NodeId id)
    {
        if (list is null)
        {
            return;
        }

        var index = list.FindIndex(node => node.Id == id);
        if (index >= 0)
        {
            list.RemoveAt(index);
        }
    }

    private static void ReplaceInList(List<NodeInfo>? list, NodeInfo node)
    {
        if (list is null)
        {
            return;
        }

        var index = list.FindIndex(existing => existing.Id == node.Id);
        if (index >= 0)
        {
            list[index] = node;
        }
    }

    private sealed class PinComparer : IComparer<NodeInfo>
    {
        public static PinComparer Instance { get; } = new();

        public int Compare(NodeInfo? x, NodeInfo? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var byPin = string.CompareOrdinal(x.PinPosition, y.PinPosition);
            return byPin != 0 ? byPin : string.CompareOrdinal(x.Id.ToString(), y.Id.ToString());
        }
    }
}
