using System.Text.Json.Nodes;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Sync;
using Xunit;

namespace NimPad.Core.Tests.Support;

/// <summary>
/// A model of the server's copy of one workspace tree, for checking that the local queue is replayable: every
/// operation is valid by the contract and finds what it refers to when pushed in order, and after the whole queue the
/// server holds exactly what is stored locally. It enforces the rules the local services follow (a parent must be a
/// live folder, a purge needs a trashed node, a restore brings back a whole batch, a document push reads its text).
/// </summary>
internal sealed class ServerReplica
{
    // What the server acknowledged; every replay starts from it.
    private readonly Dictionary<NodeId, Node> _synced = [];

    // The replay in progress.
    private Dictionary<NodeId, Node> _nodes = [];

    /// <summary>Records a node as the server knows it (its state when it was acknowledged).</summary>
    public void AddSynced(NodeInfo node) => _synced[node.Id] = Node.From(node);

    /// <summary>
    /// Replays the store's queue on top of the synced nodes and asserts that the result equals the local tree of the
    /// workspace (IDs, parents, positions, names, pins and Trash batches).
    /// </summary>
    public async Task AssertQueueConvergesAsync(TestStore store)
    {
        _nodes = new Dictionary<NodeId, Node>(_synced);
        var operations = await store.Queue.GetPendingAsync(limit: 100_000);
        var withText = (await store.QueryAsync("SELECT node_id FROM document_contents;", reader => NodeId.Parse(reader.GetString(0)))).ToHashSet();
        foreach (var operation in operations)
        {
            var errors = PendingOperationContract.Validate(operation.ToContractJson());
            Assert.True(errors.Count == 0, $"#{operation.Seq} {operation.Kind}: {string.Join("; ", errors)}");
            Apply(operation, withText);
        }

        var local = await store.QueryAsync(
            $"SELECT {NodeRows.Columns} {NodeRows.From} WHERE n.workspace_id = $ws;",
            NodeRows.Read,
            ("$ws", store.Workspace.Id.ToString()));
        var expected = local.Select(Node.From).OrderBy(node => node.Id.ToString(), StringComparer.Ordinal).ToList();
        var actual = _nodes.Values.OrderBy(node => node.Id.ToString(), StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
    }

    private void Apply(PendingOperation operation, HashSet<NodeId> withText)
    {
        var payload = operation.Payload;
        var where = $"#{operation.Seq} {operation.Kind.ToWireName()} of {operation.NodeId}";
        _nodes.TryGetValue(operation.NodeId, out var node);
        switch (operation.Kind)
        {
            case PendingOperationKind.Create:
                Assert.True(node is null, $"{where}: the node already exists");
                var parent = ParentOf(payload, where);
                var kind = NodeKinds.Parse(payload["nodeKind"]!.GetValue<string>());
                if (kind == NodeKind.Document && operation.Attempts == 0)
                {
                    Assert.True(withText.Contains(operation.NodeId), $"{where}: the push reads the text, which is gone");
                }

                _nodes[operation.NodeId] = new Node(
                    operation.NodeId, parent, kind, payload["name"]!.GetValue<string>(), payload["position"]!.GetValue<string>(), null, null);
                break;
            case PendingOperationKind.UpdateContent:
                Assert.True(node is { Kind: NodeKind.Document }, $"{where}: no such document");
                if (operation.Attempts == 0)
                {
                    Assert.True(withText.Contains(operation.NodeId), $"{where}: the push reads the text, which is gone");
                }

                break;
            case PendingOperationKind.Rename:
                _nodes[operation.NodeId] = RequireLive(node, where) with { Name = payload["name"]!.GetValue<string>() };
                break;
            case PendingOperationKind.Move:
                var live = RequireLive(node, where);
                var target = ParentOf(payload, where);
                for (var ancestor = target; ancestor is { } id; ancestor = _nodes[id].ParentId)
                {
                    Assert.True(id != operation.NodeId, $"{where}: moves the folder into itself");
                }

                _nodes[operation.NodeId] = live with { ParentId = target, Position = payload["position"]!.GetValue<string>() };
                break;
            case PendingOperationKind.Pin:
                _nodes[operation.NodeId] = RequireLive(node, where) with { PinPosition = payload["pinPosition"]?.GetValue<string>() };
                break;
            case PendingOperationKind.Delete:
                RequireLive(node, where);
                var batch = payload["trashBatchId"]!.GetValue<string>();
                foreach (var id in LiveSubtree(operation.NodeId))
                {
                    _nodes[id] = _nodes[id] with { TrashBatch = batch };
                }

                break;
            case PendingOperationKind.Restore:
                var restored = payload["trashBatchId"]!.GetValue<string>();
                Assert.True(node is not null && node.TrashBatch == restored, $"{where}: the node is not in Trash batch {restored}");
                foreach (var member in _nodes.Values.Where(item => item.TrashBatch == restored).ToList())
                {
                    _nodes[member.Id] = member with { TrashBatch = null };
                }

                break;
            case PendingOperationKind.Purge:
                Assert.True(node is { TrashBatch: not null }, $"{where}: only a node in the Trash can be purged");
                foreach (var id in Subtree(operation.NodeId))
                {
                    _nodes.Remove(id);
                }

                break;
        }
    }

    private NodeId? ParentOf(JsonObject payload, string where)
    {
        if (payload["parentId"]?.GetValue<string>() is not { } text)
        {
            return null;
        }

        var parentId = NodeId.Parse(text);
        Assert.True(
            _nodes.TryGetValue(parentId, out var parent) && parent is { Kind: NodeKind.Folder, TrashBatch: null },
            $"{where}: the parent {parentId} is not a live folder on the server");
        return parentId;
    }

    private static Node RequireLive(Node? node, string where)
    {
        Assert.True(node is { TrashBatch: null }, $"{where}: no such live node");
        return node!;
    }

    private List<NodeId> Subtree(NodeId rootId)
    {
        var result = new List<NodeId> { rootId };
        for (var i = 0; i < result.Count; i++)
        {
            var parentId = result[i];
            result.AddRange(_nodes.Values.Where(node => node.ParentId == parentId).Select(node => node.Id));
        }

        return result;
    }

    private List<NodeId> LiveSubtree(NodeId rootId)
    {
        var result = new List<NodeId> { rootId };
        for (var i = 0; i < result.Count; i++)
        {
            var parentId = result[i];
            result.AddRange(_nodes.Values.Where(node => node.ParentId == parentId && node.TrashBatch is null).Select(node => node.Id));
        }

        return result;
    }

    private sealed record Node(NodeId Id, NodeId? ParentId, NodeKind Kind, string Name, string Position, string? PinPosition, string? TrashBatch)
    {
        public static Node From(NodeInfo info) =>
            new(info.Id, info.ParentId, info.Kind, info.Name, info.Position, info.PinPosition, info.TrashBatchId?.ToString());
    }
}
