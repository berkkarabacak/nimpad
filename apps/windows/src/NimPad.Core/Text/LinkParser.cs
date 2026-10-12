using System.Text;
using NimPad.Core.Domain;
using NimPad.Core.Ids;

namespace NimPad.Core.Text;

/// <summary>One <c>nimpad://note/{id}</c> reference found in a text.</summary>
/// <param name="Start">The offset of the first character (the <c>n</c> of <c>nimpad</c>).</param>
/// <param name="Length">The length of the whole URI.</param>
/// <param name="Target">The referenced note.</param>
public readonly record struct NoteLink(int Start, int Length, NodeId Target);

/// <summary>
/// Internal links between notes (architecture.md, "Internal links"): <c>nimpad://note/{uuid}</c>. Links name the
/// target by its stable ID, never by path, so moving or renaming a note never breaks them.
/// </summary>
/// <remarks>
/// A link is recognized when the scheme and host (<c>nimpad://note/</c>, any letter case) are directly followed by a
/// hyphenated 8-4-4-4-12 UUID (hex digits in any letter case; no braces, signs or <c>0x</c>) and the URI stands on its
/// own: the character before it is not a letter or digit, and the character after the UUID is not a letter, digit,
/// hyphen or underscore. The shared contract fixture <c>note-links.json</c> pins these rules. So <c>[Plan](nimpad://note/…)</c>,
/// <c>&lt;nimpad://note/…&gt;</c> and a bare URI at the end of a sentence are all found.
/// </remarks>
public static class LinkParser
{
    /// <summary>The prefix of every note link.</summary>
    public const string NotePrefix = "nimpad://note/";

    /// <summary>The link URI of a note: <c>nimpad://note/{id}</c> with the lowercase ID.</summary>
    public static string CreateUri(NodeId id) => NotePrefix + id.ToString();

    /// <summary>
    /// The text to paste for a link to a note (the explorer's "Copy link"): a Markdown link
    /// <c>[title](nimpad://note/{id})</c> when the destination is Markdown, otherwise the bare URI.
    /// </summary>
    /// <param name="title">The link text for Markdown, usually the note's name.</param>
    /// <param name="id">The linked note.</param>
    /// <param name="destinationFormat">The format of the note the link will be pasted into.</param>
    public static string CreateLinkText(string title, NodeId id, DocumentFormat destinationFormat = DocumentFormat.Plain)
    {
        ArgumentNullException.ThrowIfNull(title);
        var uri = CreateUri(id);
        if (destinationFormat != DocumentFormat.Markdown)
        {
            return uri;
        }

        var builder = new StringBuilder(title.Length + uri.Length + 4).Append('[');
        foreach (var ch in title)
        {
            if (ch is '\\' or '[' or ']')
            {
                builder.Append('\\');
            }

            builder.Append(ch is '\r' or '\n' ? ' ' : ch);
        }

        return builder.Append("](").Append(uri).Append(')').ToString();
    }

    /// <inheritdoc cref="CreateLinkText(string, NodeId, DocumentFormat)"/>
    /// <param name="node">The linked note; its name is the Markdown link text.</param>
    /// <param name="destinationFormat">The format of the note the link will be pasted into.</param>
    public static string CreateLinkText(NodeInfo node, DocumentFormat destinationFormat = DocumentFormat.Plain)
    {
        ArgumentNullException.ThrowIfNull(node);
        return CreateLinkText(node.Name, node.Id, destinationFormat);
    }

    /// <summary>Parses a whole string (surrounding white space allowed) that is exactly one note link URI.</summary>
    public static bool TryParseUri(string? text, out NodeId id)
    {
        id = default;
        if (text is null)
        {
            return false;
        }

        var trimmed = text.AsSpan().Trim();
        return trimmed.Length == NotePrefix.Length + Uuid.HyphenatedLength
            && StartsWithPrefix(trimmed)
            && TryParseUuid(trimmed[NotePrefix.Length..], out id);
    }

    /// <summary>Every note link in <paramref name="text"/>, in order of appearance.</summary>
    public static IReadOnlyList<NoteLink> FindLinks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var links = new List<NoteLink>();
        Scan(text, link => links.Add(link));
        return links;
    }

    /// <summary>The distinct notes that <paramref name="text"/> links to, in order of first appearance.</summary>
    public static IReadOnlyList<NodeId> ExtractTargets(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var seen = new HashSet<NodeId>();
        var targets = new List<NodeId>();
        Scan(text, link =>
        {
            if (seen.Add(link.Target))
            {
                targets.Add(link.Target);
            }
        });
        return targets;
    }

    private static void Scan(string text, Action<NoteLink> found)
    {
        var span = text.AsSpan();
        var from = 0;
        while (from < span.Length)
        {
            var relative = span[from..].IndexOfAny('n', 'N');
            if (relative < 0)
            {
                return;
            }

            var start = from + relative;
            if (!StartsWithPrefix(span[start..]))
            {
                from = start + 1;
                continue;
            }

            var uuidStart = start + NotePrefix.Length;
            var end = uuidStart + Uuid.HyphenatedLength;
            if (end <= span.Length
                && (start == 0 || !char.IsLetterOrDigit(span[start - 1]))
                && (end == span.Length || !IsUriContinuation(span[end]))
                && TryParseUuid(span[uuidStart..end], out var id))
            {
                found(new NoteLink(start, end - start, id));
                from = end;
            }
            else
            {
                from = start + 1;
            }
        }
    }

    // The prefix in any letter case, folding ASCII letters only: unlike OrdinalIgnoreCase, U+0131 (dotless i) or
    // U+017F (long s) never stand in for a prefix letter.
    private static bool StartsWithPrefix(ReadOnlySpan<char> text) =>
        text.Length >= NotePrefix.Length && Ascii.EqualsIgnoreCase(text[..NotePrefix.Length], NotePrefix);

    private static bool IsUriContinuation(char ch) => char.IsLetterOrDigit(ch) || ch is '-' or '_';

    // Exactly 8-4-4-4-12 hex digits in any letter case: no braces, signs or "0x" (fixtures/note-links.json).
    private static bool TryParseUuid(ReadOnlySpan<char> candidate, out NodeId id)
    {
        var ok = Uuid.TryParse(candidate, out var guid);
        id = new NodeId(guid);
        return ok;
    }
}
