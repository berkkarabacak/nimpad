using NimPad.Core.Ids;

namespace NimPad.Core.Domain;

/// <summary>How the explorer orders the children of a folder (the sidebar's sort menu).</summary>
public enum ExplorerSortOrder
{
    /// <summary>The user's own order (drag and drop); siblings sort by position.</summary>
    Manual,

    /// <summary>Most recently edited first.</summary>
    LastEdited,

    /// <summary>Newest first.</summary>
    DateCreated,

    /// <summary>Name, A to Z.</summary>
    Name,
}

/// <summary>This device's window layout (stored in <c>ui_state</c>; never synced).</summary>
public sealed record UiState
{
    /// <summary>The docked sidebar width from the design (288 px).</summary>
    public const double DefaultSidebarWidth = 288;

    /// <summary>The folders expanded in the explorer.</summary>
    public IReadOnlyList<NodeId> ExpandedFolderIds { get; init; } = [];

    /// <summary>The sidebar width in device-independent pixels.</summary>
    public double SidebarWidth { get; init; } = DefaultSidebarWidth;

    /// <summary>Whether the sidebar is collapsed.</summary>
    public bool IsSidebarCollapsed { get; init; }

    /// <summary>The explorer's sort order.</summary>
    public ExplorerSortOrder SortOrder { get; init; } = ExplorerSortOrder.Manual;

    /// <summary>The node selected in the explorer, if any.</summary>
    public NodeId? SelectedNodeId { get; init; }

    /// <summary>Whether the right-hand context panel is shown (hidden by default).</summary>
    public bool IsContextPanelVisible { get; init; }

    /// <summary>The defaults of a first run.</summary>
    public static UiState Default { get; } = new();

    /// <summary>Value equality, comparing <see cref="ExpandedFolderIds"/> element by element.</summary>
    public bool Equals(UiState? other) =>
        other is not null
        && ExpandedFolderIds.SequenceEqual(other.ExpandedFolderIds)
        && SidebarWidth.Equals(other.SidebarWidth)
        && IsSidebarCollapsed == other.IsSidebarCollapsed
        && SortOrder == other.SortOrder
        && SelectedNodeId == other.SelectedNodeId
        && IsContextPanelVisible == other.IsContextPanelVisible;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in ExpandedFolderIds)
        {
            hash.Add(id);
        }

        hash.Add(SidebarWidth);
        hash.Add(IsSidebarCollapsed);
        hash.Add(SortOrder);
        hash.Add(SelectedNodeId);
        hash.Add(IsContextPanelVisible);
        return hash.ToHashCode();
    }
}
