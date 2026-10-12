using NimPad.Core.Ids;

namespace NimPad.Core.Domain;

/// <summary>What a tab shows.</summary>
public enum SessionTabKind
{
    /// <summary>A managed note in the workspace (<see cref="SessionTab.NodeId"/>).</summary>
    Managed,

    /// <summary>An external file opened from disk (<see cref="SessionTab.FilePath"/>).</summary>
    External,
}

/// <summary>
/// One open tab of this device's session, with the editor's view state. Create one with <see cref="ForNote"/> or
/// <see cref="ForFile"/> and set the rest with <c>with</c>.
/// </summary>
/// <param name="TabId">The tab's stable ID.</param>
/// <param name="Kind">Managed note or external file.</param>
/// <param name="NodeId">The note, for a managed tab; otherwise <see langword="null"/>.</param>
/// <param name="FilePath">The full path, for an external tab; otherwise <see langword="null"/>.</param>
/// <param name="Title">The title shown before the note or file has loaded.</param>
public sealed record SessionTab(Guid TabId, SessionTabKind Kind, NodeId? NodeId, string? FilePath, string Title)
{
    /// <summary>Whether the tab is pinned (kept at the start of the strip, no close button).</summary>
    public bool IsPinned { get; init; }

    /// <summary>
    /// The tab's place in the strip. When saving, tabs are ordered by this value (ties keep their list order) and
    /// renumbered 0, 1, 2, …; loaded tabs carry their index.
    /// </summary>
    public int Order { get; init; }

    /// <summary>Whether this is the selected tab (at most one per session).</summary>
    public bool IsActive { get; init; }

    /// <summary>The caret offset in the text.</summary>
    public int CaretOffset { get; init; }

    /// <summary>The selection's start offset.</summary>
    public int SelectionStart { get; init; }

    /// <summary>The selection's length (0 for none).</summary>
    public int SelectionLength { get; init; }

    /// <summary>The editor's vertical scroll offset, in device-independent pixels.</summary>
    public double VerticalScrollOffset { get; init; }

    /// <summary>The editor's horizontal scroll offset, in device-independent pixels.</summary>
    public double HorizontalScrollOffset { get; init; }

    /// <summary>A tab showing a managed note.</summary>
    /// <param name="nodeId">The note.</param>
    /// <param name="title">The title to show until the note loads (usually its name).</param>
    /// <param name="tabId">The tab ID; a new one by default.</param>
    public static SessionTab ForNote(NodeId nodeId, string title, Guid? tabId = null) =>
        new(tabId ?? Guid.CreateVersion7(), SessionTabKind.Managed, nodeId, null, title);

    /// <summary>A tab showing an external file.</summary>
    /// <param name="filePath">The file's full path.</param>
    /// <param name="title">The title to show until the file loads (usually the file name).</param>
    /// <param name="tabId">The tab ID; a new one by default.</param>
    public static SessionTab ForFile(string filePath, string title, Guid? tabId = null) =>
        new(tabId ?? Guid.CreateVersion7(), SessionTabKind.External, null, filePath, title);
}

/// <summary>A closed tab on the reopen stack (Ctrl+Shift+T), with the state it had when it was closed.</summary>
/// <param name="Id">The stack entry's ID (higher is more recent).</param>
/// <param name="Tab">The tab as it was when closed (<see cref="SessionTab.IsActive"/> is always false).</param>
/// <param name="ClosedAt">When it was closed.</param>
public sealed record RecentlyClosedTab(long Id, SessionTab Tab, DateTimeOffset ClosedAt);
