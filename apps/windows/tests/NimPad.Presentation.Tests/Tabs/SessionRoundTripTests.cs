using System.Text;
using NimPad.Core.Domain;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Tabs;

public sealed class SessionRoundTripTests
{
    [Fact]
    public async Task ANewShellRestoresTabsOrderPinsSelectionCaretAndScroll()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var folder = await harness.Core.Nodes.CreateFolderAsync(harness.Shell.Context.Workspace.Id, null, "Work");
            var plan = await harness.Core.Nodes.CreateDocumentAsync(harness.Shell.Context.Workspace.Id, folder.Id, "plan.md", initialText: "# Plan\nship it\n");
            await harness.SettleAsync();
            var file = harness.Directory.Combine("todo.txt");
            await File.WriteAllTextAsync(file, "buy milk", Encoding.UTF8);

            var untitled = harness.Tabs.Tabs[0];
            await harness.ShowAsync(untitled);
            ShellHarness.Type(untitled, "scratch");
            var planTab = (await harness.Tabs.OpenAsync(plan.Id))!;
            await harness.ShowAsync(planTab);
            planTab.UpdateViewState(caretOffset: 9, selectionStart: 7, selectionLength: 4, verticalOffset: 33.5, horizontalOffset: 2);
            var fileTab = harness.Tabs.OpenExternal(file);
            await harness.ShowAsync(fileTab);
            fileTab.UpdateViewState(3, 3, 0, 0, 0);
            harness.Tabs.Pin(fileTab);
            harness.Tabs.SelectedTab = planTab;

            await harness.RestartAsync();

            var tabs = harness.Tabs.Tabs;
            Assert.Equal(["todo.txt", "Untitled 1", "plan.md"], tabs.Select(tab => tab.Title));
            Assert.Equal([true, false, false], tabs.Select(tab => tab.IsPinned));
            Assert.Equal([fileTab.TabId, untitled.TabId, planTab.TabId], tabs.Select(tab => tab.TabId));
            var restoredPlan = tabs[2];
            Assert.Same(restoredPlan, harness.Tabs.SelectedTab);
            Assert.True(restoredPlan.IsSelected);
            Assert.Equal((9, 7, 4, 33.5, 2.0), (restoredPlan.CaretOffset, restoredPlan.SelectionStart, restoredPlan.SelectionLength, restoredPlan.VerticalOffset, restoredPlan.HorizontalOffset));
            Assert.Equal(3, tabs[0].CaretOffset);
            Assert.Equal(DocumentFormat.Markdown, restoredPlan.Format);
            Assert.Equal("My Workspace / Work / plan.md", restoredPlan.ToolTip);

            // Titles are known at once; only the active tab loads its text, the others on first activation.
            Assert.Equal(TabLoadState.Loaded, restoredPlan.LoadState);
            Assert.Equal(TabLoadState.NotLoaded, tabs[0].LoadState);
            Assert.Equal(TabLoadState.NotLoaded, tabs[1].LoadState);
            Assert.Equal("# Plan\nship it\n", await restoredPlan.LoadAsync());
            Assert.Equal(["Work", "plan.md"], harness.Shell.Editor.Breadcrumbs.Select(crumb => crumb.Name));
            Assert.Equal([false, true], harness.Shell.Editor.Breadcrumbs.Select(crumb => crumb.IsLast));

            harness.Tabs.SelectedTab = tabs[1];
            await harness.SettleAsync();
            Assert.Equal(TabLoadState.Loaded, tabs[1].LoadState);
            Assert.Equal("scratch", await tabs[1].LoadAsync());
            Assert.Equal(["Untitled 1"], harness.Shell.Editor.Breadcrumbs.Select(crumb => crumb.Name));
        });
    }

    [Fact]
    public async Task TheSessionIsSavedHalfASecondAfterAChange()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
            var second = await harness.Tabs.NewUntitledAsync();
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(499));
            Assert.Single(await harness.Core.Sessions.LoadTabsAsync());

            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(1));

            var saved = await harness.Core.Sessions.LoadTabsAsync();
            Assert.Equal(2, saved.Count);
            Assert.Equal(second.TabId, Assert.Single(saved, tab => tab.IsActive).TabId);

            second.UpdateViewState(5, 5, 0, 10, 0);
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(5, (await harness.Core.Sessions.LoadTabsAsync())[1].CaretOffset);
        });
    }

    [Fact]
    public async Task WithRestoreTabsOffANewShellStartsEmpty()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "kept as a note");
            harness.Shell.Settings.RestoreTabs = false;

            await harness.RestartAsync();

            Assert.Empty(harness.Tabs.Tabs);
            Assert.True(harness.Shell.IsEmptyStateVisible);
            Assert.Equal("No notes open", harness.Shell.EmptyStateTitle);
            Assert.NotNull(harness.Shell.Context.Model.Get(tab.NodeId!.Value));
        });
    }

    [Fact]
    public async Task AfterClosingEveryTabTheNextStartShowsTheEmptyState()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "x");
            await harness.Tabs.CloseAsync(tab);

            await harness.RestartAsync();

            Assert.Empty(harness.Tabs.Tabs);
            Assert.True(harness.Shell.IsEmptyStateVisible);
            Assert.True(harness.Tabs.CanReopenClosedTab);
            Assert.NotNull(await harness.Tabs.ReopenClosedAsync());
        });
    }

    [Fact]
    public async Task TabsOfNotesDeletedSinceAreDropped()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "x");
            var keep = await harness.Tabs.NewUntitledAsync();
            await harness.ShowAsync(keep);
            ShellHarness.Type(keep, "y");
            await harness.SettleAsync();
            await harness.Shell.ShutdownAsync();

            // Deleted by another session of the app (for example the 30-day purge) while NimPad was closed.
            var services = await harness.RunElsewhereAsync(core => core.Nodes.DeleteForeverAsync([tab.NodeId!.Value]));
            Assert.Equal(1, services);

            await harness.RestartAsync();

            Assert.Equal([keep.TabId], harness.Tabs.Tabs.Select(t => t.TabId));
        });
    }
}
