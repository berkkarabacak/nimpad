using NimPad.Core.Domain;
using Xunit;

namespace NimPad.Core.Tests.Domain;

public class DocumentFormatTests
{
    [Theory]
    [InlineData("notes.md", DocumentFormat.Markdown)]
    [InlineData("README.MARKDOWN", DocumentFormat.Markdown)]
    [InlineData("config.json", DocumentFormat.Json)]
    [InlineData("App.xaml", DocumentFormat.Xml)]
    [InlineData("NimPad.Core.csproj", DocumentFormat.Xml)]
    [InlineData("icon.svg", DocumentFormat.Xml)]
    [InlineData("data.xml", DocumentFormat.Xml)]
    [InlineData("ci.yml", DocumentFormat.Yaml)]
    [InlineData("compose.yaml", DocumentFormat.Yaml)]
    [InlineData("script.py", DocumentFormat.Python)]
    [InlineData("build.gradle", DocumentFormat.Groovy)]
    [InlineData("pipeline.groovy", DocumentFormat.Groovy)]
    [InlineData("Jenkinsfile", DocumentFormat.Groovy)]
    [InlineData("index.js", DocumentFormat.JavaScript)]
    [InlineData("module.mjs", DocumentFormat.JavaScript)]
    [InlineData("module.cjs", DocumentFormat.JavaScript)]
    [InlineData("server.ts", DocumentFormat.TypeScript)]
    [InlineData("Program.cs", DocumentFormat.CSharp)]
    [InlineData("schema.sql", DocumentFormat.Sql)]
    [InlineData("setup.ps1", DocumentFormat.PowerShell)]
    [InlineData("tools.psm1", DocumentFormat.PowerShell)]
    [InlineData("settings.ini", DocumentFormat.Ini)]
    [InlineData("app.cfg", DocumentFormat.Ini)]
    [InlineData("pyproject.toml", DocumentFormat.Ini)]
    [InlineData("nginx.conf", DocumentFormat.Ini)]
    [InlineData("site.css", DocumentFormat.Css)]
    [InlineData("index.html", DocumentFormat.Html)]
    [InlineData("old.htm", DocumentFormat.Html)]
    [InlineData("Untitled 1", DocumentFormat.Plain)]
    [InlineData("notes.txt", DocumentFormat.Plain)]
    [InlineData("archive.tar.gz", DocumentFormat.Plain)]
    [InlineData("trailing.", DocumentFormat.Plain)]
    [InlineData("", DocumentFormat.Plain)]
    [InlineData(null, DocumentFormat.Plain)]
    public void FromNameDetectsByExtension(string? name, DocumentFormat expected) =>
        Assert.Equal(expected, DocumentFormats.FromName(name));

    [Fact]
    public void WireNamesMatchTheContractList()
    {
        string[] expected =
        [
            "plain", "markdown", "json", "xml", "yaml", "python", "groovy", "javascript", "typescript", "csharp",
            "sql", "powershell", "ini", "css", "html",
        ];

        Assert.Equal(expected, DocumentFormats.All.Select(format => format.ToWireName()));
        foreach (var name in expected)
        {
            Assert.Equal(name, DocumentFormats.Parse(name).ToWireName());
        }

        Assert.False(DocumentFormats.TryParse("Markdown", out _));
        Assert.Throws<FormatException>(() => DocumentFormats.Parse("rust"));
    }
}

public class NodeNamesTests
{
    [Theory]
    [InlineData("  Projects  ", "Projects")]
    [InlineData("Weekly groceries — 日本 😀.md", "Weekly groceries — 日本 😀.md")]
    [InlineData("a", "a")]
    public void NormalizeTrimsValidNames(string input, string expected) => Assert.Equal(expected, NodeNames.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    [InlineData("bell\u0007")]
    [InlineData("del\u007f")]
    public void NormalizeRejectsInvalidNames(string? input)
    {
        var error = Assert.Throws<InvalidNodeNameException>(() => NodeNames.Normalize(input));
        Assert.False(string.IsNullOrWhiteSpace(error.Reason));
        Assert.False(NodeNames.IsValid(input));
    }

    [Fact]
    public void NormalizeRejectsUnpairedSurrogates()
    {
        // Not an InlineData case: test-case serialization would replace the lone surrogate.
        Assert.Throws<InvalidNodeNameException>(() => NodeNames.Normalize("lone \uD800 surrogate"));
        Assert.Throws<InvalidNodeNameException>(() => NodeNames.Normalize("reversed \uDE00\uD83D pair"));
        Assert.Equal("ok \uD83D\uDE00", NodeNames.Normalize("ok \uD83D\uDE00"));
    }

    [Fact]
    public void NormalizeEnforcesTheLengthLimit()
    {
        Assert.Equal(255, NodeNames.Normalize(new string('x', 255)).Length);
        Assert.Throws<InvalidNodeNameException>(() => NodeNames.Normalize(new string('x', 256)));
    }

    [Fact]
    public void NextUntitledNameTakesTheLowestFreeNumber()
    {
        Assert.Equal("Untitled 1", NodeNames.NextUntitledName([]));
        Assert.Equal("Untitled 3", NodeNames.NextUntitledName(["Untitled 1", "untitled 2", "Untitled 4", "Notes"]));
        Assert.Equal("Untitled 1", NodeNames.NextUntitledName(["Untitled 01", "Untitled", "Untitled 2"]));
    }

    [Theory]
    [InlineData("Notes", NodeKind.Folder, new string[0], "Notes")]
    [InlineData("Notes", NodeKind.Folder, new[] { "notes" }, "Notes (copy)")]
    [InlineData("Notes", NodeKind.Folder, new[] { "Notes", "Notes (copy)" }, "Notes (copy 2)")]
    [InlineData("Notes (copy)", NodeKind.Folder, new[] { "Notes", "Notes (copy)" }, "Notes (copy 2)")]
    [InlineData("Notes (copy 2)", NodeKind.Folder, new[] { "Notes (copy 2)", "Notes (copy)" }, "Notes (copy 3)")]
    [InlineData("config.json", NodeKind.Document, new[] { "config.json" }, "config (copy).json")]
    [InlineData("config (copy).json", NodeKind.Document, new[] { "config (copy).json" }, "config (copy 2).json")]
    [InlineData(".gitignore", NodeKind.Document, new[] { ".gitignore" }, ".gitignore (copy)")]
    [InlineData("v1.2", NodeKind.Folder, new[] { "v1.2" }, "v1.2 (copy)")]
    public void CopyNameAddsCopySuffixOnlyOnConflict(string original, NodeKind kind, string[] siblings, string expected) =>
        Assert.Equal(expected, NodeNames.CopyName(original, kind, siblings));

    [Fact]
    public void CopyNameStaysWithinTheLengthLimit()
    {
        var longName = new string('n', 250) + ".md";
        var copy = NodeNames.CopyName(longName, NodeKind.Document, [longName]);

        Assert.True(copy.Length <= NodeNames.MaxLength);
        Assert.EndsWith(" (copy).md", copy);
    }

    [Fact]
    public void CopyNameNeverSplitsASurrogatePair()
    {
        // 244 + 2 (emoji) + 5 = 251: the cut for " (copy)" falls between the emoji's two halves.
        var name = new string('x', 244) + "😀yy.md";
        var copy = NodeNames.CopyName(name, NodeKind.Document, [name]);

        Assert.True(NodeNames.IsStoredForm(copy), copy);
        Assert.Equal(new string('x', 244) + " (copy).md", copy);
    }

    [Fact]
    public void CopyNameShortensTheWholeNameWhenTheExtensionLeavesNoRoom()
    {
        var name = "a." + new string('b', 252);
        var copy = NodeNames.CopyName(name, NodeKind.Document, [name]);
        var second = NodeNames.CopyName(name, NodeKind.Document, [name, copy]);

        Assert.True(NodeNames.IsStoredForm(copy), copy);
        Assert.Equal(NodeNames.MaxLength, copy.Length);
        Assert.EndsWith(" (copy)", copy);
        Assert.True(NodeNames.IsStoredForm(second), second);
        Assert.EndsWith(" (copy 2)", second);
    }

    [Theory]
    [InlineData("Projects", true)]
    [InlineData(" Projects", false)]
    [InlineData("Projects\u00a0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsStoredFormRequiresAValidTrimmedName(string? name, bool expected) =>
        Assert.Equal(expected, NodeNames.IsStoredForm(name));
}
