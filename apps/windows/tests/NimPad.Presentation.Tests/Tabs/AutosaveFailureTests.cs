using NimPad.Core.Ids;
using NimPad.Presentation.Editor;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Tabs;

/// <summary>What the tabs do when Core's own writer fails to store a note (a real failure, see <see cref="DatabaseFaults"/>).</summary>
public sealed class AutosaveFailureTests
{
    [Fact]
    public async Task AFailingSaveShowsAStickyRetryBannerUntilASaveSucceeds()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            var id = tab.NodeId!.Value;
            var failure = DatabaseFaults.FailSavesOf(harness, id);

            ShellHarness.Type(tab, "keep me");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(TabSaveState.Failed, tab.SaveState);
            Assert.True(tab.IsDirty);
            Assert.Equal(SyncState.SaveFailed, harness.Shell.StatusBar.SyncState);
            Assert.Equal("Not saved · Retry", harness.Shell.StatusBar.SyncText);
            var banner = Assert.Single(harness.Shell.Banners.Banners);
            Assert.Equal(BannerKind.Error, banner.Kind);
            Assert.Equal("Couldn't save “Untitled 1”. NimPad keeps trying; your text stays in this tab.", banner.Message);
            Assert.Equal("Retry", banner.ActionText);
            Assert.False(banner.CanDismiss);
            Assert.Equal(string.Empty, (await harness.Core.Documents.GetContentAsync(id)).Text);

            // Retry while the cause is still there: still failing, still shown.
            await harness.Shell.StatusBar.RetrySaveCommand.ExecuteAsync(null);
            await harness.SettleAsync();
            Assert.Equal(TabSaveState.Failed, tab.SaveState);
            Assert.NotNull(harness.Shell.Banners.Find(tab.SaveBannerKey));

            failure.Dispose();
            await harness.Shell.StatusBar.RetrySaveCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.Equal("keep me", (await harness.Core.Documents.GetContentAsync(id)).Text);
            Assert.Equal(TabSaveState.Saved, tab.SaveState);
            Assert.False(tab.IsDirty);
            Assert.Equal(SyncState.LocalOnly, harness.Shell.StatusBar.SyncState);
            Assert.Empty(harness.Shell.Banners.Banners);
        });
    }

    [Fact]
    public async Task TheAutomaticRetryClearsTheBannerOnceTheCauseIsGone()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            using (DatabaseFaults.FailSavesOf(harness, tab.NodeId!.Value))
            {
                ShellHarness.Type(tab, "later");
                await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
                Assert.NotNull(harness.Shell.Banners.Find(tab.SaveBannerKey));
            }

            // The first retry comes 1 s after the failure.
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

            Assert.Equal("later", (await harness.Core.Documents.GetContentAsync(tab.NodeId.Value)).Text);
            Assert.Equal(TabSaveState.Saved, tab.SaveState);
            Assert.Null(harness.Shell.Banners.Find(tab.SaveBannerKey));
        });
    }

    [Fact]
    public async Task ClosingANoteWhoseFinalSaveFailsKeepsItsTabOpen()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "not yet stored");
            var failure = DatabaseFaults.FailSavesOf(harness, tab.NodeId!.Value);

            Assert.False(await harness.Tabs.CloseAsync(tab));
            await harness.SettleAsync();

            Assert.Contains(tab, harness.Tabs.Tabs);
            Assert.Equal("not yet stored", ((FakeEditorBuffer)tab.Buffer!).Text);
            Assert.Contains(harness.Shell.Toasts.Toasts, toast => toast.Kind == ToastKind.Error && toast.Message == "“Untitled 1” couldn't be saved, so its tab stays open.");
            Assert.False(harness.Tabs.CanReopenClosedTab);

            failure.Dispose();
            Assert.True(await harness.Tabs.CloseAsync(tab));
            await harness.SettleAsync();
            Assert.Empty(harness.Tabs.Tabs);
        });
    }

    [Fact]
    public async Task TrashingANoteWhoseLastEditsCannotBeSavedKeepsItsTabOpen()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            var id = tab.NodeId!.Value;
            ShellHarness.Type(tab, "last words");
            var failure = DatabaseFaults.FailSavesOf(harness, id);

            await harness.Core.Nodes.TrashAsync([id]);
            await harness.SettleAsync();

            Assert.Contains(tab, harness.Tabs.Tabs);
            Assert.Equal("last words", ((FakeEditorBuffer)tab.Buffer!).Text);
            Assert.Contains(
                harness.Shell.Toasts.Toasts,
                toast => toast.Kind == ToastKind.Error && toast.Message == "“Untitled 1” is in the Trash, but its last changes couldn't be saved, so its tab stays open.");

            // Saves to a note in the Trash are accepted: once storing works again, nothing was lost.
            failure.Dispose();
            await harness.Shell.StatusBar.RetrySaveCommand.ExecuteAsync(null);
            await harness.SettleAsync();
            Assert.Equal("last words", (await harness.Core.Documents.GetContentAsync(id)).Text);
            Assert.False(tab.IsDirty);
        });
    }

    [Fact]
    public async Task ANoteDeletedElsewhereOffersToKeepTheTextAsANewNote()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            var id = tab.NodeId!.Value;
            DatabaseFaults.DeleteBehindTheAppsBack(harness, id);

            ShellHarness.Type(tab, "orphaned text");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(TabSaveState.Failed, tab.SaveState);
            var banner = Assert.Single(harness.Shell.Banners.Banners);
            Assert.Equal("“Untitled 1” no longer exists, so your edits can't be saved there.", banner.Message);
            Assert.Equal("Save as a new note", banner.ActionText);

            await banner.ActionCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.DoesNotContain(tab, harness.Tabs.Tabs);
            var copy = Assert.Single(harness.Tabs.Tabs);
            Assert.NotEqual((NodeId?)id, copy.NodeId);
            Assert.Equal("orphaned text", await copy.LoadAsync());
            Assert.Empty(harness.Shell.Banners.Banners);
            Assert.DoesNotContain(harness.Shell.Toasts.Toasts, toast => toast.Kind == ToastKind.Error);
        });
    }
}
