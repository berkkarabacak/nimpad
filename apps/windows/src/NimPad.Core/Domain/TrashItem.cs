using NimPad.Core.Ids;

namespace NimPad.Core.Domain;

/// <summary>
/// One row of the Trash view: a trashed node whose parent is not part of the same Trash batch (the top of what
/// one action deleted). Restoring it restores the node and its descendants from the same batch.
/// </summary>
/// <param name="Node">The trashed node's metadata.</param>
/// <param name="BatchId">The Trash batch the node belongs to.</param>
/// <param name="DeletedAt">When it was moved to the Trash.</param>
/// <param name="PurgeAt">When it becomes eligible for automatic purge (<paramref name="DeletedAt"/> + retention).</param>
/// <param name="DaysLeft">Whole days until <paramref name="PurgeAt"/>, rounded up; 0 when already due.</param>
/// <param name="DescendantCount">How many trashed descendants from the same batch come back with it.</param>
/// <param name="OriginalParentAvailable">
/// True when the original parent is a live folder; otherwise restoring puts the node at the workspace root.
/// </param>
public sealed record TrashItem(
    NodeInfo Node,
    TrashBatchId BatchId,
    DateTimeOffset DeletedAt,
    DateTimeOffset PurgeAt,
    int DaysLeft,
    int DescendantCount,
    bool OriginalParentAvailable);
