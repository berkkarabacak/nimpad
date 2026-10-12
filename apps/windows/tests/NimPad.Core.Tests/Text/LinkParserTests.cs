using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Text;
using Xunit;

namespace NimPad.Core.Tests.Text;

public class LinkParserTests
{
    private static readonly NodeId A = NodeId.Parse("0192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0f");
    private static readonly NodeId B = NodeId.Parse("0192f0c4-1b2a-7c3d-8e4f-000000000002");

    [Fact]
    public void CreatesLowercaseUris()
    {
        var upper = new NodeId(Guid.Parse("0192F0C4-1B2A-7C3D-8E4F-5A6B7C8D9E0F"));
        Assert.Equal("nimpad://note/0192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0f", LinkParser.CreateUri(upper));
    }

    [Fact]
    public void FindsLinksInMarkdownAnglesAndProse()
    {
        var text =
            $"See [Plan](nimpad://note/{A}) and <nimpad://note/{B}>.\n" +
            $"Also NIMPAD://NOTE/{A.ToString().ToUpperInvariant()}, then nimpad://note/{B}";

        var links = LinkParser.FindLinks(text);

        Assert.Equal([A, B, A, B], links.Select(link => link.Target));
        Assert.All(links, link => Assert.Equal(LinkParser.NotePrefix.Length + 36, link.Length));
        Assert.Equal(text.IndexOf("nimpad://", StringComparison.Ordinal), links[0].Start);
        Assert.Equal($"nimpad://note/{B}", text.Substring(links[^1].Start, links[^1].Length));
        Assert.Equal([A, B], LinkParser.ExtractTargets(text));
    }

    [Theory]
    [InlineData("nimpad://note/0192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0")] // too short
    [InlineData("nimpad://note/0192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0fa")] // UUID continues
    [InlineData("nimpad://note/0192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0f_x")]
    [InlineData("xnimpad://note/0192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0f")] // not a separate URI
    [InlineData("nimpad://note/0192f0c41b2a7c3d8e4f5a6b7c8d9e0f")] // no hyphens
    [InlineData("nimpad://note/0192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0g")] // not hex
    [InlineData("nimpad://folder/0192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0f")]
    [InlineData("nimpad://note/+192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0f")] // Guid.TryParseExact accepts a sign
    [InlineData("nimpad://note/0x92f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0f")] // and a 0x prefix
    [InlineData("n\u0131mpad://note/0192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0f")] // dotless i is not an ASCII I
    [InlineData("nimpad://note/\uFF10192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0")] // fullwidth digit
    [InlineData("")]
    public void IgnoresMalformedLinks(string text)
    {
        Assert.Empty(LinkParser.FindLinks(text));
    }

    [Fact]
    public void KeepsScanningAfterAFalseStart()
    {
        var text = $"nimpad://note/nope nimpad://note/{A}";
        Assert.Equal([A], LinkParser.ExtractTargets(text));
    }

    [Fact]
    public void ParsesAWholeUri()
    {
        Assert.True(LinkParser.TryParseUri($"  nimpad://note/{A} ", out var id));
        Assert.Equal(A, id);
        Assert.False(LinkParser.TryParseUri($"nimpad://note/{A} extra", out _));
        Assert.False(LinkParser.TryParseUri("https://example.com", out _));
        Assert.False(LinkParser.TryParseUri(null, out _));
        Assert.False(LinkParser.TryParseUri("nimpad://note/+192f0c4-1b2a-7c3d-8e4f-5a6b7c8d9e0f", out _));
        Assert.False(LinkParser.TryParseUri("n\u0131mpad://note/" + A, out _));
        Assert.True(LinkParser.TryParseUri("NIMPAD://Note/" + A.ToString().ToUpperInvariant(), out var upper));
        Assert.Equal(A, upper);
    }

    [Fact]
    public void ProducesLinkTextForTheDestinationFormat()
    {
        Assert.Equal($"nimpad://note/{A}", LinkParser.CreateLinkText("Plan", A));
        Assert.Equal($"[Plan \\[v2\\]](nimpad://note/{A})", LinkParser.CreateLinkText("Plan [v2]", A, DocumentFormat.Markdown));
        var node = new NodeInfo(
            A, WorkspaceId.New(), null, NodeKind.Document, "Roadmap.md", "a0", false, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null, null, DocumentFormat.Markdown);
        Assert.Equal($"[Roadmap.md](nimpad://note/{A})", LinkParser.CreateLinkText(node, DocumentFormat.Markdown));
        Assert.Equal([A], LinkParser.ExtractTargets(LinkParser.CreateLinkText(node, DocumentFormat.Markdown)));
    }

    [Fact]
    public void ScansLargeTextsQuickly()
    {
        var text = string.Concat(Enumerable.Repeat("plain words without links ", 200_000)) + $"nimpad://note/{B}";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal([B], LinkParser.ExtractTargets(text));
        Assert.True(watch.ElapsedMilliseconds < 500, $"Scanning {text.Length} chars took {watch.ElapsedMilliseconds} ms");
    }
}
