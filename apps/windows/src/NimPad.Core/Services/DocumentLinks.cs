using NimPad.Core.Ids;
using NimPad.Core.Storage;
using NimPad.Core.Text;

namespace NimPad.Core.Services;

/// <summary>
/// Keeps <c>document_links</c> in step with document text. Rows are <c>(source_id, target_id)</c>; a target may be
/// missing (a dangling link) and a note may link to itself (backlink queries skip that row).
/// </summary>
internal static class DocumentLinks
{
    /// <summary>Makes the stored links of <paramref name="sourceId"/> equal to <paramref name="targets"/>.</summary>
    public static void Replace(WriteContext context, NodeId sourceId, IReadOnlyCollection<NodeId> targets)
    {
        var existing = new HashSet<NodeId>();
        using (var command = context.CreateCommand("SELECT target_id FROM document_links WHERE source_id = $id;").With("$id", sourceId))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(reader.GetNodeId(0));
            }
        }

        if (existing.SetEquals(targets))
        {
            return;
        }

        foreach (var removed in existing.Where(target => !targets.Contains(target)))
        {
            context.Execute(
                "DELETE FROM document_links WHERE source_id = $source AND target_id = $target;",
                ("$source", sourceId),
                ("$target", removed));
        }

        foreach (var added in targets.Where(target => !existing.Contains(target)))
        {
            context.Execute(
                "INSERT OR IGNORE INTO document_links (source_id, target_id) VALUES ($source, $target);",
                ("$source", sourceId),
                ("$target", added));
        }
    }

    /// <summary>Stores the links found in <paramref name="text"/> for <paramref name="sourceId"/>.</summary>
    public static void ReplaceFromText(WriteContext context, NodeId sourceId, string text) =>
        Replace(context, sourceId, LinkParser.ExtractTargets(text).ToHashSet());

    /// <summary>Gives a copied document the same outgoing links as its original (the text is identical).</summary>
    public static void Copy(WriteContext context, NodeId fromId, NodeId toId) =>
        context.Execute(
            "INSERT OR IGNORE INTO document_links (source_id, target_id) SELECT $to, target_id FROM document_links WHERE source_id = $from;",
            ("$to", toId),
            ("$from", fromId));
}
