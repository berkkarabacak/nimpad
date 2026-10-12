using NimPad.Presentation.Find;
using Xunit;

namespace NimPad.Presentation.Tests.Find;

public sealed class TextSearchEngineTests
{
    private static IReadOnlyList<int> Offsets(string text, TextSearchOptions options) =>
        TextSearchEngine.Find(text, options).Matches.Select(match => match.Offset).ToList();

    [Fact]
    public void PlainSearchIgnoresCaseUnlessAsked()
    {
        const string text = "Beta beta BETA alphabeta";

        Assert.Equal([0, 5, 10, 20], Offsets(text, new("beta")));
        Assert.Equal([5, 20], Offsets(text, new("beta", MatchCase: true)));
        Assert.All(TextSearchEngine.Find(text, new("beta")).Matches, match => Assert.Equal(4, match.Length));
    }

    [Fact]
    public void WholeWordMatchesOnlyBetweenNonWordCharactersLikeNotepadPlusPlus()
    {
        const string text = "beta beta_x (beta) alphabeta beta2 beta-beta é beta";

        Assert.Equal([0, 13, 35, 40, 47], Offsets(text, new("beta", WholeWord: true)));
        // A query with non-word characters at its edges still needs non-word neighbors.
        Assert.Equal([12], Offsets(text, new("(beta)", WholeWord: true)));
        Assert.Empty(Offsets("café", new("caf", WholeWord: true)));
        Assert.Equal([0], Offsets("x_1", new("x_1", WholeWord: true)));
    }

    [Fact]
    public void MatchesDoNotOverlap()
    {
        Assert.Equal([0, 2], Offsets("aaaaa", new("aa")));
    }

    [Fact]
    public void RegexSearchIsMultilineAndHonorsCase()
    {
        const string text = "Title\nfoo 12\nFoo 345\nbar";

        Assert.Equal([10, 17], Offsets(text, new(@"\d+", UseRegex: true)));
        Assert.Equal([6, 13], Offsets(text, new("^foo", UseRegex: true)));
        Assert.Equal([6], Offsets(text, new("^foo", MatchCase: true, UseRegex: true)));
        Assert.Equal([6, 13], Offsets(text, new(@"^\w+ \d+$", UseRegex: true)));
    }

    [Theory]
    [InlineData("(beta", "Unmatched “(”")]
    [InlineData("beta)", "Unmatched “)”")]
    [InlineData("[abc", "Unmatched “[”")]
    [InlineData("*beta", "Nothing to repeat before the quantifier")]
    [InlineData("beta\\", "A “\\” at the end needs a character after it")]
    public void AnInvalidPatternIsExplainedInPlainWords(string pattern, string message)
    {
        var result = TextSearchEngine.Find("beta", new(pattern, UseRegex: true));

        Assert.Empty(result.Matches);
        Assert.Equal(message, result.Error);
        Assert.Equal(message, TextSearchEngine.ValidatePattern(pattern));
        Assert.Null(TextSearchEngine.ValidatePattern("(beta)"));
    }

    [Fact]
    public void ACatastrophicPatternTimesOutInsteadOfHanging()
    {
        var text = new string('a', 40) + "!";

        var result = TextSearchEngine.Find(text, new("^(a+)+$", UseRegex: true));

        Assert.Empty(result.Matches);
        Assert.Equal("The pattern takes too long to search. Try a simpler one.", result.Error);
    }

    [Fact]
    public void ReplaceAllReturnsOneEditPerMatchWithGroupSubstitution()
    {
        const string text = "x=1, y=22, z=333";
        var options = new TextSearchOptions(@"(\w)=(\d+)", UseRegex: true);
        var matches = TextSearchEngine.Find(text, options).Matches;

        var edits = TextSearchEngine.ReplaceAll(text, matches, "$2:$1", options);

        Assert.Equal(
            [new TextEdit(0, 3, "1:x"), new TextEdit(5, 4, "22:y"), new TextEdit(11, 5, "333:z")],
            edits);
        Assert.Equal("1:x, 22:y, 333:z", Apply(text, edits));

        var plain = new TextSearchOptions("$1");
        var plainEdits = TextSearchEngine.ReplaceAll("a $1 b", TextSearchEngine.Find("a $1 b", plain).Matches, "$$", plain);
        Assert.Equal([new TextEdit(2, 2, "$$")], plainEdits);
    }

    [Fact]
    public void ReplaceOneUsesThatMatchsGroups()
    {
        const string text = "a1 b2";
        var options = new TextSearchOptions(@"([a-z])(\d)", UseRegex: true);
        var matches = TextSearchEngine.Find(text, options).Matches;

        Assert.Equal(new TextEdit(3, 2, "2b"), TextSearchEngine.Replace(text, matches[1], "$2$1", options));
    }

    [Fact]
    public void EmptyRegexMatchesCanPrefixEveryLine()
    {
        const string text = "one\ntwo\nthree";
        var options = new TextSearchOptions("^", UseRegex: true);
        var matches = TextSearchEngine.Find(text, options).Matches;

        Assert.Equal(3, matches.Count);
        Assert.Equal("- one\n- two\n- three", Apply(text, TextSearchEngine.ReplaceAll(text, matches, "- ", options)));
    }

    [Fact]
    public void AnEmptyQueryFindsNothing()
    {
        Assert.Same(TextSearchResult.Empty, TextSearchEngine.Find("anything", new(string.Empty)));
    }

    private static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(edit => edit.Offset))
        {
            text = text[..edit.Offset] + edit.Text + text[(edit.Offset + edit.Length)..];
        }

        return text;
    }
}
