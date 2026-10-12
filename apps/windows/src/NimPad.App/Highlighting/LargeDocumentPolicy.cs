using ICSharpCode.AvalonEdit.Document;

namespace NimPad.App.Highlighting;

/// <summary>Why <see cref="LargeDocumentPolicy"/> turned highlighting off.</summary>
public enum LargeDocumentReason
{
    /// <summary>The document is small enough: highlighting stays on.</summary>
    None,

    /// <summary>The text is longer than <see cref="LargeDocumentPolicy.MaxTextLength"/>.</summary>
    TextTooLong,

    /// <summary>A line is longer than <see cref="LargeDocumentPolicy.MaxLineLength"/>.</summary>
    LineTooLong,
}

/// <summary>
/// Decides when syntax highlighting must be off so typing stays instant: AvalonEdit highlights line by line with
/// regular expressions, which gets slow on huge texts and on very long lines (minified JSON, logs). Pure and
/// allocation-free: the scans never allocate per line.
/// </summary>
public static class LargeDocumentPolicy
{
    /// <summary>Highlighting is off above this many characters (UTF-16 code units, about 10 MB of ASCII text).</summary>
    public const int MaxTextLength = 10 * 1024 * 1024;

    /// <summary>Highlighting is off when any line (without its line break) is longer than this many characters.</summary>
    public const int MaxLineLength = 20_000;

    /// <summary>The status-bar mode label while highlighting is off for size.</summary>
    public const string StatusLabel = "Plain Text (large file)";

    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>True when a text of this length is too long to highlight.</summary>
    public static bool IsTextTooLong(int textLength) => textLength > MaxTextLength;

    /// <summary>True when a line of this length (without its line break) is too long to highlight.</summary>
    public static bool IsLineTooLong(int lineLength) => lineLength > MaxLineLength;

    /// <summary>Checks a text: <see cref="LargeDocumentReason.None"/> when highlighting may stay on.</summary>
    public static LargeDocumentReason Check(ReadOnlySpan<char> text)
    {
        if (IsTextTooLong(text.Length))
        {
            return LargeDocumentReason.TextTooLong;
        }

        // A text that is not longer than the limit cannot contain a longer line.
        if (text.Length <= MaxLineLength)
        {
            return LargeDocumentReason.None;
        }

        while (true)
        {
            var lineBreak = text.IndexOfAny('\r', '\n');
            var lineLength = lineBreak < 0 ? text.Length : lineBreak;
            if (IsLineTooLong(lineLength))
            {
                return LargeDocumentReason.LineTooLong;
            }

            if (lineBreak < 0)
            {
                return LargeDocumentReason.None;
            }

            // "\r\n" leaves an empty line between \r and \n, which is harmless.
            text = text[(lineBreak + 1)..];
        }
    }

    /// <summary>Checks a text: <see cref="LargeDocumentReason.None"/> when highlighting may stay on.</summary>
    public static LargeDocumentReason Check(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Check(text.AsSpan());
    }

    /// <summary>
    /// Checks a text source, such as an editor's <see cref="TextDocument"/> (call it on the document's owner thread)
    /// or a snapshot of it (any thread). A <see cref="TextDocument"/> is checked through its line lengths, without
    /// reading the text.
    /// </summary>
    public static LargeDocumentReason Check(ITextSource text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var length = text.TextLength;
        if (IsTextTooLong(length))
        {
            return LargeDocumentReason.TextTooLong;
        }

        if (length <= MaxLineLength)
        {
            return LargeDocumentReason.None;
        }

        if (text is TextDocument document)
        {
            foreach (var line in document.Lines)
            {
                if (IsLineTooLong(line.Length))
                {
                    return LargeDocumentReason.LineTooLong;
                }
            }

            return LargeDocumentReason.None;
        }

        var lineStart = 0;
        while (true)
        {
            var lineBreak = text.IndexOfAny(LineBreaks, lineStart, length - lineStart);
            var lineEnd = lineBreak < 0 ? length : lineBreak;
            if (IsLineTooLong(lineEnd - lineStart))
            {
                return LargeDocumentReason.LineTooLong;
            }

            if (lineBreak < 0)
            {
                return LargeDocumentReason.None;
            }

            lineStart = lineBreak + 1;
        }
    }

    /// <summary>True when highlighting must be off for <paramref name="text"/> (see <see cref="Check(string)"/>).</summary>
    public static bool ShouldDisableHighlighting(string text) => Check(text) != LargeDocumentReason.None;

    /// <summary>True when highlighting must be off for <paramref name="text"/> (see <see cref="Check(ITextSource)"/>).</summary>
    public static bool ShouldDisableHighlighting(ITextSource text) => Check(text) != LargeDocumentReason.None;
}
