using NimPad.Presentation.Find;
using Xunit;

namespace NimPad.Presentation.Tests.Find;

/// <summary>Regular expressions over files with Windows (CRLF) line endings, which external files keep verbatim.</summary>
public sealed class TextSearchEngineCrlfTests
{
    private static IReadOnlyList<TextMatch> Find(string text, string pattern) =>
        TextSearchEngine.Find(text, new TextSearchOptions(pattern, UseRegex: true)).Matches;

    private static string ReplaceEverywhere(string text, string pattern, string replacement)
    {
        var result = TextSearchEngine.ReplaceEveryMatch(text, new TextSearchOptions(pattern, UseRegex: true), replacement);
        Assert.Null(result.Error);
        foreach (var edit in result.Edits.OrderByDescending(edit => edit.Offset))
        {
            text = text[..edit.Offset] + edit.Text + text[(edit.Offset + edit.Length)..];
        }

        return text;
    }

    [Fact]
    public void DollarMatchesBeforeTheCrlfLikeBeforeALf()
    {
        Assert.Equal([new TextMatch(0, 3)], Find("foo\r\nbar\r\n", "foo$"));
        Assert.Equal([new TextMatch(0, 3)], Find("foo\nbar\n", "foo$"));
        Assert.Equal([new TextMatch(5, 3)], Find("foo\r\nbar", "bar$"));

        // Empty line-end matches sit before the "\r", never between "\r" and "\n".
        Assert.Equal([3, 8, 10], Find("foo\r\nbar\r\n", "$").Select(match => match.Offset));
    }

    [Fact]
    public void DotMatchesNeitherLineBreakCharacter()
    {
        Assert.Equal([new TextMatch(1, 2)], Find("foo\r\nbar", "o.*$"));
        Assert.Equal([new TextMatch(0, 3), new TextMatch(5, 3)], Find("foo\r\nbar", "^.+$"));
        Assert.Empty(Find("a\r\nb", "a.b"));
    }

    [Fact]
    public void TrimmingTrailingWhitespaceKeepsTheLineEndings()
    {
        Assert.Equal("a\r\nb\r\nc", ReplaceEverywhere("a  \r\nb\t\r\nc ", "[ \\t]+$", string.Empty));
        Assert.Equal("a\r\nb\r\nc", ReplaceEverywhere("a  \r\nb\t\r\nc ", " +$|\\t+$", string.Empty));
        Assert.Equal("x;\r\ny;\r\n", ReplaceEverywhere("x\r\ny\r\n", "$(?=\\r\\n)", ";"));
    }

    [Fact]
    public void ExplicitLineBreaksStillMatch()
    {
        Assert.Equal("a,b,c", ReplaceEverywhere("a\r\nb\r\nc", "\\r\\n", ","));
        Assert.Equal([new TextMatch(1, 2)], Find("a\r\nb", "\\r?\\n"));
    }

    [Fact]
    public void EscapesClassesAndCommentsKeepTheirMeaning()
    {
        const string text = "cost $5.\r\nend.\r\n";
        Assert.Equal([new TextMatch(5, 1)], Find(text, "\\$"));
        Assert.Equal([new TextMatch(5, 1), new TextMatch(7, 1), new TextMatch(13, 1)], Find(text, "[$.]"));
        Assert.Equal([new TextMatch(7, 1), new TextMatch(13, 1)], Find(text, "\\.$"));
        Assert.Equal([new TextMatch(7, 1), new TextMatch(13, 1)], Find(text, "\\.(?#a $ comment)$"));
        Assert.Equal([new TextMatch(5, 3)], Find("and\r\nbnd\r\n", "[a-z-[aeiou]]nd$"));

        // A "]" right after "[" is part of the class, so the "$" after it is a literal dollar.
        Assert.Equal([new TextMatch(1, 1)], Find("a$\r\n", "[]$]$"));
    }

    [Fact]
    public void InlineSinglelineModeKeepsTheDotMatchingEverything()
    {
        Assert.Equal([new TextMatch(0, 4)], Find("a\r\nb", "(?s)a..b"));
        Assert.Equal([new TextMatch(0, 4)], Find("a\r\nb", "(?s:a..b)"));
    }

    [Fact]
    public void TheRewriteLeavesTextWithoutCarriageReturnsAlone()
    {
        Assert.Equal([new TextMatch(1, 2)], Find("foo\nbar", "o.*$"));
        Assert.Equal("(?=\\z|\\r\\n|(?<!\\r)\\n)", TextSearchEngine.AdaptToCrlf("$"));
        Assert.Equal("[^\\r\\n]+", TextSearchEngine.AdaptToCrlf(".+"));
        Assert.Equal("[.$]\\.\\$", TextSearchEngine.AdaptToCrlf("[.$]\\.\\$"));
        Assert.Equal("(?m-m:a$)", TextSearchEngine.AdaptToCrlf("(?m-m:a$)"));
        Assert.Equal("(?s).", TextSearchEngine.AdaptToCrlf("(?s)."));
    }

    [Fact]
    public void ErrorsDescribeThePatternAsTyped()
    {
        var result = TextSearchEngine.Find("a\r\n", new TextSearchOptions("(a$", UseRegex: true));
        Assert.Equal("Unmatched “(”", result.Error);
        Assert.Equal("Unmatched “(”", TextSearchEngine.ReplaceEveryMatch("a\r\n", new TextSearchOptions("(a$", UseRegex: true), "b").Error);
    }
}
