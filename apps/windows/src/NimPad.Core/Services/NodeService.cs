using System.Text;
using Microsoft.Data.Sqlite;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Ordering;
using NimPad.Core.Storage;
using NimPad.Core.Sync;
using NimPad.Core.Text;
using NimPad.Core.Time;

namespace NimPad.Core.Services;

/// <summary>
/// The workspace tree: folders and documents, their order, pins and the Trash.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every write is one <see cref="SqliteWriter"/> job (one <c>BEGIN IMMEDIATE</c> transaction) that also
/// queues its <see cref="PendingOperation"/>s, so a change and its sync record commit or roll back together.</item>
/// <item>After the commit, <see cref="WorkspaceChangeNotifier"/> reports the changed node IDs.</item>
/// <item>Reads use pooled read connections on the thread pool and see everything committed before they start.</item>
/// <item>Siblings are ordered by fractional-index <c>position</c> keys (ties broken by ID); inserting never renumbers
/// other siblings.</item>
/// <item>Writes that target a node in the Trash fail with <see cref="NodeNotFoundException"/>, except restore and
/// delete forever.</item>
/// <item>Validation failures (names, missing nodes, cycles) fault the returned task; nothing is written then.</item>
/// </list>
/// </remarks>
public sealed class NodeService
{
    /// <summary>How long trashed nodes are kept before <see cref="PurgeExpiredTrashAsync"/> deletes them forever.</summary>
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    private readonly LocalDatabase _database;
    private readonly PendingOperationQueue _queue;
    private readonly WorkspaceChangeNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The local database.</param>
    /// <param name="queue">The pending-operation queue that every write appends to.</param>
    /// <param name="notifier">Receives the committed changes.</param>
    /// <param name="timeProvider">The clock; <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="ids">The ID generator; UUIDv7 by default.</param>
    public NodeService(
        LocalDatabase database,
        PendingOperationQueue queue,
        WorkspaceChangeNotifier notifier,
        TimeProvider? timeProvider = null,
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(notifier);
        _database = database;
        _queue = queue;
        _notifier = notifier;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ids = ids ?? new UuidV7Generator(_timeProvider);
    }

    // ---------------------------------------------------------------- writes

    /// <summary>Creates a folder.</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="parentId">The parent folder, or <see langword="null"/> for the workspace root.</param>
    /// <param name="name">The name; trimmed and validated (<see cref="NodeNames.Normalize"/>).</param>
    /// <param name="position">Where among the parent's children; the end by default.</param>
    /// <param name="cancellationToken">Cancels the write before it starts.</param>
    /// <exception cref="InvalidNodeNameException">The name is invalid.</exception>
    /// <exception cref="NodeOperationException">The parent is missing, trashed, a document or in another workspace.</exception>
    public Task<NodeInfo> CreateFolderAsync(
        WorkspaceId workspaceId,
        NodeId? parentId,
        string name,
        InsertPosition position = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return WriteAsync(
            (context, changes) => CreateNode(context, workspaceId, parentId, NodeKind.Folder, name, position, null, null, LocalActorKind.User, changes),
            cancellationToken);
    }

    /// <summary>Creates a document together with its <c>document_contents</c> row.</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="parentId">The parent folder, or <see langword="null"/> for the workspace root.</param>
    /// <param name="name">The name, or <see langword="null"/> for the next free <c>Untitled N</c>.</param>
    /// <param name="format">The format, or <see langword="null"/> to detect it from the name's extension.</param>
    /// <param name="initialText">
    /// The initial text; empty by default. Line endings are normalized to LF, and U+0000 and unpaired surrogates become
    /// U+FFFD (<see cref="ContentCanonicalizer.ToStorableText"/>).
    /// </param>
    /// <param name="position">Where among the parent's children; the end by default.</param>
    /// <param name="cancellationToken">Cancels the write before it starts.</param>
    /// <exception cref="InvalidNodeNameException">The name is invalid.</exception>
    /// <exception cref="NodeOperationException">The parent is missing, trashed, a document or in another workspace.</exception>
    public Task<NodeInfo> CreateDocumentAsync(
        WorkspaceId workspaceId,
        NodeId? parentId,
        string? name = null,
        DocumentFormat? format = null,
        string? initialText = null,
        InsertPosition position = default,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            (context, changes) => CreateNode(context, workspaceId, parentId, NodeKind.Document, name, position, format, initialText, LocalActorKind.User, changes),
            cancellationToken);

    /// <summary>
    /// Renames a node. When a document's extension implies a different format and its format still matches the old
    /// name (it was not overridden), the format follows the new name.
    /// </summary>
    /// <returns>The node after the rename (unchanged when the name is the same).</returns>
    /// <exception cref="InvalidNodeNameException">The name is invalid.</exception>
    /// <exception cref="NodeNotFoundException">The node does not exist or is in the Trash.</exception>
    public Task<NodeInfo> RenameAsync(NodeId nodeId, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newName);
        return WriteAsync((context, changes) => Rename(context, nodeId, newName, LocalActorKind.User, changes), cancellationToken);
    }

    /// <summary>Moves one node (see <see cref="MoveAsync(IReadOnlyList{NodeId}, NodeId?, InsertPosition, CancellationToken)"/>).</summary>
    public Task MoveAsync(NodeId nodeId, NodeId? newParentId, InsertPosition position = default, CancellationToken cancellationToken = default) =>
        MoveAsync([nodeId], newParentId, position, cancellationToken);

    /// <summary>
    /// Moves nodes under <paramref name="newParentId"/> (or the workspace root), placed together at
    /// <paramref name="position"/> in the given order. A node whose ancestor is also in the list moves with that
    /// ancestor. Reordering within the same parent is a move too; a single node that is already in place is left
    /// unchanged.
    /// </summary>
    /// <exception cref="NodeCycleException">A folder would move into itself or one of its descendants.</exception>
    /// <exception cref="NodeNotFoundException">A node or the destination does not exist or is in the Trash.</exception>
    /// <exception cref="NodeOperationException">The destination is a document, or the reference sibling is not in the destination.</exception>
    public Task MoveAsync(IReadOnlyList<NodeId> nodeIds, NodeId? newParentId, InsertPosition position = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        return WriteAsync(
            (context, changes) =>
            {
                MoveNodes(context, nodeIds, newParentId, position, LocalActorKind.User, changes);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Deep-copies nodes (each with its live subtree and document text) under <paramref name="targetParentId"/>,
    /// with new IDs. A copy keeps its name unless a live sibling at the destination uses it; then it becomes
    /// <c>X (copy)</c>, <c>X (copy 2)</c>, … (see <see cref="NodeNames.CopyName"/>). Copies are not pinned.
    /// </summary>
    /// <returns>The new top-level copies, in order.</returns>
    /// <exception cref="NodeCycleException">A folder would be copied into itself or one of its descendants.</exception>
    public Task<IReadOnlyList<NodeInfo>> CopyAsync(
        IReadOnlyList<NodeId> nodeIds,
        NodeId? targetParentId,
        InsertPosition position = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        return WriteAsync<IReadOnlyList<NodeInfo>>(
            (context, changes) => CopyNodes(context, nodeIds, targetParentId, position, LocalActorKind.User, changes),
            cancellationToken);
    }

    /// <summary>Copies a node next to itself (immediately after it), named <c>X (copy)</c>.</summary>
    /// <returns>The copy.</returns>
    public Task<NodeInfo> DuplicateAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        WriteAsync(
            (context, changes) =>
            {
                var node = RequireLive(context, nodeId);
                return CopyNodes(context, [nodeId], node.ParentId, InsertPosition.After(nodeId), LocalActorKind.User, changes)[0];
            },
            cancellationToken);

    /// <summary>Pins a node at the end of the Pinned section (no change when already pinned).</summary>
    public Task<NodeInfo> PinAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        WriteAsync((context, changes) => SetPinned(context, nodeId, true, LocalActorKind.User, changes), cancellationToken);

    /// <summary>Unpins a node (no change when not pinned).</summary>
    public Task<NodeInfo> UnpinAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        WriteAsync((context, changes) => SetPinned(context, nodeId, false, LocalActorKind.User, changes), cancellationToken);

    /// <summary>Moves a pinned node within the Pinned section; <paramref name="position"/> names another pinned node.</summary>
    /// <exception cref="NodeOperationException">The node is not pinned, or the reference node is not pinned.</exception>
    public Task<NodeInfo> ReorderPinnedAsync(NodeId nodeId, InsertPosition position, CancellationToken cancellationToken = default) =>
        WriteAsync((context, changes) => ReorderPinned(context, nodeId, position, LocalActorKind.User, changes), cancellationToken);

    /// <summary>
    /// Moves nodes and their live subtrees to the Trash. Each top-level node becomes its own Trash batch: it and its
    /// subtree get one <c>deleted_at</c> and <c>trash_batch_id</c>, show as one Trash item and queue one
    /// <c>delete</c>. A batch is what a restore brings back (contracts: <c>restore</c> restores a whole batch), so
    /// every Trash item can be restored on its own. A node whose ancestor is also listed travels with that ancestor.
    /// </summary>
    /// <returns>The new Trash batches, one per top-level node, in the given order.</returns>
    /// <exception cref="NodeNotFoundException">A node does not exist or is already in the Trash.</exception>
    public Task<IReadOnlyList<TrashBatchId>> TrashAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds.Count == 0)
        {
            throw new ArgumentException("Nothing to move to the Trash.", nameof(nodeIds));
        }

        return WriteAsync<IReadOnlyList<TrashBatchId>>(
            (context, changes) => TrashNodes(context, nodeIds, LocalActorKind.User, changes),
            cancellationToken);
    }

    /// <summary>
    /// Restores the Trash batch that contains a node: the item that was moved to the Trash (the batch's top-level node)
    /// with everything that went to the Trash together with it. Nodes trashed separately before stay in the Trash.
    /// When the top-level node's original parent is no longer a live folder, it goes to the end of the workspace root
    /// instead.
    /// </summary>
    /// <returns>The node after the restore.</returns>
    /// <exception cref="NodeNotFoundException">The node does not exist.</exception>
    /// <exception cref="NodeOperationException">The node is not in the Trash.</exception>
    public Task<NodeInfo> RestoreAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        WriteAsync(
            (context, changes) =>
            {
                var node = RequireNode(context, nodeId);
                if (node.TrashBatchId is not { } batchId)
                {
                    throw new NodeOperationException($"\"{node.Name}\" isn't in the Trash.");
                }

                RestoreBatch(context, batchId, LocalActorKind.User, changes);
                return RequireNode(context, nodeId);
            },
            cancellationToken);

    /// <summary>Restores a Trash batch (see <see cref="RestoreAsync"/>).</summary>
    /// <returns>The batch's restored top-level nodes (empty when the batch no longer exists).</returns>
    public Task<IReadOnlyList<NodeInfo>> RestoreBatchAsync(TrashBatchId batchId, CancellationToken cancellationToken = default) =>
        RestoreBatchAsync([batchId], cancellationToken);

    /// <summary>
    /// Restores several Trash batches in one transaction, for example to undo a <see cref="TrashAsync"/> of several
    /// items (pass the batches it returned).
    /// </summary>
    /// <returns>The restored top-level nodes, batch by batch (batches that no longer exist are skipped).</returns>
    public Task<IReadOnlyList<NodeInfo>> RestoreBatchAsync(IReadOnlyList<TrashBatchId> batchIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batchIds);
        return WriteAsync<IReadOnlyList<NodeInfo>>(
            (context, changes) => batchIds.Distinct().SelectMany(batchId => RestoreBatch(context, batchId, LocalActorKind.User, changes)).ToList(),
            cancellationToken);
    }

    /// <summary>
    /// Deletes nodes and their entire subtrees forever (document text included) and queues the purge. A node that
    /// is not in the Trash yet is moved there first, in the same transaction.
    /// </summary>
    /// <returns>How many nodes were deleted.</returns>
    public Task<int> DeleteForeverAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        return WriteAsync((context, changes) => DeleteForever(context, nodeIds, LocalActorKind.User, changes), cancellationToken);
    }

    /// <summary>
    /// Deletes a note for good, without a trip through the Trash, when it is still exactly as it was created: an
    /// <c>Untitled N</c> document that was never edited (empty text, content version 1), renamed, moved or pinned,
    /// that no note links to and that never reached the server. For closing the tab of such a note (implementation
    /// plan: empty, never-edited untitled notes are hard-deleted when their tab closes). The check and the delete are
    /// one transaction, so an autosave that commits first keeps the note. Its queued create is dropped; observers see
    /// <see cref="NodeChangeKind.Deleted"/> only.
    /// </summary>
    /// <returns><see langword="true"/> when the note was discarded; <see langword="false"/> when it is kept (or no longer exists).</returns>
    public Task<bool> DiscardIfPristineAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        WriteAsync(
            (context, changes) =>
            {
                var pristine = context.Scalar(
                    """
                    SELECT 1 FROM nodes n JOIN document_contents d ON d.node_id = n.id
                    WHERE n.id = $id AND n.kind = 'document' AND n.deleted_at IS NULL AND n.is_pinned = 0
                      AND n.updated_at = n.created_at AND n.meta_server_rev IS NULL
                      AND d.version = 1 AND d.char_count = 0
                      AND NOT EXISTS (SELECT 1 FROM document_links l WHERE l.target_id = n.id)
                      AND NOT EXISTS (SELECT 1 FROM pending_operations p WHERE p.node_id = n.id AND (p.attempts > 0 OR p.kind <> 'create'));
                    """,
                    ("$id", nodeId)) is not null;
                if (!pristine)
                {
                    return false;
                }

                var node = RequireNode(context, nodeId);
                if (!NodeNames.IsUntitledName(node.Name))
                {
                    return false;
                }

                _queue.EnqueuePurge(context, [node], LocalActorKind.User);
                context.Execute("DELETE FROM nodes WHERE id = $id;", ("$id", nodeId));
                changes.Add(node.WorkspaceId, nodeId, NodeChangeKind.Deleted);
                return true;
            },
            cancellationToken);

    /// <summary>Deletes everything in the workspace's Trash forever.</summary>
    /// <returns>How many nodes were deleted.</returns>
    public Task<int> EmptyTrashAsync(WorkspaceId workspaceId, CancellationToken cancellationToken = default) =>
        WriteAsync(
            (context, changes) =>
            {
                using var command = context.CreateCommand(
                    """
                    SELECT n.id FROM nodes n
                    WHERE n.workspace_id = $ws AND n.deleted_at IS NOT NULL
                      AND (n.parent_id IS NULL OR NOT EXISTS (
                          SELECT 1 FROM nodes p WHERE p.id = n.parent_id AND p.deleted_at IS NOT NULL));
                    """).With("$ws", workspaceId);
                return DeleteForever(context, ReadIds(command), LocalActorKind.User, changes);
            },
            cancellationToken);

    /// <summary>
    /// Deletes forever, in every workspace, the nodes that have been in the Trash for at least
    /// <paramref name="retention"/> (<see cref="TrashRetention"/> by default). Run at startup; queued as
    /// <c>system</c> operations.
    /// </summary>
    /// <returns>How many nodes were deleted.</returns>
    public Task<int> PurgeExpiredTrashAsync(TimeSpan? retention = null, CancellationToken cancellationToken = default)
    {
        var keep = retention ?? TrashRetention;
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, TimeSpan.Zero);
        return WriteAsync(
            (context, changes) =>
            {
                var cutoff = Timestamps.Now(_timeProvider) - keep;
                using var command = context.CreateCommand(
                    """
                    SELECT n.id FROM nodes n
                    WHERE n.deleted_at IS NOT NULL AND n.deleted_at <= $cutoff
                      AND (n.parent_id IS NULL OR NOT EXISTS (
                          SELECT 1 FROM nodes p
                          WHERE p.id = n.parent_id AND p.deleted_at IS NOT NULL AND p.deleted_at <= $cutoff));
                    """).With("$cutoff", cutoff);
                return DeleteForever(context, ReadIds(command), LocalActorKind.System, changes);
            },
            cancellationToken);
    }

    // ---------------------------------------------------------------- reads

    /// <summary>
    /// All live (not trashed) nodes of the workspace in one query, ordered by parent, then sibling order. Metadata
    /// only; document text is loaded on demand.
    /// </summary>
    public Task<IReadOnlyList<NodeInfo>> GetAllNodesAsync(WorkspaceId workspaceId, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<NodeInfo>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {NodeRows.Columns} {NodeRows.From} WHERE n.workspace_id = $ws AND n.deleted_at IS NULL " +
                    "ORDER BY n.parent_id, n.position, n.id;";
                command.With("$ws", workspaceId);
                return NodeRows.ReadAll(command);
            },
            cancellationToken);

    /// <summary>One node in any state (live or trashed), or <see langword="null"/> when it does not exist.</summary>
    public Task<NodeInfo?> GetNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        ReadAsync(connection => NodeRows.Get(connection, nodeId), cancellationToken);

    /// <summary>The live children of a folder (or of the workspace root), in sibling order.</summary>
    public Task<IReadOnlyList<NodeInfo>> GetChildrenAsync(WorkspaceId workspaceId, NodeId? parentId, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<NodeInfo>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                var scope = SiblingScope.Children(workspaceId, parentId);
                command.CommandText = $"SELECT {NodeRows.Columns} {NodeRows.From} WHERE {scope.Where} ORDER BY n.position, n.id;";
                scope.Bind(command);
                return NodeRows.ReadAll(command);
            },
            cancellationToken);

    /// <summary>The live pinned nodes of the workspace, in Pinned-section order.</summary>
    public Task<IReadOnlyList<NodeInfo>> GetPinnedAsync(WorkspaceId workspaceId, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<NodeInfo>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                var scope = SiblingScope.Pinned(workspaceId);
                command.CommandText = $"SELECT {NodeRows.Columns} {NodeRows.From} WHERE {scope.Where} ORDER BY n.pin_position, n.id;";
                scope.Bind(command);
                return NodeRows.ReadAll(command);
            },
            cancellationToken);

    /// <summary>
    /// The breadcrumb path: the node's ancestors from the workspace root down to the node itself (inclusive).
    /// Empty when the node does not exist.
    /// </summary>
    public Task<IReadOnlyList<NodeInfo>> GetPathAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<NodeInfo>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    WITH RECURSIVE path(id, depth) AS (
                        SELECT id, 0 FROM nodes WHERE id = $id
                        UNION ALL
                        SELECT n.parent_id, path.depth + 1 FROM nodes n JOIN path ON n.id = path.id
                        WHERE n.parent_id IS NOT NULL AND path.depth < 10000
                    )
                    SELECT {NodeRows.Columns} FROM path JOIN nodes n ON n.id = path.id
                    LEFT JOIN document_contents d ON d.node_id = n.id
                    ORDER BY path.depth DESC;
                    """;
                command.With("$id", nodeId);
                return NodeRows.ReadAll(command);
            },
            cancellationToken);

    /// <summary>
    /// The Trash view: each trashed node whose parent is not in the same batch, newest first, with the batch, days
    /// left before the automatic purge and how many descendants come back with it.
    /// </summary>
    public Task<IReadOnlyList<TrashItem>> GetTrashAsync(WorkspaceId workspaceId, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<TrashItem>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    SELECT {NodeRows.Columns},
                           (p.id IS NOT NULL AND p.deleted_at IS NULL AND p.kind = 'folder') AS parent_live
                    {NodeRows.From} LEFT JOIN nodes p ON p.id = n.parent_id
                    WHERE n.workspace_id = $ws AND n.deleted_at IS NOT NULL;
                    """;
                command.With("$ws", workspaceId);
                var trashed = new List<(NodeInfo Node, bool ParentLive)>();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        trashed.Add((NodeRows.Read(reader), reader.GetInt64(NodeRows.ColumnCount) != 0));
                    }
                }

                return BuildTrashItems(trashed, Timestamps.Now(_timeProvider));
            },
            cancellationToken);

    /// <summary>The next free <c>Untitled N</c> among the workspace's live documents.</summary>
    public Task<string> GetNextUntitledNameAsync(WorkspaceId workspaceId, CancellationToken cancellationToken = default) =>
        ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = UntitledNamesSql;
                command.With("$ws", workspaceId).With("$pattern", NodeNames.UntitledBase + " %");
                return NodeNames.NextUntitledName(ReadStrings(command));
            },
            cancellationToken);

    // ---------------------------------------------------------------- write implementations (writer thread)

    private const string UntitledNamesSql =
        "SELECT name FROM nodes WHERE workspace_id = $ws AND kind = 'document' AND deleted_at IS NULL AND name LIKE $pattern;";

    /// <summary>Creates a node inside the caller's write job. A null document name means the next <c>Untitled N</c>.</summary>
    internal NodeInfo CreateNode(
        WriteContext context,
        WorkspaceId workspaceId,
        NodeId? parentId,
        NodeKind kind,
        string? name,
        InsertPosition position,
        DocumentFormat? format,
        string? initialText,
        LocalActorKind actor,
        ChangeCollector changes)
    {
        if (name is null && kind != NodeKind.Document)
        {
            throw new InvalidNodeNameException(null, "A name can't be empty.");
        }

        var validName = name is null ? null : NodeNames.Normalize(name);
        RequireWorkspace(context, workspaceId);
        RequireParentFolder(context, workspaceId, parentId);
        string finalName;
        if (validName is not null)
        {
            finalName = validName;
        }
        else
        {
            using var command = context.CreateCommand(UntitledNamesSql);
            command.With("$ws", workspaceId).With("$pattern", NodeNames.UntitledBase + " %");
            finalName = NodeNames.NextUntitledName(ReadStrings(command));
        }

        var key = NewKeys(context, SiblingScope.Children(workspaceId, parentId), position, [], 1)[0];
        var now = Timestamps.Now(_timeProvider);
        var id = _ids.NewNodeId();
        var isDocument = kind == NodeKind.Document;
        context.Execute(
            """
            INSERT INTO nodes (id, workspace_id, parent_id, kind, name, position, created_at, updated_at, content_updated_at)
            VALUES ($id, $ws, $parent, $kind, $name, $position, $now, $now, $contentUpdated);
            """,
            ("$id", id),
            ("$ws", workspaceId),
            ("$parent", parentId),
            ("$kind", kind.ToWireName()),
            ("$name", finalName),
            ("$position", key),
            ("$now", now),
            ("$contentUpdated", isDocument ? now : null));

        string? checksum = null;
        DocumentFormat? documentFormat = null;
        if (isDocument)
        {
            // Pasted or imported text may hold characters the server cannot store (contracts: toStorableText).
            var text = ContentCanonicalizer.ToStorableText(ContentCanonicalizer.NormalizeLineEndings(initialText ?? string.Empty));
            documentFormat = format ?? DocumentFormats.FromName(finalName);
            checksum = ContentCanonicalizer.ChecksumOfCanonical(text);
            context.Execute(
                """
                INSERT INTO document_contents (node_id, text, format, checksum, char_count, preview, updated_at, fts_dirty)
                VALUES ($id, $text, $format, $checksum, $chars, $preview, $now, $dirty);
                """,
                ("$id", id),
                ("$text", text),
                ("$format", documentFormat.Value.ToWireName()),
                ("$checksum", checksum),
                ("$chars", text.Length),
                ("$preview", DocumentPreview.Create(text)),
                ("$now", now),
                ("$dirty", FtsIndex.ShouldDefer(text)));
            if (text.Length > 0)
            {
                DocumentLinks.ReplaceFromText(context, id, text);
            }
        }

        var node = new NodeInfo(
            id, workspaceId, parentId, kind, finalName, key, false, null, now, now,
            isDocument ? now : null, null, null, null, null, documentFormat);
        _queue.EnqueueCreate(context, node, checksum, actor);
        changes.Add(workspaceId, id, NodeChangeKind.Created);
        return node;
    }

    internal NodeInfo Rename(WriteContext context, NodeId nodeId, string newName, LocalActorKind actor, ChangeCollector changes)
    {
        var name = NodeNames.Normalize(newName);
        var node = RequireLive(context, nodeId);
        if (string.Equals(name, node.Name, StringComparison.Ordinal))
        {
            return node;
        }

        var now = Timestamps.Now(_timeProvider);
        context.Execute(
            "UPDATE nodes SET name = $name, updated_at = $now WHERE id = $id;",
            ("$name", name),
            ("$now", now),
            ("$id", nodeId));
        var renamed = node with { Name = name, UpdatedAt = now };
        _queue.EnqueueRename(context, renamed, actor);
        changes.Add(node.WorkspaceId, nodeId, NodeChangeKind.Renamed);

        // The format follows the extension unless the user overrode it (it no longer matches the old name).
        if (node.Format is { } current)
        {
            var oldImplied = DocumentFormats.FromName(node.Name);
            var newImplied = DocumentFormats.FromName(name);
            if (current == oldImplied && newImplied != oldImplied)
            {
                context.Execute(
                    "UPDATE document_contents SET format = $format, updated_at = $now, version = version + 1 WHERE node_id = $id;",
                    ("$format", newImplied.ToWireName()),
                    ("$now", now),
                    ("$id", nodeId));
                var checksum = (string)(context.Scalar("SELECT checksum FROM document_contents WHERE node_id = $id;", ("$id", nodeId))
                    ?? throw new InvalidOperationException($"Document {nodeId} has no content row."));
                renamed = renamed with { Format = newImplied };
                _queue.EnqueueUpdateContent(context, renamed, checksum, newImplied, actor);
                changes.Add(node.WorkspaceId, nodeId, NodeChangeKind.FormatChanged);
            }
        }

        return renamed;
    }

    internal void MoveNodes(
        WriteContext context,
        IReadOnlyList<NodeId> nodeIds,
        NodeId? newParentId,
        InsertPosition position,
        LocalActorKind actor,
        ChangeCollector changes)
    {
        var nodes = RequireLiveSameWorkspace(context, nodeIds);
        if (nodes.Count == 0)
        {
            return;
        }

        var workspaceId = nodes[0].WorkspaceId;
        RequireParentFolder(context, workspaceId, newParentId);
        RejectCycles(context, nodes, newParentId);
        var roots = TopMost(context, nodes);
        var scope = SiblingScope.Children(workspaceId, newParentId);
        var excluded = roots.Select(node => node.Id).ToArray();
        var (lo, hi) = ResolveBounds(context, scope, position, excluded);

        if (roots is [var single] && single.ParentId == newParentId && IsStrictlyBetween(single.Position, lo, hi))
        {
            return;
        }

        var keys = FractionalIndex.GenerateNKeysBetween(lo, hi, roots.Count);
        var now = Timestamps.Now(_timeProvider);
        for (var i = 0; i < roots.Count; i++)
        {
            context.Execute(
                "UPDATE nodes SET parent_id = $parent, position = $position, updated_at = $now WHERE id = $id;",
                ("$parent", newParentId),
                ("$position", keys[i]),
                ("$now", now),
                ("$id", roots[i].Id));
            var moved = roots[i] with { ParentId = newParentId, Position = keys[i], UpdatedAt = now };
            _queue.EnqueueMove(context, moved, actor);
            changes.Add(workspaceId, moved.Id, NodeChangeKind.Moved);
        }
    }

    internal List<NodeInfo> CopyNodes(
        WriteContext context,
        IReadOnlyList<NodeId> nodeIds,
        NodeId? targetParentId,
        InsertPosition position,
        LocalActorKind actor,
        ChangeCollector changes)
    {
        var nodes = RequireLiveSameWorkspace(context, nodeIds);
        if (nodes.Count == 0)
        {
            return [];
        }

        var workspaceId = nodes[0].WorkspaceId;
        RequireParentFolder(context, workspaceId, targetParentId);
        RejectCycles(context, nodes, targetParentId);
        var roots = TopMost(context, nodes);
        var keys = NewKeys(context, SiblingScope.Children(workspaceId, targetParentId), position, [], roots.Count);

        var siblingNames = new List<string>();
        using (var command = context.CreateCommand($"SELECT n.name FROM nodes n WHERE {SiblingScope.Children(workspaceId, targetParentId).Where};"))
        {
            SiblingScope.Children(workspaceId, targetParentId).Bind(command);
            siblingNames.AddRange(ReadStrings(command));
        }

        var now = Timestamps.Now(_timeProvider);
        var copies = new List<NodeInfo>(roots.Count);
        for (var i = 0; i < roots.Count; i++)
        {
            var root = roots[i];
            var newIds = new Dictionary<NodeId, NodeId>();
            foreach (var source in Subtree(context, root.Id, SubtreeFilter.Live))
            {
                var isRoot = source.Id == root.Id;
                var newId = _ids.NewNodeId();
                newIds[source.Id] = newId;
                var parent = isRoot ? targetParentId : newIds[source.ParentId!.Value];
                var name = isRoot ? NodeNames.Normalize(NodeNames.CopyName(source.Name, source.Kind, siblingNames)) : source.Name;
                var key = isRoot ? keys[i] : source.Position;
                context.Execute(
                    """
                    INSERT INTO nodes (id, workspace_id, parent_id, kind, name, position, created_at, updated_at, content_updated_at)
                    VALUES ($id, $ws, $parent, $kind, $name, $position, $now, $now, $contentUpdated);
                    """,
                    ("$id", newId),
                    ("$ws", workspaceId),
                    ("$parent", parent),
                    ("$kind", source.Kind.ToWireName()),
                    ("$name", name),
                    ("$position", key),
                    ("$now", now),
                    ("$contentUpdated", source.IsDocument ? now : null));

                string? checksum = null;
                var format = source.Format;
                if (source.IsDocument)
                {
                    (checksum, format) = CopyContent(context, source, newId, now);
                }

                var copy = new NodeInfo(
                    newId, workspaceId, parent, source.Kind, name, key, false, null, now, now,
                    source.IsDocument ? now : null, null, null, null, null, format);
                _queue.EnqueueCreate(context, copy, checksum, actor);
                changes.Add(workspaceId, newId, NodeChangeKind.Created);
                if (isRoot)
                {
                    siblingNames.Add(name);
                    copies.Add(copy);
                }
            }
        }

        return copies;
    }

    internal NodeInfo SetPinned(WriteContext context, NodeId nodeId, bool pinned, LocalActorKind actor, ChangeCollector changes)
    {
        var node = RequireLive(context, nodeId);
        if (node.IsPinned == pinned)
        {
            return node;
        }

        var key = pinned ? NewKeys(context, SiblingScope.Pinned(node.WorkspaceId), InsertPosition.End, [], 1)[0] : null;
        return ApplyPin(context, node, key, actor, changes);
    }

    internal NodeInfo ReorderPinned(WriteContext context, NodeId nodeId, InsertPosition position, LocalActorKind actor, ChangeCollector changes)
    {
        var node = RequireLive(context, nodeId);
        if (!node.IsPinned)
        {
            throw new NodeOperationException("Only pinned items can be reordered in the Pinned section.");
        }

        var (lo, hi) = ResolveBounds(context, SiblingScope.Pinned(node.WorkspaceId), position, [nodeId]);
        if (IsStrictlyBetween(node.PinPosition!, lo, hi))
        {
            return node;
        }

        return ApplyPin(context, node, FractionalIndex.GenerateKeyBetween(lo, hi), actor, changes);
    }

    internal List<TrashBatchId> TrashNodes(WriteContext context, IReadOnlyList<NodeId> nodeIds, LocalActorKind actor, ChangeCollector changes)
    {
        var nodes = RequireLiveSameWorkspace(context, nodeIds);
        var roots = TopMost(context, nodes);
        var now = Timestamps.Now(_timeProvider);
        var batches = new List<TrashBatchId>(roots.Count);
        foreach (var root in roots)
        {
            var batchId = _ids.NewTrashBatchId();
            batches.Add(batchId);
            var subtree = Subtree(context, root.Id, SubtreeFilter.Live);
            context.Execute(
                """
                UPDATE nodes SET deleted_at = $now, trash_batch_id = $batch, updated_at = $now
                WHERE id IN (SELECT value FROM json_each($ids));
                """,
                ("$now", now),
                ("$batch", batchId),
                ("$ids", ToJsonArray(subtree.Select(node => node.Id))));
            _queue.EnqueueDelete(context, root, batchId, now, actor);
            foreach (var node in subtree)
            {
                changes.Add(root.WorkspaceId, node.Id, NodeChangeKind.Trashed);
            }
        }

        return batches;
    }

    // Restores every node of the batch. One restore operation brings the whole batch back on the server; a top-level
    // node whose parent is no longer a live folder then moves to the end of the workspace root.
    internal List<NodeInfo> RestoreBatch(WriteContext context, TrashBatchId batchId, LocalActorKind actor, ChangeCollector changes)
    {
        using var command = context.CreateCommand($"SELECT {NodeRows.Columns} {NodeRows.From} WHERE n.trash_batch_id = $batch;").With("$batch", batchId);
        var members = NodeRows.ReadAll(command);
        if (members.Count == 0)
        {
            return [];
        }

        var memberIds = members.Select(member => member.Id).ToHashSet();
        var roots = members
            .Where(member => member.ParentId is not { } parentId || !memberIds.Contains(parentId))
            .Order(SiblingComparer.Instance)
            .ToList();
        var now = Timestamps.Now(_timeProvider);
        context.Execute(
            "UPDATE nodes SET deleted_at = NULL, trash_batch_id = NULL, updated_at = $now WHERE trash_batch_id = $batch;",
            ("$now", now),
            ("$batch", batchId));
        _queue.EnqueueRestore(context, roots[0], batchId, actor);
        foreach (var member in members)
        {
            changes.Add(member.WorkspaceId, member.Id, NodeChangeKind.Restored);
        }

        var restored = new List<NodeInfo>(roots.Count);
        foreach (var root in roots)
        {
            var node = root with { DeletedAt = null, TrashBatchId = null, UpdatedAt = now };
            var parentIsLiveFolder = root.ParentId is not { } parentId
                || NodeRows.Get(context, parentId) is { IsTrashed: false, IsFolder: true };
            if (!parentIsLiveFolder)
            {
                var key = NewKeys(context, SiblingScope.Children(root.WorkspaceId, null), InsertPosition.End, [root.Id], 1)[0];
                context.Execute(
                    "UPDATE nodes SET parent_id = NULL, position = $position WHERE id = $id;",
                    ("$position", key),
                    ("$id", root.Id));
                node = node with { ParentId = null, Position = key };
                _queue.EnqueueMove(context, node, actor);
                changes.Add(root.WorkspaceId, root.Id, NodeChangeKind.Moved);
            }

            restored.Add(node);
        }

        return restored;
    }

    internal int DeleteForever(WriteContext context, IReadOnlyList<NodeId> nodeIds, LocalActorKind actor, ChangeCollector changes)
    {
        var nodes = nodeIds.Distinct().Select(id => RequireNode(context, id)).ToList();
        var deleted = 0;
        foreach (var listed in TopMost(context, nodes))
        {
            var root = listed;
            if (!root.IsTrashed)
            {
                // Record the trip through the Trash so a synced node is deleted before it is purged on the server.
                TrashNodes(context, [root.Id], actor, changes);
                root = RequireNode(context, root.Id);
            }

            var subtree = Subtree(context, root.Id, SubtreeFilter.All);
            _queue.EnqueuePurge(context, subtree, actor);
            context.Execute(
                "DELETE FROM nodes WHERE id IN (SELECT value FROM json_each($ids));",
                ("$ids", ToJsonArray(subtree.Select(node => node.Id))));
            foreach (var node in subtree)
            {
                changes.Add(node.WorkspaceId, node.Id, NodeChangeKind.Deleted);
            }

            deleted += subtree.Count;
        }

        return deleted;
    }

    // ---------------------------------------------------------------- helpers

    private Task<T> WriteAsync<T>(Func<WriteContext, ChangeCollector, T> write, CancellationToken cancellationToken) =>
        _database.Writer.ExecuteAsync(
            context =>
            {
                var changes = new ChangeCollector();
                var result = write(context, changes);
                changes.PublishOnCommit(context, _notifier);
                return result;
            },
            cancellationToken);

    private Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken cancellationToken) =>
        _database.Readers.ExecuteAsync(read, cancellationToken);

    private NodeInfo ApplyPin(WriteContext context, NodeInfo node, string? pinPosition, LocalActorKind actor, ChangeCollector changes)
    {
        var now = Timestamps.Now(_timeProvider);
        context.Execute(
            "UPDATE nodes SET is_pinned = $pinned, pin_position = $pin, updated_at = $now WHERE id = $id;",
            ("$pinned", pinPosition is not null),
            ("$pin", pinPosition),
            ("$now", now),
            ("$id", node.Id));
        var updated = node with { IsPinned = pinPosition is not null, PinPosition = pinPosition, UpdatedAt = now };
        _queue.EnqueuePin(context, updated, actor);
        changes.Add(node.WorkspaceId, node.Id, NodeChangeKind.PinChanged);
        return updated;
    }

    private static (string? Checksum, DocumentFormat? Format) CopyContent(WriteContext context, NodeInfo source, NodeId newId, DateTimeOffset now)
    {
        var copied = context.Execute(
            """
            INSERT INTO document_contents (node_id, text, format, checksum, char_count, preview, updated_at, fts_dirty)
            SELECT $new, text, format, checksum, char_count, preview, $now, CASE WHEN length(text) > $threshold THEN 1 ELSE 0 END
            FROM document_contents WHERE node_id = $old;
            """,
            ("$new", newId),
            ("$now", now),
            ("$threshold", FtsIndex.LargeDocumentThreshold),
            ("$old", source.Id));
        if (copied == 0)
        {
            // Defensive: a document without a content row is copied as empty.
            var format = source.Format ?? DocumentFormats.FromName(source.Name);
            var checksum = ContentCanonicalizer.Checksum(string.Empty);
            context.Execute(
                """
                INSERT INTO document_contents (node_id, text, format, checksum, char_count, preview, updated_at, fts_dirty)
                VALUES ($new, '', $format, $checksum, 0, '', $now, 0);
                """,
                ("$new", newId),
                ("$format", format.ToWireName()),
                ("$checksum", checksum),
                ("$now", now));
            return (checksum, format);
        }

        DocumentLinks.Copy(context, source.Id, newId);
        using var command = context.CreateCommand("SELECT checksum, format FROM document_contents WHERE node_id = $id;").With("$id", newId);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetString(0), DocumentFormats.Parse(reader.GetString(1)));
    }

    private static void RequireWorkspace(WriteContext context, WorkspaceId workspaceId)
    {
        if (context.Scalar("SELECT 1 FROM workspaces WHERE id = $id;", ("$id", workspaceId)) is null)
        {
            throw new NodeOperationException($"The workspace {workspaceId} does not exist.");
        }
    }

    private static NodeInfo RequireNode(WriteContext context, NodeId nodeId) =>
        NodeRows.Get(context, nodeId) ?? throw new NodeNotFoundException(nodeId);

    private static NodeInfo RequireLive(WriteContext context, NodeId nodeId)
    {
        var node = RequireNode(context, nodeId);
        return node.IsTrashed
            ? throw new NodeNotFoundException(nodeId, $"\"{node.Name}\" is in the Trash. Restore it first.")
            : node;
    }

    private static List<NodeInfo> RequireLiveSameWorkspace(WriteContext context, IReadOnlyList<NodeId> nodeIds)
    {
        var nodes = nodeIds.Distinct().Select(id => RequireLive(context, id)).ToList();
        if (nodes.Select(node => node.WorkspaceId).Distinct().Count() > 1)
        {
            throw new NodeOperationException("Items from different workspaces can't be changed together.");
        }

        return nodes;
    }

    private static void RequireParentFolder(WriteContext context, WorkspaceId workspaceId, NodeId? parentId)
    {
        if (parentId is not { } id)
        {
            return;
        }

        var parent = NodeRows.Get(context, id) ?? throw new NodeNotFoundException(id, "The destination folder no longer exists.");
        if (parent.WorkspaceId != workspaceId)
        {
            throw new NodeOperationException("The destination folder is in another workspace.");
        }

        if (parent.IsTrashed)
        {
            throw new NodeNotFoundException(id, $"The folder \"{parent.Name}\" is in the Trash.");
        }

        if (!parent.IsFolder)
        {
            throw new NodeOperationException("Notes and folders can only be placed inside a folder.");
        }
    }

    // Rejects moving or copying a folder into itself or one of its descendants.
    private static void RejectCycles(WriteContext context, IReadOnlyList<NodeInfo> nodes, NodeId? targetParentId)
    {
        if (targetParentId is not { } target)
        {
            return;
        }

        using var command = context.CreateCommand(
            """
            WITH RECURSIVE ancestors(id) AS (
                SELECT $target
                UNION
                SELECT n.parent_id FROM nodes n JOIN ancestors a ON n.id = a.id WHERE n.parent_id IS NOT NULL
            )
            SELECT id FROM ancestors;
            """).With("$target", target);
        var ancestors = ReadIds(command).ToHashSet();
        foreach (var node in nodes)
        {
            if (ancestors.Contains(node.Id))
            {
                throw new NodeCycleException(node.Id, target);
            }
        }
    }

    // Drops nodes whose ancestor is also in the list (they travel with it); keeps the list's order.
    private static List<NodeInfo> TopMost(WriteContext context, IReadOnlyList<NodeInfo> nodes)
    {
        if (nodes.Count <= 1)
        {
            return [.. nodes];
        }

        using var command = context.CreateCommand(
            """
            WITH RECURSIVE chain(start, ancestor) AS (
                SELECT n.id, n.parent_id FROM nodes n
                WHERE n.id IN (SELECT value FROM json_each($ids)) AND n.parent_id IS NOT NULL
                UNION
                SELECT c.start, p.parent_id FROM chain c JOIN nodes p ON p.id = c.ancestor WHERE p.parent_id IS NOT NULL
            )
            SELECT DISTINCT start FROM chain WHERE ancestor IN (SELECT value FROM json_each($ids));
            """).With("$ids", ToJsonArray(nodes.Select(node => node.Id)));
        var nested = ReadIds(command).ToHashSet();
        return nodes.Where(node => !nested.Contains(node.Id)).ToList();
    }

    private enum SubtreeFilter
    {
        All,
        Live,
    }

    // The node and its descendants, parents before children (by depth, then sibling order).
    private static List<NodeInfo> Subtree(WriteContext context, NodeId rootId, SubtreeFilter filter)
    {
        var childFilter = filter == SubtreeFilter.Live ? "AND c.deleted_at IS NULL" : string.Empty;
        using var command = context.CreateCommand(
            $"""
            WITH RECURSIVE sub(id, depth) AS (
                SELECT $root, 0
                UNION ALL
                SELECT c.id, sub.depth + 1 FROM nodes c JOIN sub ON c.parent_id = sub.id WHERE 1 = 1 {childFilter}
            )
            SELECT {NodeRows.Columns} FROM sub JOIN nodes n ON n.id = sub.id
            LEFT JOIN document_contents d ON d.node_id = n.id
            ORDER BY sub.depth, n.parent_id, n.position, n.id;
            """).With("$root", rootId);
        return NodeRows.ReadAll(command);
    }

    private static string[] NewKeys(WriteContext context, SiblingScope scope, InsertPosition position, IReadOnlyCollection<NodeId> excluded, int count)
    {
        var (lo, hi) = ResolveBounds(context, scope, position, excluded);
        return FractionalIndex.GenerateNKeysBetween(lo, hi, count);
    }

    // The keys a new item must sort between. Ties (equal keys, possible after concurrent edits) are stepped over so
    // that a key strictly between the bounds always exists.
    private static (string? Lo, string? Hi) ResolveBounds(WriteContext context, SiblingScope scope, InsertPosition position, IReadOnlyCollection<NodeId> excluded)
    {
        var key = scope.KeyColumn;
        var where = $"{scope.Where} AND n.id NOT IN (SELECT value FROM json_each($excluded))";
        var excludedJson = ToJsonArray(excluded);

        (string Key, string Id)? QueryOne(string condition, string order, params (string Name, object? Value)[] extra)
        {
            using var command = context.CreateCommand($"SELECT n.{key}, n.id FROM nodes n WHERE {where} {condition} ORDER BY {order} LIMIT 1;");
            scope.Bind(command);
            command.With("$excluded", excludedJson);
            command.AddParameters(extra);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
        }

        switch (position.Placement)
        {
            case InsertPlacement.End:
                return (QueryOne(string.Empty, $"n.{key} DESC, n.id DESC")?.Key, null);
            case InsertPlacement.Start:
                return (null, QueryOne(string.Empty, $"n.{key}, n.id")?.Key);
            case InsertPlacement.Before or InsertPlacement.After:
                var siblingId = position.Sibling ?? throw new ArgumentException("Before/After need a sibling.", nameof(position));
                var sibling = QueryOne("AND n.id = $sibling", "n.id", ("$sibling", siblingId))
                    ?? throw new NodeOperationException("The item to place next to is not in the destination.");
                if (position.Placement == InsertPlacement.Before)
                {
                    var previous = QueryOne(
                        $"AND (n.{key} < $k OR (n.{key} = $k AND n.id < $sid))",
                        $"n.{key} DESC, n.id DESC",
                        ("$k", sibling.Key),
                        ("$sid", sibling.Id))?.Key;
                    if (previous == sibling.Key)
                    {
                        previous = QueryOne($"AND n.{key} < $k", $"n.{key} DESC, n.id DESC", ("$k", sibling.Key))?.Key;
                    }

                    return (previous, sibling.Key);
                }
                else
                {
                    var next = QueryOne(
                        $"AND (n.{key} > $k OR (n.{key} = $k AND n.id > $sid))",
                        $"n.{key}, n.id",
                        ("$k", sibling.Key),
                        ("$sid", sibling.Id))?.Key;
                    if (next == sibling.Key)
                    {
                        next = QueryOne($"AND n.{key} > $k", $"n.{key}, n.id", ("$k", sibling.Key))?.Key;
                    }

                    return (sibling.Key, next);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(position), position.Placement, "Unknown placement.");
        }
    }

    private static bool IsStrictlyBetween(string key, string? lo, string? hi) =>
        (lo is null || string.CompareOrdinal(lo, key) < 0) && (hi is null || string.CompareOrdinal(key, hi) < 0);

    private static List<TrashItem> BuildTrashItems(List<(NodeInfo Node, bool ParentLive)> trashed, DateTimeOffset now)
    {
        var byId = trashed.ToDictionary(item => item.Node.Id, item => item.Node);
        var childrenByParent = trashed
            .Where(item => item.Node.ParentId is not null)
            .GroupBy(item => item.Node.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Node).ToList());

        int CountSameBatchDescendants(NodeInfo node)
        {
            var count = 0;
            var stack = new Stack<NodeInfo>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                if (!childrenByParent.TryGetValue(stack.Pop().Id, out var children))
                {
                    continue;
                }

                foreach (var child in children.Where(child => child.TrashBatchId == node.TrashBatchId))
                {
                    count++;
                    stack.Push(child);
                }
            }

            return count;
        }

        var items = new List<TrashItem>();
        foreach (var (node, parentLive) in trashed)
        {
            var isBatchRoot = node.ParentId is not { } parentId
                || !byId.TryGetValue(parentId, out var parent)
                || parent.TrashBatchId != node.TrashBatchId;
            if (!isBatchRoot)
            {
                continue;
            }

            var deletedAt = node.DeletedAt!.Value;
            var purgeAt = deletedAt + TrashRetention;
            var remaining = purgeAt - now;
            var daysLeft = remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalDays);
            items.Add(new TrashItem(
                node,
                node.TrashBatchId!.Value,
                deletedAt,
                purgeAt,
                daysLeft,
                CountSameBatchDescendants(node),
                node.ParentId is null || parentLive));
        }

        return items
            .OrderByDescending(item => item.DeletedAt)
            .ThenBy(item => item.Node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Node.Id.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    private static List<NodeId> ReadIds(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var ids = new List<NodeId>();
        while (reader.Read())
        {
            ids.Add(reader.GetNodeId(0));
        }

        return ids;
    }

    private static List<string> ReadStrings(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    // IDs only contain hex digits and hyphens, so no escaping is needed.
    private static string ToJsonArray(IEnumerable<NodeId> ids)
    {
        var builder = new StringBuilder("[");
        foreach (var id in ids)
        {
            if (builder.Length > 1)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(id.ToString()).Append('"');
        }

        return builder.Append(']').ToString();
    }

    // A set of siblings: the live children of one parent, or the live pinned nodes of a workspace.
    private readonly record struct SiblingScope(WorkspaceId WorkspaceId, NodeId? ParentId, bool IsPinned)
    {
        public static SiblingScope Children(WorkspaceId workspaceId, NodeId? parentId) => new(workspaceId, parentId, false);

        public static SiblingScope Pinned(WorkspaceId workspaceId) => new(workspaceId, null, true);

        public string KeyColumn => IsPinned ? "pin_position" : "position";

        public string Where => IsPinned
            ? "n.workspace_id = $ws AND n.is_pinned = 1 AND n.deleted_at IS NULL"
            : ParentId is null
                ? "n.workspace_id = $ws AND n.parent_id IS NULL AND n.deleted_at IS NULL"
                : "n.workspace_id = $ws AND n.parent_id = $parent AND n.deleted_at IS NULL";

        public void Bind(SqliteCommand command)
        {
            command.With("$ws", WorkspaceId);
            if (!IsPinned && ParentId is { } parentId)
            {
                command.With("$parent", parentId);
            }
        }
    }
}
