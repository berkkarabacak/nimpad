using Microsoft.Data.Sqlite;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Storage;
using NimPad.Core.Sync;
using NimPad.Core.Text;
using NimPad.Core.Time;

namespace NimPad.Core.Services;

/// <summary>Saves the text of a managed document; implemented by <see cref="DocumentService"/>.</summary>
public interface IDocumentContentSaver
{
    /// <summary>
    /// Saves <paramref name="text"/> as the document's content, unless the stored text is no longer the one with
    /// <paramref name="expectedChecksum"/> (see
    /// <see cref="DocumentService.SaveContentAsync(NodeId, string, string?, CancellationToken)"/>).
    /// </summary>
    Task<DocumentSaveResult> SaveContentAsync(NodeId nodeId, string text, string? expectedChecksum, CancellationToken cancellationToken = default);
}

/// <summary>
/// The text and format of managed documents, and the links between them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A save is one <see cref="SqliteWriter"/> transaction that updates <c>document_contents</c> (text,
/// checksum, character count, preview, version), <c>nodes.content_updated_at</c>, <c>document_links</c>, the search
/// index and the coalesced <c>update_content</c> pending operation together, so they commit or roll back as one.</item>
/// <item>The text is put in its stored form (LF line endings; U+0000 and unpaired surrogates become U+FFFD, which the
/// server could not store) and its checksum, preview and links are computed before the job reaches the writer thread:
/// on the calling thread for short texts, on the thread pool for long ones. Text that is already in stored form is
/// passed through without a copy. A save whose checksum equals the stored one writes nothing.</item>
/// <item>Saves of one document commit in the order they were called, even when a long text is still being prepared
/// while a shorter, later one is ready.</item>
/// <item>A save can name the stored text it is based on (its checksum); when the stored text has changed since, it
/// fails with <see cref="DocumentConflictException"/> instead of overwriting the other change.</item>
/// <item>Documents longer than <see cref="FtsIndex.LargeDocumentThreshold"/> are not indexed inside the save (that
/// would hold up the writer while the user types); they are flagged <c>fts_dirty</c> and the
/// <see cref="SearchIndexer"/> indexes them once saving goes quiet.</item>
/// <item>After the commit, <see cref="WorkspaceChangeNotifier"/> reports <see cref="NodeChangeKind.ContentChanged"/>
/// or <see cref="NodeChangeKind.FormatChanged"/>.</item>
/// </list>
/// </remarks>
public sealed class DocumentService : IDocumentContentSaver
{
    // Below this length, canonicalizing and hashing on the caller's thread costs less than a thread-pool hop.
    private const int InlinePreparationLimit = 32 * 1024;

    private readonly LocalDatabase _database;
    private readonly PendingOperationQueue _queue;
    private readonly WorkspaceChangeNotifier _notifier;
    private readonly TimeProvider _timeProvider;

    // The last save of each document with a save in flight; the next save of the same document waits for it.
    private readonly Lock _saveOrderGate = new();
    private readonly Dictionary<NodeId, Task<DocumentSaveResult>> _lastSaves = [];

    /// <summary>Creates the service.</summary>
    /// <param name="database">The local database.</param>
    /// <param name="queue">The pending-operation queue that every content change appends to.</param>
    /// <param name="notifier">Receives the committed changes.</param>
    /// <param name="timeProvider">The clock; <see cref="TimeProvider.System"/> by default.</param>
    public DocumentService(
        LocalDatabase database,
        PendingOperationQueue queue,
        WorkspaceChangeNotifier notifier,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(notifier);
        _database = database;
        _queue = queue;
        _notifier = notifier;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Loads a document's text, format, checksum and version. Documents in the Trash can be read too.</summary>
    /// <exception cref="NodeNotFoundException">The node does not exist or is a folder.</exception>
    public Task<DocumentContent> GetContentAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT d.text, d.format, d.checksum, d.updated_at, d.version
                    FROM document_contents d JOIN nodes n ON n.id = d.node_id
                    WHERE d.node_id = $id AND n.kind = 'document';
                    """;
                command.With("$id", nodeId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    throw new NodeNotFoundException(nodeId, "The note no longer exists.");
                }

                return new DocumentContent(
                    nodeId,
                    reader.GetString(0),
                    DocumentFormats.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetTimestamp(3),
                    reader.GetInt64(4));
            },
            cancellationToken);

    /// <summary>
    /// Saves a document's text unconditionally (see
    /// <see cref="SaveContentAsync(NodeId, string, string?, CancellationToken)"/> with no expected checksum).
    /// </summary>
    public Task<DocumentSaveResult> SaveContentAsync(NodeId nodeId, string text, CancellationToken cancellationToken = default) =>
        SaveContentAsync(nodeId, text, expectedChecksum: null, cancellationToken);

    /// <summary>
    /// Saves a document's text. It is stored with LF line endings, and with U+FFFD for every U+0000 and unpaired
    /// surrogate (<see cref="ContentCanonicalizer.ToStorableText"/>); when the result has the stored checksum nothing is
    /// written and <see cref="DocumentSaveResult.Changed"/> is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>Documents in the Trash accept saves too, so that edits typed just before a note was trashed are never
    /// lost.</para>
    /// <para>Saves of the same document commit in call order. A text longer than 32K characters is prepared on the
    /// thread pool, so the returned task may complete later than the call returns.</para>
    /// </remarks>
    /// <param name="nodeId">The document.</param>
    /// <param name="text">The new text, with any line endings.</param>
    /// <param name="expectedChecksum">
    /// The checksum of the stored text this text is based on: the <see cref="DocumentContent.Checksum"/> the editor
    /// loaded, then the <see cref="DocumentSaveResult.Checksum"/> of its last save. <see langword="null"/> saves
    /// regardless of what is stored.
    /// </param>
    /// <param name="cancellationToken">Cancels the save before the write job starts.</param>
    /// <exception cref="NodeNotFoundException">The node does not exist or is a folder.</exception>
    /// <exception cref="DocumentConflictException">
    /// The stored text no longer has <paramref name="expectedChecksum"/> (and differs from <paramref name="text"/>);
    /// nothing was written.
    /// </exception>
    public Task<DocumentSaveResult> SaveContentAsync(NodeId nodeId, string text, string? expectedChecksum, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var prepared = text.Length <= InlinePreparationLimit
            ? Task.FromResult(Prepare(text))
            : Task.Run(() => Prepare(text), cancellationToken);
        Task<DocumentSaveResult> save;
        lock (_saveOrderGate)
        {
            _lastSaves.TryGetValue(nodeId, out var previous);
            save = SaveAfterAsync(previous, nodeId, prepared, expectedChecksum, cancellationToken);
            _lastSaves[nodeId] = save;
        }

        _ = save.ContinueWith(
            completed =>
            {
                lock (_saveOrderGate)
                {
                    if (_lastSaves.TryGetValue(nodeId, out var last) && last == completed)
                    {
                        _lastSaves.Remove(nodeId);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return save;
    }

    /// <summary>
    /// Overrides a document's format (the status bar's mode picker). A later rename no longer changes it, because it
    /// no longer matches the name's extension.
    /// </summary>
    /// <returns>The document after the change (unchanged when it already had the format).</returns>
    /// <exception cref="NodeNotFoundException">The node does not exist, is in the Trash or is a folder.</exception>
    public Task<NodeInfo> SetFormatAsync(NodeId nodeId, DocumentFormat format, CancellationToken cancellationToken = default)
    {
        _ = format.ToWireName(); // validates the value before queuing
        return WriteAsync(
            (context, changes) =>
            {
                var node = RequireDocument(context, nodeId);
                if (node.IsTrashed)
                {
                    throw new NodeNotFoundException(nodeId, $"\"{node.Name}\" is in the Trash. Restore it first.");
                }

                if (node.Format == format)
                {
                    return node;
                }

                var now = Timestamps.Now(_timeProvider);
                context.Execute(
                    "UPDATE document_contents SET format = $format, updated_at = $now, version = version + 1 WHERE node_id = $id;",
                    ("$format", format.ToWireName()),
                    ("$now", now),
                    ("$id", nodeId));
                context.Execute("UPDATE nodes SET updated_at = $now WHERE id = $id;", ("$now", now), ("$id", nodeId));
                var checksum = (string)(context.Scalar("SELECT checksum FROM document_contents WHERE node_id = $id;", ("$id", nodeId))
                    ?? throw new InvalidOperationException($"Document {nodeId} has no content row."));
                var updated = node with { Format = format, UpdatedAt = now };
                _queue.EnqueueUpdateContent(context, updated, checksum, format, LocalActorKind.User);
                changes.Add(node.WorkspaceId, nodeId, NodeChangeKind.FormatChanged);
                return updated;
            },
            cancellationToken);
    }

    /// <summary>
    /// The live documents whose text links to <paramref name="nodeId"/> (<c>nimpad://note/{id}</c>), by name. A note
    /// that links to itself is not its own backlink; documents in the Trash are left out.
    /// </summary>
    public Task<IReadOnlyList<NodeInfo>> GetBacklinksAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync<IReadOnlyList<NodeInfo>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    SELECT {NodeRows.Columns}
                    FROM document_links l JOIN nodes n ON n.id = l.source_id
                    LEFT JOIN document_contents d ON d.node_id = n.id
                    WHERE l.target_id = $id AND l.source_id <> $id AND n.deleted_at IS NULL
                    ORDER BY n.name COLLATE NOCASE, n.id;
                    """;
                command.With("$id", nodeId);
                return NodeRows.ReadAll(command);
            },
            cancellationToken);

    /// <summary>The notes a document links to (including links to notes that no longer exist), in ID order.</summary>
    public Task<IReadOnlyList<NodeId>> GetOutgoingLinksAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync<IReadOnlyList<NodeId>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT target_id FROM document_links WHERE source_id = $id ORDER BY target_id;";
                command.With("$id", nodeId);
                using var reader = command.ExecuteReader();
                var targets = new List<NodeId>();
                while (reader.Read())
                {
                    targets.Add(reader.GetNodeId(0));
                }

                return targets;
            },
            cancellationToken);

    /// <summary>Called when preparing a text for saving starts, on the thread that prepares it (tests only).</summary>
    internal Action<string>? PreparingText { get; set; }

    // Waits for the document's previous save (whatever its outcome), then submits this one: a long text still being
    // prepared therefore never lets a later, shorter save commit first. With nothing to wait for, it reaches the
    // writer synchronously.
    private async Task<DocumentSaveResult> SaveAfterAsync(
        Task<DocumentSaveResult>? previous,
        NodeId nodeId,
        Task<PreparedText> preparation,
        string? expectedChecksum,
        CancellationToken cancellationToken)
    {
        if (previous is not null)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // That save reported its own failure to its caller; this one is independent of it.
            }
        }

        var prepared = await preparation.ConfigureAwait(false);
        return await WriteAsync((context, changes) => Save(context, nodeId, prepared, expectedChecksum, changes), cancellationToken).ConfigureAwait(false);
    }

    private PreparedText Prepare(string text)
    {
        PreparingText?.Invoke(text);
        return PreparedText.From(text);
    }

    private DocumentSaveResult Save(WriteContext context, NodeId nodeId, PreparedText prepared, string? expectedChecksum, ChangeCollector changes)
    {
        var node = RequireDocument(context, nodeId);
        string storedChecksum;
        long version;
        DateTimeOffset updatedAt;
        DocumentFormat format;
        using (var command = context.CreateCommand(
            "SELECT checksum, version, updated_at, format FROM document_contents WHERE node_id = $id;").With("$id", nodeId))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Document {nodeId} has no content row.");
            }

            storedChecksum = reader.GetString(0);
            version = reader.GetInt64(1);
            updatedAt = reader.GetTimestamp(2);
            format = DocumentFormats.Parse(reader.GetString(3));
        }

        if (string.Equals(storedChecksum, prepared.Checksum, StringComparison.Ordinal))
        {
            return new DocumentSaveResult(nodeId, false, storedChecksum, version, updatedAt);
        }

        if (expectedChecksum is not null && !string.Equals(storedChecksum, expectedChecksum, StringComparison.Ordinal))
        {
            throw new DocumentConflictException(nodeId, expectedChecksum, storedChecksum);
        }

        var now = Timestamps.Now(_timeProvider);
        context.Execute(
            """
            UPDATE document_contents
            SET text = $text, checksum = $checksum, char_count = $chars, preview = $preview, updated_at = $now,
                fts_dirty = $dirty, version = version + 1
            WHERE node_id = $id;
            """,
            ("$text", prepared.Text),
            ("$checksum", prepared.Checksum),
            ("$chars", prepared.Text.Length),
            ("$preview", prepared.Preview),
            ("$now", now),
            ("$dirty", prepared.DeferIndexing),
            ("$id", nodeId));
        context.Execute(
            "UPDATE nodes SET content_updated_at = $now, updated_at = $now WHERE id = $id;",
            ("$now", now),
            ("$id", nodeId));
        DocumentLinks.Replace(context, nodeId, prepared.LinkTargets);
        var updated = node with { ContentUpdatedAt = now, UpdatedAt = now };
        _queue.EnqueueUpdateContent(context, updated, prepared.Checksum, format, LocalActorKind.User);
        changes.Add(node.WorkspaceId, nodeId, NodeChangeKind.ContentChanged);
        return new DocumentSaveResult(nodeId, true, prepared.Checksum, version + 1, now);
    }

    private static NodeInfo RequireDocument(WriteContext context, NodeId nodeId)
    {
        var node = NodeRows.Get(context, nodeId) ?? throw new NodeNotFoundException(nodeId, "The note no longer exists.");
        return node.IsDocument ? node : throw new NodeNotFoundException(nodeId, $"\"{node.Name}\" is a folder, not a note.");
    }

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

    // Everything a save derives from the text, computed before the write job so the writer thread only writes.
    internal sealed record PreparedText(string Text, string Checksum, string Preview, HashSet<NodeId> LinkTargets, bool DeferIndexing)
    {
        public static PreparedText From(string text)
        {
            // The stored form: LF line endings, and nothing the server cannot store (U+0000 and unpaired surrogates
            // become U+FFFD), so the checksum queued for the push is the checksum of what the server will keep.
            var canonical = ContentCanonicalizer.ToStorableText(ContentCanonicalizer.NormalizeLineEndings(text));
            return new PreparedText(
                canonical,
                ContentCanonicalizer.ChecksumOfCanonical(canonical),
                DocumentPreview.Create(canonical),
                LinkParser.ExtractTargets(canonical).ToHashSet(),
                FtsIndex.ShouldDefer(canonical));
        }
    }
}
