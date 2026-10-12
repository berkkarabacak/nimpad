using NimPad.Core.Files;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Tabs;

/// <summary>Unsaved text of external files that outlived a crash or an earlier session.</summary>
public sealed class CrashRecoveryTests
{
    [Fact]
    public async Task ARestoredTabStartsFromItsRecoveredTextAndNewEditsKeepIt()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "notes.txt", "disk v1\r\n");
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "UNSAVED BEFORE CRASH\r\n");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(2.1));
            Assert.Equal("disk v1\r\nUNSAVED BEFORE CRASH\r\n", Assert.Single(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync()).Text);

            await harness.RestartAsync();

            var restored = Assert.Single(harness.Tabs.Tabs, t => t.IsExternal);
            var buffer = await harness.ShowAsync(restored);
            Assert.Equal("disk v1\r\nUNSAVED BEFORE CRASH\r\n", buffer.Text);
            Assert.True(restored.IsDirty);
            Assert.Equal("disk v1\r\n", File.ReadAllText(path));

            // The file did not change since: no "changed on disk" warning.
            Assert.Null(harness.Shell.Banners.Find(restored.DiskBannerKey));
            Assert.NotNull(harness.Shell.Banners.Find(ShellViewModel.RecoveredFilesBannerKey));

            ShellHarness.Type(restored, "new edit\r\n");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(2.1));
            Assert.Equal(
                "disk v1\r\nUNSAVED BEFORE CRASH\r\nnew edit\r\n",
                Assert.Single(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync()).Text);

            // Review only brings the tab forward; it never puts the older recovered text back over the new edit.
            harness.Tabs.SelectedTab = harness.Tabs.Tabs[0];
            await harness.Shell.Banners.Find(ShellViewModel.RecoveredFilesBannerKey)!.ActionCommand.ExecuteAsync(null);
            await harness.SettleAsync();
            Assert.Same(restored, harness.Tabs.SelectedTab);
            Assert.Equal("disk v1\r\nUNSAVED BEFORE CRASH\r\nnew edit\r\n", buffer.Text);

            Assert.True(await restored.SaveAsync());
            await harness.SettleAsync();
            Assert.Equal("disk v1\r\nUNSAVED BEFORE CRASH\r\nnew edit\r\n", File.ReadAllText(path));
            Assert.Empty(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync());
        });
    }

    [Fact]
    public async Task ReviewOpensRecoveredFilesThatAreNotOpen()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "todo.md", "- a\n");
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "- b\n");
            harness.Shell.Settings.RestoreTabs = false;

            await harness.RestartAsync();

            Assert.DoesNotContain(harness.Tabs.Tabs, t => t.IsExternal);
            var banner = harness.Shell.Banners.Find(ShellViewModel.RecoveredFilesBannerKey)!;
            Assert.Equal("NimPad recovered unsaved changes to 1 file", banner.Message);

            await banner.ActionCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            var reopened = harness.Tabs.SelectedTab!;
            Assert.Equal(path, reopened.FilePath);
            Assert.Equal("- a\n- b\n", (await harness.ShowAsync(reopened)).Text);
            Assert.True(reopened.IsDirty);
            Assert.Null(harness.Shell.Banners.Find(ShellViewModel.RecoveredFilesBannerKey));
        });
    }

    [Fact]
    public async Task AFileOpenedFromTheCommandLineStartsFromItsRecoveredText()
    {
        await using var harness = await ShellHarness.StartAsync();
        var path = string.Empty;
        await harness.RunAsync(async () =>
        {
            path = TestFiles.Write(harness, "app.config", "a=1\n");
            await harness.Core.ExternalFiles.SaveRecoveryBufferAsync(
                path,
                "a=1\nb=2\n",
                TextEncodingInfo.Utf8,
                LineEndingStyle.Lf,
                File.GetLastWriteTimeUtc(path));
            harness.Shell.Settings.RestoreTabs = false;

            await harness.RestartAsync(files: [path]);

            var tab = Assert.Single(harness.Tabs.Tabs, t => t.IsExternal);
            Assert.Equal("a=1\nb=2\n", (await harness.ShowAsync(tab)).Text);
            Assert.True(tab.IsDirty);
        });
    }

    [Fact]
    public async Task RecoveredTextOfAFileThatChangedOnDiskSinceComesWithAWarning()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "shared.txt", "v1\n");
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "mine\n");

            // Another program writes the file; the crash comes before NimPad hears about it.
            File.WriteAllText(path, "theirs\n");
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));
            await harness.RestartAsync();

            var restored = Assert.Single(harness.Tabs.Tabs, t => t.IsExternal);
            var buffer = await harness.ShowAsync(restored);
            Assert.Equal("v1\nmine\n", buffer.Text);
            var warning = harness.Shell.Banners.Find(restored.DiskBannerKey);
            Assert.NotNull(warning);
            Assert.Equal("“shared.txt” changed on disk.", warning.Message);
            Assert.Equal("Reload", warning.ActionText);
        });
    }
}
