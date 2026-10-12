using NimPad.Presentation.Shell;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Shell;

public sealed class ShellLayoutTests
{
    [Theory]
    [InlineData(1120, LayoutMode.Wide)]
    [InlineData(720, LayoutMode.Wide)]
    [InlineData(719, LayoutMode.Medium)]
    [InlineData(520, LayoutMode.Medium)]
    [InlineData(519, LayoutMode.Narrow)]
    [InlineData(400, LayoutMode.Narrow)]
    public async Task TheWindowWidthPicksTheLayout(double width, LayoutMode expected)
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(() =>
        {
            var shell = harness.Shell;
            shell.WindowWidth = width;

            Assert.Equal(expected, shell.LayoutMode);
            var wide = expected == LayoutMode.Wide;
            Assert.Equal(wide, shell.ShowAppName);
            Assert.Equal(wide, shell.IsCharacterCountVisible);
            Assert.Equal(!wide, shell.IsSidebarOverlay);
            Assert.Equal(expected == LayoutMode.Narrow, shell.IsStatusBarCompact);
            Assert.Equal(wide ? 32 : 24, shell.EditorHorizontalPadding);
            Assert.Equal(shell.EditorHorizontalPadding, shell.Editor.HorizontalPadding);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheOverlaySidebarStartsClosedAndTheDockedPreferenceComesBack()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var shell = harness.Shell;
            var focus = new List<FocusRegion>();
            shell.FocusRequested += (_, e) => focus.Add(e.Region);
            Assert.True(shell.IsSidebarOpen);

            shell.WindowWidth = 600;
            Assert.False(shell.IsSidebarOpen);

            shell.ToggleSidebarCommand.Execute(null);
            Assert.True(shell.IsSidebarOpen);
            Assert.Equal(FocusRegion.Sidebar, focus[^1]);
            shell.DismissSidebarOverlayCommand.Execute(null);
            Assert.False(shell.IsSidebarOpen);
            Assert.Equal(FocusRegion.Editor, focus[^1]);

            shell.WindowWidth = 1000;
            Assert.True(shell.IsSidebarOpen);

            shell.ToggleSidebarCommand.Execute(null);
            Assert.False(shell.IsSidebarOpen);
            shell.StatusBar.ToggleSidebarCommand.Execute(null);
            Assert.True(shell.IsSidebarOpen);
            shell.ToggleSidebarCommand.Execute(null);
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

            await harness.RestartAsync();
            Assert.False(harness.Shell.IsSidebarOpen);
            harness.Shell.FocusSearchCommand.Execute(null);
            Assert.True(harness.Shell.IsSidebarOpen);
        });
    }

    [Fact]
    public async Task TheSidebarWidthIsClampedAndRemembered()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var shell = harness.Shell;
            Assert.Equal(288, shell.SidebarWidth);

            shell.SidebarWidth = 1000;
            Assert.Equal(480, shell.SidebarWidth);
            shell.SidebarWidth = 50;
            Assert.Equal(200, shell.SidebarWidth);
            shell.SidebarWidth = 320;
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(320, (await harness.Core.Sessions.LoadUiStateAsync()).SidebarWidth);

            await harness.RestartAsync();
            Assert.Equal(320, harness.Shell.SidebarWidth);
        });
    }

    [Fact]
    public async Task F6CyclesTheRegionsAndSkipsAClosedSidebar()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(() =>
        {
            var shell = harness.Shell;
            var focus = new List<FocusRegion>();
            shell.FocusRequested += (_, e) => focus.Add(e.Region);
            shell.ReportFocus(FocusRegion.TabStrip);

            for (var i = 0; i < 4; i++)
            {
                shell.NextRegionCommand.Execute(null);
            }

            Assert.Equal([FocusRegion.Editor, FocusRegion.Sidebar, FocusRegion.StatusBar, FocusRegion.TabStrip], focus);

            shell.ToggleSidebarCommand.Execute(null);
            focus.Clear();
            shell.ReportFocus(FocusRegion.Editor);
            shell.NextRegionCommand.Execute(null);
            shell.PreviousRegionCommand.Execute(null);
            shell.PreviousRegionCommand.Execute(null);
            Assert.Equal([FocusRegion.StatusBar, FocusRegion.Editor, FocusRegion.TabStrip], focus);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SettingsAndShortcutsAreOpenedByTheView()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(() =>
        {
            var settings = 0;
            var shortcuts = 0;
            harness.Shell.OpenSettingsRequested += (_, _) => settings++;
            harness.Shell.ShowShortcutsRequested += (_, _) => shortcuts++;

            harness.Shell.OpenSettingsCommand.Execute(null);
            harness.Shell.ShowShortcutsCommand.Execute(null);

            Assert.Equal((1, 1), (settings, shortcuts));
            return Task.CompletedTask;
        });
    }
}
