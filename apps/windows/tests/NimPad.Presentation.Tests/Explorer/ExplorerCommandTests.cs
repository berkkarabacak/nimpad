using NimPad.Core.Ids;
using NimPad.Presentation.Explorer;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Explorer;

public sealed class ExplorerCommandTests
{
    // Projects/ (Alpha/ (deep.md), notes.md), readme.txt
    private static async Task<Dictionary<string, NodeId>> SeedAsync(ShellHarness harness)
    {
        var nodes = harness.Core.Nodes;
        var ws = harness.Shell.Context.Workspace.Id;
        var ids = new Dictionary<string, NodeId>();
        ids["Projects"] = (await nodes.CreateFolderAsync(ws, null, "Projects")).Id;
        ids["Alpha"] = (await nodes.CreateFolderAsync(ws, ids["Projects"], "Alpha")).Id;
        ids["deep.md"] = (await nodes.CreateDocumentAsync(ws, ids["Alpha"], "deep.md", initialText: "deep")).Id;
        ids["notes.md"] = (await nodes.CreateDocumentAsync(ws, ids["Projects"], "notes.md", initialText: "notes")).Id;
        ids["readme.txt"] = (await nodes.CreateDocumentAsync(ws, null, "readme.txt", initialText: "readme")).Id;
        await harness.SettleAsync();
        return ids;
    }

    private static ExplorerNodeRowViewModel TreeRow(ExplorerViewModel explorer, string name) =>
        explorer.Rows.OfType<ExplorerNodeRowViewModel>().Single(row => row.Section == ExplorerSection.Workspace && row.Name == name);

    [Fact]
    public async Task RevealExpandsSelectsAndAsksTheViewToScroll()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            var scrolled = new List<ExplorerRowViewModel>();
            explorer.ScrollIntoViewRequested += (_, e) => scrolled.Add(e.Row);

            Assert.True(explorer.Reveal(ids["deep.md"]));

            var row = TreeRow(explorer, "deep.md");
            Assert.Same(row, explorer.SelectedRow);
            Assert.Equal([row], scrolled);
            Assert.True(TreeRow(explorer, "Projects").IsExpanded);
            Assert.True(TreeRow(explorer, "Alpha").IsExpanded);

            // Left moves to the parent and scrolls to it.
            explorer.CollapseRowCommand.Execute(null);
            Assert.Same(TreeRow(explorer, "Alpha"), scrolled[^1]);
        });
    }

    [Fact]
    public async Task F2RenamesAnExistingRowAndEscLeavesTheNameAlone()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            var readme = TreeRow(explorer, "readme.txt");
            explorer.SelectedRow = readme;
            ExplorerNodeRowViewModel? started = null;
            explorer.RenameStarted += (_, row) => started = row;

            explorer.RenameCommand.Execute(null);
            Assert.Same(readme, started);
            Assert.True(readme.IsRenaming);
            Assert.Equal("readme.txt", readme.RenameText);

            readme.RenameText = "changed my mind.txt";
            explorer.CancelRenameCommand.Execute(null);
            await harness.SettleAsync();
            Assert.False(readme.IsRenaming);
            Assert.Equal("readme.txt", readme.Name);
            Assert.Equal("readme.txt", (await harness.Core.Nodes.GetNodeAsync(ids["readme.txt"]))!.Name);

            explorer.RenameCommand.Execute(readme);
            readme.RenameText = "README.md";
            await explorer.CommitRenameCommand.ExecuteAsync(readme);
            await harness.SettleAsync();
            Assert.False(readme.IsRenaming);
            Assert.Same(readme, TreeRow(explorer, "README.md"));
            Assert.Equal("README.md", (await harness.Core.Nodes.GetNodeAsync(ids["readme.txt"]))!.Name);
        });
    }

    [Fact]
    public async Task RestoringAFolderFromTheTrashBringsItsContentsBack()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            await explorer.DeleteCommand.ExecuteAsync(TreeRow(explorer, "Projects"));
            await harness.SettleAsync();
            Assert.False(harness.Shell.Context.Model.Contains(ids["deep.md"]));

            explorer.ShowTrash();
            await harness.SettleAsync();
            var trashed = Assert.Single(explorer.Rows.OfType<ExplorerTrashRowViewModel>());
            Assert.Equal("Projects", trashed.Title);
            Assert.Equal(3, trashed.DescendantCount);
            Assert.Equal("Folder with 3 items", trashed.Preview);

            await explorer.RestoreCommand.ExecuteAsync(trashed);
            await harness.SettleAsync();

            Assert.Empty(explorer.Rows);
            Assert.Equal(ExplorerEmptyStateKind.TrashEmpty, explorer.EmptyState!.Kind);
            Assert.Equal(0, explorer.TrashCount);
            foreach (var name in new[] { "Projects", "Alpha", "deep.md", "notes.md" })
            {
                Assert.True(harness.Shell.Context.Model.Contains(ids[name]), name);
            }

            Assert.Equal(ids["Alpha"], harness.Shell.Context.Model.Get(ids["deep.md"])!.ParentId);
            Assert.Equal("Restored “Projects”", harness.Shell.Toasts.Toasts[^1].Message);
        });
    }

    [Fact]
    public async Task DraggingSeveralNodesFollowsTheRulesForEachOfThem()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            explorer.Reveal(ids["deep.md"]);
            var alpha = TreeRow(explorer, "Alpha");
            var readme = TreeRow(explorer, "readme.txt");

            // One of the dragged folders is an ancestor of the target.
            Assert.False(explorer.CanDrop([ids["readme.txt"], ids["Projects"]], alpha, DropPosition.Inside));
            Assert.False(await explorer.DropAsync([ids["readme.txt"], ids["Projects"]], alpha, DropPosition.Inside));

            // A missing node in the selection refuses the whole drop.
            Assert.False(explorer.CanDrop([ids["readme.txt"], NodeId.New()], alpha, DropPosition.Inside));

            Assert.True(explorer.CanDrop([ids["readme.txt"], ids["notes.md"]], alpha, DropPosition.Inside));
            Assert.True(await explorer.DropAsync([ids["readme.txt"], ids["notes.md"]], alpha, DropPosition.Inside));
            await harness.SettleAsync();

            Assert.Equal(ids["Alpha"], harness.Shell.Context.Model.Get(ids["readme.txt"])!.ParentId);
            Assert.Equal(ids["Alpha"], harness.Shell.Context.Model.Get(ids["notes.md"])!.ParentId);
            Assert.Equal(["deep.md", "readme.txt", "notes.md"], harness.Shell.Context.Model.GetChildren(ids["Alpha"]).Select(node => node.Name));
            Assert.Equal(2, TreeRow(explorer, "readme.txt").Depth);
            Assert.Same(readme, TreeRow(explorer, "readme.txt"));
        });
    }
}
