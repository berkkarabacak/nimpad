using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace NimPad.Core.Text;

/// <summary>
/// Canonical note text and the content checksum (architecture.md, "Canonical note text" and "Content checksum").
/// Managed notes use <c>\n</c> line endings; the checksum is <c>sha256:</c> + the lowercase hex SHA-256 of the
/// canonical text's UTF-8 bytes (no BOM).
/// </summary>
public static class ContentCanonicalizer
{
    /// <summary>The prefix of every content checksum.</summary>
    public const string ChecksumPrefix = "sha256:";

    // Lone surrogates encode as U+FFFD, matching Node's Buffer.from(text, 'utf8').
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // U+0000 and every surrogate code unit: the characters that can make a text unstorable.
    private static readonly SearchValues<char> UnstorableCandidates =
        SearchValues.Create("\0" + string.Concat(Enumerable.Range(0xD800, 0x800).Select(code => (char)code)));

    // Texts up to this many UTF-16 code units are encoded in one go; longer ones chunk by chunk.
    private const int ChunkChars = 64 * 1024;

    // Room for one chunk: at most 3 UTF-8 bytes per UTF-16 code unit, plus a carried-over surrogate.
    private static readonly int ChunkBufferBytes = Utf8NoBom.GetMaxByteCount(ChunkChars);

    /// <summary>
    /// Normalizes line endings: <c>\r\n</c> and lone <c>\r</c> become <c>\n</c>. Nothing else changes (no Unicode
    /// normalization, no BOM or whitespace stripping). Returns the same instance when it is already canonical.
    /// </summary>
    public static string NormalizeLineEndings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var firstCr = text.IndexOf('\r');
        if (firstCr < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, firstCr);
        for (var i = firstCr; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\r')
            {
                builder.Append(ch);
                continue;
            }

            builder.Append('\n');
            if (i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }
        }

        return builder.ToString();
    }

    /// <summary>True when <paramref name="text"/> is already canonical (contains no <c>\r</c>).</summary>
    public static bool IsCanonical(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return !text.Contains('\r');
    }

    /// <summary>
    /// True when PostgreSQL <c>text</c> (and so the server) stores <paramref name="text"/> exactly: it contains no
    /// U+0000 and no unpaired surrogate (contracts: <c>isStorableText</c>). Managed note text must be storable.
    /// </summary>
    public static bool IsStorable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IndexOfUnstorable(text, 0) < 0;
    }

    /// <summary>
    /// Replaces every U+0000 and every unpaired surrogate with U+FFFD and keeps everything else (contracts:
    /// <c>toStorableText</c>). Applied to every text before it is saved as a managed note, so pasted or imported
    /// text the server would reject (or store differently under the same checksum) never enters the store. Returns the
    /// same instance when the text is already storable.
    /// </summary>
    public static string ToStorableText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var first = IndexOfUnstorable(text, 0);
        if (first < 0)
        {
            return text;
        }

        var chars = text.ToCharArray();
        for (var i = first; i >= 0; i = IndexOfUnstorable(text, i + 1))
        {
            chars[i] = '�';
        }

        return new string(chars);
    }

    // The index of the next U+0000 or unpaired surrogate at or after start, or -1. The vectorized search skips
    // everything else, so a long text without emoji is scanned at memory speed.
    private static int IndexOfUnstorable(string text, int start)
    {
        var i = start;
        while (i < text.Length)
        {
            var next = text.AsSpan(i).IndexOfAny(UnstorableCandidates);
            if (next < 0)
            {
                return -1;
            }

            i += next;
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i += 2;
                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// The content checksum of <paramref name="text"/> after line-ending normalization:
    /// <c>sha256:</c> followed by 64 lowercase hex digits.
    /// </summary>
    public static string Checksum(string text) => ChecksumOfCanonical(NormalizeLineEndings(text));

    /// <summary>
    /// The content checksum of text that is already canonical (see <see cref="IsCanonical"/>), without scanning it for
    /// <c>\r</c> again. Large texts are encoded and hashed in chunks, so no buffer of the whole UTF-8 text is needed.
    /// </summary>
    internal static string ChecksumOfCanonical(ReadOnlySpan<char> canonical)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBufferBytes);
        try
        {
            if (canonical.Length <= ChunkChars)
            {
                var written = Utf8NoBom.GetBytes(canonical, buffer);
                SHA256.HashData(buffer.AsSpan(0, written), hash);
            }
            else
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                // The encoder carries a high surrogate that ends one chunk over to the next.
                var encoder = Utf8NoBom.GetEncoder();
                var remaining = canonical;
                bool last;
                do
                {
                    var take = Math.Min(remaining.Length, ChunkChars);
                    last = take == remaining.Length;
                    encoder.Convert(remaining[..take], buffer, last, out var charsUsed, out var bytesUsed, out _);
                    hasher.AppendData(buffer.AsSpan(0, bytesUsed));
                    remaining = remaining[charsUsed..];
                }
                while (!last || !remaining.IsEmpty);

                hasher.GetHashAndReset(hash);
            }

            return ChecksumPrefix + Convert.ToHexStringLower(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
