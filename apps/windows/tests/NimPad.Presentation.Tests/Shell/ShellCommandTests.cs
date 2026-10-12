using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Shell;

/// <summary>The keyboard-bound shell commands, run the way the App's InputBindings run them.</summary>
public sealed class ShellCommandTests
{
    private static async Task<List<TabViewModel>> ThreeTypedNotesAsync(ShellHarness harness)
    {
        var tabs = new List<TabViewModel> { harness.Tabs.Tabs[0], await harness.Tabs.NewUntitledAsync(), await harness.Tabs.NewUntitledAsync() };
        foreach (var tab in tabs)
        {
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, tab.Title);
        }

        return tabs;
    }

    [Fact]
    public async Task CtrlWClosesTheActiveTabAndCtrlShiftTBringsItBack()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tabs = await ThreeTypedNotesAsync(harness);
            harness.Tabs.SelectedTab = tabs[1];
            var focus = new List<FocusRegion>();
            harness.Shell.FocusRequested += (_, e) => focus.Add(e.Region);

            await harness.Shell.CloseTabCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal(["Untitled 1", "Untitled 3"], harness.Tabs.Tabs.Select(tab => tab.Title));
            Assert.Same(tabs[2], harness.Tabs.SelectedTab);

            await harness.Shell.ReopenClosedTabCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal(["Untitled 1", "Untitled 2", "Untitled 3"], harness.Tabs.Tabs.Select(tab => tab.Title));
            Assert.Equal(tabs[1].NodeId, harness.Tabs.SelectedTab!.NodeId);
            Assert.Equal([FocusRegion.Editor], focus);
        });
    }

    [Fact]
    public async Task CtrlTabAndCtrlShiftTabCycleThroughTheTabs()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tabs = await ThreeTypedNotesAsync(harness);
            harness.Tabs.SelectedTab = tabs[2];

            harness.Shell.NextTabCommand.Execute(null);
            Assert.Same(tabs[0], harness.Tabs.SelectedTab);
            harness.Shell.NextTabCommand.Execute(null);
            Assert.Same(tabs[1], harness.Tabs.SelectedTab);
            harness.Shell.PreviousTabCommand.Execute(null);
            harness.Shell.PreviousTabCommand.Execute(null);
            Assert.Same(tabs[2], harness.Tabs.SelectedTab);
            harness.Shell.GoToTabCommand.Execute("1");
            Assert.Same(tabs[0], harness.Tabs.SelectedTab);
            harness.Shell.GoToTabCommand.Execute("9");
            Assert.Same(tabs[2], harness.Tabs.SelectedTab);
        });
    }

    [Fact]
    public async Task CtrlOOpensThePickedFilesAsTabsAndFocusesTheEditor()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var a = TestFiles.Write(harness, "a.txt", "a");
            var b = TestFiles.Write(harness, "b.md", "b");
            var focus = new List<FocusRegion>();
            harness.Shell.FocusRequested += (_, e) => focus.Add(e.Region);

            // Cancelling the dialog does nothing.
            harness.Shell.OpenFileCommand.Execute(null);
            Assert.Single(harness.Tabs.Tabs);
            Assert.Empty(focus);

            harness.Platform.FileDialogs.FilesToOpen.Enqueue([a, b]);
            harness.Shell.OpenFileCommand.Execute(null);
            await harness.SettleAsync();

            Assert.Equal([a, b], harness.Tabs.Tabs.Where(tab => tab.IsExternal).Select(tab => tab.FilePath));
            Assert.Equal(b, harness.Tabs.SelectedTab!.FilePath);
            Assert.Equal("b", await harness.Tabs.SelectedTab.LoadAsync());
            Assert.Equal([FocusRegion.Editor], focus);
        });
    }

    [Fact]
    public async Task ANewNoteFromTheSidebarRootGetsTheKeyboardLikeCtrlN()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            harness.Shell.WindowWidth = 600;
            harness.Shell.FocusSearchCommand.Execute(null);
            Assert.True(harness.Shell.IsSidebarOverlay);
            Assert.True(harness.Shell.IsSidebarOpen);
            var focus = new List<FocusRegion>();
            harness.Shell.FocusRequested += (_, e) => focus.Add(e.Region);

            await harness.Shell.Explorer.NewNoteCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal("Untitled 2", harness.Tabs.SelectedTab!.Title);
            Assert.Equal([FocusRegion.Editor], focus);
            Assert.False(harness.Shell.IsSidebarOpen);
        });
    }
}
