using System.Collections.Specialized;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Presentation.Explorer;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Explorer;

/// <summary>Live updates change the sidebar list row by row, never resetting it (scroll, containers and focus survive).</summary>
public sealed class ExplorerLiveUpdateTests
{
    private sealed class ChangeLog
    {
        public ChangeLog(ExplorerViewModel explorer)
        {
            explorer.Rows.CollectionChanged += (_, e) => Changes.Add(e.Action);
        }

        public List<NotifyCollectionChangedAction> Changes { get; } = [];

        public int Count(NotifyCollectionChangedAction action) => Changes.Count(change => change == action);
    }

    private static async Task<List<NodeId>> RootNotesAsync(ShellHarness harness, int count, string? text = null)
    {
        var ids = new List<NodeId>();
        for (var i = 0; i < count; i++)
        {
            ids.Add((await harness.Core.Nodes.CreateDocumentAsync(harness.Shell.Context.Workspace.Id, null, $"n{i:000}.txt", initialText: text)).Id);
        }

        await harness.SettleAsync();
        return ids;
    }

    private static List<ExplorerNodeRowViewModel> WorkspaceRows(ExplorerViewModel explorer) =>
        explorer.Rows.OfType<ExplorerNodeRowViewModel>().Where(row => row.Section == ExplorerSection.Workspace).ToList();

    [Fact]
    public async Task MovingANoteFromTheTopToTheBottomIsOneMove()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await RootNotesAsync(harness, 100);
            var explorer = harness.Shell.Explorer;
            var before = explorer.Rows.ToList();
            var moved = WorkspaceRows(explorer).Single(row => row.Id == ids[0]);
            explorer.SelectedRow = moved;
            var log = new ChangeLog(explorer);

            await harness.Core.Nodes.MoveAsync(ids[0], null, InsertPosition.End);
            await harness.SettleAsync();

            Assert.Equal([NotifyCollectionChangedAction.Move], log.Changes);
            Assert.Same(moved, WorkspaceRows(explorer)[^1]);
            Assert.Same(moved, explorer.SelectedRow);
            Assert.Equal(before.Count, explorer.Rows.Count);
            Assert.All(explorer.Rows, row => Assert.Contains(row, before));
            Assert.Equal(explorer.BuildTreeRows(), explorer.Rows);
        });
    }

    [Fact]
    public async Task ARenameThatSortsANoteLowerIsOneMove()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await RootNotesAsync(harness, 100);
            var explorer = harness.Shell.Explorer;
            explorer.SetSortOrder(ExplorerSortOrder.Name);
            var log = new ChangeLog(explorer);

            await harness.Core.Nodes.RenameAsync(ids[0], "zzz.txt");
            await harness.SettleAsync();

            Assert.Equal([NotifyCollectionChangedAction.Move], log.Changes);
            Assert.Equal("zzz.txt", WorkspaceRows(explorer)[^1].Name);
        });
    }

    [Fact]
    public async Task SearchResultsFollowEditsRowByRow()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ws = harness.Shell.Context.Workspace.Id;
            for (var i = 0; i < 5; i++)
            {
                await harness.Core.Nodes.CreateDocumentAsync(ws, null, $"beta {i}.txt", initialText: "beta text");
            }

            await harness.SettleAsync();
            var explorer = harness.Shell.Explorer;
            explorer.SearchQuery = "beta";
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(200));
            Assert.Equal(ExplorerMode.Search, explorer.Mode);
            var results = explorer.Rows.ToList();
            Assert.Equal(5, results.Count);
            explorer.SelectedRow = results[2];
            var log = new ChangeLog(explorer);
            var searches = 0;
            explorer.PropertyChanged += (_, e) => searches += e.PropertyName == nameof(ExplorerViewModel.IsSearching) && explorer.IsSearching ? 1 : 0;

            // Typing in a note that does not match: each autosave searches again, and nothing in the list changes.
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            for (var i = 0; i < 3; i++)
            {
                ShellHarness.Type(tab, "x");
                await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
                await harness.AdvanceAsync(ExplorerViewModel.SearchDelay);
            }

            Assert.Equal(3, searches);
            Assert.Empty(log.Changes);
            Assert.Equal(results, explorer.Rows);
            Assert.Same(results[2], explorer.SelectedRow);

            // Now it matches: it joins the results with one insert.
            ShellHarness.Type(tab, " beta");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
            await harness.AdvanceAsync(ExplorerViewModel.SearchDelay);

            Assert.Equal([NotifyCollectionChangedAction.Add], log.Changes);
            Assert.Equal(6, explorer.Rows.Count);
            Assert.Contains(explorer.Rows, row => row.NodeId == tab.NodeId);
            Assert.All(results, row => Assert.Contains(row, explorer.Rows));
            Assert.Equal("6 notes match “beta”", explorer.SearchSummaryText);
        });
    }

    [Fact]
    public async Task TheTrashIgnoresEditsOfLiveNotesAndChangesRowByRow()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await RootNotesAsync(harness, 3, "text");
            await harness.Core.Nodes.TrashAsync([ids[0]]);
            await harness.Core.Nodes.TrashAsync([ids[1]]);
            await harness.SettleAsync();
            var explorer = harness.Shell.Explorer;
            explorer.ShowTrash();
            await harness.SettleAsync();
            var rows = explorer.Rows.ToList();
            Assert.Equal(2, rows.Count);
            var log = new ChangeLog(explorer);

            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            for (var i = 0; i < 3; i++)
            {
                ShellHarness.Type(tab, "x");
                await harness.AdvanceAsync(TimeSpan.FromSeconds(1.2));
            }

            Assert.Empty(log.Changes);
            Assert.Equal(rows, explorer.Rows);

            await harness.Core.Nodes.TrashAsync([ids[2]]);
            await harness.SettleAsync();

            Assert.Equal([NotifyCollectionChangedAction.Add], log.Changes);
            Assert.Equal(3, explorer.Rows.Count);
            Assert.All(rows, row => Assert.Contains(row, explorer.Rows));
            Assert.Equal(3, explorer.TrashCount);
        });
    }

    [Fact]
    public async Task TheTrashCountIsKeptInEveryMode()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await RootNotesAsync(harness, 2, "text");
            var explorer = harness.Shell.Explorer;
            Assert.Equal(0, explorer.TrashCount);
            Assert.False(explorer.HasTrashItems);
            Assert.False(explorer.EmptyTrashCommand.CanExecute(null));

            await harness.Core.Nodes.TrashAsync([ids[0]]);
            await harness.SettleAsync();

            Assert.Equal(ExplorerMode.Tree, explorer.Mode);
            Assert.Equal(1, explorer.TrashCount);
            Assert.True(explorer.HasTrashItems);
            Assert.True(explorer.EmptyTrashCommand.CanExecute(null));

            explorer.SearchQuery = "text";
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(200));
            await harness.Core.Nodes.TrashAsync([ids[1]]);
            await harness.SettleAsync();
            Assert.Equal(2, explorer.TrashCount);

            explorer.ShowTree();
            await harness.Core.Nodes.RestoreAsync(ids[0]);
            await harness.SettleAsync();
            Assert.Equal(1, explorer.TrashCount);

            // Read at startup too.
            await harness.RestartAsync();
            Assert.Equal(1, harness.Shell.Explorer.TrashCount);
            Assert.True(harness.Shell.Explorer.HasTrashItems);
        });
    }

    [Fact]
    public async Task TheMovedToTrashToastCanBeUndoneFromTheKeyboard()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await RootNotesAsync(harness, 2, "text");
            var explorer = harness.Shell.Explorer;
            var row = WorkspaceRows(explorer).Single(r => r.Id == ids[0]);
            Assert.False(explorer.UndoTrashCommand.CanExecute(null));

            await explorer.DeleteCommand.ExecuteAsync(row);
            await harness.SettleAsync();

            var toast = Assert.Single(harness.Shell.Toasts.Toasts);
            Assert.Equal("Moved “n000.txt” to Trash", toast.Message);
            Assert.Equal("Undo", toast.ActionText);
            Assert.Equal("Ctrl+Z", toast.ActionShortcutText);
            Assert.True(explorer.UndoTrashCommand.CanExecute(null));

            await explorer.UndoTrashCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.True(harness.Shell.Context.Model.Contains(ids[0]));
            Assert.Empty(harness.Shell.Toasts.Toasts);
            Assert.False(explorer.UndoTrashCommand.CanExecute(null));
            Assert.Equal(0, explorer.TrashCount);

            // The keyboard Undo lasts as long as its toast.
            await explorer.DeleteCommand.ExecuteAsync(WorkspaceRows(explorer).Single(r => r.Id == ids[1]));
            await harness.SettleAsync();
            Assert.True(explorer.UndoTrashCommand.CanExecute(null));
            await harness.AdvanceAsync(TimeSpan.FromSeconds(6));
            Assert.Empty(harness.Shell.Toasts.Toasts);
            Assert.False(explorer.UndoTrashCommand.CanExecute(null));
            Assert.False(harness.Shell.Context.Model.Contains(ids[1]));

            var shortcut = harness.Shell.Shortcuts.AllGroups.SelectMany(group => group.Shortcuts).Single(s => s.Label == "Undo move to Trash (in the sidebar)");
            Assert.Equal("Ctrl+Z", shortcut.KeysText);
        });
    }

    [Fact]
    public async Task RowCommandsAreSafeAndEnabledOnlyWhereTheyApply()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ws = harness.Shell.Context.Workspace.Id;
            var folder = await harness.Core.Nodes.CreateFolderAsync(ws, null, "Folder");
            await harness.SettleAsync();
            var explorer = harness.Shell.Explorer;
            var folderRow = WorkspaceRows(explorer).Single(row => row.Id == folder.Id);
            var noteRow = WorkspaceRows(explorer).Single(row => !row.IsFolder);
            var header = explorer.Rows.OfType<ExplorerHeaderRowViewModel>().First();

            Assert.False(explorer.ExportCommand.CanExecute(folderRow));
            Assert.False(explorer.ExportCommand.CanExecute(header));
            Assert.True(explorer.ExportCommand.CanExecute(noteRow));
            var changed = 0;
            explorer.ExportCommand.CanExecuteChanged += (_, _) => changed++;
            explorer.SelectedRow = folderRow;
            Assert.False(explorer.ExportCommand.CanExecute(null));
            explorer.SelectedRow = noteRow;
            Assert.True(explorer.ExportCommand.CanExecute(null));
            Assert.True(changed >= 2);

            // The clipboard can be held by another app; the command reports it instead of throwing.
            harness.Platform.Clipboard.Fail = true;
            explorer.CopyLinkCommand.Execute(noteRow);
            Assert.Equal("Couldn't copy the link. Try again.", harness.Shell.Toasts.Toasts[^1].Message);
        });
    }

    [Fact]
    public async Task SidebarTimestampsUseTheUsersTimeZone()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            // 13:59 UTC on Monday 12 October is 23:59 in UTC+10.
            harness.Clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "UTC+10", "UTC+10"));
            var note = await harness.Core.Nodes.CreateDocumentAsync(harness.Shell.Context.Workspace.Id, null, "late.txt", initialText: "late");
            await harness.SettleAsync();
            var explorer = harness.Shell.Explorer;

            Assert.Equal("11:59 PM", WorkspaceRows(explorer).Single(row => row.Id == note.Id).TimestampText.Replace(' ', ' '));

            // A minute later it is Tuesday there: the note was edited on Monday.
            await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
            await harness.Core.Nodes.CreateDocumentAsync(harness.Shell.Context.Workspace.Id, null, "next.txt");
            await harness.SettleAsync();

            Assert.Equal("Mon", WorkspaceRows(explorer).Single(row => row.Id == note.Id).TimestampText);
        });
    }
}
