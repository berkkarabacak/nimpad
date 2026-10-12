using System.Text;
using System.Text.Unicode;
using Microsoft.Data.Sqlite;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Storage;
using NimPad.Core.Sync;
using NimPad.Core.Text;
using NimPad.Core.Time;

namespace NimPad.Core.Files;

/// <summary>An external file as read from disk.</summary>
/// <param name="Path">The full path.</param>
/// <param name="Text">The text, verbatim (line endings are not normalized).</param>
/// <param name="Encoding">The detected encoding and BOM, which a save keeps.</param>
/// <param name="LineEnding">The line breaks found in the text.</param>
/// <param name="LastWriteTimeUtc">The file's last write time when it was opened.</param>
/// <param name="Length">The file's size in bytes.</param>
public sealed record ExternalFileContent(
    string Path,
    string Text,
    TextEncodingInfo Encoding,
    LineEndingInfo LineEnding,
    DateTime LastWriteTimeUtc,
    long Length);

/// <summary>Unsaved edits of an external file, kept so they survive a crash (never synced).</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="Text">The edited text, verbatim.</param>
/// <param name="Encoding">The encoding the file is saved with.</param>
/// <param name="LineEnding">The file's line-break style when the buffer was taken.</param>
/// <param name="DiskLastWriteTimeUtc">
/// The file's last write time the edits are based on (millisecond precision), or <see langword="null"/> when unknown.
/// </param>
/// <param name="UpdatedAt">When the buffer was stored.</param>
public sealed record RecoveryBuffer(
    string Path,
    string Text,
    TextEncodingInfo Encoding,
    LineEndingStyle LineEnding,
    DateTime? DiskLastWriteTimeUtc,
    DateTimeOffset UpdatedAt);

/// <summary>The file is larger than NimPad opens (<see cref="ExternalFileService.MaxFileSize"/>).</summary>
public sealed class ExternalFileTooLargeException : IOException
{
    /// <summary>Creates the exception.</summary>
    public ExternalFileTooLargeException(string path, long length, long limit)
        : base($"\"{System.IO.Path.GetFileName(path)}\" is {length / (1024 * 1024)} MB. NimPad opens files up to {limit / (1024 * 1024)} MB.")
    {
        FilePath = path;
        Length = length;
        Limit = limit;
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>The file's size in bytes.</summary>
    public long Length { get; }

    /// <summary>The largest size NimPad opens, in bytes.</summary>
    public long Limit { get; }
}

/// <summary>The text contains characters the file's encoding cannot represent; nothing was written.</summary>
public sealed class UnrepresentableCharactersException : IOException
{
    /// <summary>Creates the exception.</summary>
    public UnrepresentableCharactersException(string path, TextEncodingInfo encoding, int index, Exception innerException)
        : base($"Some characters can't be saved as {encoding.DisplayName}. Save the file as UTF-8 to keep them.", innerException)
    {
        FilePath = path;
        Encoding = encoding;
        Index = index;
    }

    /// <summary>The file that was being saved.</summary>
    public string FilePath { get; }

    /// <summary>The encoding that cannot represent the text.</summary>
    public TextEncodingInfo Encoding { get; }

    /// <summary>The offset in the text of the first character that cannot be encoded.</summary>
    public int Index { get; }
}

/// <summary>
/// Ordinary files opened from disk (spec §3, "Local Files"): reading with encoding detection, atomic saving in the
/// original encoding, crash-recovery buffers of unsaved edits, importing into the workspace and exporting notes.
/// External files are never uploaded: nothing here touches the pending-operation queue except an explicit import,
/// which creates a managed note.
/// </summary>
public sealed class ExternalFileService
{
    /// <summary>The largest file NimPad opens (256 MB).</summary>
    public const long MaxFileSize = 256L * 1024 * 1024;

    /// <summary>Files larger than this (5 MB) report read progress.</summary>
    public const long ProgressThreshold = 5L * 1024 * 1024;

    private const int ReadChunkBytes = 1024 * 1024;
    private const int Utf16SniffBytes = 64 * 1024;
    private const int DecodeChunkBytes = 4 * 1024 * 1024;
    private const int EncodeChunkChars = 1024 * 1024;

    private readonly LocalDatabase _database;
    private readonly NodeService _nodes;
    private readonly DocumentService _documents;
    private readonly WorkspaceChangeNotifier _notifier;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The local database (recovery buffers and imports).</param>
    /// <param name="nodes">Creates imported notes.</param>
    /// <param name="documents">Reads notes for export.</param>
    /// <param name="notifier">Receives the committed import.</param>
    /// <param name="timeProvider">The clock for timestamps; <see cref="TimeProvider.System"/> by default.</param>
    public ExternalFileService(
        LocalDatabase database,
        NodeService nodes,
        DocumentService documents,
        WorkspaceChangeNotifier notifier,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(notifier);
        _database = database;
        _nodes = nodes;
        _documents = documents;
        _notifier = notifier;
        _timeProvider = timeProvider ?? TimeProvider.System;
        TextEncodingInfo.EnsureCodePages();
    }

    /// <summary>
    /// Replaces the target with the finished temporary file, keeping the original under the backup name (temporary,
    /// target, backup). Tests substitute it to exercise the fallbacks;
    /// <see cref="File.Replace(string, string, string?, bool)"/> by default.
    /// </summary>
    internal Action<string, string, string> ReplaceFile { get; set; } =
        static (temporary, target, backup) => File.Replace(temporary, target, backup, ignoreMetadataErrors: true);

    /// <summary>Renames a file over another (source, target). Tests substitute it; a plain overwriting move by default.</summary>
    internal Action<string, string> MoveFile { get; set; } = static (source, target) => File.Move(source, target, overwrite: true);

    // ------------------------------------------------------------------ open

    /// <summary>
    /// Reads a file. The encoding is detected from a byte order mark (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE); without
    /// one, text whose every other byte is mostly zero is read as UTF-16 (and saved again without a BOM), valid UTF-8
    /// as UTF-8 and anything else as Windows-1252. The text is kept verbatim.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="progress">Receives 0–1 while a file larger than <see cref="ProgressThreshold"/> is read and decoded.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ExternalFileTooLargeException">The file is larger than <see cref="MaxFileSize"/>.</exception>
    public async Task<ExternalFileContent> OpenAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"\"{info.Name}\" no longer exists.", fullPath);
        }

        // Taken before reading: if the file changes while it is read, the watcher's newer time reveals it.
        var lastWrite = info.LastWriteTimeUtc;
        var length = info.Length;
        if (length > MaxFileSize)
        {
            throw new ExternalFileTooLargeException(fullPath, length, MaxFileSize);
        }

        var report = length > ProgressThreshold ? progress : null;
        var bytes = await ReadAllBytesAsync(fullPath, length, report, cancellationToken).ConfigureAwait(false);
        var (text, encoding) = await Task.Run(() => Decode(bytes, report, cancellationToken), cancellationToken).ConfigureAwait(false);
        report?.Report(1.0);
        return new ExternalFileContent(fullPath, text, encoding, LineEndingInfo.Detect(text), lastWrite, bytes.Length);
    }

    /// <summary>Detects the encoding of raw file bytes (see <see cref="OpenAsync"/>) and returns the BOM length.</summary>
    public static (TextEncodingInfo Encoding, int BomLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (TextEncodingInfo.Utf8WithBom, 3);
        }

        // UTF-32 LE before UTF-16 LE: its BOM starts with the UTF-16 LE BOM.
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return (TextEncodingInfo.Utf32LittleEndian, 4);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return (TextEncodingInfo.Utf32BigEndian, 4);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (TextEncodingInfo.Utf16LittleEndian, 2);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (TextEncodingInfo.Utf16BigEndian, 2);
        }

        if (DetectUtf16WithoutBom(bytes) is { } utf16)
        {
            return (utf16, 0);
        }

        return (Utf8.IsValid(bytes) ? TextEncodingInfo.Utf8 : TextEncodingInfo.Windows1252, 0);
    }

    // UTF-16 without a byte order mark, recognized the way Notepad does in practice: mostly Latin text, so one byte
    // of almost every code unit is zero, always on the same side. Valid UTF-8 text has no zero bytes, so it is never
    // mistaken for it; UTF-16 text with few zero bytes (most CJK text) is not recognized and falls through.
    private static TextEncodingInfo? DetectUtf16WithoutBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes.Length % 2 != 0)
        {
            return null;
        }

        var sample = bytes[..Math.Min(bytes.Length, Utf16SniffBytes)];
        int zeroLow = 0, zeroHigh = 0;
        for (var i = 0; i < sample.Length; i += 2)
        {
            zeroLow += sample[i] == 0 ? 1 : 0;
            zeroHigh += sample[i + 1] == 0 ? 1 : 0;
        }

        var units = sample.Length / 2;
        static bool Mostly(int count, int total) => count * 10 >= total * 3;
        static bool Rarely(int count, int total) => count * 20 <= total;
        if (Mostly(zeroHigh, units) && Rarely(zeroLow, units))
        {
            return TextEncodingInfo.Utf16LittleEndianWithoutBom;
        }

        return Mostly(zeroLow, units) && Rarely(zeroHigh, units) ? TextEncodingInfo.Utf16BigEndianWithoutBom : null;
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// Saves <paramref name="text"/> to <paramref name="path"/> atomically, in <paramref name="encoding"/> (with its
    /// BOM when it has one) and with the text's own line endings: the bytes go to a temporary file in the same
    /// directory, which is flushed to disk and then swapped in with <see cref="File.Replace(string, string, string?)"/>
    /// (or moved over the target where replacing is not supported, such as some network shares). A symbolic link is
    /// saved through to its target.
    /// </summary>
    /// <remarks>
    /// A failure leaves the original file in place and no temporary file behind. The replace keeps the original under
    /// a backup name until the new file is in place, so a replace that fails halfway (Windows can leave the original
    /// renamed) is completed by the fallback move or undone by putting the original back. Only if even that fails are
    /// both copies kept, and the <see cref="IOException"/> names them.
    /// </remarks>
    /// <returns>The file's new last write time, so the caller can recognize (and ignore) its own change notification.</returns>
    /// <exception cref="UnrepresentableCharactersException">The encoding cannot represent part of the text.</exception>
    public async Task<DateTime> SaveAsync(string path, string text, TextEncodingInfo encoding, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);
        var target = ResolveTarget(Path.GetFullPath(path));
        var directory = Path.GetDirectoryName(target) ?? throw new IOException($"\"{target}\" has no parent directory.");
        // Names of a fixed length, so a target whose name is near the 255-character limit can still be saved.
        var temporary = Path.Combine(directory, $".nimpad-{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(directory, $".nimpad-{Guid.NewGuid():N}.bak");
        try
        {
            await WriteFileAsync(temporary, target, text, encoding, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                try
                {
                    ReplaceFile(temporary, target, backup);
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException
                    || (ex is IOException && ex is not (FileNotFoundException or DirectoryNotFoundException)))
                {
                    // Some file systems (network shares, FAT, some sync clients) cannot replace in place. Moving over
                    // the target is still a single rename, just without keeping the original's attributes. It also
                    // completes a replace that failed after renaming the original to the backup name.
                    MoveFile(temporary, target);
                }
            }
            else
            {
                MoveFile(temporary, target);
            }
        }
        catch (Exception ex)
        {
            CleanUpFailedSave(temporary, target, backup, ex);
            throw;
        }

        // The new file is in place; the backup of the original is no longer needed.
        TryDelete(backup);
        return File.GetLastWriteTimeUtc(target);
    }

    /// <summary>
    /// Writes a managed note to a file (Ctrl+S on a note): UTF-8 without a BOM, with the note's canonical LF line
    /// endings kept as they are, saved atomically like <see cref="SaveAsync"/>.
    /// </summary>
    /// <remarks>
    /// LF is kept on purpose: the exported bytes are exactly the note's canonical text (same checksum), the export can
    /// be imported again unchanged, and every current Windows editor, Notepad included, reads LF files.
    /// </remarks>
    /// <returns>The file's new last write time.</returns>
    public async Task<DateTime> ExportAsync(NodeId nodeId, string path, CancellationToken cancellationToken = default)
    {
        var content = await _documents.GetContentAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return await SaveAsync(path, content.Text, TextEncodingInfo.Utf8, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ recovery buffers

    /// <summary>
    /// Stores (or replaces) the unsaved edits of an external file so they can be offered back after a crash. Kept in
    /// <c>external_file_buffers</c>, never in the pending-operation queue.
    /// </summary>
    public Task SaveRecoveryBufferAsync(
        string path,
        string text,
        TextEncodingInfo encoding,
        LineEndingStyle lineEnding,
        DateTime? diskLastWriteTimeUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);
        var fullPath = Path.GetFullPath(path);
        var lineEndingName = ToWireName(lineEnding);
        return _database.Writer.ExecuteAsync(
            context => context.Execute(
                """
                INSERT INTO external_file_buffers (path, text, encoding, has_bom, line_ending, disk_modified_at, updated_at)
                VALUES ($path, $text, $encoding, $bom, $eol, $disk, $now)
                ON CONFLICT (path) DO UPDATE SET text = excluded.text, encoding = excluded.encoding,
                    has_bom = excluded.has_bom, line_ending = excluded.line_ending,
                    disk_modified_at = excluded.disk_modified_at, updated_at = excluded.updated_at;
                """,
                ("$path", fullPath),
                ("$text", text),
                ("$encoding", encoding.WebName),
                ("$bom", encoding.HasBom),
                ("$eol", lineEndingName),
                ("$disk", diskLastWriteTimeUtc is { } disk ? new DateTimeOffset(DateTime.SpecifyKind(disk, DateTimeKind.Utc)) : null),
                ("$now", Timestamps.Now(_timeProvider))),
            cancellationToken);
    }

    /// <summary>Deletes the recovery buffer of a file (after it was saved or its edits were discarded).</summary>
    /// <returns><see langword="true"/> when there was one.</returns>
    public Task<bool> ClearRecoveryBufferAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return _database.Writer.ExecuteAsync(
            context => context.Execute("DELETE FROM external_file_buffers WHERE path = $path;", ("$path", fullPath)) > 0,
            cancellationToken);
    }

    /// <summary>Every stored recovery buffer, oldest first.</summary>
    public Task<IReadOnlyList<RecoveryBuffer>> GetRecoveryBuffersAsync(CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync<IReadOnlyList<RecoveryBuffer>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT path, text, encoding, has_bom, line_ending, disk_modified_at, updated_at FROM external_file_buffers ORDER BY updated_at, path;";
                using var reader = command.ExecuteReader();
                var buffers = new List<RecoveryBuffer>();
                while (reader.Read())
                {
                    buffers.Add(new RecoveryBuffer(
                        reader.GetString(0),
                        reader.GetString(1),
                        new TextEncodingInfo(reader.GetString(2), reader.GetInt64(3) != 0),
                        ParseLineEnding(reader.GetString(4)),
                        reader.GetTimestampOrNull(5)?.UtcDateTime,
                        reader.GetTimestamp(6)));
                }

                return buffers;
            },
            cancellationToken);

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Copies a file into the workspace as a new managed note, queued as an <c>import</c>: named after the file, with
    /// LF line endings, and the format detected from the name (or, without a known extension, the content).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="parentFolderId">The folder to import into, or <see langword="null"/> for the workspace root.</param>
    /// <param name="workspaceId">The workspace when importing at the root; the default (oldest) workspace by default.</param>
    /// <param name="progress">Read progress for large files (see <see cref="OpenAsync"/>).</param>
    /// <param name="cancellationToken">Cancels the read, or the write before it starts.</param>
    /// <returns>The new note.</returns>
    public async Task<NodeInfo> ImportAsync(
        string path,
        NodeId? parentFolderId = null,
        WorkspaceId? workspaceId = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = await OpenAsync(path, progress, cancellationToken).ConfigureAwait(false);
        return await ImportTextAsync(Path.GetFileName(file.Path), file.Text, parentFolderId, workspaceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports text that came from a file (for example an open file's edited, unsaved text) as a new managed note,
    /// exactly like <see cref="ImportAsync"/> does with the file's content: queued as an <c>import</c>, named after
    /// <paramref name="fileName"/>, with LF line endings, storable text and the detected format.
    /// </summary>
    /// <param name="fileName">The file's name (without directories), which becomes the note's name.</param>
    /// <param name="text">The text, with any line endings.</param>
    /// <param name="parentFolderId">The folder to import into, or <see langword="null"/> for the workspace root.</param>
    /// <param name="workspaceId">The workspace when importing at the root; the default (oldest) workspace by default.</param>
    /// <param name="cancellationToken">Cancels the write before it starts.</param>
    /// <returns>The new note.</returns>
    public async Task<NodeInfo> ImportTextAsync(
        string fileName,
        string text,
        NodeId? parentFolderId = null,
        WorkspaceId? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(text);
        var name = ImportName(Path.GetFileName(fileName));
        text = ContentCanonicalizer.ToStorableText(ContentCanonicalizer.NormalizeLineEndings(text));
        var format = DocumentFormats.Detect(name, text);
        return await _database.Writer.ExecuteAsync(
            context =>
            {
                var targetWorkspace = parentFolderId is { } parent
                    ? NodeRows.Get(context, parent)?.WorkspaceId ?? throw new NodeNotFoundException(parent, "The destination folder no longer exists.")
                    : workspaceId ?? DefaultWorkspace(context);
                var changes = new ChangeCollector();
                var node = _nodes.CreateNode(
                    context, targetWorkspace, parentFolderId, NodeKind.Document, name, InsertPosition.End, format, text, LocalActorKind.Import, changes);
                changes.PublishOnCommit(context, _notifier);
                return node;
            },
            cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<byte[]> ReadAllBytesAsync(string path, long expectedLength, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        // The file may have changed size since it was measured; read what is there, within the limit.
        var length = Math.Min(Math.Max(stream.Length, expectedLength), MaxFileSize);
        var buffer = new byte[length];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(ReadChunkBytes, buffer.Length - total)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            progress?.Report(0.5 * total / buffer.Length);
        }

        if (total < buffer.Length)
        {
            Array.Resize(ref buffer, total);
        }
        else if (stream.Length > MaxFileSize)
        {
            throw new ExternalFileTooLargeException(path, stream.Length, MaxFileSize);
        }

        return buffer;
    }

    private static (string Text, TextEncodingInfo Encoding) Decode(byte[] bytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (encoding, bomLength) = DetectEncoding(bytes);
        var decoder = DecodingFor(encoding);
        var payload = bytes.AsMemory(bomLength);
        var charCount = decoder.GetCharCount(payload.Span);
        var text = string.Create(charCount, (Payload: payload, Decoder: decoder.GetDecoder(), Progress: progress, Token: cancellationToken), static (chars, state) =>
        {
            var source = state.Payload.Span;
            var total = source.Length;
            var written = 0;
            while (true)
            {
                state.Token.ThrowIfCancellationRequested();
                var take = Math.Min(DecodeChunkBytes, source.Length);
                var last = take == source.Length;
                state.Decoder.Convert(source[..take], chars[written..], last, out var bytesUsed, out var charsUsed, out _);
                if (bytesUsed == 0 && charsUsed == 0 && !last)
                {
                    throw new InvalidOperationException("The decoder made no progress.");
                }

                written += charsUsed;
                source = source[bytesUsed..];
                state.Progress?.Report(0.5 + (0.5 * (total - source.Length) / Math.Max(total, 1)));
                if (last && source.IsEmpty)
                {
                    break;
                }
            }
        });
        return (text, encoding);
    }

    // Decoding never fails: invalid sequences in a BOM-declared Unicode file become U+FFFD.
    private static Encoding DecodingFor(TextEncodingInfo encoding) => encoding.WebName.ToLowerInvariant() switch
    {
        "utf-8" => new UTF8Encoding(false, throwOnInvalidBytes: false),
        "utf-16" => new UnicodeEncoding(false, false, throwOnInvalidBytes: false),
        "utf-16be" => new UnicodeEncoding(true, false, throwOnInvalidBytes: false),
        "utf-32" => new UTF32Encoding(false, false, throwOnInvalidCharacters: false),
        "utf-32be" => new UTF32Encoding(true, false, throwOnInvalidCharacters: false),
        _ => CodePage(encoding.WebName),
    };

    private static Encoding CodePage(string webName)
    {
        TextEncodingInfo.EnsureCodePages();
        return Encoding.GetEncoding(webName, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    private static async Task WriteFileAsync(string temporary, string target, string text, TextEncodingInfo info, CancellationToken cancellationToken)
    {
        var encoding = info.GetEncoding();
        var encoder = encoding.GetEncoder();
        var preamble = info.GetPreamble();
        await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
        if (preamble.Length > 0)
        {
            await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
        }

        var buffer = new byte[encoding.GetMaxByteCount(Math.Min(EncodeChunkChars, Math.Max(text.Length, 1)))];
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(EncodeChunkChars, text.Length - offset);
            var last = offset + take == text.Length;
            int charsUsed, bytesUsed;
            try
            {
                encoder.Convert(text.AsSpan(offset, take), buffer, last, out charsUsed, out bytesUsed, out _);
            }
            catch (EncoderFallbackException ex)
            {
                throw new UnrepresentableCharactersException(target, info, offset + Math.Max(ex.Index, 0), ex);
            }

            await stream.WriteAsync(buffer.AsMemory(0, bytesUsed), cancellationToken).ConfigureAwait(false);
            offset += charsUsed;
            if (last && offset == text.Length)
            {
                break;
            }
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        // Durable before it replaces the original: a crash right after the swap must not leave an empty file.
        stream.Flush(flushToDisk: true);
    }

    // After a failed save: makes sure the original is back at its path, then removes the temporary copies. When the
    // original cannot be put back, nothing is deleted: the backup is the original, and the temporary file the new text.
    private void CleanUpFailedSave(string temporary, string target, string backup, Exception error)
    {
        if (!File.Exists(target) && File.Exists(backup))
        {
            try
            {
                MoveFile(backup, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceError($"Could not put '{target}' back from '{backup}': {ex.Message}");
            }
        }

        if (!File.Exists(target) && File.Exists(backup))
        {
            throw new IOException(
                $"\"{Path.GetFileName(target)}\" could not be saved, and the previous version could not be put back. " +
                $"The previous version is in \"{backup}\" and your text in \"{temporary}\".",
                error);
        }

        TryDelete(temporary);
        TryDelete(backup);
    }

    private static string ResolveTarget(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
        }
        catch (IOException)
        {
            return fullPath;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not delete the temporary file '{path}': {ex.Message}");
        }
    }

    // A file name is a valid note name unless it is too long; then the stem is shortened and the extension kept.
    private static string ImportName(string fileName)
    {
        var name = fileName.Trim();
        if (name.Length > NodeNames.MaxLength)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length >= NodeNames.MaxLength / 2)
            {
                extension = string.Empty;
            }

            var stem = name[..(NodeNames.MaxLength - extension.Length)];
            if (char.IsHighSurrogate(stem[^1]))
            {
                stem = stem[..^1];
            }

            name = stem.TrimEnd() + extension;
        }

        return NodeNames.Normalize(name.Length == 0 ? "Imported file" : name);
    }

    private static WorkspaceId DefaultWorkspace(WriteContext context) =>
        context.Scalar("SELECT id FROM workspaces ORDER BY created_at, id LIMIT 1;") is string id
            ? WorkspaceId.Parse(id)
            : throw new InvalidOperationException("There is no workspace to import into.");

    private static string ToWireName(LineEndingStyle style) => style switch
    {
        LineEndingStyle.Crlf => "crlf",
        LineEndingStyle.Lf => "lf",
        LineEndingStyle.Cr => "cr",
        LineEndingStyle.Mixed => "mixed",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown line ending style."),
    };

    private static LineEndingStyle ParseLineEnding(string name) => name switch
    {
        "lf" => LineEndingStyle.Lf,
        "cr" => LineEndingStyle.Cr,
        "mixed" => LineEndingStyle.Mixed,
        _ => LineEndingStyle.Crlf,
    };
}
