using NimPad.Presentation.Platform;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Shell;

public sealed class ShutdownTests
{
    [Fact]
    public async Task AFailingRecoveryBufferDoesNotKeepNotesFromBeingSaved()
    {
        await using var harness = await ShellHarness.StartAsync();
        var noteId = default(Core.Ids.NodeId);
        await harness.RunAsync(async () =>
        {
            var note = harness.Tabs.SelectedTab!;
            noteId = note.NodeId!.Value;
            await harness.ShowAsync(note);
            var file = harness.Tabs.OpenExternal(TestFiles.Write(harness, "x.txt", "x"));
            var broken = new FailingEditorBuffer((await file.LoadAsync()).Length);
            file.AttachBuffer(broken);
            broken.Touch();
            file.OnBufferChanged();
            harness.Tabs.SelectedTab = note;

            // Typed just now: the autosave is still waiting for its debounce.
            ShellHarness.Type(note, "important words");
            await harness.Shell.ShutdownAsync();
        });

        var stored = await harness.RunElsewhereAsync(core => core.Documents.GetContentAsync(noteId));
        Assert.Equal("important words", stored.Text);
    }

    [Fact]
    public async Task EmergencyFlushWritesUnsavedFilesToTheirRecoveryBuffers()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "draft.txt", "v1\n");
            var file = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(file);
            ShellHarness.Type(file, "unsaved\n");
            var note = harness.Tabs.Tabs[0];
            await harness.ShowAsync(note);
            ShellHarness.Type(note, "note text");

            Assert.True(harness.Shell.EmergencyFlush(TimeSpan.FromSeconds(10)));

            var recovery = Assert.Single(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync());
            Assert.Equal(path, recovery.Path);
            Assert.Equal("v1\nunsaved\n", recovery.Text);
            Assert.Equal("note text", (await harness.Core.Documents.GetContentAsync(note.NodeId!.Value)).Text);
            Assert.Equal("v1\n", File.ReadAllText(path));
        });
    }

    [Fact]
    public async Task EmergencyFlushStillSavesNotesWhenAFileCannotBeRead()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var note = harness.Tabs.Tabs[0];
            await harness.ShowAsync(note);
            var file = harness.Tabs.OpenExternal(TestFiles.Write(harness, "x.txt", "x"));
            var broken = new FailingEditorBuffer((await file.LoadAsync()).Length);
            file.AttachBuffer(broken);
            broken.Touch();
            file.OnBufferChanged();
            ShellHarness.Type(note, "saved anyway");

            Assert.False(harness.Shell.EmergencyFlush(TimeSpan.FromSeconds(10)));

            Assert.Equal("saved anyway", (await harness.Core.Documents.GetContentAsync(note.NodeId!.Value)).Text);
        });
    }

    [Fact]
    public async Task EditsAfterShutdownAreIgnoredInsteadOfThrowing()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var note = harness.Tabs.Tabs[0];
            await harness.ShowAsync(note);
            var file = harness.Tabs.OpenExternal(TestFiles.Write(harness, "x.txt", "x"));
            await harness.ShowAsync(file);
            ShellHarness.Type(file, "1");
            Assert.False(harness.Shell.IsShuttingDown);

            var shutdown = harness.Shell.ShutdownAsync();
            Assert.True(harness.Shell.IsShuttingDown);

            // Typed while the shutdown runs (the App makes the editors read-only; this is what happens if it did not).
            ShellHarness.Type(file, "2");
            await shutdown;
            ShellHarness.Type(note, "late");
            ShellHarness.Type(file, "3");

            // The recovery debounce of the edit typed during the shutdown fires after the database closed.
            await Task.Run(() => harness.Clock.Advance(TimeSpan.FromSeconds(3)));
            await harness.Ui.DrainAsync();

            Assert.Empty(harness.Ui.Errors);
            Assert.True(harness.Shell.IsShuttingDown);
        });
    }

    [Fact]
    public async Task ClosingTheWindowCanBeCancelledFromTheSavePrompt()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var a = harness.Tabs.OpenExternal(TestFiles.Write(harness, "a.txt", "a"));
            await harness.ShowAsync(a);
            ShellHarness.Type(a, "1");
            var b = harness.Tabs.OpenExternal(TestFiles.Write(harness, "b.txt", "b"));
            await harness.ShowAsync(b);
            ShellHarness.Type(b, "2");
            harness.Platform.Dialogs.Answers.Enqueue(DialogChoice.Cancel);

            Assert.False(await harness.Shell.ConfirmCloseAsync());

            var request = Assert.Single(harness.Platform.Dialogs.Requests);
            Assert.Equal("Save changes to “a.txt”?", request.Title);
            Assert.Equal("Save", request.PrimaryText);
            Assert.Equal("Don't save", request.SecondaryText);
            Assert.True(a.IsDirty);
            Assert.True(b.IsDirty);
            Assert.Same(a, harness.Tabs.SelectedTab);
            Assert.Equal("a", File.ReadAllText(a.FilePath!));
        });
    }

    [Fact]
    public async Task ClosingTheWindowSavesOrDiscardsEachUnsavedFile()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var a = harness.Tabs.OpenExternal(TestFiles.Write(harness, "a.txt", "a"));
            await harness.ShowAsync(a);
            ShellHarness.Type(a, "1");
            var b = harness.Tabs.OpenExternal(TestFiles.Write(harness, "b.txt", "b"));
            await harness.ShowAsync(b);
            ShellHarness.Type(b, "2");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(2.1));
            Assert.Equal(2, (await harness.Core.ExternalFiles.GetRecoveryBuffersAsync()).Count);
            harness.Platform.Dialogs.Answers.Enqueue(DialogChoice.Primary);
            harness.Platform.Dialogs.Answers.Enqueue(DialogChoice.Secondary);

            Assert.True(await harness.Shell.ConfirmCloseAsync());
            await harness.SettleAsync();

            Assert.Equal(["Save changes to “a.txt”?", "Save changes to “b.txt”?"], harness.Platform.Dialogs.Requests.Select(request => request.Title));
            Assert.Equal("a1", File.ReadAllText(a.FilePath!));
            Assert.Equal("b", File.ReadAllText(b.FilePath!));
            Assert.False(a.IsDirty);
            Assert.False(b.IsDirty);
            Assert.Empty(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync());

            // Nothing is left to ask about, and the shutdown writes no recovery buffer for the discarded text.
            Assert.True(await harness.Shell.ConfirmCloseAsync());
            Assert.Equal(2, harness.Platform.Dialogs.Requests.Count);
            await harness.Shell.ShutdownAsync();
        });

        Assert.Empty(await harness.RunElsewhereAsync(core => core.ExternalFiles.GetRecoveryBuffersAsync()));
    }

    [Fact]
    public async Task ClosingTheWindowStopsWhenASaveFails()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = TestFiles.Write(harness, "gone/a.txt".Replace('/', Path.DirectorySeparatorChar), "a");
            var a = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(a);
            ShellHarness.Type(a, "1");
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            harness.Platform.Dialogs.Answers.Enqueue(DialogChoice.Primary);

            Assert.False(await harness.Shell.ConfirmCloseAsync());

            Assert.True(a.IsDirty);
            Assert.NotNull(harness.Shell.Banners.Find(a.SaveBannerKey));
        });
    }
}
