using System.Security.Cryptography;
using System.Text;
using NimPad.Core.Domain;
using NimPad.Core.Files;
using NimPad.Core.Services;
using NimPad.Core.Sync;
using NimPad.Core.Tests.Support;
using NimPad.Core.Text;
using Xunit;

namespace NimPad.Core.Tests.Files;

public sealed class ExternalFileServiceTests : IAsyncLifetime
{
    private TestStore _store = null!;
    private TempDirectory _files = null!;

    private ExternalFileService Files => _store.Files;

    public async ValueTask InitializeAsync()
    {
        _store = await TestStore.CreateAsync();
        _files = new TempDirectory();
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        _files.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------ encoding matrix

    // "héllo €" + CRLF + "ok", byte for byte, in each encoding. é = U+00E9, € = U+20AC.
    public static TheoryData<string, string, string, bool> Fixtures => new()
    {
        { "utf-8", "68C3A96C6C6F20E282AC0D0A6F6B", "utf-8", false },
        { "utf-8 BOM", "EFBBBF68C3A96C6C6F20E282AC0D0A6F6B", "utf-8", true },
        { "utf-16 LE", "FFFE6800E9006C006C006F002000AC200D000A006F006B00", "utf-16", true },
        { "utf-16 BE", "FEFF006800E9006C006C006F002020AC000D000A006F006B", "utf-16BE", true },
        { "utf-32 LE", "FFFE000068000000E90000006C0000006C0000006F00000020000000AC2000000D0000000A0000006F0000006B000000", "utf-32", true },
        { "utf-32 BE", "0000FEFF00000068000000E90000006C0000006C0000006F00000020000020AC0000000D0000000A0000006F0000006B", "utf-32BE", true },
        { "windows-1252", "68E96C6C6F20800D0A6F6B", "Windows-1252", false },
        { "utf-16 LE without BOM", "6800E9006C006C006F002000AC200D000A006F006B00", "utf-16", false },
        { "utf-16 BE without BOM", "006800E9006C006C006F002020AC000D000A006F006B", "utf-16BE", false },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task DetectsTheEncodingAndRoundTripsTheExactBytes(string label, string hex, string webName, bool hasBom)
    {
        var bytes = Convert.FromHexString(hex);
        var path = _files.Combine($"{label}.txt");
        await File.WriteAllBytesAsync(path, bytes);

        var file = await Files.OpenAsync(path);

        Assert.Equal("héllo €\r\nok", file.Text);
        Assert.Equal(new TextEncodingInfo(webName, hasBom), file.Encoding);
        Assert.Equal(LineEndingStyle.Crlf, file.LineEnding.Style);
        Assert.Equal(bytes.Length, file.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(path), file.LastWriteTimeUtc);
        Assert.Equal(Path.GetFullPath(path), file.Path);

        var copy = _files.Combine($"{label}.copy.txt");
        await Files.SaveAsync(copy, file.Text, file.Encoding);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copy));
    }

    [Theory]
    [InlineData("", "utf-8")]
    [InlineData("48656C6C6F2C20776F726C64", "utf-8")] // plain ASCII
    [InlineData("636166E9", "Windows-1252")] // "café" in 1252: a lone E9 is not UTF-8
    [InlineData("93736D61727494", "Windows-1252")] // “smart” quotes
    [InlineData("C3", "Windows-1252")] // truncated UTF-8 sequence
    [InlineData("EDA080", "Windows-1252")] // UTF-8 encoded surrogate: invalid
    [InlineData("F09F9880", "utf-8")] // 😀
    [InlineData("610062", "utf-8")] // odd length: not UTF-16
    [InlineData("6100620063000000", "utf-8")] // zero bytes on both sides: binary, not UTF-16
    public void WithoutABomStrictUtf8DecidesBetweenUtf8AndWindows1252(string hex, string expected)
    {
        var (encoding, bom) = ExternalFileService.DetectEncoding(Convert.FromHexString(hex));
        Assert.Equal(expected, encoding.WebName);
        Assert.False(encoding.HasBom);
        Assert.Equal(0, bom);
    }

    [Fact]
    public async Task Windows1252FilesDecodeEveryByte()
    {
        var path = _files.Combine("legacy.txt");
        await File.WriteAllBytesAsync(path, Convert.FromHexString("93636166E99420E280A2"));

        var file = await Files.OpenAsync(path);

        Assert.Equal("“café” â€¢", file.Text);
        Assert.Equal(TextEncodingInfo.Windows1252, file.Encoding);
    }

    [Fact]
    public async Task ABomOnlyFileIsEmptyText()
    {
        var path = _files.Combine("bom.txt");
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF]);

        var file = await Files.OpenAsync(path);

        Assert.Equal(string.Empty, file.Text);
        Assert.Equal(TextEncodingInfo.Utf8WithBom, file.Encoding);
    }

    // ------------------------------------------------------------ line endings

    [Theory]
    [InlineData("a\r\nb\r\nc", LineEndingStyle.Crlf, LineEndingStyle.Crlf, 2, 0, 0)]
    [InlineData("a\nb\n", LineEndingStyle.Lf, LineEndingStyle.Lf, 0, 2, 0)]
    [InlineData("a\rb\r", LineEndingStyle.Cr, LineEndingStyle.Cr, 0, 0, 2)]
    [InlineData("a\r\nb\nc", LineEndingStyle.Mixed, LineEndingStyle.Crlf, 1, 1, 0)]
    [InlineData("a\nb\nc\r\nd", LineEndingStyle.Mixed, LineEndingStyle.Lf, 1, 2, 0)]
    [InlineData("a\r\r\n", LineEndingStyle.Mixed, LineEndingStyle.Crlf, 1, 0, 1)]
    [InlineData("no breaks", LineEndingStyle.Crlf, LineEndingStyle.Crlf, 0, 0, 0)]
    [InlineData("", LineEndingStyle.Crlf, LineEndingStyle.Crlf, 0, 0, 0)]
    public void DetectsLineEndings(string text, LineEndingStyle style, LineEndingStyle dominant, int crlf, int lf, int cr)
    {
        Assert.Equal(new LineEndingInfo(style, dominant, crlf, lf, cr), LineEndingInfo.Detect(text));
    }

    [Fact]
    public async Task TextIsKeptVerbatimIncludingMixedLineEndings()
    {
        var path = _files.Combine("mixed.txt");
        var text = "one\r\ntwo\nthree\rfour";
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));

        var file = await Files.OpenAsync(path);
        Assert.Equal(text, file.Text);
        Assert.Equal(LineEndingStyle.Mixed, file.LineEnding.Style);
        Assert.Equal("\r\n", file.LineEnding.NewLine);

        await Files.SaveAsync(path, file.Text + "\r\nfive", file.Encoding);
        Assert.Equal(text + "\r\nfive", await File.ReadAllTextAsync(path));
    }

    // ------------------------------------------------------------ size, progress, cancellation

    [Fact]
    public async Task RefusesFilesOverTheLimit()
    {
        var path = _files.Combine("huge.log");
        await using (var stream = File.Create(path))
        {
            stream.SetLength(ExternalFileService.MaxFileSize + 1);
        }

        var error = await Assert.ThrowsAsync<ExternalFileTooLargeException>(() => Files.OpenAsync(path));

        Assert.Equal(ExternalFileService.MaxFileSize + 1, error.Length);
        Assert.Equal(ExternalFileService.MaxFileSize, error.Limit);
        Assert.Contains("256 MB", error.Message);
    }

    [Fact]
    public async Task ReportsProgressForLargeFilesOnly()
    {
        var large = _files.Combine("large.txt");
        var line = "0123456789 abcdefghij ünïcödé\r\n";
        await File.WriteAllTextAsync(large, string.Concat(Enumerable.Repeat(line, (int)(6 * 1024 * 1024 / line.Length))), new UTF8Encoding(false));
        var small = _files.Combine("small.txt");
        await File.WriteAllTextAsync(small, "tiny");
        var largeProgress = new RecordingProgress();
        var smallProgress = new RecordingProgress();

        var file = await Files.OpenAsync(large, largeProgress);
        await Files.OpenAsync(small, smallProgress);

        Assert.True(largeProgress.Values.Count > 4, $"{largeProgress.Values.Count} reports");
        Assert.Equal(largeProgress.Values.Order(), largeProgress.Values);
        Assert.Equal(1.0, largeProgress.Values[^1]);
        Assert.All(largeProgress.Values, value => Assert.InRange(value, 0.0, 1.0));
        Assert.Empty(smallProgress.Values);
        Assert.StartsWith(line, file.Text);
        Assert.Equal(LineEndingStyle.Crlf, file.LineEnding.Style);
    }

    [Fact]
    public async Task OpeningHonorsCancellation()
    {
        var path = _files.Combine("a.txt");
        await File.WriteAllTextAsync(path, "x");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Files.OpenAsync(path, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task OpeningAMissingFileFails()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Files.OpenAsync(_files.Combine("missing.txt")));
    }

    // ------------------------------------------------------------ atomic save

    [Fact]
    public async Task SaveReplacesTheFileAndLeavesNoTemporaryFiles()
    {
        var path = _files.Combine("notes.txt");
        await File.WriteAllTextAsync(path, "old");

        var written = await Files.SaveAsync(path, "new text\r\n", TextEncodingInfo.Utf8WithBom);

        Assert.Equal([0xEF, 0xBB, 0xBF, .. "new text\r\n"u8.ToArray()], await File.ReadAllBytesAsync(path));
        Assert.Equal(File.GetLastWriteTimeUtc(path), written);
        Assert.Equal([path], Directory.GetFiles(_files.Path));
    }

    [Fact]
    public async Task SaveCreatesAMissingFile()
    {
        var path = _files.Combine("fresh.md");

        await Files.SaveAsync(path, "# Fresh", TextEncodingInfo.Utf8);

        Assert.Equal("# Fresh", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_files.Path));
    }

    [Fact]
    public async Task UnrepresentableCharactersLeaveTheOriginalUntouched()
    {
        var path = _files.Combine("legacy.txt");
        var original = Convert.FromHexString("636166E9");
        await File.WriteAllBytesAsync(path, original);

        var error = await Assert.ThrowsAsync<UnrepresentableCharactersException>(
            () => Files.SaveAsync(path, "café € 😀", TextEncodingInfo.Windows1252));

        Assert.Equal(7, error.Index);
        Assert.Equal(TextEncodingInfo.Windows1252, error.Encoding);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(_files.Path));

        await Files.SaveAsync(path, "café €", TextEncodingInfo.Windows1252);
        Assert.Equal(Convert.FromHexString("636166E92080"), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FallsBackToMovingOverTheFileWhereReplaceIsUnsupported()
    {
        var path = _files.Combine("share.txt");
        await File.WriteAllTextAsync(path, "old");
        var attempts = 0;
        Files.ReplaceFile = (_, _, _) =>
        {
            attempts++;
            throw new PlatformNotSupportedException("no ReplaceFile on this share");
        };

        await Files.SaveAsync(path, "via move", TextEncodingInfo.Utf8);

        Assert.Equal(1, attempts);
        Assert.Equal("via move", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_files.Path));

        Files.ReplaceFile = (_, _, _) => throw new IOException("The process cannot access the file");
        await Files.SaveAsync(path, "via move again", TextEncodingInfo.Utf8);
        Assert.Equal("via move again", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_files.Path));
    }

    [Fact]
    public async Task AFailedSaveCleansUpItsTemporaryFile()
    {
        var path = _files.Combine("locked.txt");
        await File.WriteAllTextAsync(path, "original");
        Files.ReplaceFile = (_, _, _) => throw new FileNotFoundException("simulated");

        await Assert.ThrowsAsync<FileNotFoundException>(() => Files.SaveAsync(path, "lost", TextEncodingInfo.Utf8));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_files.Path));
    }

    [Fact]
    public async Task AFileWhoseNameIsNearTheLengthLimitCanBeSaved()
    {
        // 250 characters: any temporary name built from the target's name would exceed the 255 limit.
        var path = _files.Combine(new string('n', 246) + ".txt");
        await File.WriteAllTextAsync(path, "old");

        await Files.SaveAsync(path, "new", TextEncodingInfo.Utf8);

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_files.Path));
    }

    [Fact]
    public async Task AReplaceThatFailsHalfwayIsCompletedByTheMove()
    {
        var path = _files.Combine("antivirus.txt");
        await File.WriteAllTextAsync(path, "old");
        // Windows' ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: the original was renamed to the backup, the new file not moved in.
        Files.ReplaceFile = (_, target, backup) =>
        {
            File.Move(target, backup);
            throw new IOException("Unable to move the replacement file to the file to be replaced.");
        };

        await Files.SaveAsync(path, "new", TextEncodingInfo.Utf8);

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_files.Path));
    }

    [Fact]
    public async Task WhenTheFallbackFailsTooTheOriginalIsPutBack()
    {
        var path = _files.Combine("locked.txt");
        await File.WriteAllTextAsync(path, "old");
        Files.ReplaceFile = (_, target, backup) =>
        {
            File.Move(target, backup);
            throw new IOException("Unable to move the replacement file to the file to be replaced.");
        };
        Files.MoveFile = (source, target) =>
        {
            if (source.EndsWith(".tmp", StringComparison.Ordinal))
            {
                throw new IOException("The file is locked.");
            }

            File.Move(source, target);
        };

        await Assert.ThrowsAsync<IOException>(() => Files.SaveAsync(path, "new", TextEncodingInfo.Utf8));

        Assert.Equal("old", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_files.Path));
    }

    [Fact]
    public async Task WhenTheOriginalCannotBePutBackNeitherCopyIsDeleted()
    {
        var path = _files.Combine("stuck.txt");
        await File.WriteAllTextAsync(path, "old");
        Files.ReplaceFile = (_, target, backup) =>
        {
            File.Move(target, backup);
            throw new IOException("Unable to move the replacement file to the file to be replaced.");
        };
        Files.MoveFile = (_, _) => throw new IOException("The file is locked.");

        var error = await Assert.ThrowsAsync<IOException>(() => Files.SaveAsync(path, "new", TextEncodingInfo.Utf8));

        Assert.False(File.Exists(path));
        var kept = Directory.GetFiles(_files.Path).ToDictionary(file => File.ReadAllText(file), file => file);
        Assert.Equal(["new", "old"], kept.Keys.Order());
        Assert.Contains(kept["old"], error.Message, StringComparison.Ordinal);
        Assert.Contains(kept["new"], error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingIntoAMissingDirectoryFailsCleanly()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => Files.SaveAsync(_files.Combine("nope", "file.txt"), "x", TextEncodingInfo.Utf8));
        Assert.Empty(Directory.GetFileSystemEntries(_files.Path));
    }

    [Fact]
    public async Task SavingThroughASymbolicLinkUpdatesItsTarget()
    {
        var target = _files.Combine("real.txt");
        var link = _files.Combine("link.txt");
        await File.WriteAllTextAsync(target, "old");
        File.CreateSymbolicLink(link, target);

        await Files.SaveAsync(link, "updated", TextEncodingInfo.Utf8);

        Assert.Equal("updated", await File.ReadAllTextAsync(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(["link.txt", "real.txt"], Directory.GetFiles(_files.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task LargeTextsSaveInChunks()
    {
        var path = _files.Combine("big.txt");
        var text = string.Concat(Enumerable.Repeat("😀 chunk boundary test é\n", 200_000));

        await Files.SaveAsync(path, text, TextEncodingInfo.Utf16LittleEndian);

        Assert.Equal(text, (await Files.OpenAsync(path)).Text);
        Assert.Equal(2 + (2 * text.Length), new FileInfo(path).Length);
    }

    // ------------------------------------------------------------ recovery buffers

    [Fact]
    public async Task RecoveryBuffersRoundTripAndNeverEnterThePendingQueue()
    {
        var queued = await _store.Queue.CountAsync();
        var disk = new DateTime(2026, 10, 1, 8, 30, 15, 123, DateTimeKind.Utc);
        var path = _files.Combine("draft.txt");

        await Files.SaveRecoveryBufferAsync(path, "unsaved\r\nedits", TextEncodingInfo.Utf16BigEndian, LineEndingStyle.Crlf, disk);
        _store.Clock.Advance(TimeSpan.FromSeconds(1));
        await Files.SaveRecoveryBufferAsync(_files.Combine("other.txt"), "x", TextEncodingInfo.Windows1252, LineEndingStyle.Lf);

        var buffers = await Files.GetRecoveryBuffersAsync();
        Assert.Equal(2, buffers.Count);
        Assert.Equal(new RecoveryBuffer(Path.GetFullPath(path), "unsaved\r\nedits", TextEncodingInfo.Utf16BigEndian, LineEndingStyle.Crlf, disk, TestStore.Start), buffers[0]);
        Assert.Null(buffers[1].DiskLastWriteTimeUtc);
        Assert.Equal(LineEndingStyle.Lf, buffers[1].LineEnding);
        Assert.Equal(queued, await _store.Queue.CountAsync());

        // Saving again replaces the buffer of the same file.
        await Files.SaveRecoveryBufferAsync(path, "newer", TextEncodingInfo.Utf8, LineEndingStyle.Mixed);
        var replaced = (await Files.GetRecoveryBuffersAsync()).Single(buffer => buffer.Path == Path.GetFullPath(path));
        Assert.Equal("newer", replaced.Text);
        Assert.Equal(LineEndingStyle.Mixed, replaced.LineEnding);

        Assert.True(await Files.ClearRecoveryBufferAsync(path));
        Assert.False(await Files.ClearRecoveryBufferAsync(path));
        Assert.Single(await Files.GetRecoveryBuffersAsync());
    }

    [Fact]
    public async Task RecoveryBuffersSurviveAReopen()
    {
        using var temp = new TempDirectory();
        using (var first = await TestStore.CreateAsync(temp))
        {
            await first.Files.SaveRecoveryBufferAsync("/tmp/crash.txt", "typed before the crash", TextEncodingInfo.Utf8, LineEndingStyle.Lf);
        }

        using var second = await TestStore.CreateAsync(temp);
        Assert.Equal("typed before the crash", Assert.Single(await second.Files.GetRecoveryBuffersAsync()).Text);
    }

    // ------------------------------------------------------------ import and export

    [Fact]
    public async Task ImportCreatesANoteQueuedAsAnImport()
    {
        var target = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "Target");
        var folder = await _store.Nodes.CreateFolderAsync(_store.Workspace.Id, null, "Inbox");
        var path = _files.Combine("Meeting notes.md");
        await File.WriteAllTextAsync(path, $"# Meeting\r\nSee {LinkParser.CreateUri(target.Id)}\r\n", new UTF8Encoding(true));

        var note = await Files.ImportAsync(path, folder.Id);

        Assert.Equal("Meeting notes.md", note.Name);
        Assert.Equal(folder.Id, note.ParentId);
        Assert.Equal(DocumentFormat.Markdown, note.Format);
        var content = await _store.Documents.GetContentAsync(note.Id);
        Assert.Equal($"# Meeting\nSee {LinkParser.CreateUri(target.Id)}\n", content.Text);
        var create = Assert.Single(await _store.Queue.GetForNodeAsync(note.Id));
        Assert.Equal(PendingOperationKind.Create, create.Kind);
        Assert.Equal(LocalActorKind.Import, create.ActorKind);
        Assert.Equal(content.Checksum, create.Payload["checksum"]!.GetValue<string>());
        Assert.Equal([note.Id], (await _store.Documents.GetBacklinksAsync(target.Id)).Select(node => node.Id));
    }

    [Fact]
    public async Task ImportDetectsFormatsFromContentAndDecodesLegacyFiles()
    {
        var json = await Files.ImportAsync(await WriteAsync("settings", "{\r\n  \"theme\": \"dark\"\r\n}"));
        var legacyPath = _files.Combine("Notes 1252.txt");
        await File.WriteAllBytesAsync(legacyPath, Convert.FromHexString("636166E90D0A"));
        var legacy = await Files.ImportAsync(legacyPath);

        Assert.Equal(DocumentFormat.Json, json.Format);
        Assert.Null(json.ParentId);
        Assert.Equal(_store.Workspace.Id, json.WorkspaceId);
        Assert.Equal("café\n", (await _store.Documents.GetContentAsync(legacy.Id)).Text);
        Assert.Equal(DocumentFormat.Plain, legacy.Format);
    }

    [Fact]
    public async Task ImportReadsBomLessUtf16AndStoresOnlyStorableText()
    {
        // Without its BOM, "hi" + CRLF in UTF-16 LE is also valid UTF-8, full of U+0000.
        var utf16Path = _files.Combine("utf16.txt");
        await File.WriteAllBytesAsync(utf16Path, Convert.FromHexString("680069000D000A00"));
        // U+0000 is valid UTF-8, but PostgreSQL cannot store it (contracts: toStorableText).
        var nulPath = _files.Combine("nul.txt");
        await File.WriteAllBytesAsync(nulPath, Convert.FromHexString("610062"));

        var utf16 = await Files.ImportAsync(utf16Path);
        var nul = await Files.ImportAsync(nulPath);

        Assert.Equal("hi\n", (await _store.Documents.GetContentAsync(utf16.Id)).Text);
        var content = await _store.Documents.GetContentAsync(nul.Id);
        Assert.Equal("a\uFFFDb", content.Text);
        Assert.Equal(ContentCanonicalizer.Checksum("a\uFFFDb"), content.Checksum);
        Assert.Equal(content.Checksum, Assert.Single(await _store.Queue.GetForNodeAsync(nul.Id)).Payload["checksum"]!.GetValue<string>());
    }

    [Fact]
    public async Task ImportIntoAFolderInTheTrashFails()
    {
        var folder = await _store.Nodes.CreateFolderAsync(_store.Workspace.Id, null, "Old");
        await _store.Nodes.TrashAsync([folder.Id]);

        await Assert.ThrowsAsync<NodeNotFoundException>(async () => await Files.ImportAsync(await WriteAsync("a.txt", "x"), folder.Id));
    }

    [Fact]
    public async Task ExportWritesTheCanonicalTextAsUtf8WithoutABom()
    {
        var note = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "Export me.md", initialText: "Grüße\r\nline two\n");
        var path = _files.Combine("export.md");

        var written = await Files.ExportAsync(note.Id, path);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal("Grüße\nline two\n"u8.ToArray(), bytes);
        Assert.Equal((await _store.Documents.GetContentAsync(note.Id)).Checksum, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal(File.GetLastWriteTimeUtc(path), written);

        var reimported = await Files.ImportAsync(path);
        Assert.Equal(
            (await _store.Documents.GetContentAsync(note.Id)).Checksum,
            (await _store.Documents.GetContentAsync(reimported.Id)).Checksum);
    }

    private async Task<string> WriteAsync(string name, string text)
    {
        var path = _files.Combine(name);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        return path;
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        private readonly Lock _gate = new();

        public List<double> Values { get; } = [];

        public void Report(double value)
        {
            lock (_gate)
            {
                Values.Add(value);
            }
        }
    }
}
