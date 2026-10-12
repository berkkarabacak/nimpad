using System.Globalization;
using System.Text;
using NimPad.Presentation.Editor;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Editor;

public sealed class StatusBarAndEditorTests
{
    [Fact]
    public async Task CaretUpdatesFormatThePositionSelectionAndCharacterCount()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var status = harness.Shell.StatusBar;
            await harness.ShowAsync(harness.Tabs.Tabs[0]);
            var changes = new List<string?>();
            status.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

            status.UpdateCaret(13, 31, 0, 312);
            Assert.Equal("Ln 13, Col 31", status.PositionText);
            Assert.Equal("312 characters", status.CharacterCountText);

            status.UpdateCaret(2, 24, 13, 312);
            Assert.Equal("Ln 2, Col 24 · 13 selected", status.PositionText);

            status.UpdateCaret(1, 1, 0, 1);
            Assert.Equal("1 character", status.CharacterCountText);
            status.UpdateCaret(1, 1, 0, 12345);
            Assert.Equal("12,345 characters", status.CharacterCountText);

            // Unchanged values raise nothing.
            changes.Clear();
            status.UpdateCaret(1, 1, 0, 12345);
            Assert.Empty(changes);

            Assert.Equal("UTF-8", status.EncodingText);
            Assert.Equal("Plain Text", status.ModeText);
            Assert.Equal("100%", status.ZoomText);
            Assert.Equal(SyncState.LocalOnly, status.SyncState);
            Assert.Equal("Local only", status.SyncText);
        });
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, "UTF-8 with BOM")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }, "UTF-16 LE")]
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, "Windows-1252")]
    [InlineData(new byte[] { 0x61, 0x62 }, "UTF-8")]
    public async Task ExternalFilesShowTheirDetectedEncoding(byte[] bytes, string expected)
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = harness.Directory.Combine("file.txt");
            await File.WriteAllBytesAsync(path, bytes);

            var tab = harness.Tabs.OpenExternal(path);
            await harness.ShowAsync(tab);

            Assert.Equal(expected, harness.Shell.StatusBar.EncodingText);
            Assert.Equal("Not synced", harness.Shell.StatusBar.SyncText);
        });
    }

    [Fact]
    public async Task AnExternalFileKeepsItsModeChangeForTheTabOnly()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var path = harness.Directory.Combine("data.txt");
            await File.WriteAllTextAsync(path, "{}", Encoding.UTF8);
            var tab = harness.Tabs.OpenExternal(path);

            await harness.Shell.StatusBar.ChangeModeCommand.ExecuteAsync("json");

            Assert.Equal(Core.Domain.DocumentFormat.Json, tab.Format);
            Assert.Equal("JSON", harness.Shell.StatusBar.ModeText);
        });
    }

    [Fact]
    public async Task ZoomStepsByTenBetween50And400AndScalesTheFont()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var editor = harness.Shell.Editor;
            Assert.Equal(14, editor.FontSize);
            Assert.False(editor.ResetZoomCommand.CanExecute(null));

            harness.Shell.ZoomInCommand.Execute(null);
            Assert.Equal(110, editor.Zoom);
            Assert.Equal(15.4, editor.FontSize, 3);
            Assert.Equal("110%", harness.Shell.StatusBar.ZoomText);

            editor.SetZoom(1000);
            Assert.Equal(400, editor.Zoom);
            Assert.False(editor.ZoomInCommand.CanExecute(null));
            editor.SetZoom(3);
            Assert.Equal(50, editor.Zoom);
            Assert.False(editor.ZoomOutCommand.CanExecute(null));
            editor.SetZoom(77);
            Assert.Equal(80, editor.Zoom);

            harness.Shell.StatusBar.ResetZoomCommand.Execute(null);
            Assert.Equal(100, editor.Zoom);
            harness.Shell.ZoomOutCommand.Execute(null);
            await harness.SettleAsync();
            await harness.SettingsStore().WhenSavedAsync();
            Assert.Equal(90, (await harness.Core.Settings.LoadAsync()).Zoom);
        });
    }

    [Fact]
    public async Task TheEmptyNoteHintNeverClaimsSyncing()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            await harness.ShowAsync(harness.Tabs.Tabs[0]);

            Assert.True(harness.Shell.Editor.ShowEmptyHint);
            Assert.Equal("Start typing. It saves on its own.", EditorViewModel.EmptyHint);
            Assert.DoesNotContain("sync", EditorViewModel.EmptyHint, StringComparison.OrdinalIgnoreCase);

            ShellHarness.Type(harness.Tabs.Tabs[0], "x");
            Assert.False(harness.Shell.Editor.ShowEmptyHint);
        });
    }

    [Theory]
    [InlineData("2026-10-12T09:05:00Z", "9:05 AM")]
    [InlineData("2026-10-10T23:00:00Z", "Sat")]
    [InlineData("2026-10-06T08:00:00Z", "Tue")]
    [InlineData("2026-10-05T08:00:00Z", "Oct 5")]
    [InlineData("2026-01-03T08:00:00Z", "Jan 3")]
    [InlineData("2025-12-31T08:00:00Z", "Dec 31, 2025")]
    public void TimestampsAreRelativeToTheLocalDay(string when, string expected)
    {
        var now = DateTimeOffset.Parse("2026-10-12T13:59:43Z", CultureInfo.InvariantCulture);

        var text = DisplayText.RelativeTimestamp(DateTimeOffset.Parse(when, CultureInfo.InvariantCulture), now, TimeZoneInfo.Utc, ShellHarness.Culture);

        Assert.Equal(expected, text.Replace(" ", " ", StringComparison.Ordinal));
    }

    [Fact]
    public void TimestampsUseTheUsersTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+10", TimeSpan.FromHours(10), "UTC+10", "UTC+10");
        var now = DateTimeOffset.Parse("2026-10-12T13:59:43Z", CultureInfo.InvariantCulture);

        // Now is 23:59 on Oct 12 there; 14:30 UTC on Oct 11 is 00:30 on Oct 12 (today), 13:00 UTC is 23:00 on Oct 11.
        var yesterdayThere = DisplayText.RelativeTimestamp(DateTimeOffset.Parse("2026-10-11T13:00:00Z", CultureInfo.InvariantCulture), now, zone, ShellHarness.Culture);
        var todayThere = DisplayText.RelativeTimestamp(DateTimeOffset.Parse("2026-10-11T14:30:00Z", CultureInfo.InvariantCulture), now, zone, ShellHarness.Culture);

        Assert.Equal("Sun", yesterdayThere);
        Assert.Equal("12:30 AM", todayThere.Replace(" ", " ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Meeting notes", Core.Domain.DocumentFormat.Plain, "Meeting notes.txt")]
    [InlineData("config.json", Core.Domain.DocumentFormat.Json, "config.json")]
    [InlineData("plan", Core.Domain.DocumentFormat.Markdown, "plan.md")]
    [InlineData("a/b:c?", Core.Domain.DocumentFormat.Plain, "a_b_c_.txt")]
    public void ExportFileNamesGetAnExtensionAndLoseForbiddenCharacters(string name, Core.Domain.DocumentFormat format, string expected)
    {
        Assert.Equal(expected, FormatInfo.SuggestFileName(name, format));
    }
}
