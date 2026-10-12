using CommunityToolkit.Mvvm.ComponentModel;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;

namespace NimPad.Presentation.Explorer;

/// <summary>What the explorer shows.</summary>
public enum ExplorerMode
{
    /// <summary>Pinned, Recent and the workspace tree (the default).</summary>
    Tree,

    /// <summary>Search results (while the search field has text).</summary>
    Search,

    /// <summary>The Trash.</summary>
    Trash,
}

/// <summary>The sections of the tree view.</summary>
public enum ExplorerSection
{
    /// <summary>"PINNED": pinned notes and folders, in pin order.</summary>
    Pinned,

    /// <summary>"RECENT": the eight most recently edited notes.</summary>
    Recent,

    /// <summary>The workspace folder tree ("MY WORKSPACE").</summary>
    Workspace,
}

/// <summary>Where a dragged row would land relative to the row under the pointer.</summary>
public enum DropPosition
{
    /// <summary>Just above the target row (same parent, before it).</summary>
    Before,

    /// <summary>Inside the target folder (or the workspace root for its header).</summary>
    Inside,

    /// <summary>Just below the target row (same parent, after it).</summary>
    After,
}

/// <summary>A piece of a highlighted text: matched text is drawn with the match background.</summary>
/// <param name="Text">The text of the piece.</param>
/// <param name="IsMatch">True for matched text.</param>
public readonly record struct HighlightSegment(string Text, bool IsMatch)
{
    /// <summary>Splits <paramref name="text"/> into plain and matched pieces.</summary>
    public static IReadOnlyList<HighlightSegment> Split(string text, IReadOnlyList<TextRange> highlights)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(highlights);
        var segments = new List<HighlightSegment>();
        var position = 0;
        foreach (var range in highlights.Where(range => range.Length > 0).OrderBy(range => range.Start))
        {
            var start = Math.Clamp(range.Start, position, text.Length);
            var end = Math.Clamp(range.End, start, text.Length);
            if (start > position)
            {
                segments.Add(new HighlightSegment(text[position..start], false));
            }

            if (end > start)
            {
                segments.Add(new HighlightSegment(text[start..end], true));
            }

            position = end;
        }

        if (position < text.Length || segments.Count == 0)
        {
            segments.Add(new HighlightSegment(text[position..], false));
        }

        return segments;
    }
}

/// <summary>Identifies a row of the flattened list (a node can appear in several sections).</summary>
internal readonly record struct ExplorerRowKey(ExplorerMode Mode, ExplorerSection Section, NodeId NodeId, bool IsHeader);

/// <summary>
/// One row of the explorer's single virtualized list. The App picks a data template by type:
/// <see cref="ExplorerHeaderRowViewModel"/>, <see cref="ExplorerNodeRowViewModel"/>,
/// <see cref="ExplorerSearchResultRowViewModel"/> or <see cref="ExplorerTrashRowViewModel"/>.
/// </summary>
public abstract partial class ExplorerRowViewModel : ObservableObject
{
    internal ExplorerRowViewModel(ExplorerRowKey key)
    {
        Key = key;
    }

    /// <summary>True for the selected row.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    /// <summary>The node the row shows, or <see langword="null"/> for a header.</summary>
    public NodeId? NodeId => Key.IsHeader ? null : Key.NodeId;

    internal ExplorerRowKey Key { get; }
}

/// <summary>A section header: <c>PINNED</c>, <c>RECENT</c> or the workspace name (<c>MY WORKSPACE</c>).</summary>
public sealed class ExplorerHeaderRowViewModel : ExplorerRowViewModel
{
    internal ExplorerHeaderRowViewModel(ExplorerSection section, string title)
        : base(new ExplorerRowKey(ExplorerMode.Tree, section, default, true))
    {
        Section = section;
        Title = title;
    }

    /// <summary>The section.</summary>
    public ExplorerSection Section { get; }

    /// <summary>The header text, uppercase.</summary>
    public string Title { get; internal set; }

    /// <summary>True for the workspace header, which stands for the workspace root (a drop target).</summary>
    public bool IsWorkspaceRoot => Section == ExplorerSection.Workspace;
}

/// <summary>
/// A folder or note in the Pinned, Recent or Workspace section. Pinned and Recent rows show the title, a preview line
/// and a time like the design's note rows (<see cref="ShowsDetails"/>); tree rows are indented by <see cref="Depth"/>.
/// </summary>
public sealed partial class ExplorerNodeRowViewModel : ExplorerRowViewModel
{
    internal ExplorerNodeRowViewModel(ExplorerSection section, NodeInfo node)
        : base(new ExplorerRowKey(ExplorerMode.Tree, section, node.Id, false))
    {
        Section = section;
        Node = node;
        Name = node.Name;
        Kind = node.Kind;
    }

    /// <summary>The section the row is in.</summary>
    public ExplorerSection Section { get; }

    /// <summary>The node's ID.</summary>
    public NodeId Id => Key.NodeId;

    /// <summary>Folder or note.</summary>
    public NodeKind Kind { get; }

    /// <summary>True for folders.</summary>
    public bool IsFolder => Kind == NodeKind.Folder;

    /// <summary>True for Pinned and Recent rows (title, preview and time).</summary>
    public bool ShowsDetails => Section != ExplorerSection.Workspace;

    /// <summary>The node's name.</summary>
    [ObservableProperty]
    public partial string Name { get; private set; }

    /// <summary>The nesting level in the tree (0 at the root; always 0 in Pinned and Recent).</summary>
    [ObservableProperty]
    public partial int Depth { get; private set; }

    /// <summary>True for an expanded folder.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; private set; }

    /// <summary>True for a folder with live children (it shows a chevron).</summary>
    [ObservableProperty]
    public partial bool HasChildren { get; private set; }

    /// <summary>True when the node is pinned.</summary>
    [ObservableProperty]
    public partial bool IsPinned { get; private set; }

    /// <summary>The relative time of the last edit: <c>2:14 PM</c>, <c>Sat</c>, <c>Oct 3</c>.</summary>
    [ObservableProperty]
    public partial string TimestampText { get; private set; } = string.Empty;

    /// <summary>A note's first line (empty for folders).</summary>
    [ObservableProperty]
    public partial string Preview { get; private set; } = string.Empty;

    /// <summary>True while the name is an inline text box (F2, or right after New folder).</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; internal set; }

    /// <summary>The inline rename text.</summary>
    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>Why the typed name is not accepted (shown inline), or <see langword="null"/>.</summary>
    [ObservableProperty]
    public partial string? RenameError { get; internal set; }

    internal NodeInfo Node { get; private set; }

    // The tree build that last produced this row (see ExplorerViewModel.BuildTreeRows).
    internal long Generation { get; set; }

    // What TimestampText was formatted from (see ExplorerViewModel.TimestampFor).
    internal DateTimeOffset? TimestampSource { get; set; }

    internal DateOnly TimestampDay { get; set; }

    partial void OnRenameTextChanged(string value) => RenameError = null;

    internal void Update(NodeInfo node, int depth, bool isExpanded, bool hasChildren, string timestamp, string preview)
    {
        Node = node;
        Name = node.Name;
        Depth = depth;
        IsExpanded = isExpanded;
        HasChildren = hasChildren;
        IsPinned = node.IsPinned;
        TimestampText = timestamp;
        Preview = preview;
    }
}

/// <summary>
/// A search hit: title and snippet with highlighted matches, the folder path and the time. A refreshed search updates
/// the row of the same node in place.
/// </summary>
public sealed partial class ExplorerSearchResultRowViewModel : ExplorerRowViewModel
{
    internal ExplorerSearchResultRowViewModel(SearchResult result, string timestamp)
        : base(new ExplorerRowKey(ExplorerMode.Search, ExplorerSection.Workspace, result.NodeId, false))
    {
        Kind = result.Kind;
        Result = result;
        Title = result.Name;
        TitleSegments = HighlightSegment.Split(result.Name, result.NameHighlights);
        PathText = string.Join(" / ", result.Path);
        Snippet = result.Snippet;
        SnippetSegments = HighlightSegment.Split(result.Snippet, result.SnippetHighlights);
        TimestampText = timestamp;
    }

    /// <summary>The note or folder.</summary>
    public NodeId Id => Key.NodeId;

    /// <summary>Folder or note.</summary>
    public NodeKind Kind { get; }

    /// <summary>The name.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>The name in plain and matched pieces.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HighlightSegment> TitleSegments { get; private set; }

    /// <summary>The containing folders, <c>Projects / 2026</c> (empty at the root).</summary>
    [ObservableProperty]
    public partial string PathText { get; private set; }

    /// <summary>The line around the first match (or the preview line).</summary>
    [ObservableProperty]
    public partial string Snippet { get; private set; }

    /// <summary>The snippet in plain and matched pieces.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HighlightSegment> SnippetSegments { get; private set; }

    /// <summary>The relative time of the last edit (empty for folders).</summary>
    [ObservableProperty]
    public partial string TimestampText { get; private set; }

    internal SearchResult Result { get; private set; }

    // Applies a newer search's result for the same node; only what changed raises PropertyChanged.
    internal void Update(SearchResult result, string timestamp)
    {
        Result = result;
        Title = result.Name;
        var titleSegments = HighlightSegment.Split(result.Name, result.NameHighlights);
        if (!titleSegments.SequenceEqual(TitleSegments))
        {
            TitleSegments = titleSegments;
        }

        PathText = string.Join(" / ", result.Path);
        Snippet = result.Snippet;
        var snippetSegments = HighlightSegment.Split(result.Snippet, result.SnippetHighlights);
        if (!snippetSegments.SequenceEqual(SnippetSegments))
        {
            SnippetSegments = snippetSegments;
        }

        TimestampText = timestamp;
    }
}

/// <summary>
/// A Trash item: title, preview, "Deleted today" or "12 days left", Restore and Delete forever. A reloaded Trash
/// updates the row of the same node in place.
/// </summary>
public sealed partial class ExplorerTrashRowViewModel : ExplorerRowViewModel
{
    internal ExplorerTrashRowViewModel(TrashItem item, string preview, string statusText)
        : base(new ExplorerRowKey(ExplorerMode.Trash, ExplorerSection.Workspace, item.Node.Id, false))
    {
        Kind = item.Node.Kind;
        Item = item;
        Title = item.Node.Name;
        Preview = preview;
        StatusText = statusText;
        DescendantCount = item.DescendantCount;
    }

    /// <summary>The trashed node.</summary>
    public NodeId Id => Key.NodeId;

    /// <summary>Folder or note.</summary>
    public NodeKind Kind { get; }

    /// <summary>The name.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>A note's first line, or what a folder holds.</summary>
    [ObservableProperty]
    public partial string Preview { get; private set; }

    /// <summary><c>Deleted today</c>, or the days left before it is deleted forever (<c>12 days left</c>).</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>How many trashed items come back with it.</summary>
    [ObservableProperty]
    public partial int DescendantCount { get; private set; }

    internal TrashItem Item { get; private set; }

    internal void Update(TrashItem item, string preview, string statusText)
    {
        Item = item;
        Title = item.Node.Name;
        Preview = preview;
        StatusText = statusText;
        DescendantCount = item.DescendantCount;
    }
}

/// <summary>The kind of empty state, for its icon.</summary>
public enum ExplorerEmptyStateKind
{
    /// <summary>The workspace has no notes.</summary>
    NoNotes,

    /// <summary>The search found nothing.</summary>
    NoResults,

    /// <summary>The Trash is empty.</summary>
    TrashEmpty,
}

/// <summary>An empty state of the sidebar (design page 8): one plain sentence and at most one action.</summary>
/// <param name="Kind">Which one (icon).</param>
/// <param name="Title">For example <c>No notes yet</c>.</param>
/// <param name="Message">The sentence under the title.</param>
/// <param name="ActionText">The action label, or <see langword="null"/>.</param>
/// <param name="ActionShortcutText">The action's shortcut, or <see langword="null"/>.</param>
/// <param name="ActionCommand">The action, or <see langword="null"/>.</param>
public sealed record ExplorerEmptyState(
    ExplorerEmptyStateKind Kind,
    string Title,
    string Message,
    string? ActionText,
    string? ActionShortcutText,
    System.Windows.Input.ICommand? ActionCommand);

/// <summary>One choice of the sort menu.</summary>
/// <param name="Order">The order.</param>
/// <param name="Label">The menu label.</param>
public sealed record ExplorerSortOption(ExplorerSortOrder Order, string Label);
