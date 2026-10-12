using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Tabs;

public sealed class TabCommandTests
{
    private static async Task<List<TabViewModel>> OpenTypedNotesAsync(ShellHarness harness, int count)
    {
        var tabs = new List<TabViewModel> { harness.Tabs.Tabs[0] };
        while (tabs.Count < count)
        {
            tabs.Add(await harness.Tabs.NewUntitledAsync());
        }

        foreach (var tab in tabs)
        {
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, tab.Title);
        }

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        return tabs;
    }

    [Fact]
    public async Task ReopeningWithTheShortcutRetiresTheClosedToast()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tabs = await OpenTypedNotesAsync(harness, 3);
            await harness.Tabs.CloseAsync(tabs[0]);
            await harness.Tabs.CloseAsync(tabs[1]);
            Assert.Equal("Closed “Untitled 2”", Assert.Single(harness.Shell.Toasts.Toasts).Message);

            // Ctrl+Shift+T reopens "Untitled 2"; the toast's Undo would now reopen "Untitled 1" instead, so it goes.
            await harness.Shell.ReopenClosedTabCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Empty(harness.Shell.Toasts.Toasts);
            Assert.Equal(["Untitled 2", "Untitled 3"], harness.Tabs.Tabs.Select(tab => tab.Title));
            Assert.True(harness.Tabs.CanReopenClosedTab);
        });
    }

    [Fact]
    public async Task ClosingSeveralTabsOffersOneUndoThatReopensAllOfThemInPlace()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tabs = await OpenTypedNotesAsync(harness, 4);
            harness.Tabs.SelectedTab = tabs[2];

            Assert.Equal(3, await harness.Tabs.CloseOthersAsync(tabs[2]));
            await harness.SettleAsync();

            var toast = Assert.Single(harness.Shell.Toasts.Toasts);
            Assert.Equal("Closed 3 tabs", toast.Message);
            Assert.Equal("Undo", toast.ActionText);

            // Ctrl+Shift+T reopens one tab, so the toast does not claim it as the shortcut of its Undo.
            Assert.Null(toast.ActionShortcutText);

            await toast.ActionCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal(["Untitled 1", "Untitled 2", "Untitled 3", "Untitled 4"], harness.Tabs.Tabs.Select(tab => tab.Title));
            Assert.Equal(tabs.Select(tab => tab.NodeId), harness.Tabs.Tabs.Select(tab => tab.NodeId));
            Assert.False(harness.Tabs.CanReopenClosedTab);
        });
    }

    [Fact]
    public async Task TabMenuCommandsAreDisabledWhenTheyCannotAct()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tabs = harness.Tabs;
            var note = tabs.Tabs[0];
            Assert.False(tabs.CloseOtherTabsCommand.CanExecute(note));
            Assert.False(tabs.CloseTabsToTheRightCommand.CanExecute(note));
            Assert.True(tabs.DuplicateTabCommand.CanExecute(note));
            Assert.True(tabs.RenameTabCommand.CanExecute(note));
            Assert.False(tabs.ImportTabCommand.CanExecute(note));

            var changes = 0;
            tabs.CloseOtherTabsCommand.CanExecuteChanged += (_, _) => changes++;
            var file = tabs.OpenExternal(TestFiles.Write(harness, "a.txt", "a"));
            Assert.True(changes > 0);

            Assert.True(tabs.CloseOtherTabsCommand.CanExecute(note));
            Assert.True(tabs.CloseTabsToTheRightCommand.CanExecute(note));
            Assert.False(tabs.CloseTabsToTheRightCommand.CanExecute(file));
            Assert.False(tabs.DuplicateTabCommand.CanExecute(file));
            Assert.False(tabs.RenameTabCommand.CanExecute(file));
            Assert.True(tabs.ImportTabCommand.CanExecute(file));

            // Without a parameter the commands act on the selected tab.
            Assert.Same(file, tabs.SelectedTab);
            Assert.True(tabs.ImportTabCommand.CanExecute(null));
            Assert.False(tabs.DuplicateTabCommand.CanExecute(null));

            // Pinned tabs are never closed by these commands.
            changes = 0;
            tabs.Pin(file);
            Assert.True(changes > 0);
            Assert.Equal([file, note], tabs.Tabs);
            Assert.False(tabs.CloseOtherTabsCommand.CanExecute(note));
            Assert.False(tabs.CloseTabsToTheRightCommand.CanExecute(note));
            Assert.True(tabs.CloseTabsToTheRightCommand.CanExecute(file));
            Assert.True(tabs.CloseOtherTabsCommand.CanExecute(file));
        });
    }

    [Fact]
    public async Task AnUntouchedUntitledNoteCarriedOverFromTheLastSessionIsDiscardedWhenClosed()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            // A brand-new profile opens "Untitled 1"; the user quits without typing.
            var id = Assert.Single(harness.Tabs.Tabs).NodeId!.Value;
            await harness.RestartAsync();

            var tab = Assert.Single(harness.Tabs.Tabs);
            Assert.Equal(id, tab.NodeId);
            await harness.ShowAsync(tab);
            Assert.True(await harness.Tabs.CloseAsync(tab));
            await harness.SettleAsync();

            Assert.Null(await harness.Core.Nodes.GetNodeAsync(id));
            Assert.False(harness.Shell.Context.Model.Contains(id));
            Assert.Empty(harness.Shell.Toasts.Toasts);
            Assert.False(harness.Tabs.CanReopenClosedTab);
        });
    }

    [Fact]
    public async Task AnUntouchedUntitledNoteOpenedFromTheSidebarIsKeptWhenClosed()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var first = harness.Tabs.Tabs[0];
            await harness.ShowAsync(first);
            ShellHarness.Type(first, "keeps the session busy");
            var empty = await harness.Core.Nodes.CreateDocumentAsync(harness.Shell.Context.Workspace.Id, null);
            await harness.SettleAsync();
            Assert.StartsWith("Untitled", empty.Name, StringComparison.Ordinal);

            var tab = (await harness.Tabs.OpenAsync(empty.Id))!;
            await harness.ShowAsync(tab);
            Assert.True(await harness.Tabs.CloseAsync(tab));
            await harness.SettleAsync();

            Assert.NotNull(await harness.Core.Nodes.GetNodeAsync(empty.Id));
            Assert.True(harness.Shell.Context.Model.Contains(empty.Id));
            Assert.Equal($"Closed “{empty.Name}”", Assert.Single(harness.Shell.Toasts.Toasts).Message);
        });
    }
}
