using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace NimPad.Core.Services;

/// <summary>A range of characters in a string.</summary>
/// <param name="Start">The offset of the first character.</param>
/// <param name="Length">The number of characters (UTF-16 code units).</param>
public readonly record struct TextRange(int Start, int Length)
{
    /// <summary>The offset just past the range.</summary>
    public int End => Start + Length;
}

/// <summary>
/// A parsed search query. Each whitespace-separated piece of user input becomes one term: an FTS5 string, quoted
/// (with <c>"</c> doubled) and followed by <c>*</c> for a prefix match; terms are joined with <c>AND</c>. User input
/// therefore never reaches FTS5 as syntax (<c>NEAR</c>, <c>OR</c>, <c>-</c>, <c>:</c> and quotes are all literal).
/// </summary>
internal sealed class SearchQuery
{
    /// <summary>More terms than this are ignored.</summary>
    public const int MaxTerms = 32;

    private SearchQuery(IReadOnlyList<SearchTerm> terms, string ftsExpression)
    {
        Terms = terms;
        FtsExpression = ftsExpression;
    }

    /// <summary>The terms, each with its folded words.</summary>
    public IReadOnlyList<SearchTerm> Terms { get; }

    /// <summary>The FTS5 MATCH expression.</summary>
    public string FtsExpression { get; }

    /// <summary>
    /// Parses user input. Returns <see langword="null"/> when nothing searchable is left: blank input, or pieces with
    /// no letters or digits (which the tokenizer would drop, leaving an empty phrase).
    /// </summary>
    public static SearchQuery? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var terms = new List<SearchTerm>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fts = new StringBuilder();
        foreach (var piece in input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var words = SearchText.Words(piece, 0, piece.Length).Select(word => word.Folded).ToArray();
            if (words.Length == 0 || !seen.Add(string.Join(' ', words)))
            {
                continue;
            }

            terms.Add(new SearchTerm(piece, words));
            if (fts.Length > 0)
            {
                fts.Append(" AND ");
            }

            fts.Append('"').Append(piece.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\"*");
            if (terms.Count == MaxTerms)
            {
                break;
            }
        }

        return terms.Count == 0 ? null : new SearchQuery(terms, fts.ToString());
    }
}

/// <summary>One query term: consecutive words, the last of which may be a prefix (FTS5 <c>"a b"*</c>).</summary>
/// <param name="Input">The user's text for the term.</param>
/// <param name="Words">The folded words (lowercase, no diacritics).</param>
internal sealed record SearchTerm(string Input, string[] Words);

/// <summary>
/// Locating query matches in text, for snippets and highlights. Words and folding follow the <c>notes_fts</c>
/// tokenizer (<c>unicode61 remove_diacritics 2</c>) closely: a word is a run of letters, digits, combining marks and
/// private-use characters; folding removes diacritics and lowercases.
/// </summary>
internal static class SearchText
{
    /// <summary>The longest snippet, ellipses included.</summary>
    public const int MaxSnippetLength = 120;

    // How much of the line before the first match a long-line snippet keeps.
    private const int ContextBefore = 30;

    private const char Ellipsis = '…';

    private static readonly ConcurrentDictionary<int, string> FoldCache = new();

    /// <summary>One word of a text.</summary>
    /// <param name="Start">The offset of the first character in the source text.</param>
    /// <param name="End">The offset just past the word.</param>
    /// <param name="Folded">The folded word.</param>
    /// <param name="FoldedEnds">For each folded character, the source offset just past the character it came from.</param>
    public readonly record struct Word(int Start, int End, string Folded, int[] FoldedEnds);

    /// <summary>The words of <c>text[start..end)</c>, in order.</summary>
    public static IEnumerable<Word> Words(string text, int start, int end)
    {
        var i = start;
        var folded = new StringBuilder();
        var ends = new List<int>();
        while (i < end)
        {
            while (i < end && !IsWordChar(text, i, end, out var skip))
            {
                i += skip;
            }

            if (i >= end)
            {
                yield break;
            }

            var wordStart = i;
            folded.Clear();
            ends.Clear();
            while (i < end && IsWordChar(text, i, end, out var length))
            {
                var fold = Fold(text, i, length);
                i += length;
                foreach (var ch in fold)
                {
                    folded.Append(ch);
                    ends.Add(i);
                }
            }

            if (folded.Length > 0)
            {
                yield return new Word(wordStart, i, folded.ToString(), [.. ends]);
            }
        }
    }

    /// <summary>
    /// Builds the result snippet: the line of <paramref name="text"/> that holds the first match, trimmed, cut to
    /// <see cref="MaxSnippetLength"/> characters around the match (with <c>…</c> where it was cut), and the ranges of
    /// every match inside it.
    /// </summary>
    /// <returns><see langword="null"/> when no term matches the text.</returns>
    public static (string Snippet, IReadOnlyList<TextRange> Highlights)? BuildSnippet(string text, SearchQuery query)
    {
        var words = new WordCursor(text, 0, text.Length);
        for (var i = 0; words.Has(i); i++)
        {
            foreach (var term in query.Terms)
            {
                if (MatchEnd(words, i, term) is { } matchEnd)
                {
                    return CutAround(text, words[i].Start, matchEnd, query);
                }
            }
        }

        return null;
    }

    /// <summary>The ranges of every match of the query in <paramref name="text"/> (merged where they overlap).</summary>
    public static IReadOnlyList<TextRange> Highlight(string text, SearchQuery query) => Highlight(text, 0, text.Length, query, 0);

    private static (string, IReadOnlyList<TextRange>) CutAround(string text, int matchStart, int matchEnd, SearchQuery query)
    {
        var lineStart = matchStart == 0 ? 0 : text.LastIndexOf('\n', matchStart - 1) + 1;
        var lineEnd = text.IndexOf('\n', matchStart);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        while (lineStart < matchStart && char.IsWhiteSpace(text[lineStart]))
        {
            lineStart++;
        }

        while (lineEnd > Math.Max(matchEnd, lineStart) && char.IsWhiteSpace(text[lineEnd - 1]))
        {
            lineEnd--;
        }

        int start, end;
        if (lineEnd - lineStart <= MaxSnippetLength)
        {
            (start, end) = (lineStart, lineEnd);
        }
        else
        {
            // Room for an ellipsis on each side.
            var budget = MaxSnippetLength - 2;
            start = Math.Max(lineStart, matchStart - ContextBefore);
            end = Math.Min(lineEnd, start + budget);
            start = Math.Max(lineStart, end - budget);
            if (start > lineStart && char.IsLowSurrogate(text[start]))
            {
                start++;
            }

            if (end < lineEnd && char.IsLowSurrogate(text[end]))
            {
                end--;
            }
        }

        var leading = start > lineStart;
        var trailing = end < lineEnd;
        var builder = new StringBuilder(end - start + 2);
        if (leading)
        {
            builder.Append(Ellipsis);
        }

        foreach (var ch in text.AsSpan(start, end - start))
        {
            builder.Append(ch == '\t' ? ' ' : ch);
        }

        if (trailing)
        {
            builder.Append(Ellipsis);
        }

        return (builder.ToString(), Highlight(text, start, end, query, leading ? 1 : 0));
    }

    // Matches inside text[start..end), as ranges relative to start, shifted by offset.
    private static List<TextRange> Highlight(string text, int start, int end, SearchQuery query, int offset)
    {
        var words = new WordCursor(text, start, end);
        var ranges = new List<TextRange>();
        for (var i = 0; words.Has(i); i++)
        {
            foreach (var term in query.Terms)
            {
                if (MatchEnd(words, i, term) is { } matchEnd)
                {
                    var from = words[i].Start;
                    ranges.Add(new TextRange(from - start + offset, Math.Min(matchEnd, end) - from));
                }
            }
        }

        ranges.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : y.Length.CompareTo(x.Length));
        var merged = new List<TextRange>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = last with { Length = Math.Max(last.End, range.End) - last.Start };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    // When term matches the words starting at index i: the source offset just past the matched text (the last word
    // only up to the end of its matched prefix).
    private static int? MatchEnd(WordCursor words, int i, SearchTerm term)
    {
        var last = term.Words.Length - 1;
        for (var k = 0; k <= last; k++)
        {
            if (!words.Has(i + k))
            {
                return null;
            }

            var word = words[i + k];
            var wanted = term.Words[k];
            var matches = k == last
                ? word.Folded.StartsWith(wanted, StringComparison.Ordinal)
                : string.Equals(word.Folded, wanted, StringComparison.Ordinal);
            if (!matches)
            {
                return null;
            }

            if (k == last)
            {
                return word.FoldedEnds[wanted.Length - 1];
            }
        }

        return null;
    }

    private static bool IsWordChar(string text, int index, int end, out int length)
    {
        var ch = text[index];
        if (ch < 0x80)
        {
            length = 1;
            return char.IsAsciiLetterOrDigit(ch);
        }

        UnicodeCategory category;
        if (char.IsHighSurrogate(ch) && index + 1 < end && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            category = CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(ch, text[index + 1]));
        }
        else
        {
            length = 1;
            category = CharUnicodeInfo.GetUnicodeCategory(ch);
        }

        return category is <= UnicodeCategory.OtherNumber or UnicodeCategory.PrivateUse;
    }

    // Folds one character (or surrogate pair): diacritics removed, lowercased. Combining marks fold to nothing.
    private static string Fold(string text, int index, int length)
    {
        var ch = text[index];
        if (length == 1 && ch < 0x80)
        {
            return ch is >= 'A' and <= 'Z' ? ((char)(ch + 32)).ToString() : ch.ToString();
        }

        var codePoint = length == 2 ? char.ConvertToUtf32(ch, text[index + 1]) : ch;
        return FoldCache.GetOrAdd(codePoint, static cp =>
        {
            var source = char.ConvertFromUtf32(cp);
            var builder = new StringBuilder(source.Length);
            foreach (var part in source.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(part) is not (UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                {
                    builder.Append(part);
                }
            }

            return builder.ToString().ToLowerInvariant();
        });
    }

    // Tokenizes lazily: words are produced only as far as they are looked at.
    private sealed class WordCursor(string text, int start, int end)
    {
        private readonly IEnumerator<Word> _source = Words(text, start, end).GetEnumerator();
        private readonly List<Word> _words = [];
        private bool _done;

        public Word this[int index] => _words[index];

        public bool Has(int index)
        {
            while (!_done && _words.Count <= index)
            {
                if (_source.MoveNext())
                {
                    _words.Add(_source.Current);
                }
                else
                {
                    _done = true;
                    _source.Dispose();
                }
            }

            return index < _words.Count;
        }
    }
}
