using System.Text;
using NimPad.Core.Domain;
using NimPad.Presentation.Editor;
using NimPad.Presentation.Explorer;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Tabs;

public sealed class ExternalFileTabTests
{
    private static string WriteFile(ShellHarness harness, string name, byte[] bytes)
    {
        var folder = harness.Directory.Combine("files");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Utf8WithBom(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    [Fact]
    public async Task OpeningTheSameFileTwiceUsesOneTabAndShowsItsEncoding()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = WriteFile(harness, "notes.txt", Utf8WithBom("line one\r\nline two\r\n"));

            var tab = harness.Tabs.OpenExternal(path);
            var again = harness.Tabs.OpenExternal(Path.Combine(Path.GetDirectoryName(path)!, ".", "notes.txt"));
            var buffer = await harness.ShowAsync(tab);

            Assert.Same(tab, again);
            Assert.Equal("notes.txt", tab.Title);
            Assert.Equal(path, tab.ToolTip);
            Assert.Equal(TabKind.External, tab.Kind);
            Assert.Equal("line one\r\nline two\r\n", buffer.Text);
            Assert.Equal("UTF-8 with BOM", harness.Shell.StatusBar.EncodingText);
            Assert.Equal(SyncState.NotSynced, harness.Shell.StatusBar.SyncState);
            Assert.Equal("Not synced", harness.Shell.StatusBar.SyncText);
            Assert.Empty(harness.Shell.Editor.Breadcrumbs);
            Assert.Equal(path, harness.Shell.Editor.FilePathText);
            Assert.False(harness.Shell.Editor.ShowEmptyHint);
        });
    }

    [Fact]
    public async Task EditsMakeTheTabDirtyUntilSavedInPlaceWithTheSameEncoding()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = WriteFile(harness, "todo.txt", Utf8WithBom("a\r\n"));
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            Assert.False(tab.IsDirty);

            ShellHarness.Type(tab, "b\r\n");

            Assert.True(tab.IsDirty);
            Assert.Equal(TabSaveState.Pending, tab.SaveState);
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(1999));
            Assert.Empty(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync());
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(1));
            var recovery = Assert.Single(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync());
            Assert.Equal("a\r\nb\r\n", recovery.Text);

            await harness.Shell.SaveCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.False(tab.IsDirty);
            Assert.Equal(TabSaveState.Saved, tab.SaveState);
            Assert.Equal(Utf8WithBom("a\r\nb\r\n"), File.ReadAllBytes(path));
            Assert.Equal(File.GetLastWriteTimeUtc(path), tab.LastSavedWriteTimeUtc);
            Assert.Empty(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync());
        });
    }

    [Fact]
    public async Task AChangeOnDiskOffersReloadButNimPadsOwnSaveDoesNot()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = WriteFile(harness, "log.txt", Encoding.UTF8.GetBytes("v1"));
            var tab = harness.Tabs.OpenExternal(path);
            var buffer = await harness.ShowAsync(tab);
            tab.ReplaceTextRequested += (_, e) =>
            {
                buffer.SetText(e.Text);
                tab.OnBufferChanged();
            };
            ShellHarness.Type(tab, " edited");
            Assert.True(await tab.SaveAsync());

            Assert.False(tab.OnFileChangedOnDisk(tab.LastSavedWriteTimeUtc));
            Assert.Empty(harness.Shell.Banners.Banners);

            File.WriteAllText(path, "v2 from another app");
            File.SetLastWriteTimeUtc(path, tab.LastSavedWriteTimeUtc!.Value.AddSeconds(5));
            Assert.True(tab.OnFileChangedOnDisk());

            var banner = Assert.Single(harness.Shell.Banners.Banners);
            Assert.Equal(BannerKind.Warning, banner.Kind);
            Assert.Equal("“log.txt” changed on disk.", banner.Message);
            Assert.Equal("Reload", banner.ActionText);
            Assert.True(banner.CanDismiss);

            await banner.ActionCommand.ExecuteAsync(null);

            Assert.Equal("v2 from another app", buffer.Text);
            Assert.False(tab.IsDirty);
            Assert.Empty(harness.Shell.Banners.Banners);
        });
    }

    [Fact]
    public async Task ClosingAFileWithUnsavedChangesAsksSaveDontSaveOrCancel()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = WriteFile(harness, "draft.txt", Encoding.UTF8.GetBytes("original"));
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, " + more");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(2));

            harness.Platform.Dialogs.Answers.Enqueue(DialogChoice.Cancel);
            Assert.False(await harness.Tabs.CloseAsync(tab));
            Assert.Contains(tab, harness.Tabs.Tabs);
            var question = Assert.Single(harness.Platform.Dialogs.Requests);
            Assert.Equal("Save changes to “draft.txt”?", question.Title);
            Assert.Equal("Save", question.PrimaryText);
            Assert.Equal("Don't save", question.SecondaryText);
            Assert.Equal("Cancel", question.CancelText);

            harness.Platform.Dialogs.Answers.Enqueue(DialogChoice.Secondary);
            Assert.True(await harness.Tabs.CloseAsync(tab));
            await harness.SettleAsync();
            Assert.DoesNotContain(tab, harness.Tabs.Tabs);
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Empty(await harness.Core.ExternalFiles.GetRecoveryBuffersAsync());

            var reopened = (await harness.Tabs.ReopenClosedAsync())!;
            await harness.ShowAsync(reopened);
            ShellHarness.Type(reopened, " saved");
            harness.Platform.Dialogs.Answers.Enqueue(DialogChoice.Primary);
            Assert.True(await harness.Tabs.CloseAsync(reopened));
            Assert.Equal("original saved", File.ReadAllText(path));
        });
    }

    [Fact]
    public async Task UnsavedFileEditsSurviveARestartAndAreOfferedForReview()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = WriteFile(harness, "server.conf", Encoding.UTF8.GetBytes("port=80\n"));
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "debug=true\n");

            await harness.RestartAsync();

            Assert.Equal("port=80\n", File.ReadAllText(path));
            var banner = harness.Shell.Banners.Find(ShellViewModel.RecoveredFilesBannerKey);
            Assert.NotNull(banner);
            Assert.Equal("NimPad recovered unsaved changes to 1 file", banner.Message);
            Assert.Equal("Review", banner.ActionText);

            await banner.ActionCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            var restored = Assert.Single(harness.Tabs.Tabs, t => t.IsExternal);
            Assert.Same(restored, harness.Tabs.SelectedTab);
            var buffer = await harness.ShowAsync(restored);
            Assert.Equal("port=80\ndebug=true\n", buffer.Text);
            Assert.True(restored.IsDirty);
            Assert.Null(harness.Shell.Banners.Find(ShellViewModel.RecoveredFilesBannerKey));
        });
    }

    [Fact]
    public async Task ImportCopiesTheFileIntoTheWorkspaceAndOpensTheNote()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = WriteFile(harness, "config.json", Encoding.UTF8.GetBytes("{\r\n  \"a\": 1\r\n}"));
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);

            await harness.Shell.ImportFileCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            var note = harness.Tabs.SelectedTab!;
            Assert.True(note.IsManaged);
            Assert.Equal("config.json", note.Title);
            Assert.Equal(DocumentFormat.Json, note.Format);
            Assert.Equal("{\n  \"a\": 1\n}", await note.LoadAsync());
            Assert.Contains(tab, harness.Tabs.Tabs);
            Assert.Contains(harness.Shell.Explorer.Rows.OfType<ExplorerNodeRowViewModel>(), row => row.Name == "config.json");
            Assert.Equal("Imported “config.json” into My Workspace.", harness.Shell.Toasts.Toasts[^1].Message);
        });
    }

    [Fact]
    public async Task SaveAsMovesTheTabToTheNewFile()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = WriteFile(harness, "a.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("wide")).ToArray());
            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, " text");
            var target = Path.Combine(Path.GetDirectoryName(path)!, "b.md");
            harness.Platform.FileDialogs.SavePaths.Enqueue(target);

            await harness.Shell.SaveAsCommand.ExecuteAsync(null);

            Assert.Equal(("a.txt", Path.GetDirectoryName(path)), harness.Platform.FileDialogs.SaveRequests.Single());
            Assert.Equal(target, tab.FilePath);
            Assert.Equal("b.md", tab.Title);
            Assert.Equal(DocumentFormat.Markdown, tab.Format);
            Assert.False(tab.IsDirty);
            Assert.Equal("UTF-16 LE", harness.Shell.StatusBar.EncodingText);
            Assert.Equal("wide text", Encoding.Unicode.GetString(File.ReadAllBytes(target).AsSpan(2)));
            Assert.Equal("wide", Encoding.Unicode.GetString(File.ReadAllBytes(path).AsSpan(2)));
        });
    }

    [Fact]
    public async Task ALargeFileShowsItsSizeAndReportsProgress()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var bytes = new byte[6 * 1024 * 1024];
            Array.Fill(bytes, (byte)'x');
            var path = WriteFile(harness, "server.log", bytes);
            var tab = harness.Tabs.OpenExternal(path, activate: false);
            var progress = new List<double>();
            var panelShown = false;
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TabViewModel.LoadProgress))
                {
                    progress.Add(tab.LoadProgress);
                    panelShown &= tab.IsLoadProgressVisible;
                }
                else if (e.PropertyName == nameof(TabViewModel.IsLoadProgressVisible) && tab.IsLoadProgressVisible)
                {
                    panelShown = true;
                }
            };

            var text = await tab.LoadAsync();
            await harness.SettleAsync();

            Assert.Equal(bytes.Length, text.Length);
            Assert.Equal("Opening “server.log”", tab.LoadingText);
            Assert.Equal("6 MB", tab.LoadingSizeText);

            // The panel showed while the values came in, including some on the way.
            Assert.True(panelShown);
            Assert.Contains(progress, value => value is > 0 and < 1);
            Assert.Equal(progress.Order(), progress);
            Assert.Equal(1.0, progress[^1]);
            Assert.False(tab.IsLoadProgressVisible);
            Assert.Equal(TabLoadState.Loaded, tab.LoadState);
        });
    }

    [Fact]
    public async Task AMissingFileFailsToLoadWithAPlainMessage()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.OpenExternal(harness.Directory.Combine("missing.txt"));
            await harness.SettleAsync();

            Assert.Equal(TabLoadState.Failed, tab.LoadState);
            Assert.Equal("“missing.txt” no longer exists.", tab.LoadError);
            await Assert.ThrowsAsync<FileNotFoundException>(tab.LoadAsync);
        });
    }

    [Fact]
    public async Task CtrlSOnANoteExportsItToAFile()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var tab = harness.Tabs.Tabs[0];
            await harness.ShowAsync(tab);
            ShellHarness.Type(tab, "export me\n");
            var target = harness.Directory.Combine("Untitled 1.txt");
            harness.Platform.FileDialogs.SavePaths.Enqueue(target);

            await harness.Shell.SaveCommand.ExecuteAsync(null);

            Assert.Equal("Untitled 1.txt", harness.Platform.FileDialogs.SaveRequests.Single().SuggestedName);
            Assert.Equal("export me\n"u8.ToArray(), File.ReadAllBytes(target));
            Assert.Equal("Exported “Untitled 1” to “Untitled 1.txt”", harness.Shell.Toasts.Toasts[^1].Message);
            Assert.True(tab.IsManaged);
        });
    }
}
