using NimPad.Core.Ids;
using NimPad.Presentation.Explorer;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Explorer;

public sealed class ExplorerSearchAndTrashTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private static async Task<(NodeId Meeting, NodeId Launch, NodeId Folder)> SeedAsync(ShellHarness harness)
    {
        var nodes = harness.Core.Nodes;
        var ws = harness.Shell.Context.Workspace.Id;
        var folder = await nodes.CreateFolderAsync(ws, null, "Work");
        var meeting = await nodes.CreateDocumentAsync(ws, folder.Id, "Meeting notes", initialText: "Agenda\nShip the beta to 50 users on Thursday");
        var launch = await nodes.CreateDocumentAsync(ws, null, "Launch checklist", initialText: "Invite beta testers before the freeze");
        await nodes.CreateDocumentAsync(ws, null, "Groceries", initialText: "milk, eggs");
        await harness.SettleAsync();
        return (meeting.Id, launch.Id, folder.Id);
    }

    [Fact]
    public async Task TypingInTheSearchFieldShowsHighlightedResultsAfter120Milliseconds()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (meeting, _, _) = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;

            explorer.SearchQuery = "beta";
            await harness.AdvanceAsync(119 * Ms);
            Assert.Equal(ExplorerMode.Tree, explorer.Mode);

            await harness.AdvanceAsync(Ms);

            Assert.Equal(ExplorerMode.Search, explorer.Mode);
            Assert.Equal("2 notes match “beta”", explorer.SearchSummaryText);
            var results = explorer.Rows.Cast<ExplorerSearchResultRowViewModel>().ToList();
            Assert.Equal(2, results.Count);
            var hit = results.Single(result => result.Id == meeting);
            Assert.Equal("Work", hit.PathText);
            Assert.Equal("…Ship the beta to 50 users on Thursday".TrimStart('…'), hit.Snippet.TrimStart('…'));
            Assert.Contains(new HighlightSegment("beta", true), hit.SnippetSegments);
            Assert.Equal(hit.Snippet, string.Concat(hit.SnippetSegments.Select(segment => segment.Text)));
            Assert.Equal("1:59 PM", hit.TimestampText.Replace(" ", " ", StringComparison.Ordinal));
            Assert.Null(explorer.EmptyState);

            await explorer.OpenCommand.ExecuteAsync(hit);
            Assert.Equal(meeting, harness.Tabs.SelectedTab!.NodeId);
        });
    }

    [Fact]
    public async Task NoResultsOffersClearSearchAndEscTwiceReturnsToTheEditor()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            var focus = new List<FocusRegion>();
            harness.Shell.FocusRequested += (_, e) => focus.Add(e.Region);

            explorer.SearchQuery = "gamma";
            await harness.AdvanceAsync(120 * Ms);

            Assert.Empty(explorer.Rows);
            Assert.Equal(string.Empty, explorer.SearchSummaryText);
            var empty = explorer.EmptyState!;
            Assert.Equal(ExplorerEmptyStateKind.NoResults, empty.Kind);
            Assert.Equal("No notes match “gamma”", empty.Title);
            Assert.Equal("Check the spelling or try fewer words.", empty.Message);
            Assert.Equal("Clear search", empty.ActionText);

            empty.ActionCommand!.Execute(null);
            Assert.Equal(string.Empty, explorer.SearchQuery);
            Assert.Equal(ExplorerMode.Tree, explorer.Mode);
            Assert.Contains(explorer.Rows, row => row is ExplorerHeaderRowViewModel);
            Assert.Empty(focus);

            explorer.ClearSearchCommand.Execute(null);
            Assert.Equal([FocusRegion.Editor], focus);
        });
    }

    [Fact]
    public async Task SearchResultsFollowChangesMadeWhileSearching()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (_, launch, _) = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            explorer.SearchQuery = "beta";
            await harness.AdvanceAsync(120 * Ms);

            await harness.Core.Documents.SaveContentAsync(launch, "nothing to see");
            await harness.SettleAsync();
            await harness.AdvanceAsync(120 * Ms);

            Assert.Equal("1 note matches “beta”", explorer.SearchSummaryText);
        });
    }

    [Fact]
    public async Task DeleteMovesToTheTrashWithAnUndoToast()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (_, launch, folder) = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            var row = explorer.Rows.OfType<ExplorerNodeRowViewModel>().Single(r => r.Section == ExplorerSection.Workspace && r.Id == launch);
            explorer.SelectedRow = row;

            await explorer.DeleteCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.DoesNotContain(explorer.Rows, r => r.NodeId == launch);
            var toast = Assert.Single(harness.Shell.Toasts.Toasts);
            Assert.Equal("Moved “Launch checklist” to Trash", toast.Message);
            Assert.Equal("Undo", toast.ActionText);
            Assert.NotNull(explorer.SelectedRow);

            await toast.ActionCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Contains(explorer.Rows, r => r.NodeId == launch);
            Assert.Empty(await harness.Core.Nodes.GetTrashAsync(harness.Shell.Context.Workspace.Id));
            _ = folder;
        });
    }

    [Fact]
    public async Task TheTrashListsItemsWithTheirTimeLeftAndRestoresIntoATab()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (meeting, launch, folder) = await SeedAsync(harness);
            var nodes = harness.Core.Nodes;
            await nodes.TrashAsync([launch]);
            harness.Clock.Advance(TimeSpan.FromDays(18));
            await nodes.TrashAsync([folder]);
            await harness.SettleAsync();
            var explorer = harness.Shell.Explorer;

            explorer.ShowTrash();
            await harness.SettleAsync();

            Assert.True(explorer.IsTrashMode);
            Assert.True(explorer.HasTrashItems);
            Assert.True(explorer.EmptyTrashCommand.CanExecute(null));
            Assert.Equal("Notes are deleted forever after 30 days.", ExplorerViewModel.TrashNotice);
            var rows = explorer.Rows.Cast<ExplorerTrashRowViewModel>().ToList();
            Assert.Equal(["Work", "Launch checklist"], rows.Select(row => row.Title));
            Assert.Equal(["Deleted today", "12 days left"], rows.Select(row => row.StatusText));
            Assert.Equal("Folder with 1 item", rows[0].Preview);
            Assert.Equal("Invite beta testers before the freeze", rows[1].Preview);

            await explorer.OpenCommand.ExecuteAsync(rows[1]);
            await harness.SettleAsync();

            Assert.Equal(launch, harness.Tabs.SelectedTab!.NodeId);
            Assert.Equal(["Work"], explorer.Rows.Cast<ExplorerTrashRowViewModel>().Select(row => row.Title));
            Assert.Equal("Restored “Launch checklist”", harness.Shell.Toasts.Toasts[^1].Message);
            Assert.Null(harness.Shell.Context.Model.Get(meeting));
        });
    }

    [Fact]
    public async Task DeletingForeverAndEmptyingTheTrashAskFirst()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (_, launch, folder) = await SeedAsync(harness);
            await harness.Core.Nodes.TrashAsync([launch]);
            await harness.Core.Nodes.TrashAsync([folder]);
            await harness.SettleAsync();
            var explorer = harness.Shell.Explorer;
            explorer.ShowTrash();
            await harness.SettleAsync();
            var dialogs = harness.Platform.Dialogs;
            ExplorerTrashRowViewModel Launch() => explorer.Rows.Cast<ExplorerTrashRowViewModel>().Single(row => row.Title == "Launch checklist");

            dialogs.Answers.Enqueue(DialogChoice.Cancel);
            await explorer.DeleteCommand.ExecuteAsync(Launch());
            Assert.Equal(2, explorer.Rows.Count);
            var question = Assert.Single(dialogs.Requests);
            Assert.Equal("Delete “Launch checklist” forever?", question.Title);
            Assert.Equal("Delete forever", question.PrimaryText);
            Assert.True(question.IsDestructive);

            dialogs.Answers.Enqueue(DialogChoice.Primary);
            await explorer.DeleteForeverCommand.ExecuteAsync(Launch());
            await harness.SettleAsync();
            Assert.Null(await harness.Core.Nodes.GetNodeAsync(launch));
            Assert.Single(explorer.Rows);

            dialogs.Answers.Enqueue(DialogChoice.Primary);
            await explorer.EmptyTrashCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal("Empty the Trash?", dialogs.Requests[^1].Title);
            Assert.Empty(explorer.Rows);
            Assert.False(explorer.HasTrashItems);
            Assert.False(explorer.EmptyTrashCommand.CanExecute(null));
            var empty = explorer.EmptyState!;
            Assert.Equal("Trash is empty", empty.Title);
            Assert.Equal("Deleted notes wait here for 30 days, so nothing is lost by accident.", empty.Message);
            Assert.Null(empty.ActionText);

            explorer.ShowTree();
            Assert.Equal(ExplorerMode.Tree, explorer.Mode);
            Assert.DoesNotContain(explorer.Rows, row => row.NodeId == folder);
        });
    }

    [Fact]
    public async Task ExpiredTrashIsPurgedAtStartup()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (_, launch, _) = await SeedAsync(harness);
            await harness.Core.Nodes.TrashAsync([launch]);
            harness.Clock.Advance(TimeSpan.FromDays(31));

            await harness.RestartAsync();

            Assert.Null(await harness.Core.Nodes.GetNodeAsync(launch));
            Assert.Single(Directory.GetFiles(harness.Directory.Combine("profiles", "local", "backups"), "nimpad-20261112.db"));
        });
    }

    [Fact]
    public async Task ToastKindsAreNeutralForUndo()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (_, launch, _) = await SeedAsync(harness);
            await harness.Shell.Explorer.DeleteCommand.ExecuteAsync(
                harness.Shell.Explorer.Rows.OfType<ExplorerNodeRowViewModel>().First(row => row.Id == launch));
            Assert.Equal(ToastKind.Neutral, harness.Shell.Toasts.Toasts[^1].Kind);
        });
    }
}
