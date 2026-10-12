using NimPad.Core.Domain;
using NimPad.Presentation.Editor;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Tabs;

public sealed class AutosaveIntegrationTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task TypingSavesThroughTheAutosaveSchedulerWithoutFlashingTheDot()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            var buffer = await harness.ShowAsync(tab);
            Assert.Equal(TabSaveState.Saved, tab.SaveState);

            ShellHarness.Type(tab, "Hello");
            Assert.True(tab.IsDirty);
            Assert.True(tab.HasBeenEdited);
            Assert.False(harness.Shell.Editor.ShowEmptyHint);
            Assert.Equal(TabSaveState.Saved, tab.SaveState);

            await harness.AdvanceAsync(399 * Ms);
            Assert.Equal(string.Empty, (await harness.Core.Documents.GetContentAsync(tab.NodeId!.Value)).Text);

            await harness.AdvanceAsync(Ms);

            Assert.Equal("Hello", (await harness.Core.Documents.GetContentAsync(tab.NodeId.Value)).Text);
            Assert.False(tab.IsDirty);
            Assert.Equal(TabSaveState.Saved, tab.SaveState);
            Assert.False(tab.ShowsPendingDot);

            // The dot only shows when a save stays pending for 600 ms.
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(TabSaveState.Saved, tab.SaveState);
            Assert.Equal(5, buffer.TextLength);
        });
    }

    [Fact]
    public async Task ContinuousTypingShowsThePendingDotUntilTheSaveLands()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            for (var i = 0; i < 7; i++)
            {
                ShellHarness.Type(tab, "a");
                await harness.AdvanceAsync(100 * Ms);
            }

            // 700 ms of unsaved typing: the max-wait save (1000 ms) has not happened yet.
            Assert.Equal(TabSaveState.Pending, tab.SaveState);
            Assert.True(tab.ShowsPendingDot);

            await harness.AdvanceAsync(300 * Ms);

            Assert.Equal("aaaaaaa", (await harness.Core.Documents.GetContentAsync(tab.NodeId!.Value)).Text);
            Assert.Equal(TabSaveState.Saved, tab.SaveState);
            Assert.False(tab.IsDirty);
        });
    }

    [Fact]
    public async Task AConflictingSaveStopsAndOffersToKeepTheTextAsANewNote()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            var id = tab.NodeId!.Value;

            // Another writer changes the note after the editor loaded it.
            await harness.Core.Documents.SaveContentAsync(id, "changed elsewhere");
            ShellHarness.Type(tab, "mine");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(TabSaveState.Failed, tab.SaveState);
            Assert.Equal(SyncState.SaveFailed, harness.Shell.StatusBar.SyncState);
            Assert.Equal("Not saved · Retry", harness.Shell.StatusBar.SyncText);
            var banner = Assert.Single(harness.Shell.Banners.Banners);
            Assert.Equal(BannerKind.Error, banner.Kind);
            Assert.Equal("“Untitled 1” was changed somewhere else, so your edits were not saved over it.", banner.Message);
            Assert.Equal("Save as a new note", banner.ActionText);
            Assert.Equal("changed elsewhere", (await harness.Core.Documents.GetContentAsync(id)).Text);

            var replaced = new List<string>();
            tab.ReplaceTextRequested += (_, e) =>
            {
                ((FakeEditorBuffer)tab.Buffer!).SetText(e.Text);
                tab.OnBufferChanged();
                replaced.Add(e.Text);
            };
            await banner.ActionCommand.ExecuteAsync(null);
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(["changed elsewhere"], replaced);
            Assert.Empty(harness.Shell.Banners.Banners);
            Assert.Equal(TabSaveState.Saved, tab.SaveState);
            Assert.Equal("changed elsewhere", (await harness.Core.Documents.GetContentAsync(id)).Text);
            var copy = harness.Tabs.SelectedTab!;
            Assert.NotEqual(id, copy.NodeId);
            Assert.Equal("Untitled 1 (copy)", copy.Title);
            Assert.Equal("mine", await copy.LoadAsync());
        });
    }

    [Fact]
    public async Task AReplacementDocumentContinuesTheVersionSequence()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "typed in the first document");

            // The App rebuilt the tab's document (for example after releasing it): it starts at version 0 again.
            var replacement = new FakeEditorBuffer(await tab.LoadAsync());
            tab.AttachBuffer(replacement);
            Assert.True(tab.IsDirty);
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("typed in the first document", (await harness.Core.Documents.GetContentAsync(tab.NodeId!.Value)).Text);
            Assert.False(tab.IsDirty);

            replacement.Append(" and the second");
            tab.OnBufferChanged();
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("typed in the first document and the second", (await harness.Core.Documents.GetContentAsync(tab.NodeId.Value)).Text);
        });
    }

    [Fact]
    public async Task ShutdownFlushesPendingEditsAndRestartShowsThem()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "not yet autosaved");

            await harness.RestartAsync();

            var restored = Assert.Single(harness.Tabs.Tabs);
            Assert.Equal(tab.NodeId, restored.NodeId);
            Assert.Equal("not yet autosaved", await restored.LoadAsync());
        });
    }

    [Fact]
    public async Task EmergencyFlushSavesSynchronously()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "crash-safe");

            Assert.True(harness.Shell.EmergencyFlush(TimeSpan.FromSeconds(10)));

            Assert.Equal("crash-safe", (await harness.Core.Documents.GetContentAsync(tab.NodeId!.Value)).Text);
            Assert.Equal([tab.TabId], (await harness.Core.Sessions.LoadTabsAsync()).Select(saved => saved.TabId));
        });
    }

    [Fact]
    public async Task ChangingTheModeOfANoteStoresIt()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            Assert.Equal("Plain Text", harness.Shell.StatusBar.ModeText);

            await harness.Shell.StatusBar.ChangeModeCommand.ExecuteAsync(DocumentFormat.Json);
            await harness.SettleAsync();

            Assert.Equal(DocumentFormat.Json, tab.Format);
            Assert.Equal("JSON", harness.Shell.StatusBar.ModeText);
            Assert.Equal("JSON", harness.Shell.Editor.FormatDisplayName);
            Assert.Equal(DocumentFormat.Json, (await harness.Core.Documents.GetContentAsync(tab.NodeId!.Value)).Format);

            await harness.RestartAsync();
            Assert.Equal(DocumentFormat.Json, harness.Tabs.Tabs[0].Format);
        });
    }
}
