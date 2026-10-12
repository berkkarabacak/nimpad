using System.Text.RegularExpressions;
using NimPad.Core.Tests.Support;
using NimPad.Core.Text;
using Xunit;

namespace NimPad.Core.Tests.Text;

public class ContentCanonicalizerTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("abc", "abc")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\r\nb", "a\n\nb")]
    [InlineData("a\n\rb", "a\n\nb")]
    [InlineData("\r\n\r\n", "\n\n")]
    [InlineData("end\r", "end\n")]
    [InlineData("a\u2028b\u0085c", "a\u2028b\u0085c")]
    public void NormalizeLineEndingsConvertsCrLfAndLoneCr(string input, string expected) =>
        Assert.Equal(expected, ContentCanonicalizer.NormalizeLineEndings(input));

    [Fact]
    public void NormalizeReturnsSameInstanceWhenAlreadyCanonical()
    {
        var text = "line 1\nline 2";
        Assert.Same(text, ContentCanonicalizer.NormalizeLineEndings(text));
        Assert.True(ContentCanonicalizer.IsCanonical(text));
        Assert.False(ContentCanonicalizer.IsCanonical("a\r\nb"));
    }

    [Fact]
    public void ChecksumHasPrefixAndLowercaseHex()
    {
        Assert.Equal(
            "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ContentCanonicalizer.Checksum(string.Empty));
        Assert.Matches(new Regex("^sha256:[0-9a-f]{64}$"), ContentCanonicalizer.Checksum("Hello, NimPad 😀"));
    }

    [Fact]
    public void ChecksumIgnoresLineEndingStyle() =>
        Assert.Equal(ContentCanonicalizer.Checksum("a\nb\n"), ContentCanonicalizer.Checksum("a\r\nb\r"));

    [Fact]
    public void LoneSurrogateIsHashedAsReplacementCharacterLikeNode() =>
        Assert.Equal(ContentCanonicalizer.Checksum("\uFFFD"), ContentCanonicalizer.Checksum("\uD800"));

    [Fact]
    public void ToStorableTextReplacesOnlyU0000AndUnpairedSurrogates()
    {
        var storable = "emoji 😀, U+FFFD \uFFFD, U+FEFF \uFEFF";
        Assert.Same(storable, ContentCanonicalizer.ToStorableText(storable));
        Assert.True(ContentCanonicalizer.IsStorable(storable));

        Assert.Equal("a\uFFFDb\uFFFD😀\uFFFD", ContentCanonicalizer.ToStorableText("a\0b\uDC00😀\uD83D"));
        Assert.False(ContentCanonicalizer.IsStorable("a\0b"));
        Assert.False(ContentCanonicalizer.IsStorable("\uDE00\uD83D"));

        // Long texts take the vectorized path; the replacement lands exactly where the character was.
        var large = new string('x', 100_000) + "\0" + new string('y', 100_000);
        Assert.Equal(100_000, ContentCanonicalizer.ToStorableText(large).IndexOf('\uFFFD'));
    }

    [Fact]
    public void MatchesSharedFixture()
    {
        var cases = ContractFixtures.Load("checksum.json").GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var testCase in cases)
        {
            var name = testCase.GetProperty("name").GetString();
            var input = testCase.GetProperty("input").GetString()!;
            var canonical = testCase.GetProperty("canonical").GetString()!;
            var checksum = testCase.GetProperty("checksum").GetString();
            Assert.True(canonical == ContentCanonicalizer.NormalizeLineEndings(input), $"canonical: {name}");
            Assert.True(checksum == ContentCanonicalizer.Checksum(input), $"checksum(input): {name}");
            Assert.True(checksum == ContentCanonicalizer.Checksum(canonical), $"checksum(canonical): {name}");
        }
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   \n\n  ", "")]
    [InlineData("\n\n  First   line  here \nsecond", "First line here")]
    [InlineData("\tindented", "indented")]
    public void PreviewIsFirstNonBlankLineCollapsed(string text, string expected) =>
        Assert.Equal(expected, DocumentPreview.Create(text));

    [Fact]
    public void PreviewIsCappedWithoutSplittingSurrogatePairs()
    {
        var text = new string('x', DocumentPreview.MaxLength - 1) + "😀 tail";
        var preview = DocumentPreview.Create(text);

        Assert.True(preview.Length <= DocumentPreview.MaxLength);
        Assert.False(char.IsHighSurrogate(preview[^1]));
        Assert.Equal(DocumentPreview.MaxLength, DocumentPreview.Create(new string('y', 1000)).Length);
    }

    [Fact]
    public void PreviewIsAtMost120Characters() => Assert.Equal(120, DocumentPreview.MaxLength);

    [Theory]
    [InlineData(64 * 1024 - 1)]
    [InlineData(64 * 1024)]
    [InlineData(64 * 1024 + 1)]
    [InlineData(300_000)]
    public void LargeTextsHashInChunksToTheSameChecksum(int length)
    {
        // Surrogate pairs and multi-byte characters straddle every chunk boundary; a lone surrogate hashes as U+FFFD.
        var builder = new System.Text.StringBuilder(length + 4);
        while (builder.Length < length)
        {
            builder.Append("a😀é€\n");
        }

        builder.Length = length;
        builder.Append('\uD800');
        var text = builder.ToString();
        var expected = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(new System.Text.UTF8Encoding(false).GetBytes(text)));

        Assert.Equal(expected, ContentCanonicalizer.Checksum(text));
    }
}
