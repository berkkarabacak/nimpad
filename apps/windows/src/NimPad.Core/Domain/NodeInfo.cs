using NimPad.Core.Ids;

namespace NimPad.Core.Domain;

/// <summary>
/// The metadata of one folder or document (no text). Mirrors a <c>nodes</c> row plus the document's format.
/// Siblings sort by <c>(Position, Id)</c> using ordinal comparison; see <see cref="SiblingComparer"/>.
/// </summary>
/// <param name="Id">The node's stable ID.</param>
/// <param name="WorkspaceId">The workspace that contains the node.</param>
/// <param name="ParentId">The parent folder, or <see langword="null"/> at the workspace root.</param>
/// <param name="Kind">Folder or document.</param>
/// <param name="Name">The display name (trimmed, 1–255 characters).</param>
/// <param name="Position">The fractional-index key among siblings.</param>
/// <param name="IsPinned">Whether the node is in the Pinned section.</param>
/// <param name="PinPosition">The fractional-index key within the Pinned section; set exactly when pinned.</param>
/// <param name="CreatedAt">When the node was created (UTC, millisecond precision).</param>
/// <param name="UpdatedAt">The last metadata or content change.</param>
/// <param name="ContentUpdatedAt">The last text change of a document; always <see langword="null"/> for folders.</param>
/// <param name="DeletedAt">When the node was moved to the Trash, or <see langword="null"/> when live.</param>
/// <param name="TrashBatchId">The Trash batch, set together with <paramref name="DeletedAt"/>.</param>
/// <param name="MetaServerRev">The server metadata revision; <see langword="null"/> while never synced.</param>
/// <param name="ContentServerRev">The server content revision; <see langword="null"/> while never synced.</param>
/// <param name="Format">The document format; <see langword="null"/> for folders.</param>
public sealed record NodeInfo(
    NodeId Id,
    WorkspaceId WorkspaceId,
    NodeId? ParentId,
    NodeKind Kind,
    string Name,
    string Position,
    bool IsPinned,
    string? PinPosition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ContentUpdatedAt,
    DateTimeOffset? DeletedAt,
    TrashBatchId? TrashBatchId,
    long? MetaServerRev,
    long? ContentServerRev,
    DocumentFormat? Format)
{
    /// <summary>True for folders.</summary>
    public bool IsFolder => Kind == NodeKind.Folder;

    /// <summary>True for documents.</summary>
    public bool IsDocument => Kind == NodeKind.Document;

    /// <summary>True when the node is in the Trash.</summary>
    public bool IsTrashed => DeletedAt is not null;

    /// <summary>True when the node has never been acknowledged by the server.</summary>
    public bool IsNeverSynced => MetaServerRev is null;
}

/// <summary>Sibling order: by <c>Position</c>, ties broken by <c>Id</c>, both compared ordinally as text.</summary>
public sealed class SiblingComparer : IComparer<NodeInfo>
{
    /// <summary>The shared instance.</summary>
    public static SiblingComparer Instance { get; } = new();

    /// <inheritdoc/>
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

        var byPosition = string.CompareOrdinal(x.Position, y.Position);
        return byPosition != 0 ? byPosition : string.CompareOrdinal(x.Id.ToString(), y.Id.ToString());
    }
}
