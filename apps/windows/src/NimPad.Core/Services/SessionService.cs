using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Storage;
using NimPad.Core.Time;

namespace NimPad.Core.Services;

/// <summary>
/// This device's session: the open tabs with their view state, the reopen-closed-tab stack and the window layout.
/// Device-local; none of it enters the pending-operation queue.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="SaveTabsAsync"/> replaces the whole tab list in one transaction, so a crash leaves either the old
/// or the new session, never a mix.</item>
/// <item>Managed tabs reference their note; a tab (or closed-tab entry) whose note was deleted forever disappears
/// with it. Tabs of notes in the Trash are kept; the App decides how to show them.</item>
/// </list>
/// </remarks>
public sealed class SessionService
{
    /// <summary>How many closed tabs the reopen stack keeps.</summary>
    public const int RecentlyClosedCapacity = 20;

    private const string ExpandedFoldersKey = "explorer.expandedFolderIds";
    private const string SortOrderKey = "explorer.sortOrder";
    private const string SelectedNodeKey = "explorer.selectedNodeId";
    private const string SidebarWidthKey = "sidebar.width";
    private const string SidebarCollapsedKey = "sidebar.collapsed";
    private const string ContextPanelKey = "contextPanel.visible";

    private const string TabColumns =
        "tab_id, node_id, external_path, title, tab_order, is_pinned, is_active, caret_offset, selection_start, " +
        "selection_length, scroll_y, scroll_x";

    private readonly LocalDatabase _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The local database.</param>
    /// <param name="timeProvider">The clock for timestamps; <see cref="TimeProvider.System"/> by default.</param>
    public SessionService(LocalDatabase database, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------ tabs

    /// <summary>The saved tabs in strip order (<see cref="SessionTab.Order"/> is the index).</summary>
    public Task<IReadOnlyList<SessionTab>> LoadTabsAsync(CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync<IReadOnlyList<SessionTab>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {TabColumns} FROM session_tabs ORDER BY tab_order, tab_id;";
                using var reader = command.ExecuteReader();
                var tabs = new List<SessionTab>();
                while (reader.Read())
                {
                    tabs.Add(ReadTab(reader) with { Order = tabs.Count });
                }

                return tabs;
            },
            cancellationToken);

    /// <summary>
    /// Replaces the saved session with <paramref name="tabs"/>, atomically. Managed tabs whose note no longer exists
    /// are skipped.
    /// </summary>
    /// <returns>How many tabs were saved.</returns>
    /// <exception cref="ArgumentException">Two tabs share an ID, more than one is active, or a tab is inconsistent.</exception>
    public Task<int> SaveTabsAsync(IReadOnlyList<SessionTab> tabs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var ordered = tabs
            .Select((tab, index) => (Tab: tab, Index: index))
            .OrderBy(item => item.Tab.Order)
            .ThenBy(item => item.Index)
            .Select((item, order) => Validate(item.Tab) with { Order = order })
            .ToList();
        if (ordered.Select(tab => tab.TabId).Distinct().Count() != ordered.Count)
        {
            throw new ArgumentException("Two tabs have the same ID.", nameof(tabs));
        }

        if (ordered.Count(tab => tab.IsActive) > 1)
        {
            throw new ArgumentException("At most one tab can be active.", nameof(tabs));
        }

        return _database.Writer.ExecuteAsync(
            context =>
            {
                var now = Timestamps.Now(_timeProvider);
                var saved = new JsonArray();
                foreach (var tab in ordered)
                {
                    var inserted = context.Execute(
                        """
                        INSERT INTO session_tabs (tab_id, node_id, external_path, title, tab_order, is_pinned, is_active,
                            caret_offset, selection_start, selection_length, scroll_y, scroll_x, opened_at, updated_at)
                        SELECT $tab, $node, $path, $title, $order, $pinned, $active, $caret, $selStart, $selLength,
                            $scrollY, $scrollX, $now, $now
                        WHERE $node IS NULL OR EXISTS (SELECT 1 FROM nodes WHERE id = $node)
                        ON CONFLICT (tab_id) DO UPDATE SET
                            node_id = excluded.node_id, external_path = excluded.external_path, title = excluded.title,
                            tab_order = excluded.tab_order, is_pinned = excluded.is_pinned, is_active = excluded.is_active,
                            caret_offset = excluded.caret_offset, selection_start = excluded.selection_start,
                            selection_length = excluded.selection_length, scroll_y = excluded.scroll_y,
                            scroll_x = excluded.scroll_x, updated_at = excluded.updated_at;
                        """,
                        ("$tab", tab.TabId),
                        ("$node", tab.NodeId),
                        ("$path", tab.FilePath),
                        ("$title", tab.Title),
                        ("$order", tab.Order),
                        ("$pinned", tab.IsPinned),
                        ("$active", tab.IsActive),
                        ("$caret", tab.CaretOffset),
                        ("$selStart", tab.SelectionStart),
                        ("$selLength", tab.SelectionLength),
                        ("$scrollY", tab.VerticalScrollOffset),
                        ("$scrollX", tab.HorizontalScrollOffset),
                        ("$now", now));
                    if (inserted > 0)
                    {
                        saved.Add(tab.TabId.ToString("D"));
                    }
                }

                context.Execute(
                    "DELETE FROM session_tabs WHERE tab_id NOT IN (SELECT value FROM json_each($ids));",
                    ("$ids", saved.ToJsonString()));
                return saved.Count;
            },
            cancellationToken);
    }

    // ------------------------------------------------------------------ recently closed

    /// <summary>
    /// Pushes a closed tab onto the reopen stack, dropping the oldest entries beyond
    /// <see cref="RecentlyClosedCapacity"/>.
    /// </summary>
    /// <returns>The new entry, or <see langword="null"/> when the tab's note no longer exists.</returns>
    public Task<RecentlyClosedTab?> PushRecentlyClosedAsync(SessionTab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var closed = Validate(tab) with { IsActive = false };
        return _database.Writer.ExecuteAsync(
            context =>
            {
                if (closed.NodeId is { } nodeId && context.Scalar("SELECT 1 FROM nodes WHERE id = $id;", ("$id", nodeId)) is null)
                {
                    return null;
                }

                var now = Timestamps.Now(_timeProvider);
                context.Execute(
                    """
                    INSERT INTO recently_closed (node_id, external_path, title, tab_state, closed_at)
                    VALUES ($node, $path, $title, $state, $now);
                    """,
                    ("$node", closed.NodeId),
                    ("$path", closed.FilePath),
                    ("$title", closed.Title),
                    ("$state", ToStateJson(closed)),
                    ("$now", now));
                var id = Convert.ToInt64(context.Scalar("SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
                context.Execute(
                    "DELETE FROM recently_closed WHERE seq NOT IN (SELECT seq FROM recently_closed ORDER BY seq DESC LIMIT $keep);",
                    ("$keep", RecentlyClosedCapacity));
                return new RecentlyClosedTab(id, closed, now);
            },
            cancellationToken);
    }

    /// <summary>Removes and returns the most recently closed tab, or <see langword="null"/> when the stack is empty.</summary>
    public Task<RecentlyClosedTab?> PopRecentlyClosedAsync(CancellationToken cancellationToken = default) =>
        _database.Writer.ExecuteAsync(
            context =>
            {
                RecentlyClosedTab? entry;
                using (var command = context.CreateCommand(
                    "SELECT seq, node_id, external_path, title, tab_state, closed_at FROM recently_closed ORDER BY seq DESC LIMIT 1;"))
                using (var reader = command.ExecuteReader())
                {
                    entry = reader.Read() ? ReadClosed(reader) : null;
                }

                if (entry is not null)
                {
                    context.Execute("DELETE FROM recently_closed WHERE seq = $seq;", ("$seq", entry.Id));
                }

                return entry;
            },
            cancellationToken);

    /// <summary>The reopen stack, most recently closed first.</summary>
    public Task<IReadOnlyList<RecentlyClosedTab>> GetRecentlyClosedAsync(CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync<IReadOnlyList<RecentlyClosedTab>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT seq, node_id, external_path, title, tab_state, closed_at FROM recently_closed ORDER BY seq DESC;";
                using var reader = command.ExecuteReader();
                var entries = new List<RecentlyClosedTab>();
                while (reader.Read())
                {
                    entries.Add(ReadClosed(reader));
                }

                return entries;
            },
            cancellationToken);

    /// <summary>Empties the reopen stack.</summary>
    public Task ClearRecentlyClosedAsync(CancellationToken cancellationToken = default) =>
        _database.Writer.ExecuteAsync(context => context.Execute("DELETE FROM recently_closed;"), cancellationToken);

    // ------------------------------------------------------------------ UI state

    /// <summary>The saved layout; missing or unreadable values take the <see cref="UiState.Default"/> values.</summary>
    public Task<UiState> LoadUiStateAsync(CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync(
            connection =>
            {
                var values = KeyValueRows.ReadAll(connection, KeyValueRows.UiState);
                var state = UiState.Default;
                if (KeyValueRows.TryGet<string[]>(values, ExpandedFoldersKey, out var expanded))
                {
                    var ids = new List<NodeId>(expanded.Length);
                    foreach (var text in expanded)
                    {
                        if (NodeId.TryParse(text, out var id))
                        {
                            ids.Add(id);
                        }
                    }

                    state = state with { ExpandedFolderIds = ids };
                }

                if (KeyValueRows.TryGet<double>(values, SidebarWidthKey, out var width) && double.IsFinite(width) && width > 0)
                {
                    state = state with { SidebarWidth = width };
                }

                if (KeyValueRows.TryGet<bool>(values, SidebarCollapsedKey, out var collapsed))
                {
                    state = state with { IsSidebarCollapsed = collapsed };
                }

                if (KeyValueRows.TryGet<string>(values, SortOrderKey, out var sort) && TryParseSortOrder(sort, out var order))
                {
                    state = state with { SortOrder = order };
                }

                if (KeyValueRows.TryGet<string>(values, SelectedNodeKey, out var selected) && NodeId.TryParse(selected, out var selectedId))
                {
                    state = state with { SelectedNodeId = selectedId };
                }

                if (KeyValueRows.TryGet<bool>(values, ContextPanelKey, out var panel))
                {
                    state = state with { IsContextPanelVisible = panel };
                }

                return state;
            },
            cancellationToken);

    /// <summary>
    /// Saves the layout in one transaction. Only the keys this version knows are written; others are left as they
    /// are.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The sidebar width is not a positive finite number.</exception>
    public Task SaveUiStateAsync(UiState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!double.IsFinite(state.SidebarWidth) || state.SidebarWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state.SidebarWidth, "The sidebar width must be a positive number.");
        }

        var rows = new (string Key, string Json)[]
        {
            (ExpandedFoldersKey, KeyValueRows.ToJson(state.ExpandedFolderIds.Select(id => id.ToString()).ToArray())),
            (SidebarWidthKey, KeyValueRows.ToJson(state.SidebarWidth)),
            (SidebarCollapsedKey, KeyValueRows.ToJson(state.IsSidebarCollapsed)),
            (SortOrderKey, KeyValueRows.ToJson(ToWireName(state.SortOrder))),
            (SelectedNodeKey, KeyValueRows.ToJson(state.SelectedNodeId?.ToString())),
            (ContextPanelKey, KeyValueRows.ToJson(state.IsContextPanelVisible)),
        };
        return _database.Writer.ExecuteAsync(
            context =>
            {
                var now = Timestamps.Now(_timeProvider);
                foreach (var (key, json) in rows)
                {
                    KeyValueRows.Upsert(context, KeyValueRows.UiState, key, json, now);
                }
            },
            cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    private static SessionTab Validate(SessionTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(tab.Title, nameof(tab.Title));
        var consistent = tab.Kind switch
        {
            SessionTabKind.Managed => tab.NodeId is not null && tab.FilePath is null,
            SessionTabKind.External => tab.NodeId is null && !string.IsNullOrWhiteSpace(tab.FilePath),
            _ => false,
        };
        if (!consistent)
        {
            throw new ArgumentException("A managed tab needs a note ID and no path; an external tab needs a path and no note ID.", nameof(tab));
        }

        if (tab.CaretOffset < 0 || tab.SelectionStart < 0 || tab.SelectionLength < 0)
        {
            throw new ArgumentException("Caret and selection offsets can't be negative.", nameof(tab));
        }

        if (!double.IsFinite(tab.VerticalScrollOffset) || !double.IsFinite(tab.HorizontalScrollOffset))
        {
            throw new ArgumentException("Scroll offsets must be finite numbers.", nameof(tab));
        }

        return tab;
    }

    private static SessionTab ReadTab(SqliteDataReader reader)
    {
        var tabId = Uuid.Parse(reader.GetString(0), "tab_id");
        var nodeId = reader.GetNodeIdOrNull(1);
        var path = reader.GetStringOrNull(2);
        return new SessionTab(tabId, nodeId is null ? SessionTabKind.External : SessionTabKind.Managed, nodeId, path, reader.GetString(3))
        {
            Order = reader.GetInt32(4),
            IsPinned = reader.GetInt64(5) != 0,
            IsActive = reader.GetInt64(6) != 0,
            CaretOffset = reader.GetInt32(7),
            SelectionStart = reader.GetInt32(8),
            SelectionLength = reader.GetInt32(9),
            VerticalScrollOffset = reader.GetDouble(10),
            HorizontalScrollOffset = reader.GetDouble(11),
        };
    }

    private static RecentlyClosedTab ReadClosed(SqliteDataReader reader)
    {
        var nodeId = reader.GetNodeIdOrNull(1);
        var path = reader.GetStringOrNull(2);
        var title = reader.GetString(3);
        var state = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4)) as JsonObject;
        var tabId = state?["tabId"]?.GetValue<string>() is { } text && Uuid.TryParse(text, out var parsed) ? parsed : Guid.CreateVersion7();
        var tab = new SessionTab(tabId, nodeId is null ? SessionTabKind.External : SessionTabKind.Managed, nodeId, path, title)
        {
            IsPinned = Get(state, "pinned", false),
            CaretOffset = Get(state, "caretOffset", 0),
            SelectionStart = Get(state, "selectionStart", 0),
            SelectionLength = Get(state, "selectionLength", 0),
            VerticalScrollOffset = Get(state, "verticalScrollOffset", 0.0),
            HorizontalScrollOffset = Get(state, "horizontalScrollOffset", 0.0),
        };
        return new RecentlyClosedTab(reader.GetInt64(0), tab, reader.GetTimestamp(5));
    }

    private static T Get<T>(JsonObject? state, string name, T fallback)
    {
        try
        {
            return state?[name] is { } node ? node.GetValue<T>() : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    private static string ToStateJson(SessionTab tab) =>
        new JsonObject
        {
            ["tabId"] = tab.TabId.ToString("D"),
            ["pinned"] = tab.IsPinned,
            ["caretOffset"] = tab.CaretOffset,
            ["selectionStart"] = tab.SelectionStart,
            ["selectionLength"] = tab.SelectionLength,
            ["verticalScrollOffset"] = tab.VerticalScrollOffset,
            ["horizontalScrollOffset"] = tab.HorizontalScrollOffset,
        }.ToJsonString(JsonSerializerOptions.Default);

    private static string ToWireName(ExplorerSortOrder order) => order switch
    {
        ExplorerSortOrder.Manual => "manual",
        ExplorerSortOrder.LastEdited => "lastEdited",
        ExplorerSortOrder.DateCreated => "dateCreated",
        ExplorerSortOrder.Name => "name",
        _ => throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown sort order."),
    };

    private static bool TryParseSortOrder(string name, out ExplorerSortOrder order)
    {
        foreach (var candidate in Enum.GetValues<ExplorerSortOrder>())
        {
            if (string.Equals(ToWireName(candidate), name, StringComparison.Ordinal))
            {
                order = candidate;
                return true;
            }
        }

        order = ExplorerSortOrder.Manual;
        return false;
    }
}
