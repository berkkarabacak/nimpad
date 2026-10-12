using System.Diagnostics;
using NimPad.Presentation.Integration;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Integration;

/// <summary>
/// The watcher over real <see cref="FileSystemWatcher"/>s and real files. File-system events arrive in real time; the
/// debounce runs on the harness's fake clock, so the tests wait (in real time) until the watcher holds a pending
/// check and then step the clock across <see cref="ExternalFileWatcher.DebounceDelay"/>.
/// </summary>
public sealed class ExternalFileWatcherTests
{
    private static readonly TimeSpan JustBefore = ExternalFileWatcher.DebounceDelay - TimeSpan.FromMilliseconds(1);

    // Lets the burst of events of one file operation arrive before the clock moves.
    private static readonly TimeSpan EventsSettle = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task AChangeByAnotherProgramShowsTheBannerOnceItsEventsSettle()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "watched.txt", "one");
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            using var watcher = new ExternalFileWatcher(harness.Shell);
            Assert.Equal([path], watcher.WatchedFiles);
            Assert.True(watcher.IsWatching(tab));

            File.WriteAllText(path, "two");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            await WaitForPendingCheckAsync(harness, watcher, tab);

            await harness.AdvanceAsync(JustBefore);
            Assert.Empty(harness.Shell.Banners.Banners);
            Assert.True(watcher.HasPendingCheck(tab));

            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(1));

            Assert.False(watcher.HasPendingCheck(tab));
            Assert.Equal("“watched.txt” changed on disk.", Assert.Single(harness.Shell.Banners.Banners).Message);
        });
    }

    [Fact]
    public async Task NimPadsOwnSaveIsIgnored()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "mine.txt", "draft");
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            using var watcher = new ExternalFileWatcher(harness.Shell);

            ShellHarness.Type(tab, " saved");
            Assert.True(await tab.SaveAsync());
            await WaitForPendingCheckAsync(harness, watcher, tab);
            await harness.AdvanceAsync(ExternalFileWatcher.DebounceDelay);

            Assert.False(watcher.HasPendingCheck(tab));
            Assert.Equal(File.GetLastWriteTimeUtc(path), tab.LastSavedWriteTimeUtc);
            Assert.Empty(harness.Shell.Banners.Banners);
            Assert.False(tab.IsDirty);
        });
    }

    [Fact]
    public async Task DeletingRenamingAndRecreatingTheFileAreReported()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "report.md", "# Report");
            var tab = harness.Tabs.OpenExternal(path);
            var buffer = await harness.ShowAsync(tab);
            using var watcher = new ExternalFileWatcher(harness.Shell);

            File.Move(path, path + ".old");
            await WaitForPendingCheckAsync(harness, watcher, tab);
            await harness.AdvanceAsync(ExternalFileWatcher.DebounceDelay);

            Assert.True(tab.IsMissingOnDisk);
            Assert.True(tab.IsDirty);
            Assert.Equal("# Report", buffer.Text);
            Assert.Equal("“report.md” was moved or deleted.", Assert.Single(harness.Shell.Banners.Banners).Message);

            File.WriteAllText(path, "# Another report");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            await WaitForPendingCheckAsync(harness, watcher, tab);
            await harness.AdvanceAsync(ExternalFileWatcher.DebounceDelay);

            Assert.False(tab.IsMissingOnDisk);
            Assert.False(tab.IsDirty);
            Assert.Equal("“report.md” changed on disk.", Assert.Single(harness.Shell.Banners.Banners).Message);

            File.Delete(path);
            await WaitForPendingCheckAsync(harness, watcher, tab);
            await harness.AdvanceAsync(ExternalFileWatcher.DebounceDelay);

            Assert.True(tab.IsMissingOnDisk);
            Assert.Equal("“report.md” was moved or deleted.", Assert.Single(harness.Shell.Banners.Banners).Message);
        });
    }

    [Fact]
    public async Task WatchersFollowSaveAsAndStopWhenTabsClose()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var first = TestFiles.Write(harness, "a.txt", "a");
            var second = TestFiles.Write(harness, "b.txt", "b");
            using var watcher = new ExternalFileWatcher(harness.Shell);
            var tabA = harness.Tabs.OpenExternal(first);
            var tabB = harness.Tabs.OpenExternal(second);
            await harness.ShowAsync(tabA);
            await harness.NewNoteAndSettleAsync();

            Assert.Equal([first, second], watcher.WatchedFiles.Order(StringComparer.Ordinal));

            var renamed = harness.Directory.Combine("files", "a (copy).txt");
            Assert.True(await tabA.SaveAsAsync(renamed));
            Assert.Equal([renamed, second], watcher.WatchedFiles.Order(StringComparer.Ordinal));

            Assert.True(await harness.Tabs.CloseAsync(tabB));
            Assert.Equal([renamed], watcher.WatchedFiles);
            Assert.False(watcher.IsWatching(tabB));

            // The old file and the closed tab's file are not watched any more.
            File.WriteAllText(first, "changed");
            File.WriteAllText(second, "changed");
            await Task.Delay(EventsSettle);
            await harness.AdvanceAsync(ExternalFileWatcher.DebounceDelay);
            Assert.Empty(harness.Shell.Banners.Banners);

            watcher.Dispose();
            Assert.Empty(watcher.WatchedFiles);
            File.WriteAllText(renamed, "changed after dispose");
            await Task.Delay(EventsSettle);
            await harness.AdvanceAsync(ExternalFileWatcher.DebounceDelay);
            Assert.Empty(harness.Shell.Banners.Banners);
        });
    }

    [Fact]
    public async Task AFileInAFolderThatCannotBeWatchedStillOpensWithoutAWatcher()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            using var watcher = new ExternalFileWatcher(harness.Shell);
            var tab = harness.Tabs.OpenExternal(harness.Directory.Combine("no such folder", "x.txt"));
            await harness.SettleAsync();

            Assert.Single(watcher.WatchedFiles);
            Assert.False(watcher.IsWatching(tab));
            Assert.Equal(TabLoadState.Failed, tab.LoadState);
        });
    }

    [Fact]
    public async Task OnlyExternalTabsAreWatchedAndTheWatcherMustStartOnTheUiThread()
    {
        await using var harness = await ShellHarness.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => new ExternalFileWatcher(harness.Shell)));
        await harness.RunAsync(async () =>
        {
            using var watcher = new ExternalFileWatcher(harness.Shell);
            await harness.NewNoteAndSettleAsync();

            Assert.NotEmpty(harness.Tabs.Tabs);
            Assert.Empty(watcher.WatchedFiles);
        });
    }

    // From the UI thread: waits (real time) until the file's events reached the watcher and stopped arriving.
    private static async Task WaitForPendingCheckAsync(ShellHarness harness, ExternalFileWatcher watcher, TabViewModel tab)
    {
        var clock = Stopwatch.StartNew();
        while (!watcher.HasPendingCheck(tab))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "The file watcher saw no event.");
            await Task.Delay(10);
        }

        await Task.Delay(EventsSettle);
        await harness.SettleAsync();
    }
}

internal static class ShellHarnessWatcherExtensions
{
    /// <summary>Opens a new untitled note (so the external tabs are not the active one) and settles.</summary>
    public static async Task NewNoteAndSettleAsync(this ShellHarness harness)
    {
        await harness.Tabs.NewUntitledAsync();
        await harness.SettleAsync();
    }
}
