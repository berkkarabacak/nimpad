using NimPad.Core.Ids;

namespace NimPad.Core.Storage;

/// <summary>
/// Maintenance helpers for the <c>notes_fts</c> full-text index (FTS5, <c>rowid = nodes.row_id</c>, columns
/// <c>name</c> and <c>body</c>).
/// </summary>
/// <remarks>
/// Triggers keep the index current for ordinary writes: inserting, renaming or deleting a node updates its
/// <c>name</c>; inserting or updating a <c>document_contents</c> row with <c>fts_dirty = 0</c> updates its
/// <c>body</c>. To defer indexing of a large document, write it with <c>fts_dirty = 1</c> (the body is then left as
/// it was) and later call <see cref="Reindex"/>, or set <c>fts_dirty = 0</c>, which fires the trigger;
/// <see cref="Services.SearchIndexer"/> does that in the background. Trashed nodes stay indexed; searches join
/// <c>nodes</c> and filter on <c>deleted_at</c>.
/// </remarks>
internal static class FtsIndex
{
    /// <summary>Documents longer than this (UTF-16 code units, about 256 KB) should be indexed in the background.</summary>
    public const int LargeDocumentThreshold = 256 * 1024;

    /// <summary>True when <paramref name="text"/> is large enough that indexing should be deferred.</summary>
    public static bool ShouldDefer(string text) => text.Length > LargeDocumentThreshold;

    /// <summary>
    /// Brings the index row of <paramref name="nodeId"/> up to date with <c>nodes</c> and <c>document_contents</c> and
    /// clears its <c>fts_dirty</c> flag. Does nothing when the node does not exist.
    /// </summary>
    /// <returns><see langword="true"/> when the document was flagged <c>fts_dirty</c>.</returns>
    public static bool Reindex(WriteContext context, NodeId nodeId)
    {
        // Clearing the flag fires tr_document_contents_fts_update, which writes the body into the existing row; only
        // a missing row or a forced reindex (flag already clear) needs the row rebuilt, so large bodies are tokenized
        // once.
        var wasDirty = context.Execute(
            "UPDATE document_contents SET fts_dirty = 0 WHERE node_id = $id AND fts_dirty = 1;", ("$id", nodeId)) > 0;
        var hasRow = context.Scalar(
            "SELECT 1 FROM notes_fts WHERE rowid = (SELECT row_id FROM nodes WHERE id = $id);", ("$id", nodeId)) is not null;
        if (wasDirty && hasRow)
        {
            return true;
        }

        context.Execute(
            """
            DELETE FROM notes_fts WHERE rowid = (SELECT row_id FROM nodes WHERE id = $id);
            INSERT INTO notes_fts (rowid, name, body)
            SELECT n.row_id, n.name, coalesce(d.text, '')
            FROM nodes n LEFT JOIN document_contents d ON d.node_id = n.id
            WHERE n.id = $id;
            """,
            ("$id", nodeId));
        return wasDirty;
    }

    /// <summary>The next document waiting for its body to be indexed (<c>fts_dirty = 1</c>), or <see langword="null"/>.</summary>
    public static NodeId? NextDirty(WriteContext context) =>
        context.Scalar("SELECT node_id FROM document_contents WHERE fts_dirty = 1 LIMIT 1;") is string id ? NodeId.Parse(id) : null;

    /// <summary>Removes the index row of <paramref name="nodeId"/> (hard deletes do this automatically).</summary>
    public static void Remove(WriteContext context, NodeId nodeId) =>
        context.Execute("DELETE FROM notes_fts WHERE rowid = (SELECT row_id FROM nodes WHERE id = $id);", ("$id", nodeId));

    /// <summary>
    /// Rebuilds the whole index from the tables: every node's name, and the body of every document whose
    /// <c>fts_dirty</c> flag is clear.
    /// </summary>
    public static void RebuildAll(WriteContext context) =>
        context.Execute(
            """
            DELETE FROM notes_fts;
            INSERT INTO notes_fts (rowid, name, body)
            SELECT n.row_id, n.name, CASE WHEN d.fts_dirty = 0 THEN d.text ELSE '' END
            FROM nodes n LEFT JOIN document_contents d ON d.node_id = n.id;
            """);
}
