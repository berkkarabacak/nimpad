using System.Text;

namespace NimPad.Core.Text;

/// <summary>The one-line preview shown under a note's title in the sidebar (stored in <c>document_contents.preview</c>).</summary>
public static class DocumentPreview
{
    /// <summary>The longest preview, in UTF-16 code units.</summary>
    public const int MaxLength = 120;

    /// <summary>
    /// The first non-blank line of <paramref name="text"/>, trimmed, with runs of whitespace collapsed to one space
    /// and cut to <see cref="MaxLength"/> characters (never splitting a surrogate pair). Empty when the text is blank.
    /// </summary>
    public static string Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(Math.Min(text.Length, MaxLength));
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (ch is '\n' or '\r')
            {
                if (builder.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (builder.Length + 1 >= MaxLength)
                {
                    break;
                }

                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length >= MaxLength || (char.IsHighSurrogate(ch) && builder.Length + 1 >= MaxLength))
            {
                break;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
