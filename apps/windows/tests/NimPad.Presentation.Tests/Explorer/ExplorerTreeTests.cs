using System.Collections.Specialized;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Presentation.Explorer;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Explorer;

public sealed class ExplorerTreeTests
{
    // Projects/ (Alpha/ (deep.md), notes.md), Inbox/, readme.txt — created in that order.
    private static async Task<Dictionary<string, NodeId>> SeedAsync(ShellHarness harness)
    {
        var nodes = harness.Core.Nodes;
        var ws = harness.Shell.Context.Workspace.Id;
        var ids = new Dictionary<string, NodeId>();
        var projects = await nodes.CreateFolderAsync(ws, null, "Projects");
        ids["Projects"] = projects.Id;
        ids["Alpha"] = (await nodes.CreateFolderAsync(ws, projects.Id, "Alpha")).Id;
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        ids["deep.md"] = (await nodes.CreateDocumentAsync(ws, ids["Alpha"], "deep.md", initialText: "deep down")).Id;
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        ids["notes.md"] = (await nodes.CreateDocumentAsync(ws, projects.Id, "notes.md", initialText: "# Notes\nfirst")).Id;
        ids["Inbox"] = (await nodes.CreateFolderAsync(ws, null, "Inbox")).Id;
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        ids["readme.txt"] = (await nodes.CreateDocumentAsync(ws, null, "readme.txt", initialText: "Read me")).Id;
        await harness.SettleAsync();
        return ids;
    }

    private static string Describe(ExplorerViewModel explorer) => string.Join(
        " | ",
        explorer.Rows.Select(row => row switch
        {
            ExplorerHeaderRowViewModel header => "#" + header.Title,
            ExplorerNodeRowViewModel node => new string('.', node.Depth) + node.Name + (node.IsFolder ? (node.IsExpanded ? "/-" : node.HasChildren ? "/+" : "/") : string.Empty),
            _ => row.ToString()!,
        }));

    private static string Tree(ExplorerViewModel explorer)
    {
        var index = explorer.Rows.ToList().FindIndex(row => row is ExplorerHeaderRowViewModel { IsWorkspaceRoot: true });
        return Describe(explorer).Split(" | ").Skip(index).Aggregate((a, b) => a + " | " + b);
    }

    private static ExplorerNodeRowViewModel TreeRow(ExplorerViewModel explorer, string name) =>
        explorer.Rows.OfType<ExplorerNodeRowViewModel>().Single(row => row.Section == ExplorerSection.Workspace && row.Name == name);

    [Fact]
    public async Task TheTreeFlattensWithSectionHeadersAndCollapsedFolders()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            await harness.Core.Nodes.PinAsync(ids["notes.md"]);
            await harness.SettleAsync();

            Assert.Equal(
                "#PINNED | notes.md | #RECENT | readme.txt | notes.md | deep.md | Untitled 1 | #MY WORKSPACE | Untitled 1 | Projects/+ | Inbox/ | readme.txt",
                Describe(explorer));
            var pinned = explorer.Rows.OfType<ExplorerNodeRowViewModel>().First();
            Assert.True(pinned.ShowsDetails);
            Assert.Equal("# Notes", pinned.Preview);
            Assert.Equal("2:01 PM", pinned.TimestampText.Replace("\u202F", " ", StringComparison.Ordinal));
            Assert.False(TreeRow(explorer, "readme.txt").ShowsDetails);
            Assert.Equal("Read me", TreeRow(explorer, "readme.txt").Preview);
            Assert.Null(explorer.EmptyState);
            Assert.True(explorer.IsSearchEnabled);
        });
    }

    [Fact]
    public async Task ExpandAndCollapseChangeRowsOneAtATime()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            var changes = new List<NotifyCollectionChangedEventArgs>();
            explorer.Rows.CollectionChanged += (_, e) => changes.Add(e);

            explorer.ToggleExpand(TreeRow(explorer, "Projects"));
            explorer.ExpandRow(TreeRow(explorer, "Alpha"));

            Assert.Equal("#MY WORKSPACE | Untitled 1 | Projects/- | .Alpha/- | ..deep.md | .notes.md | Inbox/ | readme.txt", Tree(explorer));
            Assert.Equal(3, changes.Count);
            Assert.All(changes, change =>
            {
                Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
                Assert.Single(change.NewItems!);
            });

            // Right on an expanded folder moves to its first child; Left on a child moves to its parent.
            explorer.SelectedRow = TreeRow(explorer, "Alpha");
            explorer.ExpandRow(null);
            Assert.Equal("deep.md", ((ExplorerNodeRowViewModel)explorer.SelectedRow!).Name);
            explorer.CollapseRow(null);
            Assert.Equal("Alpha", ((ExplorerNodeRowViewModel)explorer.SelectedRow!).Name);

            changes.Clear();
            explorer.SelectedRow = TreeRow(explorer, "deep.md");
            explorer.CollapseRow(TreeRow(explorer, "Projects"));

            Assert.Equal("#MY WORKSPACE | Untitled 1 | Projects/+ | Inbox/ | readme.txt", Tree(explorer));
            Assert.Equal(3, changes.Count);
            Assert.All(changes, change => Assert.Single(change.OldItems!));
            // The selection follows the hidden note to its nearest visible ancestor.
            Assert.Equal("Projects", ((ExplorerNodeRowViewModel)explorer.SelectedRow!).Name);
        });
    }

    [Fact]
    public async Task SortOrdersApplyToSiblingsAndAreRememberedWithExpansionAndSelection()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var ws = harness.Shell.Context.Workspace.Id;
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            await harness.Core.Nodes.CreateDocumentAsync(ws, null, "item 9");
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            await harness.Core.Nodes.CreateDocumentAsync(ws, null, "Item 10");
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            await harness.Core.Documents.SaveContentAsync(ids["readme.txt"], "Read me, edited last");
            await harness.SettleAsync();
            var explorer = harness.Shell.Explorer;

            // Each order gives a different list: names (numbers compared as numbers), creation, last edit.
            explorer.SetSortOrder(ExplorerSortOrder.Name);
            Assert.Equal("#MY WORKSPACE | Inbox/ | Projects/+ | item 9 | Item 10 | readme.txt | Untitled 1", Tree(explorer));

            explorer.SetSortOrder(ExplorerSortOrder.DateCreated);
            Assert.Equal("#MY WORKSPACE | Inbox/ | Projects/+ | Item 10 | item 9 | readme.txt | Untitled 1", Tree(explorer));

            explorer.SetSortOrderCommand.Execute(ExplorerSortOrder.LastEdited);
            Assert.Equal("#MY WORKSPACE | Inbox/ | Projects/+ | readme.txt | Item 10 | item 9 | Untitled 1", Tree(explorer));

            // Typing in a note brings it to the top of its folder, row by row.
            var resets = 0;
            explorer.Rows.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            await harness.Core.Documents.SaveContentAsync(harness.Tabs.Tabs[0].NodeId!.Value, "now the newest");
            await harness.SettleAsync();
            Assert.Equal("#MY WORKSPACE | Inbox/ | Projects/+ | Untitled 1 | readme.txt | Item 10 | item 9", Tree(explorer));
            Assert.Equal(0, resets);

            explorer.Reveal(ids["deep.md"]);
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

            await harness.RestartAsync();

            explorer = harness.Shell.Explorer;
            Assert.Equal(ExplorerSortOrder.LastEdited, explorer.SortOrder);
            Assert.Equal(ids["deep.md"], explorer.SelectedNodeId);
            Assert.Equal(ids["deep.md"], explorer.SelectedRow!.NodeId);
            Assert.Contains("Projects/- | .Alpha/- | ..deep.md", Tree(explorer));
            Assert.Equal(new[] { ids["Projects"], ids["Alpha"] }.Select(id => id.ToString()).Order(), explorer.ExpandedFolderIds.Select(id => id.ToString()));
        });
    }

    [Fact]
    public async Task DropValidationFollowsTheTreeRules()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            explorer.SetExpanded(ids["Projects"], true);
            explorer.SetExpanded(ids["Alpha"], true);
            await harness.Core.Nodes.PinAsync(ids["readme.txt"]);
            await harness.Core.Nodes.PinAsync(ids["notes.md"]);
            await harness.SettleAsync();
            var projects = TreeRow(explorer, "Projects");
            var alpha = TreeRow(explorer, "Alpha");
            var readme = TreeRow(explorer, "readme.txt");
            var inbox = TreeRow(explorer, "Inbox");
            var workspaceHeader = explorer.Rows.OfType<ExplorerHeaderRowViewModel>().Single(header => header.IsWorkspaceRoot);
            var pinnedHeader = explorer.Rows.OfType<ExplorerHeaderRowViewModel>().Single(header => header.Section == ExplorerSection.Pinned);
            var recentRow = explorer.Rows.OfType<ExplorerNodeRowViewModel>().First(row => row.Section == ExplorerSection.Recent);
            var pinnedRows = explorer.Rows.OfType<ExplorerNodeRowViewModel>().Where(row => row.Section == ExplorerSection.Pinned).ToList();

            Assert.False(explorer.CanDrop([ids["Projects"]], alpha, DropPosition.Inside));
            Assert.False(explorer.CanDrop([ids["Projects"]], projects, DropPosition.Inside));
            Assert.False(explorer.CanDrop([ids["Projects"]], TreeRow(explorer, "deep.md"), DropPosition.After));
            Assert.False(explorer.CanDrop([ids["Inbox"]], readme, DropPosition.Inside));
            Assert.False(explorer.CanDrop([ids["readme.txt"]], pinnedHeader, DropPosition.Inside));
            Assert.False(explorer.CanDrop([ids["readme.txt"]], recentRow, DropPosition.Before));
            Assert.False(explorer.CanDrop([], inbox, DropPosition.Inside));
            Assert.True(explorer.CanDrop([ids["deep.md"]], workspaceHeader, DropPosition.Inside));
            Assert.True(explorer.CanDrop([ids["readme.txt"]], inbox, DropPosition.Inside));
            Assert.True(explorer.CanDrop([ids["Inbox"]], alpha, DropPosition.Inside));
            Assert.True(explorer.CanDrop([ids["readme.txt"]], projects, DropPosition.Before));
            Assert.True(explorer.CanDrop([ids["notes.md"]], pinnedRows[0], DropPosition.Before));
            Assert.False(explorer.CanDrop([ids["Inbox"]], pinnedRows[0], DropPosition.Before));

            // Reordering inside a folder needs the Manual order; moving to another folder always works.
            explorer.SetSortOrder(ExplorerSortOrder.Name);
            Assert.False(explorer.CanDrop([ids["readme.txt"]], projects, DropPosition.Before));
            Assert.True(explorer.CanDrop([ids["readme.txt"]], inbox, DropPosition.Inside));
            Assert.True(explorer.CanDrop([ids["notes.md"]], workspaceHeader, DropPosition.Inside));
            explorer.SetSortOrder(ExplorerSortOrder.Manual);

            Assert.True(await explorer.DropAsync([ids["readme.txt"]], inbox, DropPosition.Inside));
            Assert.True(await explorer.DropAsync([ids["Inbox"]], projects, DropPosition.Before));
            Assert.True(await explorer.DropAsync([ids["notes.md"]], pinnedRows[0], DropPosition.Before));
            await harness.SettleAsync();

            Assert.Equal(ids["Inbox"], (await harness.Core.Nodes.GetNodeAsync(ids["readme.txt"]))!.ParentId);
            Assert.Equal("#MY WORKSPACE | Untitled 1 | Inbox/- | .readme.txt | Projects/- | .Alpha/- | ..deep.md | .notes.md", Tree(explorer));
            Assert.Equal(["notes.md", "readme.txt"], explorer.Rows.OfType<ExplorerNodeRowViewModel>().Where(row => row.Section == ExplorerSection.Pinned).Select(row => row.Name));
        });
    }

    [Fact]
    public async Task ChangesMadeElsewhereUpdateRowsInPlace()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            explorer.SetExpanded(ids["Projects"], true);
            var notes = TreeRow(explorer, "notes.md");
            explorer.SelectedRow = notes;
            var resets = 0;
            explorer.Rows.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

            await harness.Core.Nodes.RenameAsync(ids["notes.md"], "minutes.md");
            await harness.Core.Documents.SaveContentAsync(ids["notes.md"], "fresh preview\nmore");
            var created = await harness.Core.Nodes.CreateDocumentAsync(harness.Shell.Context.Workspace.Id, ids["Projects"], "new.txt");
            await harness.Core.Nodes.TrashAsync([ids["Inbox"]]);
            await harness.Core.Nodes.MoveAsync(ids["readme.txt"], ids["Projects"]);
            await harness.SettleAsync();

            Assert.Same(notes, TreeRow(explorer, "minutes.md"));
            Assert.Equal("fresh preview", notes.Preview);
            Assert.Same(notes, explorer.SelectedRow);
            Assert.True(notes.IsSelected);
            Assert.Equal(0, resets);
            Assert.Equal("#MY WORKSPACE | Untitled 1 | Projects/- | .Alpha/+ | .minutes.md | .new.txt | .readme.txt", Tree(explorer));
            Assert.Equal(created.Id, TreeRow(explorer, "new.txt").Id);
        });
    }

    [Fact]
    public async Task NewFolderStartsAnInlineRenameAndBadNamesShowInline()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            ExplorerNodeRowViewModel? started = null;
            explorer.RenameStarted += (_, row) => started = row;

            await explorer.NewFolderCommand.ExecuteAsync(TreeRow(explorer, "Projects"));

            Assert.NotNull(started);
            Assert.True(started.IsRenaming);
            Assert.Equal("New folder", started.RenameText);
            Assert.Same(started, explorer.SelectedRow);
            Assert.Equal(1, started.Depth);

            started.RenameText = "bad\tname";
            await explorer.CommitRenameCommand.ExecuteAsync(started);
            Assert.True(started.IsRenaming);
            Assert.Equal("A name can't contain line breaks, tabs or other control characters.", started.RenameError);

            started.RenameText = "Archive";
            await explorer.CommitRenameCommand.ExecuteAsync(started);
            await harness.SettleAsync();
            Assert.False(started.IsRenaming);
            Assert.Equal("Archive", started.Name);
            Assert.Contains(await harness.Core.Nodes.GetChildrenAsync(harness.Shell.Context.Workspace.Id, ids["Projects"]), node => node.Name == "Archive");

            var archive = started;
            await explorer.NewNoteCommand.ExecuteAsync(archive);
            await harness.SettleAsync();
            var note = harness.Tabs.SelectedTab!;
            Assert.Equal("Untitled 2", note.Title);
            Assert.Equal(archive.Id, harness.Shell.Context.Model.Get(note.NodeId!.Value)!.ParentId);
            Assert.True(archive.IsExpanded);
            Assert.Equal(note.NodeId, started.Id);
            Assert.True(started.IsRenaming);
        });
    }

    [Fact]
    public async Task CommandsOnRowsPinCopyLinksAndOpen()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            var explorer = harness.Shell.Explorer;
            var readme = TreeRow(explorer, "readme.txt");

            explorer.CopyLink(readme);
            Assert.Equal("nimpad://note/" + ids["readme.txt"], harness.Platform.Clipboard.Text);

            await explorer.TogglePinCommand.ExecuteAsync(readme);
            await harness.SettleAsync();
            Assert.True(readme.IsPinned);
            Assert.Contains(explorer.Rows, row => row is ExplorerNodeRowViewModel { Section: ExplorerSection.Pinned, Name: "readme.txt" });

            await explorer.OpenCommand.ExecuteAsync(readme);
            Assert.Equal(ids["readme.txt"], harness.Tabs.SelectedTab!.NodeId);
            await explorer.OpenCommand.ExecuteAsync(TreeRow(explorer, "Projects"));
            Assert.True(TreeRow(explorer, "Projects").IsExpanded);

            await explorer.DuplicateCommand.ExecuteAsync(TreeRow(explorer, "notes.md"));
            await harness.SettleAsync();
            Assert.Equal("notes (copy).md", ((ExplorerNodeRowViewModel)explorer.SelectedRow!).Name);
        });
    }

    [Fact]
    public async Task BreadcrumbsRevealTheirNodeInTheExplorer()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var ids = await SeedAsync(harness);
            await harness.Tabs.OpenAsync(ids["deep.md"]);
            var crumbs = harness.Shell.Editor.Breadcrumbs;
            Assert.Equal(["Projects", "Alpha", "deep.md"], crumbs.Select(crumb => crumb.Name));
            harness.Shell.ToggleSidebarCommand.Execute(null);
            Assert.False(harness.Shell.IsSidebarOpen);

            harness.Shell.Editor.NavigateToBreadcrumbCommand.Execute(crumbs[1]);

            Assert.True(harness.Shell.IsSidebarOpen);
            Assert.Equal(ids["Alpha"], harness.Shell.Explorer.SelectedNodeId);
            Assert.True(TreeRow(harness.Shell.Explorer, "Projects").IsExpanded);
        });
    }

    [Fact]
    public async Task AnEmptyWorkspaceSaysNoNotesYetWithoutClaimingSync()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            await harness.Tabs.CloseAsync(harness.Tabs.Tabs[0]);
            await harness.SettleAsync();
            var explorer = harness.Shell.Explorer;

            Assert.Empty(explorer.Rows);
            Assert.False(explorer.IsSearchEnabled);
            var empty = explorer.EmptyState!;
            Assert.Equal(ExplorerEmptyStateKind.NoNotes, empty.Kind);
            Assert.Equal("No notes yet", empty.Title);
            Assert.Equal("Start typing in any tab. It is saved automatically on this computer.", empty.Message);
            Assert.Equal("New note", empty.ActionText);
            Assert.Equal("Ctrl+N", empty.ActionShortcutText);

            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)empty.ActionCommand!).ExecuteAsync(null);
            await harness.SettleAsync();
            Assert.Null(explorer.EmptyState);
            Assert.Single(harness.Tabs.Tabs);
        });
    }
}
