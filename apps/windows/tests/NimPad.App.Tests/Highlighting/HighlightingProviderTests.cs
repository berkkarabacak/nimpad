using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using NimPad.App.Highlighting;
using NimPad.App.Tests.Themes;
using NimPad.App.Themes;
using NimPad.Core.Domain;
using Xunit;

namespace NimPad.App.Tests.Highlighting;

/// <summary>Themed copies of every definition: loading, caching, isolation, and the "only palette colors" guarantee.</summary>
public class HighlightingProviderTests
{
    /// <summary>The AvalonEdit definition each format is themed from.</summary>
    private static readonly IReadOnlyDictionary<DocumentFormat, string> ExpectedNames = new Dictionary<DocumentFormat, string>
    {
        [DocumentFormat.Markdown] = "MarkDown",
        [DocumentFormat.Json] = "Json",
        [DocumentFormat.Xml] = "XML",
        [DocumentFormat.Yaml] = "YAML",
        [DocumentFormat.Python] = "Python",
        [DocumentFormat.Groovy] = "Groovy",
        [DocumentFormat.JavaScript] = "JavaScript",
        [DocumentFormat.TypeScript] = "JavaScript",
        [DocumentFormat.CSharp] = "C#",
        [DocumentFormat.Sql] = "TSQL",
        [DocumentFormat.PowerShell] = "PowerShell",
        [DocumentFormat.Ini] = "INI",
        [DocumentFormat.Css] = "CSS",
        [DocumentFormat.Html] = "HTML",
    };

    /// <summary>(definition, color) pairs allowed to be bold or italic: headings and Markdown emphasis only.</summary>
    private static readonly HashSet<string> Bold = ["MarkDown/Heading", "MarkDown/StrongEmphasis", "INI/Section"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryFormatLoadsOnceAndPlainTextHasNone(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var provider = new HighlightingProvider();
            foreach (var format in DocumentFormats.All)
            {
                var definition = provider.Get(format, dark);
                if (format == DocumentFormat.Plain)
                {
                    Assert.Null(definition);
                    continue;
                }

                Assert.NotNull(definition);
                Assert.Equal(ExpectedNames[format], definition.Name);
                Assert.NotNull(definition.MainRuleSet);
                Assert.Same(definition, provider.Get(format, dark));
            }

            Assert.Equal(DocumentFormats.All.Count - 1, ExpectedNames.Count);
            Assert.Same(provider.Get(DocumentFormat.JavaScript, dark), provider.Get(DocumentFormat.TypeScript, dark));
        });
    }

    [Fact]
    public void DefaultProviderIsOneSharedInstance()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            Assert.Same(HighlightingProvider.Default, HighlightingProvider.Default);
            Assert.Same(SyntaxPalette.Light, HighlightingProvider.Default.GetPalette(dark: false));
            Assert.Same(SyntaxPalette.Dark, HighlightingProvider.Default.GetPalette(dark: true));
            Assert.Same(
                HighlightingProvider.Default.Get(DocumentFormat.Json, dark: true),
                HighlightingProvider.Default.Get(DocumentFormat.Json, dark: true));
        });
    }

    [Fact]
    public void ThemingNeverTouchesTheBuiltInDefinitions()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var builtInNames = new[] { "XmlDoc", "C#", "JavaScript", "HTML", "CSS", "Json", "XML", "MarkDown", "Python", "PowerShell", "TSQL" };
            var builtIns = builtInNames.ToDictionary(name => name, name =>
            {
                var definition = HighlightingManager.Instance.GetDefinition(name);
                Assert.True(definition is not null, $"AvalonEdit has no built-in definition named '{name}'.");
                return definition;
            });
            var before = builtIns.ToDictionary(pair => pair.Key, pair => Describe(pair.Value));

            var provider = new HighlightingProvider();
            var themed = DocumentFormats.All
                .SelectMany(format => new[] { provider.Get(format, dark: false), provider.Get(format, dark: true) })
                .OfType<IHighlightingDefinition>()
                .ToList();

            // The shared definitions still have their own colors (unfrozen) and rules, unchanged.
            foreach (var (name, definition) in builtIns)
            {
                Assert.Equal(before[name], Describe(definition));
                Assert.DoesNotContain(themed, copy => ReferenceEquals(copy, definition));
            }

            // No color object is shared between a built-in and a themed copy.
            var builtInColors = builtIns.Values.SelectMany(HighlightingProvider.EnumerateColors).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var definition in themed)
            {
                foreach (var color in HighlightingProvider.EnumerateColors(definition))
                {
                    Assert.False(builtInColors.Contains(color), $"{definition.Name} shares the built-in color {color}.");
                }
            }
        });
    }

    [Fact]
    public void LightAndDarkCopiesAreSeparateAndUseTheirOwnTokens()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var provider = new HighlightingProvider();
            foreach (var format in ExpectedNames.Keys)
            {
                var light = provider.Get(format, dark: false)!;
                var dark = provider.Get(format, dark: true)!;
                Assert.NotSame(light, dark);
                var lightColors = HighlightingProvider.EnumerateColors(light).ToHashSet(ReferenceEqualityComparer.Instance);
                Assert.DoesNotContain(HighlightingProvider.EnumerateColors(dark), color => lightColors.Contains(color));
            }

            var lightJson = provider.Get(DocumentFormat.Json, dark: false)!;
            var darkJson = provider.Get(DocumentFormat.Json, dark: true)!;
            Assert.Equal(SyntaxPalette.Light.Key, Foreground(lightJson, "FieldName"));
            Assert.Equal(SyntaxPalette.Dark.Key, Foreground(darkJson, "FieldName"));
            Assert.NotEqual(Foreground(lightJson, "FieldName"), Foreground(darkJson, "FieldName"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThemedDefinitionsPaintOnlyWithPaletteColors(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var provider = new HighlightingProvider();
            var palette = provider.GetPalette(dark);
            foreach (var format in ExpectedNames.Keys)
            {
                var definition = provider.Get(format, dark)!;
                foreach (var color in HighlightingProvider.EnumerateColors(definition))
                {
                    var where = $"{definition.Name}/{color.Name ?? "(inline)"}";
                    if (color.Foreground is { } foreground)
                    {
                        var value = foreground.GetColor(null);
                        Assert.True(value is { } c && palette.Contains(c), $"{where} paints with {value}, which is not a {(dark ? "dark" : "light")} palette color.");
                    }

                    Assert.Null(color.Background);
                    Assert.Null(color.FontFamily);
                    Assert.Null(color.FontSize);
                    Assert.Null(color.Underline);
                    Assert.Null(color.Strikethrough);
                    Assert.True(color.IsFrozen, $"{where} is not frozen.");
                    if (color.FontWeight is { } weight)
                    {
                        Assert.Equal(FontWeights.Bold, weight);
                        Assert.Contains(where, Bold);
                    }

                    if (color.FontStyle is { } style)
                    {
                        Assert.Equal(FontStyles.Italic, style);
                        Assert.Equal("MarkDown/Emphasis", where);
                    }
                }
            }
        });
    }

    [Fact]
    public void MappingTablesNameEveryColorOfEveryDefinition()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            Assert.Empty(HighlightingProvider.GetUnmappedColorNames());
            Assert.Empty(HighlightingProvider.GetUnusedMappingNames());
        });
    }

    [Fact]
    public void MappingFollowsTheDesignTokens()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var provider = new HighlightingProvider();
            var palette = provider.GetPalette(dark: false);

            var json = provider.Get(DocumentFormat.Json, dark: false)!;
            Assert.Equal(palette.Key, Foreground(json, "FieldName"));
            Assert.Equal(palette.String, Foreground(json, "String"));
            Assert.Equal(palette.Number, Foreground(json, "Number"));
            Assert.Equal(palette.Literal, Foreground(json, "Bool"));
            Assert.Equal(palette.Literal, Foreground(json, "Null"));
            Assert.Equal(palette.Punctuation, Foreground(json, "Punctuation"));

            var xml = provider.Get(DocumentFormat.Xml, dark: false)!;
            Assert.Equal(palette.Comment, Foreground(xml, "Comment"));
            Assert.Equal(palette.Key, Foreground(xml, "XmlTag"));
            Assert.Equal(palette.Literal, Foreground(xml, "AttributeName"));
            Assert.Equal(palette.String, Foreground(xml, "AttributeValue"));
            Assert.Equal(palette.Number, Foreground(xml, "Entity"));

            var markdown = provider.Get(DocumentFormat.Markdown, dark: false)!;
            Assert.Equal(palette.Key, Foreground(markdown, "Heading"));
            Assert.Equal(FontWeights.Bold, markdown.GetNamedColor("Heading").FontWeight);
            Assert.Null(markdown.GetNamedColor("Emphasis").Foreground);
            Assert.Equal(FontStyles.Italic, markdown.GetNamedColor("Emphasis").FontStyle);
            Assert.Null(markdown.GetNamedColor("StrongEmphasis").Foreground);
            Assert.Equal(FontWeights.Bold, markdown.GetNamedColor("StrongEmphasis").FontWeight);
            Assert.Equal(palette.String, Foreground(markdown, "Code"));
            Assert.Equal(palette.Comment, Foreground(markdown, "BlockQuote"));
            Assert.Equal(palette.Accent, Foreground(markdown, "Link"));
            Assert.Equal(palette.Accent, Foreground(markdown, "Image"));
            Assert.Equal(palette.Punctuation, Foreground(markdown, "LineBreak"));
            Assert.Null(markdown.GetNamedColor("LineBreak").Background);

            // Code keywords are never bold (the built-in C# keywords are).
            var csharp = provider.Get(DocumentFormat.CSharp, dark: false)!;
            Assert.Equal(palette.Key, Foreground(csharp, "Keywords"));
            Assert.Null(csharp.GetNamedColor("Keywords").FontWeight);
            Assert.Equal(palette.Comment, Foreground(csharp, "Comment"));
            Assert.Equal(palette.String, Foreground(csharp, "Char"));
            Assert.Equal(palette.Literal, Foreground(csharp, "TrueFalse"));
            Assert.Equal(palette.Literal, Foreground(csharp, "Preprocessor"));
            Assert.Equal(palette.Text, Foreground(csharp, "MethodCall"));
        });
    }

    [Fact]
    public void UnmappedNamesFallBackToTextAndInlineColorsAreCleared()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            const string Xshd = """
                <SyntaxDefinition name="Test" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
                    <Color name="Mapped" foreground="Red" background="Yellow" fontWeight="bold" underline="true" />
                    <Color name="Unknown" foreground="Green" fontStyle="italic" />
                    <RuleSet>
                        <Keywords color="Mapped"><Word>alpha</Word></Keywords>
                        <Keywords color="Unknown"><Word>beta</Word></Keywords>
                        <Keywords foreground="Blue" fontWeight="bold"><Word>gamma</Word></Keywords>
                    </RuleSet>
                </SyntaxDefinition>
                """;
            XshdSyntaxDefinition xshd;
            using (var reader = XmlReader.Create(new StringReader(Xshd)))
            {
                xshd = HighlightingLoader.LoadXshd(reader);
            }

            var palette = SyntaxPalette.Light;
            var styles = new Dictionary<string, SyntaxStyle> { ["Mapped"] = new(SyntaxRole.Number) };
            var definition = HighlightingProvider.Build(xshd, styles, palette, resolver: null);

            var mapped = definition.GetNamedColor("Mapped");
            Assert.Equal(palette.Number, mapped.Foreground.GetColor(null));
            Assert.Null(mapped.Background);
            Assert.Null(mapped.FontWeight);
            Assert.Null(mapped.Underline);

            var unknown = definition.GetNamedColor("Unknown");
            Assert.Equal(palette.Text, unknown.Foreground.GetColor(null));
            Assert.Null(unknown.FontStyle);

            var inline = Assert.Single(HighlightingProvider.EnumerateColors(definition), color => color.Name is null);
            Assert.Null(inline.Foreground);
            Assert.Null(inline.FontWeight);
            Assert.All(HighlightingProvider.EnumerateColors(definition), color => Assert.True(color.IsFrozen));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaletteIsReadFromTheTokenDictionaries(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            ResourceDictionary tokens = dark ? new TokensDark() : new TokensLight();
            var palette = SyntaxPalette.For(dark);
            Assert.Equal(Token(tokens, "SyntaxKeyColor"), palette.Key);
            Assert.Equal(Token(tokens, "SyntaxStringColor"), palette.String);
            Assert.Equal(Token(tokens, "SyntaxNumberColor"), palette.Number);
            Assert.Equal(Token(tokens, "SyntaxLiteralColor"), palette.Literal);
            Assert.Equal(Token(tokens, "SyntaxPunctuationColor"), palette.Punctuation);
            Assert.Equal(Token(tokens, "SyntaxCommentColor"), palette.Comment);
            Assert.Equal(Token(tokens, "AccentColor"), palette.Accent);
            Assert.Equal(Token(tokens, "TextColor"), palette.Text);
            Assert.Equal(8, palette.AllColors.Count);

            // ... which hold the values of docs/design/tokens.md.
            var (light, darkValue) = TokenTests.Expected["SyntaxKey"];
            Assert.Equal(TokenTests.ParseToken(dark ? darkValue : light), palette.Key);

            // Any dictionary that merges the tokens works too (e.g. Application.Current.Resources).
            var merged = new ResourceDictionary();
            merged.MergedDictionaries.Add(tokens);
            Assert.Equal(palette.AllColors, SyntaxPalette.FromResources(merged).AllColors);
        });
    }

    [Fact]
    public void PaletteRequiresEveryToken()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var tokens = new TokensLight();
            var partial = new ResourceDictionary();
            foreach (var key in tokens.Keys.Cast<object>().Where(key => !Equals(key, "SyntaxCommentColor")))
            {
                partial[key] = tokens[key];
            }

            var error = Assert.Throws<InvalidOperationException>(() => SyntaxPalette.FromResources(partial));
            Assert.Contains("SyntaxCommentColor", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ConcurrentCallersShareOneDefinitionPerFormatAndTheme()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var provider = new HighlightingProvider(SyntaxPalette.Light, SyntaxPalette.Dark);
            var formats = ExpectedNames.Keys.ToArray();
            var results = new ConcurrentBag<(DocumentFormat Format, bool Dark, IHighlightingDefinition? Definition)>();
            Parallel.For(0, formats.Length * 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var format = formats[i % formats.Length];
                var dark = i / formats.Length % 2 == 1;
                results.Add((format, dark, provider.Get(format, dark)));
            });

            foreach (var group in results.GroupBy(result => (result.Format, result.Dark)))
            {
                var first = group.First().Definition;
                Assert.NotNull(first);
                Assert.All(group, result => Assert.Same(first, result.Definition));
                Assert.Same(first, provider.Get(group.Key.Format, group.Key.Dark));
            }
        });
    }

    private static Color? Foreground(IHighlightingDefinition definition, string colorName)
    {
        var color = definition.GetNamedColor(colorName);
        Assert.True(color is not null, $"{definition.Name} has no color '{colorName}'.");
        return color.Foreground?.GetColor(null);
    }

    private static Color Token(ResourceDictionary tokens, string key) => Assert.IsType<Color>(tokens[key]);

    /// <summary>Every color (named and inline) and every rule pattern of a definition as text, in a stable order.</summary>
    private static string Describe(IHighlightingDefinition definition)
    {
        var colors = HighlightingProvider.EnumerateColors(definition)
            .Select(color => $"{color.Name}: {color.ToCss()} family={color.FontFamily} size={color.FontSize} frozen={color.IsFrozen}");
        var patterns = new List<string>();
        CollectPatterns(definition.MainRuleSet, patterns, new HashSet<HighlightingRuleSet>(ReferenceEqualityComparer.Instance));
        return string.Join("\n", colors.Concat(patterns).Order(StringComparer.Ordinal));
    }

    private static void CollectPatterns(HighlightingRuleSet? ruleSet, List<string> patterns, HashSet<HighlightingRuleSet> visited)
    {
        if (ruleSet is null || !visited.Add(ruleSet))
        {
            return;
        }

        patterns.AddRange(ruleSet.Rules.Select(rule => $"rule {rule.Regex}"));
        foreach (var span in ruleSet.Spans)
        {
            patterns.Add($"span {span.StartExpression} .. {span.EndExpression}");
            CollectPatterns(span.RuleSet, patterns, visited);
        }
    }
}
