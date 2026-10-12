using Microsoft.Extensions.Time.Testing;
using NimPad.Core.Ids;
using NimPad.Core.Time;
using Xunit;

namespace NimPad.Core.Tests.Domain;

public class IdsAndTimeTests
{
    [Fact]
    public void NewIdsAreLowercaseUuidV7()
    {
        var id = NodeId.New();
        var text = id.ToString();

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", text);
        Assert.Equal(7, id.Value.Version);
        Assert.Matches("^[0-9a-f-]{36}$", WorkspaceId.New().ToString());
        Assert.Matches("^[0-9a-f-]{36}$", TrashBatchId.New().ToString());
    }

    [Fact]
    public void ParseRoundTripsAndAcceptsUppercase()
    {
        var id = NodeId.New();

        Assert.Equal(id, NodeId.Parse(id.ToString()));
        Assert.Equal(id, NodeId.Parse(id.ToString().ToUpperInvariant()));
        Assert.True(WorkspaceId.TryParse("0192f0c4-0000-7000-8000-000000000001", out var workspaceId));
        Assert.Equal("0192f0c4-0000-7000-8000-000000000001", workspaceId.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("0192f0c400007000800000000000000a")]
    [InlineData("{0192f0c4-0000-7000-8000-000000000001}")]
    public void ParseRejectsNonHyphenatedForms(string text)
    {
        Assert.False(NodeId.TryParse(text, out _));
        Assert.Throws<FormatException>(() => NodeId.Parse(text));
    }

    [Fact]
    public void GeneratorEmbedsTheProvidedClock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 13, 59, 43, 123, TimeSpan.Zero));
        var generator = new UuidV7Generator(clock);

        var first = generator.NewGuid().ToString("N");
        // The first 48 bits are the Unix time in milliseconds.
        var millis = Convert.ToInt64(first[..12], 16);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), millis);
        Assert.NotEqual(generator.NewGuid(), generator.NewGuid());
    }

    [Fact]
    public void TimestampsUseMillisecondsAndZ()
    {
        var instant = new DateTimeOffset(2026, 10, 12, 15, 59, 43, 123, TimeSpan.FromHours(2)).AddTicks(4567);

        Assert.Equal("2026-10-12T13:59:43.123Z", Timestamps.ToText(instant));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 13, 59, 43, 123, TimeSpan.Zero), Timestamps.Truncate(instant));
        Assert.Equal(Timestamps.Truncate(instant), Timestamps.Parse("2026-10-12T13:59:43.123Z"));
        Assert.Equal(TimeSpan.Zero, Timestamps.Parse("2026-10-12T13:59:43.123Z").Offset);
    }

    [Theory]
    [InlineData("2026-10-12T13:59:43Z")]
    [InlineData("2026-10-12T13:59:43.123+00:00")]
    [InlineData("2026-02-30T13:59:43.123Z")]
    [InlineData("2026-10-12 13:59:43.123Z")]
    public void TimestampParsingIsExact(string text) => Assert.False(Timestamps.TryParse(text, out _));

    [Fact]
    public void NowTruncatesTheClock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 13, 59, 43, 123, TimeSpan.Zero).AddTicks(9999));
        Assert.Equal("2026-10-12T13:59:43.123Z", Timestamps.ToText(Timestamps.Now(clock)));
        Assert.Equal(0, Timestamps.Now(clock).Ticks % TimeSpan.TicksPerMillisecond);
    }
}
