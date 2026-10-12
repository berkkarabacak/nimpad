using System.Text.Json;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Sync;
using NimPad.Core.Tests.Support;
using NimPad.Core.Text;
using Xunit;

namespace NimPad.Core.Tests;

/// <summary>
/// The shared contract fixtures (packages/contracts/fixtures) that the TypeScript suite also runs: the desktop's C#
/// implementation must give the same answer for every case. fractional-index.json and checksum.json are covered next
/// to their implementations (FractionalIndexTests, ContentCanonicalizerTests).
/// </summary>
public class ContractFixtureTests
{
    [Fact]
    public void NoteTextStorability()
    {
        var cases = Cases("note-text.json");
        foreach (var testCase in cases)
        {
            var description = testCase.GetProperty("description").GetString();
            var input = TextOf(testCase, "text");
            Assert.True(testCase.GetProperty("storable").GetBoolean() == ContentCanonicalizer.IsStorable(input), $"storable: {description}");
            Assert.True(testCase.GetProperty("storableText").GetString() == ContentCanonicalizer.ToStorableText(input), $"storableText: {description}");
            Assert.True(testCase.GetProperty("checksum").GetString() == ContentCanonicalizer.Checksum(input), $"checksum: {description}");
            Assert.True(ContentCanonicalizer.IsStorable(ContentCanonicalizer.ToStorableText(input)), $"result is storable: {description}");
        }
    }

    [Fact]
    public void NodeNameNormalization()
    {
        var root = ContractFixtures.Load("node-names.json");
        var whiteSpace = root.GetProperty("whiteSpace").EnumerateArray().Select(item => item.GetString()!.Single()).ToList();
        Assert.Equal(whiteSpace, Enumerable.Range(0, 0x10000).Select(code => (char)code).Where(char.IsWhiteSpace));

        foreach (var testCase in Cases("node-names.json"))
        {
            var description = testCase.GetProperty("description").GetString();
            var input = TextOf(testCase, "text");
            var expected = testCase.GetStringOrNull("normalized");
            string? normalized;
            try
            {
                normalized = NimPad.Core.Domain.NodeNames.Normalize(input);
            }
            catch (InvalidNodeNameException)
            {
                normalized = null;
            }

            Assert.True(expected == normalized, $"normalized: {description}");
            Assert.True(testCase.GetProperty("valid").GetBoolean() == NimPad.Core.Domain.NodeNames.IsStoredForm(input), $"valid: {description}");
        }
    }

    [Fact]
    public void FormatsFromNames()
    {
        var root = ContractFixtures.Load("formats.json");
        Assert.Equal(
            root.GetProperty("formats").EnumerateArray().Select(item => item.GetString()),
            DocumentFormats.All.Select(format => format.ToWireName()));
        foreach (var testCase in Cases("formats.json"))
        {
            var name = testCase.GetProperty("name").GetString();
            Assert.True(testCase.GetProperty("format").GetString() == DocumentFormats.FromName(name).ToWireName(), $"format of '{name}'");
        }
    }

    [Fact]
    public void NoteLinks()
    {
        var root = ContractFixtures.Load("note-links.json");
        Assert.Equal(LinkParser.NotePrefix, root.GetProperty("prefix").GetString());
        foreach (var testCase in Cases("note-links.json"))
        {
            var description = testCase.GetProperty("description").GetString();
            var text = TextOf(testCase, "text");
            var expected = testCase.GetProperty("links").EnumerateArray()
                .Select(link => (link.GetProperty("start").GetInt32(), link.GetProperty("length").GetInt32(), link.GetProperty("target").GetString()))
                .ToList();
            var actual = LinkParser.FindLinks(text).Select(link => (link.Start, link.Length, (string?)link.Target.ToString())).ToList();
            Assert.True(expected.SequenceEqual(actual), $"links: {description}");
            Assert.Equal(expected.Select(link => link.Item3).Distinct(), LinkParser.ExtractTargets(text).Select(id => (string?)id.ToString()));
        }
    }

    [Fact]
    public void PendingOperationsAreValidatedLikeTheContract()
    {
        var root = ContractFixtures.Load("pending-operations.json");
        foreach (var (section, valid, pushable) in new[] { ("valid", true, true), ("invalid", false, false), ("needsRebase", true, false) })
        {
            var examples = root.GetProperty(section).EnumerateArray().ToList();
            Assert.NotEmpty(examples);
            foreach (var example in examples)
            {
                var description = example.GetProperty("description").GetString();
                var json = example.GetProperty("operation").GetRawText();
                var errors = PendingOperationContract.Validate(json);
                Assert.True(valid == (errors.Count == 0), $"{section}: {description}: {string.Join("; ", errors)}");
                var pushErrors = PendingOperationContract.Validate(json, forPush: true);
                Assert.True(pushable == (pushErrors.Count == 0), $"{section} (push): {description}: {string.Join("; ", pushErrors)}");
            }
        }
    }

    private static List<JsonElement> Cases(string file)
    {
        var cases = ContractFixtures.Load(file).GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        return cases;
    }

    // A case's string, or the string built from its utf16 code units (strings that are not well-formed UTF-16 cannot
    // be JSON strings for strict readers, see fixtures/README.md).
    private static string TextOf(JsonElement testCase, string property) =>
        testCase.TryGetProperty("utf16", out var units)
            ? new string(units.EnumerateArray().Select(unit => (char)unit.GetInt32()).ToArray())
            : testCase.GetProperty(property).GetString()!;
}
