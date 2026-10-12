using System.Collections.Specialized;
using NimPad.Core.Domain;
using NimPad.Presentation.Explorer;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Tabs;

public sealed class TabLifecycleTests
{
    [Fact]
    public async Task NewUntitledNotesAreNumberedAndOpenAtTheEnd()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var first = harness.Tabs.Tabs[0];
            var second = await harness.Tabs.NewUntitledAsync();
            var third = await harness.Tabs.NewUntitledAsync();

            Assert.Equal(["Untitled 1", "Untitled 2", "Untitled 3"], harness.Tabs.Tabs.Select(tab => tab.Title));
            Assert.Same(third, harness.Tabs.SelectedTab);
            Assert.False(first.IsSelected);
            Assert.True(third.IsSelected);
            Assert.True(second.IsCreatedThisSession);
            var node = await harness.Core.Nodes.GetNodeAsync(third.NodeId!.Value);
            Assert.NotNull(node);
            Assert.Null(node.ParentId);
        });
    }

    [Fact]
    public async Task OpeningAnOpenNoteActivatesItsTab()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var note = await harness.Core.Nodes.CreateDocumentAsync(harness.Shell.Context.Workspace.Id, null, "Plan");
            await harness.SettleAsync();

            var opened = await harness.Tabs.OpenAsync(note.Id);
            await harness.Tabs.NewUntitledAsync();
            var again = await harness.Tabs.OpenAsync(note.Id);

            Assert.Same(opened, again);
            Assert.Same(opened, harness.Tabs.SelectedTab);
            Assert.Equal(1, harness.Tabs.Tabs.Count(tab => tab.NodeId == note.Id));
        });
    }

    [Fact]
    public async Task ClosingFlushesThePendingAutosaveFirst()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "Typed just before closing");

            Assert.True(await harness.Tabs.CloseAsync(tab));

            var content = await harness.Core.Documents.GetContentAsync(tab.NodeId!.Value);
            Assert.Equal("Typed just before closing", content.Text);
            Assert.False(harness.Core.Autosave.IsDirty(tab.NodeId.Value));
        });
    }

    [Fact]
    public async Task ClosingAnUntouchedNewNoteDeletesItForGood()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var untouched = harness.Tabs.Tabs[0];
            await harness.ShowAsync(untouched);
            var id = untouched.NodeId!.Value;

            Assert.True(await harness.Tabs.CloseAsync(untouched));
            await harness.SettleAsync();

            Assert.Null(await harness.Core.Nodes.GetNodeAsync(id));
            Assert.Empty(await harness.Core.Nodes.GetTrashAsync(harness.Shell.Context.Workspace.Id));
            Assert.Empty(await harness.Core.Sessions.GetRecentlyClosedAsync());
            Assert.Empty(harness.Shell.Toasts.Toasts);
            Assert.False(harness.Tabs.CanReopenClosedTab);
            Assert.True(harness.Shell.IsEmptyStateVisible);
        });
    }

    [Fact]
    public async Task ANewNoteThatWasTypedInIsKeptWhenItsTabCloses()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            var buffer = await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "x");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
            buffer.SetText(string.Empty);
            tab.OnBufferChanged();

            Assert.True(await harness.Tabs.CloseAsync(tab));

            var node = await harness.Core.Nodes.GetNodeAsync(tab.NodeId!.Value);
            Assert.NotNull(node);
            Assert.False(node.IsTrashed);
            Assert.Equal(string.Empty, (await harness.Core.Documents.GetContentAsync(node.Id)).Text);
        });
    }

    [Fact]
    public async Task ClosingShowsAnUndoToastThatReopensTheTabWhereItWas()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var a = harness.Tabs.Tabs[0];
            await harness.ShowAsync(a);
            ShellHarness.Type(a, "alpha");
            var b = await harness.Tabs.NewUntitledAsync();
            await harness.ShowAsync(b);
            ShellHarness.Type(b, "bravo text");
            b.UpdateViewState(caretOffset: 3, selectionStart: 1, selectionLength: 2, verticalOffset: 120.5, horizontalOffset: 7);
            var c = await harness.Tabs.NewUntitledAsync();
            await harness.ShowAsync(c);
            ShellHarness.Type(c, "charlie");

            Assert.True(await harness.Tabs.CloseAsync(b));
            await harness.SettleAsync();

            var toast = Assert.Single(harness.Shell.Toasts.Toasts);
            Assert.Equal("Closed “Untitled 2”", toast.Message);
            Assert.Equal("Undo", toast.ActionText);
            Assert.Equal("Ctrl+Shift+T", toast.ActionShortcutText);
            Assert.Equal(["Untitled 1", "Untitled 3"], harness.Tabs.Tabs.Select(tab => tab.Title));
            Assert.True(harness.Tabs.CanReopenClosedTab);

            await toast.ActionCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Empty(harness.Shell.Toasts.Toasts);
            Assert.Equal(["Untitled 1", "Untitled 2", "Untitled 3"], harness.Tabs.Tabs.Select(tab => tab.Title));
            var reopened = harness.Tabs.Tabs[1];
            Assert.Same(reopened, harness.Tabs.SelectedTab);
            Assert.Equal(b.NodeId, reopened.NodeId);
            Assert.Equal(3, reopened.CaretOffset);
            Assert.Equal(1, reopened.SelectionStart);
            Assert.Equal(2, reopened.SelectionLength);
            Assert.Equal(120.5, reopened.VerticalOffset);
            Assert.Equal(7, reopened.HorizontalOffset);
            Assert.Equal("bravo text", await reopened.LoadAsync());
        });
    }

    [Fact]
    public async Task ReopenClosedRestoresTabsInReverseClosingOrder()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tabs = new List<TabViewModel> { harness.Tabs.Tabs[0] };
            for (var i = 0; i < 3; i++)
            {
                tabs.Add(await harness.Tabs.NewUntitledAsync());
            }

            foreach (var tab in tabs)
            {
                await harness.ShowAsync(tab);
                ShellHarness.Type(tab, tab.Title);
            }

            await harness.Tabs.CloseAsync(tabs[1]);
            await harness.Tabs.CloseAsync(tabs[3]);
            await harness.Tabs.CloseAsync(tabs[0]);
            await harness.SettleAsync();
            Assert.Equal(["Untitled 3"], harness.Tabs.Tabs.Select(tab => tab.Title));

            Assert.Equal("Untitled 1", (await harness.Tabs.ReopenClosedAsync())!.Title);
            Assert.Equal("Untitled 4", (await harness.Tabs.ReopenClosedAsync())!.Title);
            Assert.Equal("Untitled 2", (await harness.Tabs.ReopenClosedAsync())!.Title);
            Assert.Null(await harness.Tabs.ReopenClosedAsync());

            Assert.Equal(["Untitled 1", "Untitled 2", "Untitled 3", "Untitled 4"], harness.Tabs.Tabs.Select(tab => tab.Title));
            Assert.False(harness.Tabs.CanReopenClosedTab);
        });
    }

    [Fact]
    public async Task PinnedTabsStayAtTheLeftEdge()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            for (var i = 0; i < 4; i++)
            {
                await harness.Tabs.NewUntitledAsync();
            }

            var tabs = harness.Tabs;
            string Order() => string.Join(",", tabs.Tabs.Select(tab => tab.Title[^1..] + (tab.IsPinned ? "*" : string.Empty)));

            tabs.Pin(tabs.Tabs[3]);
            Assert.Equal("4*,1,2,3,5", Order());
            tabs.Pin(tabs.Tabs[4]);
            Assert.Equal("4*,5*,1,2,3", Order());
            Assert.Equal(2, tabs.PinnedCount);

            // A pinned tab never leaves the pinned group, an unpinned one never enters it.
            Assert.Equal(1, tabs.Move(tabs.Tabs[0], 4));
            Assert.Equal("5*,4*,1,2,3", Order());
            Assert.Equal(2, tabs.Move(tabs.Tabs[4], 0));
            Assert.Equal("5*,4*,3,1,2", Order());
            tabs.MoveTabRightCommand.Execute(tabs.Tabs[1]);
            Assert.Equal("5*,4*,3,1,2", Order());
            tabs.MoveTabLeftCommand.Execute(tabs.Tabs[3]);
            Assert.Equal("5*,4*,1,3,2", Order());

            tabs.Unpin(tabs.Tabs[0]);
            Assert.Equal("4*,5,1,3,2", Order());

            var pinned = tabs.Tabs[0];
            var left = await tabs.CloseOthersAsync(tabs.Tabs[2]);
            Assert.Equal(3, left);
            Assert.Equal("4*,1", Order());
            Assert.Same(pinned, tabs.Tabs[0]);
            Assert.Equal("Closed 3 tabs", harness.Shell.Toasts.Toasts[^1].Message);
        });
    }

    [Fact]
    public async Task CloseToTheRightKeepsPinnedTabs()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                await harness.Tabs.NewUntitledAsync();
            }

            var tabs = harness.Tabs;
            tabs.Pin(tabs.Tabs[2]);
            Assert.Equal(["Untitled 3", "Untitled 1", "Untitled 2", "Untitled 4"], tabs.Tabs.Select(tab => tab.Title));

            Assert.Equal(2, await tabs.CloseToTheRightAsync(tabs.Tabs[1]));

            Assert.Equal(["Untitled 3", "Untitled 1"], tabs.Tabs.Select(tab => tab.Title));
        });
    }

    [Fact]
    public async Task NextAndPreviousWrapAndCtrlNineSelectsTheLastTab()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                await harness.Tabs.NewUntitledAsync();
            }

            var tabs = harness.Tabs;
            Assert.Same(tabs.Tabs[2], tabs.SelectedTab);
            tabs.NextTab();
            Assert.Same(tabs.Tabs[0], tabs.SelectedTab);
            tabs.PreviousTab();
            Assert.Same(tabs.Tabs[2], tabs.SelectedTab);

            Assert.True(tabs.GoTo(2));
            Assert.Same(tabs.Tabs[1], tabs.SelectedTab);
            Assert.False(tabs.GoTo(5));
            Assert.Same(tabs.Tabs[1], tabs.SelectedTab);
            Assert.True(tabs.GoTo(9));
            Assert.Same(tabs.Tabs[2], tabs.SelectedTab);
            harness.Shell.GoToTabCommand.Execute("1");
            Assert.Same(tabs.Tabs[0], tabs.SelectedTab);
            harness.Shell.GoToTabCommand.Execute(9);
            Assert.Same(tabs.Tabs[2], tabs.SelectedTab);
        });
    }

    [Fact]
    public async Task RenamingATabRenamesTheNoteAndShowsInvalidNamesInline()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            harness.Shell.RenameTabCommand.Execute(null);
            Assert.True(tab.IsRenaming);
            Assert.Equal("Untitled 1", tab.RenameText);

            tab.RenameText = "   ";
            await tab.CommitRenameCommand.ExecuteAsync(null);
            Assert.True(tab.IsRenaming);
            Assert.Equal("A name can't be empty.", tab.RenameError);

            tab.RenameText = "plan.md";
            Assert.Null(tab.RenameError);
            await tab.CommitRenameCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.False(tab.IsRenaming);
            Assert.Equal("plan.md", tab.Title);
            Assert.Equal(DocumentFormat.Markdown, tab.Format);
            Assert.Equal("plan.md", (await harness.Core.Nodes.GetNodeAsync(tab.NodeId!.Value))!.Name);
            Assert.Contains(harness.Shell.Explorer.Rows.OfType<ExplorerNodeRowViewModel>(), row => row.Name == "plan.md");
        });
    }

    [Fact]
    public async Task ARenameInTheExplorerUpdatesTheTabTitle()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];

            await harness.Core.Nodes.RenameAsync(tab.NodeId!.Value, "Groceries");
            await harness.SettleAsync();

            Assert.Equal("Groceries", tab.Title);
            Assert.Equal("My Workspace / Groceries", tab.ToolTip);
        });
    }

    [Fact]
    public async Task DuplicateOpensTheCopyNextToTheTab()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var original = harness.Tabs.Tabs[0];
            await harness.ShowAsync(original);
            ShellHarness.Type(original, "copy me");
            await harness.Tabs.NewUntitledAsync();
            harness.Tabs.SelectedTab = original;

            await harness.Shell.DuplicateNoteCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal(["Untitled 1", "Untitled 1 (copy)", "Untitled 2"], harness.Tabs.Tabs.Select(tab => tab.Title));
            var copy = harness.Tabs.Tabs[1];
            Assert.Same(copy, harness.Tabs.SelectedTab);
            Assert.Equal("copy me", await copy.LoadAsync());
        });
    }

    [Fact]
    public async Task TrashingANoteClosesItsTabAndRestoringReopensIt()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "keep this");
            var other = await harness.Tabs.NewUntitledAsync();
            harness.Tabs.SelectedTab = tab;
            var id = tab.NodeId!.Value;

            var batches = await harness.Core.Nodes.TrashAsync([id]);
            await harness.SettleAsync();

            Assert.Equal([other], harness.Tabs.Tabs);
            Assert.Equal("keep this", (await harness.Core.Documents.GetContentAsync(id)).Text);

            await harness.Core.Nodes.RestoreBatchAsync(batches);
            await harness.SettleAsync();

            Assert.Equal(["Untitled 1", "Untitled 2"], harness.Tabs.Tabs.Select(t => t.Title));
            Assert.Equal(id, harness.Tabs.SelectedTab!.NodeId);
        });
    }

    [Fact]
    public async Task TheTabStripChangesOneTabAtATime()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var changes = new List<NotifyCollectionChangedEventArgs>();
            harness.Tabs.Tabs.CollectionChanged += (_, e) => changes.Add(e);
            await harness.Tabs.NewUntitledAsync();
            harness.Tabs.Pin(harness.Tabs.Tabs[1]);
            await harness.Tabs.CloseAsync(harness.Tabs.Tabs[0]);

            Assert.All(changes, change => Assert.True((change.NewItems?.Count ?? 0) <= 1 && (change.OldItems?.Count ?? 0) <= 1));
            Assert.DoesNotContain(changes, change => change.Action == NotifyCollectionChangedAction.Reset);
        });
    }

    [Fact]
    public async Task ToastsForTheUndoOfAnotherCloseAreReplaced()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var a = harness.Tabs.Tabs[0];
            await harness.ShowAsync(a);
            ShellHarness.Type(a, "a");
            var b = await harness.Tabs.NewUntitledAsync();
            await harness.ShowAsync(b);
            ShellHarness.Type(b, "b");

            await harness.Tabs.CloseAsync(a);
            await harness.Tabs.CloseAsync(b);

            var toast = Assert.Single(harness.Shell.Toasts.Toasts);
            Assert.Equal(ToastKind.Neutral, toast.Kind);
            Assert.Equal("Closed “Untitled 2”", toast.Message);
        });
    }
}
