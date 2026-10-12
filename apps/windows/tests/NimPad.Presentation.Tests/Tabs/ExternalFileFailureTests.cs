using NimPad.Presentation.Feedback;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Tabs;

/// <summary>Loading a large file can be cancelled; failed loads and saves explain themselves and can be retried.</summary>
public sealed class ExternalFileFailureTests
{
    [Fact]
    public async Task CancellingTheLoadOfALargeFileClosesItsTab()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var bytes = new byte[12 * 1024 * 1024];
            Array.Fill(bytes, (byte)'x');
            var tab = harness.Tabs.OpenExternal(TestFiles.Write(harness, "huge.log", bytes), activate: false);
            await harness.Tabs.SaveSessionAsync();
            Assert.Contains(await harness.Core.Sessions.LoadTabsAsync(), saved => saved.FilePath == tab.FilePath);
            harness.Tabs.SelectedTab = tab;
            var load = tab.LoadAsync();
            Assert.Equal(TabLoadState.Loading, tab.LoadState);
            Assert.True(tab.IsLoadProgressVisible);
            var closed = new List<TabViewModel>();
            harness.Tabs.TabClosed += (_, t) => closed.Add(t);

            tab.CancelLoadCommand.Execute(null);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
            await harness.SettleAsync();
            Assert.DoesNotContain(tab, harness.Tabs.Tabs);
            Assert.Equal([tab], closed);
            Assert.False(tab.IsLoadProgressVisible);
            Assert.NotSame(tab, harness.Tabs.SelectedTab);

            await harness.AdvanceAsync(TabsViewModel.SessionSaveDelay);
            Assert.DoesNotContain(await harness.Core.Sessions.LoadTabsAsync(), saved => saved.FilePath == tab.FilePath);
        });
    }

    [Fact]
    public async Task ACancelThatArrivesJustAsTheReadFinishesStillClosesTheTab()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.OpenExternal(TestFiles.Write(harness, "medium.txt", new string('m', 1024 * 1024)), activate: false);
            await harness.SettleAsync();
            var load = tab.LoadAsync();
            Assert.False(load.IsCompleted);

            // Hold the UI thread until the read has finished and the load's continuation waits in the UI queue, then
            // cancel: the user asked for the tab to go, so it goes even though the text is already there.
            Assert.True(SpinWait.SpinUntil(() => harness.Ui.PendingCount > 0, TimeSpan.FromSeconds(10)));
            tab.CancelLoadCommand.Execute(null);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
            await harness.SettleAsync();
            Assert.DoesNotContain(tab, harness.Tabs.Tabs);
        });
    }

    [Fact]
    public async Task AFailedLoadCanBeRetried()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = harness.Directory.Combine("later.txt");
            var tab = harness.Tabs.OpenExternal(path);
            await harness.SettleAsync();
            Assert.Equal(TabLoadState.Failed, tab.LoadState);
            Assert.Equal("“later.txt” no longer exists.", tab.LoadError);

            await File.WriteAllTextAsync(path, "here now");
            await tab.RetryLoadCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal(TabLoadState.Loaded, tab.LoadState);
            Assert.Null(tab.LoadError);
            Assert.Equal("here now", await tab.LoadAsync());
        });
    }

    [Fact]
    public async Task CharactersTheEncodingCannotStoreKeepTheFileAsItWas()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            // 0xE9 alone is not UTF-8, so the file reads as Windows-1252 ("café").
            var path = TestFiles.Write(harness, "menu.txt", [(byte)'c', (byte)'a', (byte)'f', 0xE9]);
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            Assert.Equal("Windows-1252", harness.Shell.StatusBar.EncodingText);
            ShellHarness.Type(tab, " 😀");

            Assert.False(await tab.SaveAsync());

            var banner = harness.Shell.Banners.Find(tab.SaveBannerKey);
            Assert.NotNull(banner);
            Assert.Equal(BannerKind.Error, banner.Kind);
            Assert.Equal("“menu.txt” wasn't saved: it contains characters that Windows-1252 can't store.", banner.Message);
            Assert.True(tab.IsDirty);
            Assert.Equal([(byte)'c', (byte)'a', (byte)'f', 0xE9], File.ReadAllBytes(path));
        });
    }

    [Fact]
    public async Task ASaveThatFailsOffersRetryAndTheBannerGoesOnceItWorks()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, Path.Combine("share", "todo.txt"), "a\n");
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "b\n");

            // The folder went away (a disconnected share, a removed drive).
            var folder = Path.GetDirectoryName(path)!;
            Directory.Delete(folder, recursive: true);
            await harness.Shell.SaveCommand.ExecuteAsync(null);

            var banner = harness.Shell.Banners.Find(tab.SaveBannerKey)!;
            Assert.StartsWith("Couldn't save “todo.txt”.", banner.Message, StringComparison.Ordinal);
            Assert.Equal("Retry", banner.ActionText);
            Assert.True(tab.IsDirty);

            Directory.CreateDirectory(folder);
            await banner.ActionCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal("a\nb\n", File.ReadAllText(path));
            Assert.False(tab.IsDirty);
            Assert.Null(harness.Shell.Banners.Find(tab.SaveBannerKey));
        });
    }
}
