using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Storage;
using NimPad.Core.Time;

namespace NimPad.Core.Sync;

/// <summary>
/// The durable local → server operation queue (architecture.md, "Pending operation queue"). Services append to it
/// inside the same writer transaction as the change itself; Phase 1 never drains it (no sync is simulated).
/// </summary>
/// <remarks>
/// <para>Coalescing keeps the queue bounded. It only ever rewrites operations that have not been attempted yet
/// (<c>attempts = 0</c>), so nothing already sent to the server changes under its idempotency key:</para>
/// <list type="bullet">
/// <item>There is at most one <c>update_content</c> per node. A newer one replaces its payload in place, keeping the
/// original base revisions; the payload carries only the checksum and format, and the push reads the current text.</item>
/// <item>A rename or content update of a node whose <c>create</c> is still queued folds into the create's payload
/// instead of adding an operation. (Moves and pins are not folded: a move can target a folder created later.)</item>
/// </list>
/// <para>Purging (deleting forever) keeps the queue replayable: pushed in order, every operation finds what it
/// refers to on the server, and the server ends up with what is left locally.</para>
/// <list type="bullet">
/// <item>A purged node the server cannot know (never acknowledged, and none of its operations attempted) has all its
/// operations dropped, so create + … + delete + purge of such a node leaves nothing behind.</item>
/// <item>Except when a surviving queued <c>create</c> or <c>move</c> names it as the parent (a note created in a new
/// folder, then dragged out before the folder was deleted): then its operations stay so the parent exists on the
/// server when that operation is pushed.</item>
/// <item>Unattempted <c>update_content</c>s of purged documents are dropped: the push reads the current text, which is
/// gone.</item>
/// <item>When the purged root keeps its operations (the server knows it, or will), one <c>purge</c> of the root is
/// queued after them. Its trip through the Trash is already queued (or acknowledged), and every kept descendant is
/// below it on the server by then, so that one purge removes them all.</item>
/// </list>
/// <para>In Debug builds every queued operation is checked against the contract
/// (<see cref="PendingOperationContract"/>), so a write that would queue something the server rejects fails.</para>
/// </remarks>
public sealed class PendingOperationQueue
{
    // The parent named by a create or move payload; ix_pending_operations_parent indexes exactly this expression.
    private const string ParentIdExpression = "json_extract(payload, '$.parentId')";

    private const string SelectColumns =
        "seq, op_id, workspace_id, node_id, kind, payload, base_meta_rev, base_content_rev, device_id, actor_kind, " +
        "created_at, attempts, last_error";

    private readonly LocalDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    // Read and written only on the writer thread.
    private Guid? _deviceId;

    /// <summary>Creates the queue over <paramref name="database"/>.</summary>
    /// <param name="database">The local database.</param>
    /// <param name="timeProvider">The clock for <c>created_at</c>; <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="ids">The generator for operation and device IDs; UUIDv7 by default.</param>
    public PendingOperationQueue(LocalDatabase database, TimeProvider? timeProvider = null, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ids = ids ?? new UuidV7Generator(_timeProvider);
    }

    /// <summary>The oldest queued operations in push order (by <c>seq</c>).</summary>
    /// <param name="limit">The maximum number to return (at least 1).</param>
    /// <param name="cancellationToken">Cancels the read before it starts.</param>
    public Task<IReadOnlyList<PendingOperation>> GetPendingAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return _database.Readers.ExecuteAsync<IReadOnlyList<PendingOperation>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {SelectColumns} FROM pending_operations ORDER BY seq LIMIT $limit;";
                command.With("$limit", limit);
                return ReadAll(command);
            },
            cancellationToken);
    }

    /// <summary>The queued operations targeting <paramref name="nodeId"/>, in push order.</summary>
    public Task<IReadOnlyList<PendingOperation>> GetForNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync<IReadOnlyList<PendingOperation>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {SelectColumns} FROM pending_operations WHERE node_id = $id ORDER BY seq;";
                command.With("$id", nodeId);
                return ReadAll(command);
            },
            cancellationToken);

    /// <summary>The number of queued operations.</summary>
    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync(
            connection => Convert.ToInt32(connection.ExecuteScalar("SELECT count(*) FROM pending_operations;"), System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);

    /// <summary>Queues the <c>create</c> of <paramref name="node"/> (a document also needs its text's <paramref name="checksum"/>).</summary>
    internal void EnqueueCreate(WriteContext context, NodeInfo node, string? checksum, LocalActorKind actor)
    {
        var payload = PendingOperationPayloads.Create(node.Kind, node.ParentId, node.Name, node.Position, node.Format, checksum);
        Insert(context, node, PendingOperationKind.Create, payload, null, null, actor);
    }

    /// <summary>Queues a <c>rename</c> to <paramref name="node"/>'s current name, or folds it into a pending create.</summary>
    internal void EnqueueRename(WriteContext context, NodeInfo node, LocalActorKind actor)
    {
        if (FindUnattempted(context, node.Id, PendingOperationKind.Create) is { } create)
        {
            UpdatePayload(context, create.Seq, PendingOperationPayloads.With(create.Payload, ("name", node.Name)));
            return;
        }

        Insert(context, node, PendingOperationKind.Rename, PendingOperationPayloads.Rename(node.Name), node.MetaServerRev, node.ContentServerRev, actor);
    }

    /// <summary>Queues a <c>move</c> to <paramref name="node"/>'s current parent and position.</summary>
    internal void EnqueueMove(WriteContext context, NodeInfo node, LocalActorKind actor) =>
        Insert(context, node, PendingOperationKind.Move, PendingOperationPayloads.Move(node.ParentId, node.Position), node.MetaServerRev, node.ContentServerRev, actor);

    /// <summary>Queues a <c>pin</c> with <paramref name="node"/>'s current pin state.</summary>
    internal void EnqueuePin(WriteContext context, NodeInfo node, LocalActorKind actor) =>
        Insert(context, node, PendingOperationKind.Pin, PendingOperationPayloads.Pin(node.IsPinned, node.PinPosition), node.MetaServerRev, node.ContentServerRev, actor);

    /// <summary>
    /// Queues an <c>update_content</c> for a document whose text or format changed: folds into a pending create,
    /// else replaces a pending update in place (keeping its base revisions), else appends one.
    /// </summary>
    internal void EnqueueUpdateContent(WriteContext context, NodeInfo node, string checksum, DocumentFormat format, LocalActorKind actor)
    {
        if (FindUnattempted(context, node.Id, PendingOperationKind.Create) is { } create)
        {
            UpdatePayload(context, create.Seq, PendingOperationPayloads.With(
                create.Payload, ("format", JsonValue.Create(format.ToWireName())), ("checksum", JsonValue.Create(checksum))));
            return;
        }

        var payload = PendingOperationPayloads.UpdateContent(checksum, format);
        if (FindUnattempted(context, node.Id, PendingOperationKind.UpdateContent) is { } update)
        {
            UpdatePayload(context, update.Seq, payload);
            return;
        }

        Insert(context, node, PendingOperationKind.UpdateContent, payload, node.MetaServerRev, node.ContentServerRev, actor);
    }

    /// <summary>Queues the <c>delete</c> (move to Trash) of the subtree rooted at <paramref name="node"/>.</summary>
    internal void EnqueueDelete(WriteContext context, NodeInfo node, TrashBatchId batchId, DateTimeOffset deletedAt, LocalActorKind actor) =>
        Insert(context, node, PendingOperationKind.Delete, PendingOperationPayloads.Delete(batchId, deletedAt), node.MetaServerRev, node.ContentServerRev, actor);

    /// <summary>
    /// Queues the <c>restore</c> of the whole Trash batch <paramref name="batchId"/>; <paramref name="node"/> is its
    /// top-level node.
    /// </summary>
    internal void EnqueueRestore(WriteContext context, NodeInfo node, TrashBatchId batchId, LocalActorKind actor) =>
        Insert(context, node, PendingOperationKind.Restore, PendingOperationPayloads.Restore(batchId), node.MetaServerRev, node.ContentServerRev, actor);

    /// <summary>
    /// Applies the purge rules (see the class remarks) for a subtree that is about to be deleted forever. Call it
    /// before deleting the rows.
    /// </summary>
    /// <param name="context">The writer transaction.</param>
    /// <param name="subtree">The purged root first, then its descendants, every parent before its children.</param>
    /// <param name="actor">Who purges.</param>
    internal void EnqueuePurge(WriteContext context, IReadOnlyList<NodeInfo> subtree, LocalActorKind actor)
    {
        if (subtree.Count == 0)
        {
            return;
        }

        var root = subtree[0];
        var subtreeIds = ToJsonArray(subtree.Select(node => node.Id));
        var attempted = ReadNodeIds(
            context,
            "SELECT DISTINCT node_id FROM pending_operations WHERE attempts > 0 AND node_id IN (SELECT value FROM json_each($ids));",
            ("$ids", subtreeIds));

        // Nodes the server cannot know: never acknowledged, and nothing of theirs was ever sent.
        var cancelled = subtree
            .Where(node => node.IsNeverSynced && !attempted.Contains(node.Id))
            .Select(node => node.Id)
            .ToHashSet();

        // A queued create or move of a surviving node that names a cancelled node as its parent needs that parent on
        // the server, so the parent's own operations (its create, …, its delete) stay queued. Keeping them can keep
        // further ancestors, hence the loop.
        while (cancelled.Count > 0)
        {
            var referenced = ReadNodeIds(
                context,
                $"""
                SELECT DISTINCT {ParentIdExpression} FROM pending_operations
                WHERE kind IN ('create', 'move')
                  AND {ParentIdExpression} IN (SELECT value FROM json_each($cancelled))
                  AND node_id NOT IN (SELECT value FROM json_each($cancelled));
                """,
                ("$cancelled", ToJsonArray(cancelled)));
            if (referenced.Count == 0)
            {
                break;
            }

            cancelled.ExceptWith(referenced);
        }

        if (cancelled.Count > 0)
        {
            context.Execute(
                "DELETE FROM pending_operations WHERE node_id IN (SELECT value FROM json_each($ids));",
                ("$ids", ToJsonArray(cancelled)));
        }

        // The text is about to be deleted, and an update_content push reads the current text: it could never be sent.
        // The purge makes it moot anyway. (Attempted ones stay: they may already have been applied.)
        context.Execute(
            "DELETE FROM pending_operations WHERE kind = 'update_content' AND attempts = 0 AND node_id IN (SELECT value FROM json_each($ids));",
            ("$ids", subtreeIds));

        // Every kept node of the subtree sits below the root on the server once the queue is replayed (its placement
        // is either known to the server or queued, and a queued placement keeps its parent), so purging the root
        // purges them all.
        if (!cancelled.Contains(root.Id))
        {
            Insert(context, root, PendingOperationKind.Purge, PendingOperationPayloads.Purge(), root.MetaServerRev, root.ContentServerRev, actor);
        }
    }

    /// <summary>This installation's device ID (created on first use).</summary>
    internal Guid GetDeviceId(WriteContext context)
    {
        if (_deviceId is { } cached)
        {
            return cached;
        }

        var deviceId = DeviceIdentity.GetOrCreate(context, _timeProvider, _ids);
        // Cache only once the row is committed: a rolled-back first write must not leave a phantom device ID.
        context.OnCommitted(() => _deviceId = deviceId);
        return deviceId;
    }

    private void Insert(
        WriteContext context,
        NodeInfo node,
        PendingOperationKind kind,
        string payload,
        long? baseMetaRev,
        long? baseContentRev,
        LocalActorKind actor)
    {
        context.Execute(
            """
            INSERT INTO pending_operations
                (op_id, workspace_id, node_id, kind, payload, base_meta_rev, base_content_rev, device_id, actor_kind, created_at)
            VALUES ($op, $ws, $node, $kind, $payload, $baseMeta, $baseContent, $device, $actor, $now);
            """,
            ("$op", _ids.NewGuid()),
            ("$ws", node.WorkspaceId),
            ("$node", node.Id),
            ("$kind", kind.ToWireName()),
            ("$payload", payload),
            ("$baseMeta", baseMetaRev),
            ("$baseContent", baseContentRev),
            ("$device", GetDeviceId(context)),
            ("$actor", actor.ToWireName()),
            ("$now", Timestamps.Now(_timeProvider)));
        AssertContract(context, Convert.ToInt64(context.Scalar("SELECT last_insert_rowid();"), CultureInfo.InvariantCulture));
    }

    private static (long Seq, string Payload)? FindUnattempted(WriteContext context, NodeId nodeId, PendingOperationKind kind)
    {
        using var command = context.CreateCommand(
            "SELECT seq, payload FROM pending_operations WHERE node_id = $id AND kind = $kind AND attempts = 0 ORDER BY seq LIMIT 1;");
        command.With("$id", nodeId).With("$kind", kind.ToWireName());
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    private static void UpdatePayload(WriteContext context, long seq, string payload)
    {
        context.Execute("UPDATE pending_operations SET payload = $payload WHERE seq = $seq;", ("$payload", payload), ("$seq", seq));
        AssertContract(context, seq);
    }

    // Debug builds refuse to queue an operation the server would reject (the exception rolls the write back).
    [Conditional("DEBUG")]
    private static void AssertContract(WriteContext context, long seq)
    {
        using var command = context.CreateCommand($"SELECT {SelectColumns} FROM pending_operations WHERE seq = $seq;").With("$seq", seq);
        var operation = ReadAll(command).Single();
        var errors = PendingOperationContract.Validate(operation.ToContractJson());
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Queued a {operation.Kind.ToWireName()} operation that breaks the contract: {string.Join("; ", errors)}");
        }
    }

    private static HashSet<NodeId> ReadNodeIds(WriteContext context, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = context.CreateCommand(sql);
        command.AddParameters(parameters);
        using var reader = command.ExecuteReader();
        var ids = new HashSet<NodeId>();
        while (reader.Read())
        {
            ids.Add(reader.GetNodeId(0));
        }

        return ids;
    }

    // IDs only contain hex digits and hyphens, so no escaping is needed.
    private static string ToJsonArray(IEnumerable<NodeId> ids) =>
        "[" + string.Join(',', ids.Select(id => $"\"{id}\"")) + "]";

    private static List<PendingOperation> ReadAll(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var operations = new List<PendingOperation>();
        while (reader.Read())
        {
            operations.Add(new PendingOperation(
                reader.GetInt64(0),
                Uuid.Parse(reader.GetString(1), "op_id"),
                WorkspaceId.Parse(reader.GetString(2)),
                NodeId.Parse(reader.GetString(3)),
                PendingOperationNames.ParseKind(reader.GetString(4)),
                reader.GetString(5),
                reader.GetInt64OrNull(6),
                reader.GetInt64OrNull(7),
                Uuid.Parse(reader.GetString(8), "device_id"),
                PendingOperationNames.ParseActor(reader.GetString(9)),
                reader.GetTimestamp(10),
                reader.GetInt32(11),
                reader.GetStringOrNull(12)));
        }

        return operations;
    }
}
