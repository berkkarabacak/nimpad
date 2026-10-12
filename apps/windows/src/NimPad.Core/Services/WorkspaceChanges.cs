using System.Diagnostics;
using System.Threading.Channels;
using NimPad.Core.Ids;
using NimPad.Core.Storage;

namespace NimPad.Core.Services;

/// <summary>What happened to a node.</summary>
public enum NodeChangeKind
{
    /// <summary>The node was created (including by copy or import).</summary>
    Created,

    /// <summary>The node's name changed.</summary>
    Renamed,

    /// <summary>The node's parent and/or position changed.</summary>
    Moved,

    /// <summary>The node was pinned, unpinned or reordered within the Pinned section.</summary>
    PinChanged,

    /// <summary>A document's text changed.</summary>
    ContentChanged,

    /// <summary>A document's format changed (for example because a rename changed its extension).</summary>
    FormatChanged,

    /// <summary>The node was moved to the Trash.</summary>
    Trashed,

    /// <summary>The node was restored from the Trash (it may also be reported as <see cref="Moved"/> when its parent was gone).</summary>
    Restored,

    /// <summary>The node was deleted forever.</summary>
    Deleted,
}

/// <summary>One change to one node.</summary>
/// <param name="NodeId">The node.</param>
/// <param name="Kind">What happened.</param>
public readonly record struct NodeChange(NodeId NodeId, NodeChangeKind Kind);

/// <summary>The changes one committed write made to one workspace, in the order they were made.</summary>
public sealed class WorkspaceChangedEventArgs : EventArgs
{
    /// <summary>Creates the event arguments.</summary>
    public WorkspaceChangedEventArgs(WorkspaceId workspaceId, IReadOnlyList<NodeChange> changes)
    {
        WorkspaceId = workspaceId;
        Changes = changes;
    }

    /// <summary>The workspace that changed.</summary>
    public WorkspaceId WorkspaceId { get; }

    /// <summary>The node changes; a node can appear more than once (for example Restored and Moved).</summary>
    public IReadOnlyList<NodeChange> Changes { get; }
}

/// <summary>Notifies view models about committed workspace changes so they can refresh incrementally.</summary>
public interface IWorkspaceChangeNotifier
{
    /// <summary>
    /// Raised after a write has committed, on a background thread (never the UI thread, never the writer thread).
    /// Events arrive one at a time, in commit order.
    /// </summary>
    event EventHandler<WorkspaceChangedEventArgs>? Changed;
}

/// <summary>
/// The <see cref="IWorkspaceChangeNotifier"/> used by the Core services. Services publish from
/// <see cref="WriteContext.OnCommitted"/>; a single background loop delivers the events in order, so a slow handler
/// delays later events but never the writer.
/// </summary>
public sealed class WorkspaceChangeNotifier : IWorkspaceChangeNotifier, IDisposable
{
    private readonly Channel<Envelope> _channel = Channel.CreateUnbounded<Envelope>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    private readonly Task _pump;

    /// <summary>Starts the delivery loop.</summary>
    public WorkspaceChangeNotifier()
    {
        _pump = Task.Run(PumpAsync);
    }

    /// <inheritdoc/>
    public event EventHandler<WorkspaceChangedEventArgs>? Changed;

    /// <summary>Raised when a <see cref="Changed"/> handler throws; delivery continues with the next handler.</summary>
    public event EventHandler<Exception>? HandlerFailed;

    /// <summary>Completes when every event published before this call has been delivered.</summary>
    public Task WhenDeliveredAsync()
    {
        var marker = new Envelope(null);
        return _channel.Writer.TryWrite(marker) ? marker.Delivered!.Task : _pump;
    }

    /// <summary>Stops accepting events and waits (briefly) for queued ones to be delivered.</summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _pump.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Queues an event for delivery. Call it only after the change has committed.</summary>
    internal void Publish(WorkspaceId workspaceId, IReadOnlyList<NodeChange> changes)
    {
        if (changes.Count > 0)
        {
            _channel.Writer.TryWrite(new Envelope(new WorkspaceChangedEventArgs(workspaceId, changes)));
        }
    }

    private async Task PumpAsync()
    {
        await foreach (var envelope in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (envelope.Args is null)
            {
                envelope.Delivered!.TrySetResult();
                continue;
            }

            if (Changed is not { } handlers)
            {
                continue;
            }

            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<WorkspaceChangedEventArgs>>())
            {
                try
                {
                    handler(this, envelope.Args);
                }
                catch (Exception ex)
                {
                    if (HandlerFailed is null)
                    {
                        Trace.TraceError($"A workspace change handler failed: {ex}");
                    }
                    else
                    {
                        HandlerFailed.Invoke(this, ex);
                    }
                }
            }
        }
    }

    private sealed class Envelope(WorkspaceChangedEventArgs? args)
    {
        public WorkspaceChangedEventArgs? Args { get; } = args;

        public TaskCompletionSource? Delivered { get; } =
            args is null ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
    }
}

/// <summary>Collects the changes of one write job and publishes them after it commits.</summary>
internal sealed class ChangeCollector
{
    private readonly Dictionary<WorkspaceId, List<NodeChange>> _changes = [];

    public void Add(WorkspaceId workspaceId, NodeId nodeId, NodeChangeKind kind)
    {
        if (!_changes.TryGetValue(workspaceId, out var list))
        {
            list = [];
            _changes[workspaceId] = list;
        }

        list.Add(new NodeChange(nodeId, kind));
    }

    /// <summary>Publishes everything collected once <paramref name="context"/>'s transaction commits.</summary>
    public void PublishOnCommit(WriteContext context, WorkspaceChangeNotifier notifier)
    {
        if (_changes.Count == 0)
        {
            return;
        }

        var snapshot = _changes.Select(pair => (pair.Key, (IReadOnlyList<NodeChange>)pair.Value.ToArray())).ToArray();
        context.OnCommitted(() =>
        {
            foreach (var (workspaceId, changes) in snapshot)
            {
                notifier.Publish(workspaceId, changes);
            }
        });
    }
}
