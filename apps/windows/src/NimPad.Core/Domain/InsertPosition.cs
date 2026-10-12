using NimPad.Core.Ids;

namespace NimPad.Core.Domain;

/// <summary>Where an inserted, moved or copied node goes among its new siblings.</summary>
public enum InsertPlacement
{
    /// <summary>After the last sibling (the default).</summary>
    End,

    /// <summary>Before the first sibling.</summary>
    Start,

    /// <summary>Immediately before <see cref="InsertPosition.Sibling"/>.</summary>
    Before,

    /// <summary>Immediately after <see cref="InsertPosition.Sibling"/>.</summary>
    After,
}

/// <summary>
/// A position hint among siblings. <c>default</c> is <see cref="End"/>. <see cref="Before"/> and <see cref="After"/>
/// name a live sibling under the destination parent (or, for the Pinned section, another pinned node).
/// </summary>
public readonly record struct InsertPosition
{
    private InsertPosition(InsertPlacement placement, NodeId? sibling)
    {
        Placement = placement;
        Sibling = sibling;
    }

    /// <summary>How to place the node.</summary>
    public InsertPlacement Placement { get; }

    /// <summary>The reference sibling for <see cref="InsertPlacement.Before"/> and <see cref="InsertPlacement.After"/>.</summary>
    public NodeId? Sibling { get; }

    /// <summary>After the last sibling.</summary>
    public static InsertPosition End => default;

    /// <summary>Before the first sibling.</summary>
    public static InsertPosition Start => new(InsertPlacement.Start, null);

    /// <summary>Immediately before <paramref name="sibling"/>.</summary>
    public static InsertPosition Before(NodeId sibling) => new(InsertPlacement.Before, sibling);

    /// <summary>Immediately after <paramref name="sibling"/>.</summary>
    public static InsertPosition After(NodeId sibling) => new(InsertPlacement.After, sibling);
}
