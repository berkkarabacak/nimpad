using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Services;

public sealed class SessionServiceTests : IAsyncLifetime
{
    private TestStore _store = null!;

    private SessionService Sessions => _store.Sessions;

    public async ValueTask InitializeAsync() => _store = await TestStore.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------ tabs

    [Fact]
    public async Task AnEmptySessionHasNoTabs()
    {
        Assert.Empty(await Sessions.LoadTabsAsync());
        Assert.Empty(await Sessions.GetRecentlyClosedAsync());
        Assert.Null(await Sessions.PopRecentlyClosedAsync());
    }

    [Fact]
    public async Task TabsRoundTripWithTheirViewState()
    {
        var note = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "plan.md");
        var tabs = new[]
        {
            SessionTab.ForNote(note.Id, "plan.md") with
            {
                IsPinned = true,
                CaretOffset = 42,
                SelectionStart = 40,
                SelectionLength = 2,
                VerticalScrollOffset = 1234.5,
                HorizontalScrollOffset = 17.25,
            },
            SessionTab.ForFile(@"C:\Users\me\hosts.txt", "hosts.txt") with { Order = 1, IsActive = true, CaretOffset = 7 },
        };

        Assert.Equal(2, await Sessions.SaveTabsAsync(tabs));
        var loaded = await Sessions.LoadTabsAsync();

        Assert.Equal(tabs, loaded);
        Assert.Equal(SessionTabKind.Managed, loaded[0].Kind);
        Assert.Equal(SessionTabKind.External, loaded[1].Kind);
    }

    [Fact]
    public async Task TabsAreStoredInOrderAndRenumbered()
    {
        var a = SessionTab.ForFile("/a", "a") with { Order = 10 };
        var b = SessionTab.ForFile("/b", "b") with { Order = 5 };
        var c = SessionTab.ForFile("/c", "c") with { Order = 5 };

        await Sessions.SaveTabsAsync([a, b, c]);

        var loaded = await Sessions.LoadTabsAsync();
        Assert.Equal(["b", "c", "a"], loaded.Select(tab => tab.Title));
        Assert.Equal([0, 1, 2], loaded.Select(tab => tab.Order));
    }

    [Fact]
    public async Task SavingReplacesTheWholeSession()
    {
        var keep = SessionTab.ForFile("/keep", "keep");
        await Sessions.SaveTabsAsync([keep, SessionTab.ForFile("/drop", "drop")]);
        var openedAt = (await _store.QueryAsync("SELECT opened_at FROM session_tabs WHERE tab_id = $id;", r => r.GetString(0), ("$id", keep.TabId.ToString()))).Single();
        _store.Clock.Advance(TimeSpan.FromMinutes(5));

        await Sessions.SaveTabsAsync([keep with { CaretOffset = 3 }, SessionTab.ForFile("/new", "new")]);

        var loaded = await Sessions.LoadTabsAsync();
        Assert.Equal(["keep", "new"], loaded.Select(tab => tab.Title));
        Assert.Equal(3, loaded[0].CaretOffset);
        Assert.Equal(openedAt, (await _store.QueryAsync("SELECT opened_at FROM session_tabs WHERE tab_id = $id;", r => r.GetString(0), ("$id", keep.TabId.ToString()))).Single());

        await Sessions.SaveTabsAsync([]);
        Assert.Empty(await Sessions.LoadTabsAsync());
    }

    [Fact]
    public async Task ARejectedSaveLeavesThePreviousSession()
    {
        var tab = SessionTab.ForFile("/a", "a");
        await Sessions.SaveTabsAsync([tab]);

        await Assert.ThrowsAsync<ArgumentException>(() => Sessions.SaveTabsAsync([tab, tab]));
        await Assert.ThrowsAsync<ArgumentException>(() => Sessions.SaveTabsAsync(
            [SessionTab.ForFile("/x", "x") with { IsActive = true }, SessionTab.ForFile("/y", "y") with { IsActive = true }]));
        await Assert.ThrowsAsync<ArgumentException>(() => Sessions.SaveTabsAsync([SessionTab.ForFile("/x", "x") with { CaretOffset = -1 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => Sessions.SaveTabsAsync([SessionTab.ForFile("/x", "x") with { VerticalScrollOffset = double.NaN }]));
        await Assert.ThrowsAsync<ArgumentException>(() => Sessions.SaveTabsAsync([new SessionTab(Guid.NewGuid(), SessionTabKind.Managed, null, "/x", "x")]));
        await Assert.ThrowsAsync<ArgumentException>(() => Sessions.SaveTabsAsync([new SessionTab(Guid.NewGuid(), SessionTabKind.External, null, " ", "x")]));

        Assert.Equal([tab], await Sessions.LoadTabsAsync());
    }

    [Fact]
    public async Task TabsOfNotesThatNoLongerExistAreDropped()
    {
        var kept = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "kept");
        var purged = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "purged");
        var trashed = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "trashed");
        await Sessions.SaveTabsAsync(
        [
            SessionTab.ForNote(kept.Id, "kept"),
            SessionTab.ForNote(purged.Id, "purged"),
            SessionTab.ForNote(trashed.Id, "trashed"),
        ]);

        await _store.Nodes.DeleteForeverAsync([purged.Id]);
        await _store.Nodes.TrashAsync([trashed.Id]);
        Assert.Equal(["kept", "trashed"], (await Sessions.LoadTabsAsync()).Select(tab => tab.Title));

        // A tab for a note that is already gone is skipped when saving.
        Assert.Equal(1, await Sessions.SaveTabsAsync([SessionTab.ForNote(kept.Id, "kept"), SessionTab.ForNote(NodeId.New(), "ghost")]));
        Assert.Equal(["kept"], (await Sessions.LoadTabsAsync()).Select(tab => tab.Title));
    }

    [Fact]
    public async Task SessionsSurviveAReopen()
    {
        using var temp = new TempDirectory();
        var tab = SessionTab.ForFile("/var/log/app.log", "app.log") with { IsActive = true, VerticalScrollOffset = 99.5 };
        using (var first = await TestStore.CreateAsync(temp))
        {
            await first.Sessions.SaveTabsAsync([tab]);
            await first.Sessions.PushRecentlyClosedAsync(SessionTab.ForFile("/tmp/closed.txt", "closed.txt"));
        }

        using var second = await TestStore.CreateAsync(temp);
        Assert.Equal([tab], await second.Sessions.LoadTabsAsync());
        Assert.Equal("closed.txt", Assert.Single(await second.Sessions.GetRecentlyClosedAsync()).Tab.Title);
    }

    // ------------------------------------------------------------ recently closed

    [Fact]
    public async Task RecentlyClosedIsALastInFirstOutStackOfTwenty()
    {
        for (var i = 0; i < 25; i++)
        {
            await Sessions.PushRecentlyClosedAsync(SessionTab.ForFile($"/file{i}", $"file{i}"));
            _store.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var stack = await Sessions.GetRecentlyClosedAsync();
        Assert.Equal(SessionService.RecentlyClosedCapacity, stack.Count);
        Assert.Equal(Enumerable.Range(5, 20).Reverse().Select(i => $"file{i}"), stack.Select(entry => entry.Tab.Title));

        var popped = await Sessions.PopRecentlyClosedAsync();
        Assert.Equal("file24", popped!.Tab.Title);
        Assert.Equal(TestStore.Start.AddSeconds(24), popped.ClosedAt);
        Assert.Equal("file23", (await Sessions.PopRecentlyClosedAsync())!.Tab.Title);
        Assert.Equal(18, (await Sessions.GetRecentlyClosedAsync()).Count);

        await Sessions.ClearRecentlyClosedAsync();
        Assert.Null(await Sessions.PopRecentlyClosedAsync());
    }

    [Fact]
    public async Task AClosedTabKeepsItsViewState()
    {
        var note = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "draft");
        var tab = SessionTab.ForNote(note.Id, "draft") with
        {
            IsPinned = true,
            IsActive = true,
            CaretOffset = 12,
            SelectionStart = 10,
            SelectionLength = 2,
            VerticalScrollOffset = 400,
            HorizontalScrollOffset = 8.5,
        };

        var pushed = await Sessions.PushRecentlyClosedAsync(tab);
        var popped = await Sessions.PopRecentlyClosedAsync();

        Assert.Equal(tab with { IsActive = false }, popped!.Tab);
        Assert.Equal(pushed, popped);
    }

    [Fact]
    public async Task ClosedTabsOfDeletedNotesDisappear()
    {
        var note = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "gone");
        await Sessions.PushRecentlyClosedAsync(SessionTab.ForNote(note.Id, "gone"));

        await _store.Nodes.DeleteForeverAsync([note.Id]);

        Assert.Empty(await Sessions.GetRecentlyClosedAsync());
        Assert.Null(await Sessions.PushRecentlyClosedAsync(SessionTab.ForNote(note.Id, "gone")));
    }

    // ------------------------------------------------------------ UI state

    [Fact]
    public async Task UiStateDefaultsAndRoundTrips()
    {
        Assert.Equal(UiState.Default, await Sessions.LoadUiStateAsync());
        var state = new UiState
        {
            ExpandedFolderIds = [NodeId.New(), NodeId.New()],
            SidebarWidth = 312.5,
            IsSidebarCollapsed = true,
            SortOrder = ExplorerSortOrder.LastEdited,
            SelectedNodeId = NodeId.New(),
            IsContextPanelVisible = true,
        };

        await Sessions.SaveUiStateAsync(state);

        Assert.Equal(state, await Sessions.LoadUiStateAsync());
        await Sessions.SaveUiStateAsync(state with { SelectedNodeId = null, SortOrder = ExplorerSortOrder.Name });
        var reloaded = await Sessions.LoadUiStateAsync();
        Assert.Null(reloaded.SelectedNodeId);
        Assert.Equal(ExplorerSortOrder.Name, reloaded.SortOrder);
    }

    [Fact]
    public async Task UnreadableUiValuesFallBackToDefaultsAndUnknownKeysSurvive()
    {
        var folder = NodeId.New();
        await _store.ExecuteRawAsync(
            """
            INSERT INTO ui_state (key, value, updated_at) VALUES
                ('sidebar.width', '"wide"', '2026-01-01T00:00:00.000Z'),
                ('sidebar.collapsed', 'true', '2026-01-01T00:00:00.000Z'),
                ('explorer.sortOrder', '"byColor"', '2026-01-01T00:00:00.000Z'),
                ('explorer.expandedFolderIds', $folders, '2026-01-01T00:00:00.000Z'),
                ('future.splitEditor', '{"orientation":"vertical"}', '2026-01-01T00:00:00.000Z');
            """,
            ("$folders", $"[\"{folder}\", \"not-an-id\", 42]"));

        var state = await Sessions.LoadUiStateAsync();

        Assert.Equal(UiState.DefaultSidebarWidth, state.SidebarWidth);
        Assert.True(state.IsSidebarCollapsed);
        Assert.Equal(ExplorerSortOrder.Manual, state.SortOrder);
        Assert.Empty(state.ExpandedFolderIds); // an array with a non-string is unreadable as a whole

        await Sessions.SaveUiStateAsync(state with { ExpandedFolderIds = [folder] });
        Assert.Equal([folder], (await Sessions.LoadUiStateAsync()).ExpandedFolderIds);
        Assert.Equal(
            ["{\"orientation\":\"vertical\"}"],
            await _store.QueryAsync("SELECT value FROM ui_state WHERE key = 'future.splitEditor';", r => r.GetString(0)));
    }

    [Fact]
    public async Task InvalidSidebarWidthsAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sessions.SaveUiStateAsync(UiState.Default with { SidebarWidth = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sessions.SaveUiStateAsync(UiState.Default with { SidebarWidth = double.PositiveInfinity }));
    }

    [Fact]
    public async Task SessionDataNeverEntersThePendingQueue()
    {
        var before = await _store.Queue.CountAsync();
        await Sessions.SaveTabsAsync([SessionTab.ForFile("/a", "a")]);
        await Sessions.PushRecentlyClosedAsync(SessionTab.ForFile("/b", "b"));
        await Sessions.SaveUiStateAsync(UiState.Default with { IsSidebarCollapsed = true });
        await _store.Settings.SaveAsync(AppSettings.Default with { WordWrap = true });

        Assert.Equal(before, await _store.Queue.CountAsync());
    }
}
