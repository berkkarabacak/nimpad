using System.Diagnostics;
using System.Text;
using FlaUI.Core.WindowsAPI;
using NimPad.E2E.Infrastructure;
using Xunit;

namespace NimPad.E2E;

/// <summary>
/// Ordinary Windows files opened from the command line (spec §3 "Local Files", §12 Phase 1 "External file opening"):
/// edited and saved in place with their own encoding and line endings, and never imported into the workspace.
/// </summary>
public sealed class ExternalFileTests
{
    /// <summary>
    /// A UTF-16 LE (BOM) file with CRLF line endings opens as an external tab, saves in place with Ctrl+S keeping its
    /// BOM, encoding and CRLF, and does not become a managed note.
    /// </summary>
    [Fact]
    public void ExternalFileOpenEditSave()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Ui.WindowsOnly);
        Scenario.Run(scenario =>
        {
            const string FileName = "notes.txt";
            const string Original = "First line\r\nSecond line";
            const string Added = "Added line Quokka";
            const string Expected = Original + "\r\n" + Added;
            var path = Path.Combine(scenario.CreateFolder("files"), FileName);
            File.WriteAllText(path, Original, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            var dataDirectory = scenario.CreateDataDirectory();

            var app = scenario.Launch(dataDirectory, path);
            app.WaitForTabTitles([FileName]);
            app.WaitForName(AutomationIds.StatusEncoding, "UTF-16 LE");
            app.WaitForName(AutomationIds.StatusSync, "Not synced");
            app.WaitForEditorTextExact(Original);

            // Append a line at the end. Enter reuses the document's own line ending (CRLF).
            app.FocusEditor();
            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
            app.TypeText("\n" + Added);
            app.WaitForEditorTextExact(Expected);
            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);

            Ui.WaitForValue(() => ReadAsUtf16Le(path), text => text == Expected, $"{FileName} on disk to hold the edit (read as UTF-16 LE)");
            app.WaitForTabStatus(FileName, ItemStatuses.Saved);
            var saved = File.ReadAllBytes(path);
            Assert.True(saved.Length >= 2 && saved[0] == 0xFF && saved[1] == 0xFE, "The file must keep its UTF-16 LE byte order mark.");

            // Exact text: the new line is in, and every line break is still CRLF.
            Assert.Equal(Expected, Encoding.Unicode.GetString(saved, 2, saved.Length - 2));
            app.Close();

            // Relaunch on the same profile. A note created now that also mentions "Quokka" proves search works; the
            // file's text must not be found anywhere else, because it was never imported.
            var again = scenario.Launch(dataDirectory);
            var before = again.ReadTabTitles();
            again.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_N);
            var after = again.WaitForTabTitles(titles => titles.Count == before.Count + 1, "Ctrl+N to add a tab");
            var controlNote = after.Except(before).Single();
            again.FocusEditor();
            again.TypeText("Quokka control note");
            var sinceEdit = Stopwatch.StartNew();
            again.WaitForEditorText("Quokka control note");
            again.WaitForAutosave(controlNote, sinceEdit);

            var explorer = new ExplorerPane(again);
            explorer.Search("Quokka");
            explorer.WaitForSearchResult(controlNote);
            Assert.Equal(controlNote, Ui.NameOf(Assert.Single(explorer.FindSearchResults())));

            explorer.ClearSearch();
            explorer.WaitForTreeRow(controlNote);
            Assert.Empty(explorer.FindRows(FileName));
        });
    }

    /// <summary>The file's text after a UTF-16 LE BOM; a marker when the BOM is missing; null while it cannot be read.</summary>
    private static string? ReadAsUtf16Le(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mid-save (the temp file is being swapped in with File.Replace): read again on the next poll.
            return null;
        }

        return bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
            ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
            : $"<no UTF-16 LE BOM: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16)))}>";
    }
}
