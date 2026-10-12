using System.Text.Json;
using NimPad.Core.Ordering;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Ordering;

public class FractionalIndexTests
{
    [Theory]
    [InlineData(null, null, "a0")]
    [InlineData(null, "a0", "Zz")]
    [InlineData(null, "Zz", "Zy")]
    [InlineData("a0", null, "a1")]
    [InlineData("a1", null, "a2")]
    [InlineData("a0", "a1", "a0V")]
    [InlineData("a1", "a2", "a1V")]
    [InlineData("a0V", "a1", "a0l")]
    [InlineData("Zz", "a0", "ZzV")]
    [InlineData("Zz", "a1", "a0")]
    [InlineData(null, "Y00", "Xzzz")]
    [InlineData("bzz", null, "c000")]
    [InlineData("a0", "a0V", "a0G")]
    [InlineData("a0", "a0G", "a08")]
    [InlineData("b125", "b129", "b127")]
    [InlineData("a0", "a1V", "a1")]
    [InlineData("Zz", "a01", "a0")]
    [InlineData(null, "a0V", "a0")]
    [InlineData(null, "b999", "b99")]
    [InlineData(null, "A000000000000000000000000001", "A000000000000000000000000000V")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzy", null, "zzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzz", null, "zzzzzzzzzzzzzzzzzzzzzzzzzzzV")]
    // Halves round up (JavaScript Math.round); banker's rounding would give a different key here.
    [InlineData("a0", "a05", "a03")]
    public void GenerateKeyBetweenMatchesReferenceExamples(string? a, string? b, string expected)
    {
        var key = FractionalIndex.GenerateKeyBetween(a, b);

        Assert.Equal(expected, key);
        Assert.True(a is null || string.CompareOrdinal(a, key) < 0);
        Assert.True(b is null || string.CompareOrdinal(key, b) < 0);
    }

    [Theory]
    [InlineData("a1", "a0", "a1 >= a0")]
    [InlineData("a0", "a0", "a0 >= a0")]
    [InlineData("a00", null, "invalid order key: a00")]
    [InlineData("a00", "a1", "invalid order key: a00")]
    [InlineData("0", "1", "invalid order key head: 0")]
    [InlineData("", null, "invalid order key head: undefined")]
    [InlineData(null, "A00000000000000000000000000", "invalid order key: A00000000000000000000000000")]
    [InlineData("b0", null, "invalid order key: b0")]
    public void GenerateKeyBetweenRejectsInvalidInputWithReferenceMessages(string? a, string? b, string message)
    {
        var error = Assert.Throws<FractionalIndexException>(() => FractionalIndex.GenerateKeyBetween(a, b));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void GenerateNKeysBetweenMatchesReferenceExamples()
    {
        Assert.Equal(["a0", "a1", "a2", "a3", "a4"], FractionalIndex.GenerateNKeysBetween(null, null, 5));
        Assert.Equal(["a5", "a6", "a7", "a8", "a9", "aA", "aB", "aC", "aD", "aE"], FractionalIndex.GenerateNKeysBetween("a4", null, 10));
        Assert.Equal(["Zv", "Zw", "Zx", "Zy", "Zz"], FractionalIndex.GenerateNKeysBetween(null, "a0", 5));
        Assert.Equal(
            ["a04", "a08", "a0G", "a0K", "a0O", "a0V", "a0Z", "a0d", "a0l", "a0t", "a1", "a14", "a18", "a1G", "a1O", "a1V", "a1Z", "a1d", "a1l", "a1t"],
            FractionalIndex.GenerateNKeysBetween("a0", "a2", 20));
        Assert.Empty(FractionalIndex.GenerateNKeysBetween("a0", "a1", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FractionalIndex.GenerateNKeysBetween(null, null, -1));
    }

    [Fact]
    public void MatchesSharedFixtureForKeyBetween()
    {
        var fixture = ContractFixtures.Load("fractional-index.json");
        Assert.Equal(FractionalIndex.Base62Digits, fixture.GetProperty("digits").GetString());
        var cases = fixture.GetProperty("between").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var testCase in cases)
        {
            var a = testCase.GetStringOrNull("a");
            var b = testCase.GetStringOrNull("b");
            Assert.True(
                testCase.GetProperty("expected").GetString() == FractionalIndex.GenerateKeyBetween(a, b),
                $"generateKeyBetween({a ?? "null"}, {b ?? "null"})");
        }
    }

    [Fact]
    public void MatchesSharedFixtureForNKeys()
    {
        var fixture = ContractFixtures.Load("fractional-index.json");
        var cases = fixture.GetProperty("nKeys").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var testCase in cases)
        {
            var expected = testCase.GetProperty("expected").EnumerateArray().Select(key => key.GetString()!).ToArray();
            var actual = FractionalIndex.GenerateNKeysBetween(
                testCase.GetStringOrNull("a"),
                testCase.GetStringOrNull("b"),
                testCase.GetProperty("n").GetInt32());
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void MatchesSharedFixtureForInvalidInput()
    {
        var fixture = ContractFixtures.Load("fractional-index.json");
        var cases = fixture.GetProperty("invalid").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var testCase in cases)
        {
            var a = testCase.GetStringOrNull("a");
            var b = testCase.GetStringOrNull("b");
            var error = Assert.Throws<FractionalIndexException>(() => FractionalIndex.GenerateKeyBetween(a, b));
            if (testCase.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                Assert.Equal(message.GetString(), error.Message);
            }
        }
    }

    [Fact]
    public void LongAppendChainStaysSortedAndValid()
    {
        string? previous = null;
        var keys = new List<string>();
        for (var i = 0; i < 10_000; i++)
        {
            var key = FractionalIndex.GenerateKeyBetween(previous, null);
            Assert.True(previous is null || string.CompareOrdinal(previous, key) < 0);
            Assert.True(FractionalIndex.IsValidKey(key), key);
            keys.Add(key);
            previous = key;
        }

        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        // Appending grows the integer part slowly: 10,000 appends still fit in four characters.
        Assert.True(keys[^1].Length <= 4, keys[^1]);
    }

    [Fact]
    public void LongPrependChainStaysSorted()
    {
        string? next = null;
        var keys = new List<string>();
        for (var i = 0; i < 5_000; i++)
        {
            var key = FractionalIndex.GenerateKeyBetween(null, next);
            Assert.True(next is null || string.CompareOrdinal(key, next) < 0);
            keys.Insert(0, key);
            next = key;
        }

        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    [Fact]
    public void RandomInsertsBetweenNeighboursKeepListOrder()
    {
        var random = new Random(12345);
        var keys = new List<string> { FractionalIndex.GenerateKeyBetween(null, null) };
        for (var i = 0; i < 3_000; i++)
        {
            var index = random.Next(keys.Count + 1);
            var before = index == 0 ? null : keys[index - 1];
            var after = index == keys.Count ? null : keys[index];
            var key = FractionalIndex.GenerateKeyBetween(before, after);
            Assert.True(FractionalIndex.IsValidKey(key), key);
            keys.Insert(index, key);
        }

        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RepeatedInsertsAtTheSameSpotStayStrictlyBetween()
    {
        var low = "a0";
        var high = "a1";
        for (var i = 0; i < 500; i++)
        {
            var key = FractionalIndex.GenerateKeyBetween(low, high);
            Assert.True(string.CompareOrdinal(low, key) < 0 && string.CompareOrdinal(key, high) < 0);
            high = key;
        }
    }

    [Theory]
    [InlineData("a0", true)]
    [InlineData("Zz", true)]
    [InlineData("a0V", true)]
    [InlineData("a00", false)]
    [InlineData("a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a0!", false)]
    [InlineData("A00000000000000000000000000", false)]
    public void IsValidKeyChecksShapeAndCharacterSet(string? key, bool expected) =>
        Assert.Equal(expected, FractionalIndex.IsValidKey(key));
}
