using System.Text;

namespace NimPad.Core.Files;

/// <summary>
/// How an external file's bytes map to text: the encoding's web name (as <see cref="Encoding.WebName"/> reports it)
/// and whether the file starts with a byte order mark. Saving uses the same encoding and the same BOM choice.
/// </summary>
/// <param name="WebName">The encoding's web name, e.g. <c>utf-8</c>, <c>utf-16</c>, <c>Windows-1252</c> (compared case-insensitively).</param>
/// <param name="HasBom">Whether the file has (and keeps) a byte order mark.</param>
public sealed record TextEncodingInfo(string WebName, bool HasBom)
{
    private static readonly Lazy<bool> CodePagesRegistered = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    /// <summary>UTF-8 without a BOM (also what managed notes are exported as).</summary>
    public static TextEncodingInfo Utf8 { get; } = new("utf-8", false);

    /// <summary>UTF-8 with a BOM.</summary>
    public static TextEncodingInfo Utf8WithBom { get; } = new("utf-8", true);

    /// <summary>UTF-16 little endian with a BOM.</summary>
    public static TextEncodingInfo Utf16LittleEndian { get; } = new("utf-16", true);

    /// <summary>UTF-16 big endian with a BOM.</summary>
    public static TextEncodingInfo Utf16BigEndian { get; } = new("utf-16BE", true);

    /// <summary>UTF-16 little endian without a BOM (recognized from its zero bytes).</summary>
    public static TextEncodingInfo Utf16LittleEndianWithoutBom { get; } = new("utf-16", false);

    /// <summary>UTF-16 big endian without a BOM (recognized from its zero bytes).</summary>
    public static TextEncodingInfo Utf16BigEndianWithoutBom { get; } = new("utf-16BE", false);

    /// <summary>UTF-32 little endian with a BOM.</summary>
    public static TextEncodingInfo Utf32LittleEndian { get; } = new("utf-32", true);

    /// <summary>UTF-32 big endian with a BOM.</summary>
    public static TextEncodingInfo Utf32BigEndian { get; } = new("utf-32BE", true);

    /// <summary>Windows-1252 (Western European), the fallback for files that are not valid UTF-8.</summary>
    public static TextEncodingInfo Windows1252 { get; } = new("Windows-1252", false);

    /// <summary>The name for the status bar, e.g. <c>UTF-8</c>, <c>UTF-8 with BOM</c>, <c>UTF-16 LE</c>.</summary>
    public string DisplayName => WebName.ToLowerInvariant() switch
    {
        "utf-8" => HasBom ? "UTF-8 with BOM" : "UTF-8",
        "utf-16" => "UTF-16 LE",
        "utf-16be" => "UTF-16 BE",
        "utf-32" => "UTF-32 LE",
        "utf-32be" => "UTF-32 BE",
        "windows-1252" => "Windows-1252",
        _ => WebName,
    };

    /// <summary>Value equality; the web name compares case-insensitively.</summary>
    public bool Equals(TextEncodingInfo? other) =>
        other is not null && HasBom == other.HasBom && string.Equals(WebName, other.WebName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(WebName), HasBom);

    /// <summary>
    /// The encoding for writing: no preamble of its own (the BOM is written separately when <see cref="HasBom"/>),
    /// and for legacy code pages an exception for characters it cannot represent instead of a silent <c>?</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The web name is not a known encoding.</exception>
    public Encoding GetEncoding() => WebName.ToLowerInvariant() switch
    {
        "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        "utf-16" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        "utf-32" => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        "utf-32be" => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        _ => GetCodePage(WebName),
    };

    /// <summary>The byte order mark written at the start of the file (empty when <see cref="HasBom"/> is false).</summary>
    public byte[] GetPreamble() => HasBom ? WebName.ToLowerInvariant() switch
    {
        "utf-8" => [0xEF, 0xBB, 0xBF],
        "utf-16" => [0xFF, 0xFE],
        "utf-16be" => [0xFE, 0xFF],
        "utf-32" => [0xFF, 0xFE, 0x00, 0x00],
        "utf-32be" => [0x00, 0x00, 0xFE, 0xFF],
        _ => throw new InvalidOperationException($"{WebName} has no byte order mark."),
    } : [];

    /// <summary>Makes the legacy code pages (Windows-1252 and others) available to <see cref="Encoding.GetEncoding(string)"/>.</summary>
    internal static void EnsureCodePages() => _ = CodePagesRegistered.Value;

    private static Encoding GetCodePage(string webName)
    {
        EnsureCodePages();
        return Encoding.GetEncoding(webName, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }
}

/// <summary>The line break style of a text.</summary>
public enum LineEndingStyle
{
    /// <summary><c>\r\n</c> (Windows).</summary>
    Crlf,

    /// <summary><c>\n</c> (Unix, and every managed note).</summary>
    Lf,

    /// <summary><c>\r</c> (classic Mac OS).</summary>
    Cr,

    /// <summary>More than one style in the same text.</summary>
    Mixed,
}

/// <summary>The line breaks found in a text. External files keep theirs verbatim; this only describes them.</summary>
/// <param name="Style">The single style used, or <see cref="LineEndingStyle.Mixed"/>.</param>
/// <param name="Dominant">The most common style (never <see cref="LineEndingStyle.Mixed"/>), which the editor uses for new lines.</param>
/// <param name="CrlfCount">The number of <c>\r\n</c>.</param>
/// <param name="LfCount">The number of lone <c>\n</c>.</param>
/// <param name="CrCount">The number of lone <c>\r</c>.</param>
public sealed record LineEndingInfo(LineEndingStyle Style, LineEndingStyle Dominant, int CrlfCount, int LfCount, int CrCount)
{
    /// <summary>The line break string of <see cref="Dominant"/>.</summary>
    public string NewLine => Dominant switch
    {
        LineEndingStyle.Lf => "\n",
        LineEndingStyle.Cr => "\r",
        _ => "\r\n",
    };

    /// <summary>
    /// Counts the line breaks of <paramref name="text"/>. A text without line breaks reports <see cref="LineEndingStyle.Crlf"/>
    /// (the Windows default for lines the user adds). Ties for the dominant style prefer CRLF, then LF.
    /// </summary>
    public static LineEndingInfo Detect(ReadOnlySpan<char> text)
    {
        int crlf = 0, lf = 0, cr = 0;
        var offset = 0;
        while (true)
        {
            var index = text[offset..].IndexOfAny('\r', '\n');
            if (index < 0)
            {
                break;
            }

            var at = offset + index;
            if (text[at] == '\n')
            {
                lf++;
                offset = at + 1;
            }
            else if (at + 1 < text.Length && text[at + 1] == '\n')
            {
                crlf++;
                offset = at + 2;
            }
            else
            {
                cr++;
                offset = at + 1;
            }
        }

        var dominant = crlf >= lf && crlf >= cr ? LineEndingStyle.Crlf : lf >= cr ? LineEndingStyle.Lf : LineEndingStyle.Cr;
        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var style = kinds > 1 ? LineEndingStyle.Mixed : dominant;
        return new LineEndingInfo(style, dominant, crlf, lf, cr);
    }
}
