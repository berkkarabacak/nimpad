using ICSharpCode.AvalonEdit.Document;
using NimPad.App.Highlighting;
using NimPad.App.Tests.Themes;
using Xunit;

namespace NimPad.App.Tests.Highlighting;

/// <summary>Boundaries of the "highlighting off" policy, for strings, text sources and editor documents.</summary>
public class LargeDocumentPolicyTests
{
    private const int MaxLine = LargeDocumentPolicy.MaxLineLength;

    [Fact]
    public void ThresholdsAndLabelMatchTheSpec()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Assert.Equal(10 * 1024 * 1024, LargeDocumentPolicy.MaxTextLength);
        Assert.Equal(20_000, LargeDocumentPolicy.MaxLineLength);
        Assert.Equal("Plain Text (large file)", LargeDocumentPolicy.StatusLabel);

        Assert.False(LargeDocumentPolicy.IsTextTooLong(LargeDocumentPolicy.MaxTextLength));
        Assert.True(LargeDocumentPolicy.IsTextTooLong(LargeDocumentPolicy.MaxTextLength + 1));
        Assert.False(LargeDocumentPolicy.IsLineTooLong(MaxLine));
        Assert.True(LargeDocumentPolicy.IsLineTooLong(MaxLine + 1));
    }

    [Theory]
    [InlineData("", LargeDocumentReason.None)]
    [InlineData("short\nlines\r\nonly\r", LargeDocumentReason.None)]
    public void SmallTextsKeepHighlighting(string text, LargeDocumentReason expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        AssertAllSources(text, expected);
    }

    [Theory]
    [InlineData(0, "\n")]
    [InlineData(0, "\r\n")]
    [InlineData(0, "\r")]
    [InlineData(1, "\n")]
    [InlineData(1, "\r\n")]
    [InlineData(1, "\r")]
    public void LineLengthBoundaryInEveryPositionAndLineBreakStyle(int extra, string lineBreak)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var line = new string('x', MaxLine + extra);
        var expected = extra == 0 ? LargeDocumentReason.None : LargeDocumentReason.LineTooLong;

        AssertAllSources(line, expected);                                                    // only line
        AssertAllSources(line + lineBreak + "tail", expected);                               // first line
        AssertAllSources("head" + lineBreak + line, expected);                               // last line
        AssertAllSources("head" + lineBreak + line + lineBreak + "tail" + lineBreak, expected); // middle line
    }

    [Fact]
    public void ManyShortLinesAreFine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var text = string.Concat(Enumerable.Repeat(new string('y', 99) + "\r\n", 5_000)); // 505,000 characters
        AssertAllSources(text, LargeDocumentReason.None);
    }

    [Fact]
    public void TotalLengthBoundary()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var atLimit = ShortLines(LargeDocumentPolicy.MaxTextLength);
        Assert.Equal(LargeDocumentReason.None, LargeDocumentPolicy.Check(atLimit));
        Assert.Equal(LargeDocumentReason.None, LargeDocumentPolicy.Check(new StringTextSource(atLimit)));
        Assert.False(LargeDocumentPolicy.ShouldDisableHighlighting(atLimit));

        var overLimit = ShortLines(LargeDocumentPolicy.MaxTextLength + 1);
        Assert.Equal(LargeDocumentReason.TextTooLong, LargeDocumentPolicy.Check(overLimit));
        Assert.Equal(LargeDocumentReason.TextTooLong, LargeDocumentPolicy.Check(new StringTextSource(overLimit)));
        Assert.True(LargeDocumentPolicy.ShouldDisableHighlighting(overLimit));
        Assert.True(LargeDocumentPolicy.ShouldDisableHighlighting(new StringTextSource(overLimit)));

        // The total length is checked first, without scanning: one huge line reports TextTooLong.
        Assert.Equal(LargeDocumentReason.TextTooLong, LargeDocumentPolicy.Check(new string('z', LargeDocumentPolicy.MaxTextLength + 1)));
    }

    [Fact]
    public void EditorDocumentsAreCheckedThroughTheirLines()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var document = new TextDocument("head\n" + new string('x', MaxLine) + "\ntail");
        Assert.Equal(LargeDocumentReason.None, LargeDocumentPolicy.Check(document));

        // Typing one more character on the long line turns highlighting off.
        document.Insert(10, "x");
        Assert.Equal(LargeDocumentReason.LineTooLong, LargeDocumentPolicy.Check(document));
        Assert.True(LargeDocumentPolicy.ShouldDisableHighlighting(document));
        Assert.True(LargeDocumentPolicy.IsLineTooLong(document.GetLineByNumber(2).Length));

        // A snapshot (any thread) gives the same answer.
        Assert.Equal(LargeDocumentReason.LineTooLong, LargeDocumentPolicy.Check(document.CreateSnapshot()));
    }

    private static void AssertAllSources(string text, LargeDocumentReason expected)
    {
        Assert.Equal(expected, LargeDocumentPolicy.Check(text));
        Assert.Equal(expected, LargeDocumentPolicy.Check(text.AsSpan()));
        Assert.Equal(expected, LargeDocumentPolicy.Check(new StringTextSource(text)));
        Assert.Equal(expected, LargeDocumentPolicy.Check(new TextDocument(text)));
        Assert.Equal(expected, LargeDocumentPolicy.Check(new TextDocument(text).CreateSnapshot()));
        Assert.Equal(expected != LargeDocumentReason.None, LargeDocumentPolicy.ShouldDisableHighlighting(text));
    }

    /// <summary>A text of exactly <paramref name="length"/> characters made of 80-character lines.</summary>
    private static string ShortLines(int length) =>
        string.Create(length, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = i % 81 == 80 ? '\n' : 'a';
            }
        });
}
