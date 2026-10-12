using Microsoft.Data.Sqlite;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Storage;

namespace NimPad.Core.Services;

/// <summary>
/// Shared SQL for reading <see cref="NodeInfo"/> rows. Queries select <see cref="Columns"/> from <see cref="From"/>
/// (alias <c>n</c> for nodes, <c>d</c> for document_contents) and map rows with <see cref="Read"/>.
/// </summary>
internal static class NodeRows
{
    /// <summary>The selected columns, in the order <see cref="Read"/> expects (16 columns starting at ordinal 0).</summary>
    public const string Columns =
        "n.id, n.workspace_id, n.parent_id, n.kind, n.name, n.position, n.is_pinned, n.pin_position, n.created_at, " +
        "n.updated_at, n.content_updated_at, n.deleted_at, n.trash_batch_id, n.meta_server_rev, n.content_server_rev, " +
        "d.format";

    /// <summary>The number of columns in <see cref="Columns"/>.</summary>
    public const int ColumnCount = 16;

    /// <summary>The FROM clause matching <see cref="Columns"/>.</summary>
    public const string From = "FROM nodes n LEFT JOIN document_contents d ON d.node_id = n.id";

    /// <summary>Maps the current row (columns from <see cref="Columns"/> at ordinal 0).</summary>
    public static NodeInfo Read(SqliteDataReader reader) => new(
        reader.GetNodeId(0),
        WorkspaceId.Parse(reader.GetString(1)),
        reader.GetNodeIdOrNull(2),
        NodeKinds.Parse(reader.GetString(3)),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6) != 0,
        reader.GetStringOrNull(7),
        reader.GetTimestamp(8),
        reader.GetTimestamp(9),
        reader.GetTimestampOrNull(10),
        reader.GetTimestampOrNull(11),
        reader.IsDBNull(12) ? null : TrashBatchId.Parse(reader.GetString(12)),
        reader.GetInt64OrNull(13),
        reader.GetInt64OrNull(14),
        reader.IsDBNull(15) ? null : DocumentFormats.Parse(reader.GetString(15)));

    /// <summary>Runs <paramref name="command"/> and maps every row.</summary>
    public static List<NodeInfo> ReadAll(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var nodes = new List<NodeInfo>();
        while (reader.Read())
        {
            nodes.Add(Read(reader));
        }

        return nodes;
    }

    /// <summary>Loads one node in any state, or <see langword="null"/>.</summary>
    public static NodeInfo? Get(WriteContext context, NodeId id)
    {
        using var command = context.CreateCommand($"SELECT {Columns} {From} WHERE n.id = $id;").With("$id", id);
        return ReadAll(command) is [var node] ? node : null;
    }

    /// <summary>Loads one node in any state, or <see langword="null"/>.</summary>
    public static NodeInfo? Get(SqliteConnection connection, NodeId id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} {From} WHERE n.id = $id;";
        command.With("$id", id);
        return ReadAll(command) is [var node] ? node : null;
    }
}
