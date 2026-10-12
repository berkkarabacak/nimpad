using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Shell;

public sealed class ShellStartupTests
{
    [Fact]
    public async Task ABrandNewProfileStartsWithOneEmptyUntitledNote()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = Assert.Single(harness.Tabs.Tabs);
            Assert.Equal("Untitled 1", tab.Title);
            Assert.Same(tab, harness.Tabs.SelectedTab);
            Assert.True(tab.IsSelected);
            Assert.Equal(TabLoadState.Loaded, tab.LoadState);
            Assert.True(harness.Shell.IsInitialized);
            Assert.False(harness.Shell.IsEmptyStateVisible);

            var buffer = await harness.ShowAsync(tab);
            Assert.Equal(string.Empty, buffer.Text);
            Assert.True(harness.Shell.Editor.ShowEmptyHint);
            Assert.Equal("Local only", harness.Shell.StatusBar.SyncText);
        });
    }
}

public sealed class ShellStartupNoticeTests
{
    [Fact]
    public async Task CommandLineFilesOpenAsExternalTabs()
    {
        using var directory = new TempDirectory();
        var file = directory.Combine("readme.md");
        await File.WriteAllTextAsync(file, "# Hello");

        await using var harness = await ShellHarness.StartAsync(directory, files: [file]);
        await harness.RunAsync(async () =>
        {
            // A brand-new profile with a file to open does not also get an untitled note.
            var tab = Assert.Single(harness.Tabs.Tabs);
            Assert.True(tab.IsExternal);
            Assert.Equal("readme.md", tab.Title);
            Assert.Equal("# Hello", await tab.LoadAsync());
            Assert.Equal(Core.Domain.DocumentFormat.Markdown, tab.Format);

            harness.Shell.OpenFiles([file, directory.Combine("other.txt")]);
            Assert.Equal(2, harness.Tabs.Tabs.Count);
        });
    }

    [Fact]
    public async Task ADamagedDatabaseIsExplainedInABanner()
    {
        using var directory = new TempDirectory();
        var database = directory.Combine("profiles", "local", "nimpad.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        await File.WriteAllBytesAsync(database, Enumerable.Repeat((byte)0x5A, 8192).ToArray());

        await using var harness = await ShellHarness.StartAsync(directory);
        await harness.RunAsync(async () =>
        {
            var banner = harness.Shell.Banners.Find(NimPad.Presentation.Shell.ShellViewModel.DatabaseRecoveryBannerKey)!;
            Assert.Equal(NimPad.Presentation.Feedback.BannerKind.Error, banner.Kind);
            Assert.Equal("NimPad found damage in its database and had no backup to restore, so it started a new one. The damaged file was kept.", banner.Message);
            Assert.Equal("Show files", banner.ActionText);

            await banner.ActionCommand.ExecuteAsync(null);

            Assert.Equal(Path.GetDirectoryName(database), Assert.Single(harness.Platform.Shell.Opened).LocalPath);
            Assert.NotNull(harness.Shell.Banners.Find(NimPad.Presentation.Shell.ShellViewModel.DatabaseRecoveryBannerKey));
            Assert.Empty(harness.Tabs.Tabs);
            Assert.True(harness.Shell.IsEmptyStateVisible);
        });
    }

    [Fact]
    public async Task CommandsBeforeInitializationDoNothingHarmful()
    {
        using var ui = new TestUiThread();
        await ui.RunAsync(async () =>
        {
            var platform = new FakePlatform(ui);
            var shell = new NimPad.Presentation.Shell.ShellViewModel(platform.Services, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(ShellHarness.Start), ShellHarness.Culture);

            Assert.False(shell.IsInitialized);
            Assert.False(shell.IsEmptyStateVisible);
            Assert.True(shell.EmergencyFlush());
            await shell.ShutdownAsync();
            await shell.NewNoteCommand.ExecuteAsync(null);
            Assert.Equal("NimPad is not initialized yet.", Assert.Single(shell.Toasts.Toasts).Message.Replace("Something went wrong: ", string.Empty, StringComparison.Ordinal));
        });
        Assert.Empty(ui.Errors);
    }
}
