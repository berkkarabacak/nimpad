using System.Text;
using System.Text.RegularExpressions;

namespace NimPad.Presentation.Find;

/// <summary>One match in the text.</summary>
/// <param name="Offset">Where it starts (UTF-16 offset).</param>
/// <param name="Length">Its length (0 for an empty regular-expression match such as <c>^</c>).</param>
public readonly record struct TextMatch(int Offset, int Length)
{
    /// <summary>The offset just past the match.</summary>
    public int End => Offset + Length;
}

/// <summary>One replacement for the view to apply: replace <paramref name="Length"/> characters at <paramref name="Offset"/>.</summary>
/// <param name="Offset">Where the replaced text starts, in the text the matches were found in.</param>
/// <param name="Length">How many characters are replaced.</param>
/// <param name="Text">The new text.</param>
public readonly record struct TextEdit(int Offset, int Length, string Text);

/// <summary>What to search for.</summary>
/// <param name="Query">The text or pattern.</param>
/// <param name="MatchCase">Case-sensitive (Aa).</param>
/// <param name="WholeWord">Only matches not touching a letter, digit or underscore on either side (like Notepad++).</param>
/// <param name="UseRegex">
/// Treat <paramref name="Query"/> as a .NET regular expression, multi-line: <c>^</c> and <c>$</c> match at line breaks,
/// and in text with CRLF line endings <c>$</c> matches before the <c>\r\n</c> and <c>.</c> matches neither <c>\r</c>
/// nor <c>\n</c> (like Notepad++).
/// </param>
public sealed record TextSearchOptions(string Query, bool MatchCase = false, bool WholeWord = false, bool UseRegex = false);

/// <summary>The matches of a search, or why the query is invalid.</summary>
/// <param name="Matches">The matches in text order, at most <see cref="TextSearchEngine.MaxMatches"/>.</param>
/// <param name="Error">A user-facing message when the query is not a valid pattern (or timed out); otherwise <see langword="null"/>.</param>
/// <param name="IsTruncated">True when more than <see cref="TextSearchEngine.MaxMatches"/> matches exist.</param>
public sealed record TextSearchResult(IReadOnlyList<TextMatch> Matches, string? Error, bool IsTruncated)
{
    /// <summary>No matches and no error.</summary>
    public static TextSearchResult Empty { get; } = new([], null, false);
}

/// <summary>The edits of a Replace all, or why the query is invalid.</summary>
/// <param name="Edits">One edit per match, in text order, with offsets in the searched text (no cap).</param>
/// <param name="Error">A user-facing message when the pattern is invalid or timed out; otherwise <see langword="null"/>.</param>
public sealed record TextReplaceAllResult(IReadOnlyList<TextEdit> Edits, string? Error);

/// <summary>
/// Find and replace over a text snapshot, with the semantics of the find panel (design page 9): plain or regular
/// expression, match case, whole word (Notepad++: the characters before and after a match must not be word
/// characters), and a 1 s regex match timeout. Pure and thread-safe.
/// </summary>
public static partial class TextSearchEngine
{
    /// <summary>The regular-expression match timeout.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The most matches reported by <see cref="Find"/> (Replace all has no limit).</summary>
    public const int MaxMatches = 100_000;

    private const string TimeoutMessage = "The pattern takes too long to search. Try a simpler one.";

    // In text with CR line endings: "$" before "\r\n", a lone "\n" or the end, never between "\r" and "\n".
    private const string CrlfLineEnd = @"(?=\z|\r\n|(?<!\r)\n)";

    // In text with CR line endings: "." matches neither line-break character.
    private const string CrlfAnyCharacter = @"[^\r\n]";

    /// <summary>Finds every match of <paramref name="options"/> in <paramref name="text"/>.</summary>
    public static TextSearchResult Find(string text, TextSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Query.Length == 0)
        {
            return TextSearchResult.Empty;
        }

        return options.UseRegex ? FindRegex(text, options, MaxMatches, cancellationToken) : FindPlain(text, options, MaxMatches, cancellationToken);
    }

    /// <summary>
    /// The edits of Replace all: every match of <paramref name="options"/> in <paramref name="text"/> (no
    /// <see cref="MaxMatches"/> limit), each replaced by <paramref name="replacement"/> (for a regular expression with
    /// <c>$1</c>, <c>${name}</c> and <c>$$</c> substitutions). Never throws for the query or the replacement.
    /// </summary>
    public static TextReplaceAllResult ReplaceEveryMatch(string text, TextSearchOptions options, string replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(replacement);
        if (options.Query.Length == 0)
        {
            return new TextReplaceAllResult([], null);
        }

        if (!options.UseRegex)
        {
            var found = FindPlain(text, options, int.MaxValue, cancellationToken);
            return new TextReplaceAllResult(found.Matches.Select(match => new TextEdit(match.Offset, match.Length, replacement)).ToList(), null);
        }

        Regex regex;
        try
        {
            regex = CreateRegex(options, text);
        }
        catch (RegexParseException ex)
        {
            return new TextReplaceAllResult([], Describe(ex));
        }
        catch (ArgumentException ex)
        {
            return new TextReplaceAllResult([], ex.Message);
        }

        var edits = new List<TextEdit>();
        try
        {
            for (var match = regex.Match(text); match.Success; match = match.NextMatch())
            {
                if (options.WholeWord && !IsWholeWord(text, match.Index, match.Length))
                {
                    continue;
                }

                edits.Add(new TextEdit(match.Index, match.Length, match.Result(replacement)));
                if ((edits.Count & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new TextReplaceAllResult([], TimeoutMessage);
        }
        catch (ArgumentException ex)
        {
            // An invalid substitution in the replacement.
            return new TextReplaceAllResult([], ex.Message);
        }

        return new TextReplaceAllResult(edits, null);
    }

    /// <summary>
    /// The replacement for one match: the replacement text as is, or for a regular expression with <c>$1</c>,
    /// <c>${name}</c> and <c>$$</c> substitutions applied to that match.
    /// </summary>
    /// <exception cref="ArgumentException">The query is not a valid pattern.</exception>
    /// <exception cref="RegexMatchTimeoutException">The pattern took longer than <see cref="RegexTimeout"/>.</exception>
    public static TextEdit Replace(string text, TextMatch match, string replacement, TextSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.UseRegex)
        {
            return new TextEdit(match.Offset, match.Length, replacement);
        }

        var regex = CreateRegex(options, text);
        var found = regex.Match(text, match.Offset);
        while (found.Success && found.Index < match.Offset)
        {
            found = found.NextMatch();
        }

        var result = found.Success && found.Index == match.Offset && found.Length == match.Length
            ? found.Result(replacement)
            : replacement;
        return new TextEdit(match.Offset, match.Length, result);
    }

    /// <summary>
    /// The edits that replace the given matches, in text order, with offsets in the original text (apply them from
    /// last to first, as one undo step). <see cref="ReplaceEveryMatch"/> does the search too, without a limit.
    /// </summary>
    /// <exception cref="ArgumentException">The query is not a valid pattern.</exception>
    /// <exception cref="RegexMatchTimeoutException">The pattern took longer than <see cref="RegexTimeout"/>.</exception>
    public static IReadOnlyList<TextEdit> ReplaceAll(string text, IReadOnlyList<TextMatch> matches, string replacement, TextSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.UseRegex)
        {
            return matches.Select(match => new TextEdit(match.Offset, match.Length, replacement)).ToList();
        }

        var regex = CreateRegex(options, text);
        var wanted = matches.ToDictionary(match => match.Offset, match => match.Length);
        var edits = new List<TextEdit>(matches.Count);
        for (var found = regex.Match(text); found.Success; found = found.NextMatch())
        {
            if (wanted.TryGetValue(found.Index, out var length) && length == found.Length)
            {
                edits.Add(new TextEdit(found.Index, found.Length, found.Result(replacement)));
            }
        }

        return edits;
    }

    /// <summary>True when <paramref name="ch"/> is a word character (letter, digit, combining mark or underscore).</summary>
    public static bool IsWordCharacter(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_' || char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark;

    /// <summary>
    /// A user-facing message for an invalid pattern, for example <c>Unmatched “(”</c>, or <see langword="null"/>
    /// when it is valid.
    /// </summary>
    public static string? ValidatePattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        try
        {
            _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
            return null;
        }
        catch (RegexParseException ex)
        {
            return Describe(ex);
        }
    }

    private static TextSearchResult FindPlain(string text, TextSearchOptions options, int maxMatches, CancellationToken cancellationToken)
    {
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var query = options.Query;
        var matches = new List<TextMatch>();
        var start = 0;
        while (start <= text.Length - query.Length)
        {
            var index = text.IndexOf(query, start, comparison);
            if (index < 0)
            {
                break;
            }

            if (!options.WholeWord || IsWholeWord(text, index, query.Length))
            {
                if (matches.Count == maxMatches)
                {
                    return new TextSearchResult(matches, null, true);
                }

                matches.Add(new TextMatch(index, query.Length));
                start = index + query.Length;
            }
            else
            {
                start = index + 1;
            }

            if ((matches.Count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return new TextSearchResult(matches, null, false);
    }

    private static TextSearchResult FindRegex(string text, TextSearchOptions options, int maxMatches, CancellationToken cancellationToken)
    {
        Regex regex;
        try
        {
            regex = CreateRegex(options, text);
        }
        catch (RegexParseException ex)
        {
            return new TextSearchResult([], Describe(ex), false);
        }
        catch (ArgumentException ex)
        {
            return new TextSearchResult([], ex.Message, false);
        }

        var matches = new List<TextMatch>();
        try
        {
            for (var found = regex.Match(text); found.Success; found = found.NextMatch())
            {
                if (options.WholeWord && !IsWholeWord(text, found.Index, found.Length))
                {
                    continue;
                }

                if (matches.Count == maxMatches)
                {
                    return new TextSearchResult(matches, null, true);
                }

                matches.Add(new TextMatch(found.Index, found.Length));
                if ((matches.Count & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new TextSearchResult([], TimeoutMessage, false);
        }

        return new TextSearchResult(matches, null, false);
    }

    // The pattern as typed; for text with CR characters, with CRLF-aware "$" and "." (see AdaptToCrlf).
    private static Regex CreateRegex(TextSearchOptions options, string text)
    {
        var regexOptions = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!options.MatchCase)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        // Parse the pattern as typed first, so errors describe what the user wrote.
        var regex = new Regex(options.Query, regexOptions, RegexTimeout);
        if (!text.Contains('\r', StringComparison.Ordinal))
        {
            return regex;
        }

        var adapted = AdaptToCrlf(options.Query);
        if (string.Equals(adapted, options.Query, StringComparison.Ordinal))
        {
            return regex;
        }

        try
        {
            return new Regex(adapted, regexOptions, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return regex;
        }
    }

    /// <summary>
    /// Rewrites a pattern for text with CRLF line endings, where .NET's multi-line <c>$</c> matches only before
    /// <c>\n</c> (so <c>foo$</c> never matches before <c>\r\n</c>) and <c>.</c> matches <c>\r</c>: an unescaped
    /// <c>$</c> outside a character class becomes a line-end lookahead that also accepts <c>\r\n</c>, and an unescaped
    /// <c>.</c> becomes <c>[^\r\n]</c>. Escapes, character classes (with subtraction) and <c>(?#…)</c> comments are
    /// kept. Inline options that change these meanings (<c>s</c> on, <c>m</c> off) leave the affected token alone.
    /// </summary>
    internal static string AdaptToCrlf(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var (singleline, multilineOff) = ScanInlineOptions(pattern);
        var rewriteDot = !singleline;
        var rewriteDollar = !multilineOff;
        if ((!rewriteDot || !pattern.Contains('.', StringComparison.Ordinal)) && (!rewriteDollar || !pattern.Contains('$', StringComparison.Ordinal)))
        {
            return pattern;
        }

        var result = new StringBuilder(pattern.Length + 32);
        var classDepth = 0;
        var classStart = -1;
        var afterDash = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                result.Append(ch);
                if (i + 1 < pattern.Length)
                {
                    result.Append(pattern[++i]);
                }

                afterDash = false;
                continue;
            }

            if (classDepth > 0)
            {
                result.Append(ch);
                var wasAfterDash = afterDash;
                afterDash = ch == '-';
                if (ch == '[' && wasAfterDash)
                {
                    // Subtraction: [a-z-[aeiou]].
                    classDepth++;
                    classStart = i + 1;
                }
                else if (ch == '^' && i == classStart)
                {
                    classStart = i + 1;
                }
                else if (ch == ']' && i != classStart)
                {
                    classDepth--;
                }

                continue;
            }

            switch (ch)
            {
                case '[':
                    classDepth = 1;
                    classStart = i + 1;
                    result.Append(ch);
                    break;
                case '(' when string.CompareOrdinal(pattern, i, "(?#", 0, 3) == 0:
                    var end = pattern.IndexOf(')', i);
                    end = end < 0 ? pattern.Length - 1 : end;
                    result.Append(pattern, i, end - i + 1);
                    i = end;
                    break;
                case '.' when rewriteDot:
                    result.Append(CrlfAnyCharacter);
                    break;
                case '$' when rewriteDollar:
                    result.Append(CrlfLineEnd);
                    break;
                default:
                    result.Append(ch);
                    break;
            }
        }

        return result.ToString();
    }

    // Whether an inline option group turns single-line mode on, or multi-line mode off, anywhere in the pattern.
    private static (bool Singleline, bool MultilineOff) ScanInlineOptions(string pattern)
    {
        var singleline = false;
        var multilineOff = false;
        foreach (Match group in InlineOptions().Matches(pattern))
        {
            singleline |= group.Groups["on"].Value.Contains('s', StringComparison.Ordinal);
            multilineOff |= group.Groups["off"].Value.Contains('m', StringComparison.Ordinal);
        }

        return (singleline, multilineOff);
    }

    [GeneratedRegex(@"\(\?(?<on>[imnsx]*)(?:-(?<off>[imnsx]*))?[:)]", RegexOptions.CultureInvariant)]
    private static partial Regex InlineOptions();

    // Notepad++ (Scintilla SCFIND_WHOLEWORD): the characters before and after the match are not word characters.
    private static bool IsWholeWord(string text, int offset, int length)
    {
        var before = offset > 0 && IsWordCharacter(text[offset - 1]);
        var end = offset + length;
        var after = end < text.Length && IsWordCharacter(text[end]);
        return !before && !after;
    }

    private static string Describe(RegexParseException error) => error.Error switch
    {
        RegexParseError.InsufficientClosingParentheses => "Unmatched “(”",
        RegexParseError.InsufficientOpeningParentheses => "Unmatched “)”",
        RegexParseError.UnterminatedBracket => "Unmatched “[”",
        RegexParseError.UnescapedEndingBackslash => "A “\\” at the end needs a character after it",
        RegexParseError.QuantifierAfterNothing => "Nothing to repeat before the quantifier",
        RegexParseError.NestedQuantifiersNotParenthesized => "Two quantifiers in a row",
        RegexParseError.ReversedQuantifierRange => "The repeat range is reversed",
        RegexParseError.ReversedCharacterRange => "The character range is reversed",
        RegexParseError.UnrecognizedEscape => "Unknown escape sequence",
        RegexParseError.UndefinedNumberedReference or RegexParseError.UndefinedNamedReference => "Reference to an undefined group",
        RegexParseError.InvalidGroupingConstruct => "Invalid group",
        _ => "Invalid regular expression",
    };
}
