using NimPad.Presentation.Feedback;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Tabs;

/// <summary>An open external file that another program moves, renames or deletes keeps its text in the tab.</summary>
public sealed class ExternalFileMissingTests
{
    [Fact]
    public async Task ADeletedFileKeepsItsTextBecomesUnsavedAndOffersSaveAs()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "plan.txt", "keep me");
            var tab = harness.Tabs.OpenExternal(path);
            var buffer = await harness.ShowAsync(tab);
            Assert.False(tab.IsDirty);

            File.Delete(path);
            Assert.True(tab.OnFileMissingOnDisk());

            Assert.True(tab.IsMissingOnDisk);
            Assert.True(tab.IsDirty);
            Assert.Equal(TabSaveState.Pending, tab.SaveState);
            Assert.Equal("keep me", buffer.Text);
            var banner = Assert.Single(harness.Shell.Banners.Banners);
            Assert.Equal(BannerKind.Warning, banner.Kind);
            Assert.Equal("“plan.txt” was moved or deleted.", banner.Message);
            Assert.Equal("Save as", banner.ActionText);
            Assert.True(banner.CanDismiss);

            // Cancelling the dialog keeps everything as it was.
            await banner.ActionCommand.ExecuteAsync(null);
            Assert.Equal(("plan.txt", Path.GetDirectoryName(path)), Assert.Single(harness.Platform.FileDialogs.SaveRequests));
            Assert.Same(banner, Assert.Single(harness.Shell.Banners.Banners));
            Assert.True(tab.IsDirty);

            var target = harness.Directory.Combine("files", "plan (rescued).txt");
            harness.Platform.FileDialogs.SavePaths.Enqueue(target);
            await banner.ActionCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal("keep me", File.ReadAllText(target));
            Assert.Equal(target, tab.FilePath);
            Assert.Equal("plan (rescued).txt", tab.Title);
            Assert.False(tab.IsMissingOnDisk);
            Assert.False(tab.IsDirty);
            Assert.Empty(harness.Shell.Banners.Banners);
        });
    }

    [Fact]
    public async Task SavingInPlaceWritesTheFileBack()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "notes.md", "# Notes");
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            File.Delete(path);

            // Without a write time from the watcher, the tab looks itself and finds the file gone.
            Assert.True(tab.OnFileChangedOnDisk());
            Assert.True(tab.IsMissingOnDisk);
            Assert.Equal("“notes.md” was moved or deleted.", Assert.Single(harness.Shell.Banners.Banners).Message);

            await harness.Shell.SaveCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal("# Notes", File.ReadAllText(path));
            Assert.False(tab.IsMissingOnDisk);
            Assert.False(tab.IsDirty);
            Assert.Empty(harness.Shell.Banners.Banners);
        });
    }

    [Fact]
    public async Task AFileThatComesBackClearsTheNoticeAndIsComparedAsUsual()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "config.ini", "a=1");
            var original = File.GetLastWriteTimeUtc(path);
            var moved = path + ".moved";
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);

            File.Move(path, moved);
            Assert.True(tab.OnFileMissingOnDisk());

            // Moved back unchanged: nothing to report, and nothing unsaved.
            File.Move(moved, path);
            Assert.False(tab.OnFileChangedOnDisk(original));
            Assert.False(tab.IsMissingOnDisk);
            Assert.False(tab.IsDirty);
            Assert.Empty(harness.Shell.Banners.Banners);

            // Deleted, then recreated with other content: the usual "changed on disk" notice.
            File.Delete(path);
            Assert.True(tab.OnFileMissingOnDisk());
            File.WriteAllText(path, "a=2");
            File.SetLastWriteTimeUtc(path, original.AddMinutes(1));
            Assert.True(tab.OnFileMissingOnDisk());
            Assert.False(tab.IsMissingOnDisk);
            Assert.False(tab.IsDirty);
            Assert.Equal("“config.ini” changed on disk.", Assert.Single(harness.Shell.Banners.Banners).Message);
        });
    }

    [Fact]
    public async Task EditsMadeWhileTheFileIsMissingStayUnsavedAfterItComesBack()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "todo.txt", "one");
            var original = File.GetLastWriteTimeUtc(path);
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            File.Move(path, path + ".bak");
            tab.OnFileMissingOnDisk();

            ShellHarness.Type(tab, " two");
            File.Move(path + ".bak", path);
            tab.OnFileChangedOnDisk(original);

            Assert.False(tab.IsMissingOnDisk);
            Assert.True(tab.IsDirty);
        });
    }

    [Fact]
    public async Task ATabWhoseTextIsNotShownForgetsItsCopyAndReportsTheMissingFileWhenShown()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "later.txt", "text");
            var other = TestFiles.Write(harness, "active.txt", "active");
            var tab = harness.Tabs.OpenExternal(path);
            await tab.LoadAsync();
            harness.Tabs.OpenExternal(other);
            await harness.SettleAsync();
            Assert.Equal(TabLoadState.Loaded, tab.LoadState);
            Assert.Null(tab.Buffer);

            File.Delete(path);
            Assert.False(tab.OnFileMissingOnDisk());

            Assert.Equal(TabLoadState.NotLoaded, tab.LoadState);
            Assert.False(tab.IsMissingOnDisk);
            Assert.Empty(harness.Shell.Banners.Banners);
            await Assert.ThrowsAsync<FileNotFoundException>(tab.LoadAsync);
            Assert.Equal("“later.txt” no longer exists.", tab.LoadError);
        });
    }

    [Fact]
    public async Task DontSaveWhileClosingLeavesNoRecoveredCopyBehind()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "gone.txt", "bye");
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            File.Delete(path);
            tab.OnFileMissingOnDisk();

            harness.Platform.Dialogs.Answers.Enqueue(DialogChoice.Secondary);
            Assert.True(await harness.Shell.ConfirmCloseAsync());
            Assert.Equal("Save changes to “gone.txt”?", Assert.Single(harness.Platform.Dialogs.Requests).Title);
            Assert.False(tab.IsDirty);

            await harness.RestartAsync();

            Assert.Null(harness.Shell.Banners.Find(ShellViewModel.RecoveredFilesBannerKey));
            Assert.Empty(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync());
            Assert.False(File.Exists(path));
        });
    }
}
