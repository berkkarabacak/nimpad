using System.Windows;
using NimPad.App.Highlighting;
using NimPad.App.Tests.Themes;
using NimPad.Core.Domain;
using Xunit;

namespace NimPad.App.Tests.Highlighting;

/// <summary>
/// Representative samples run through AvalonEdit's real highlighter: NimPad's own YAML, Groovy and INI definitions,
/// plus spot checks of the themed built-ins.
/// </summary>
public class SampleHighlightingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void YamlHighlightsKeysScalarsCommentsAndAnchors(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            const string Sample = """
                %YAML 1.2
                ---
                # sync settings
                name: nimpad-sync
                version: 0.4.2
                port: 8080
                ratio: -1.5e3
                autosave: true
                missing: null
                title: It's text # trailing
                url: http://example.com/a#b
                quoted: "a #b"
                "quoted key": 'it''s'
                base: &base
                  <<: *base
                typed: !!str 123
                devices: [desktop, "laptop", 42, false]
                editor: {fontSize: 14, wordWrap: off}
                list:
                  - first
                  - key: value
                description: |
                  Block text
                group: ci-${{ github.ref }}
                """;
            var palette = SyntaxPalette.For(dark);
            using var probe = new HighlightingProbe(new HighlightingProvider().Get(DocumentFormat.Yaml, dark)!, Sample);

            Assert.Equal(palette.Literal, probe.ForegroundOf("%YAML 1.2"));
            Assert.Equal(palette.Punctuation, probe.ForegroundOf("---"));
            Assert.Equal(palette.Comment, probe.ForegroundOf("# sync settings"));
            Assert.Equal(palette.Key, probe.ForegroundOf("name"));
            Assert.Equal(palette.Punctuation, probe.ForegroundOf(":"));
            Assert.Equal(palette.String, probe.ForegroundOf("nimpad-sync"));
            Assert.Equal(palette.String, probe.ForegroundOf("0.4.2"));
            Assert.Equal(palette.Number, probe.ForegroundOf("8080"));
            Assert.Equal(palette.Number, probe.ForegroundOf("-1.5e3"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("true"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("null"));
            Assert.Equal(palette.String, probe.ForegroundOf("It's text"));
            Assert.Equal(palette.Comment, probe.ForegroundOf("# trailing"));
            Assert.Equal(palette.String, probe.ForegroundOf("http://example.com/a#b"));
            Assert.Equal(palette.String, probe.ForegroundOf("\"a #b\""));
            Assert.Equal(palette.Key, probe.ForegroundOf("\"quoted key\""));
            Assert.Equal(palette.String, probe.ForegroundOf("'it''s'"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("&base"));
            Assert.Equal(palette.Key, probe.ForegroundOf("<<"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("*base"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("!!str"));
            Assert.Equal(palette.Number, probe.ForegroundOf("123"));
            Assert.Equal(palette.Punctuation, probe.ForegroundOf("["));
            Assert.Equal(palette.String, probe.ForegroundOf("desktop"));
            Assert.Equal(palette.String, probe.ForegroundOf("\"laptop\""));
            Assert.Equal(palette.Number, probe.ForegroundOf("42"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("false"));
            Assert.Equal(palette.Key, probe.ForegroundOf("fontSize"));
            Assert.Equal(palette.Number, probe.ForegroundOf("14"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("off"));
            Assert.Equal(palette.Punctuation, probe.ForegroundOf("-", occurrence: 6)); // "  - first" (after ---, nimpad-sync, -1.5e3)
            Assert.Equal(palette.String, probe.ForegroundOf("first"));
            Assert.Equal(palette.Key, probe.ForegroundOf("key", occurrence: 2)); // "  - key: value"
            Assert.Equal(palette.String, probe.ForegroundOf("value"));
            Assert.Equal(palette.Punctuation, probe.ForegroundOf("|"));
            Assert.Null(probe.ForegroundOf("Block text"));

            // A { inside a plain scalar does not open a flow mapping (GitHub Actions expressions).
            Assert.Equal(palette.String, probe.ForegroundOf("ci-${{ github.ref }}"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroovyHighlightsKeywordsStringsNumbersCommentsAndAnnotations(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            const string Sample = """"
                #!/usr/bin/env groovy
                // line comment
                @CompileStatic
                class Greeter {
                    def greeting = "Hello ${who} and $name.size"
                    def raw = 'single \' quoted'
                    def block = '''triple
                single'''
                    def doc = """triple
                double ${who}"""
                    def n = 0x1F + 3.14d + 1_000L
                    /* block
                       comment */
                    boolean ok = true ?: null
                }
                """";
            var palette = SyntaxPalette.For(dark);
            using var probe = new HighlightingProbe(new HighlightingProvider().Get(DocumentFormat.Groovy, dark)!, Sample);

            Assert.Equal(palette.Comment, probe.ForegroundOf("#!/usr/bin/env groovy"));
            Assert.Equal(palette.Comment, probe.ForegroundOf("// line comment"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("@CompileStatic"));
            Assert.Equal(palette.Key, probe.ForegroundOf("class"));
            Assert.Null(probe.ForegroundOf("Greeter"));
            Assert.Equal(palette.Punctuation, probe.ForegroundOf("{"));
            Assert.Equal(palette.Key, probe.ForegroundOf("def"));
            Assert.Equal(palette.Punctuation, probe.ForegroundOf("="));
            Assert.Equal(palette.String, probe.ForegroundOf("\"Hello "));
            Assert.Equal(palette.Text, probe.ForegroundOf("${who}"));
            Assert.Equal(palette.Text, probe.ForegroundOf("$name.size"));
            Assert.Equal(palette.String, probe.ForegroundOf("'single \\' quoted'"));
            Assert.Equal(palette.String, probe.ForegroundOf("'''triple"));
            Assert.Equal(palette.String, probe.ForegroundOf("single'''"));
            Assert.Equal(palette.String, probe.ForegroundOf("\"\"\"triple"));
            Assert.Equal(palette.Text, probe.ForegroundOf("${who}", occurrence: 2));
            Assert.Equal(palette.Number, probe.ForegroundOf("0x1F"));
            Assert.Equal(palette.Number, probe.ForegroundOf("3.14d"));
            Assert.Equal(palette.Number, probe.ForegroundOf("1_000L"));
            Assert.Equal(palette.Comment, probe.ForegroundOf("/* block"));
            Assert.Equal(palette.Comment, probe.ForegroundOf("comment */"));
            Assert.Equal(palette.Key, probe.ForegroundOf("boolean"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("true"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("null"));
            Assert.Null(probe.FontWeightOf("class"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IniHighlightsSectionsKeysValuesAndComments(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            const string Sample = """
                ; settings
                # also a comment
                root = true

                [editor]
                font_family = Cascadia Mono
                font_size = 14
                word_wrap = off
                version = 1.2.3
                path = C:\notes;D:\more ; inline comment
                name = "NimPad \"x\"" # trailing
                literal = 'C:\raw'
                created = 1979-05-27T07:32:00Z
                ports = [ 8000, "x" ]
                inline = { size = 12, label = "two" }

                [[tool.source]]
                url: https://example.com/a=b
                """;
            var palette = SyntaxPalette.For(dark);
            using var probe = new HighlightingProbe(new HighlightingProvider().Get(DocumentFormat.Ini, dark)!, Sample);

            Assert.Equal(palette.Comment, probe.ForegroundOf("; settings"));
            Assert.Equal(palette.Comment, probe.ForegroundOf("# also a comment"));
            Assert.Equal(palette.Key, probe.ForegroundOf("root"));
            Assert.Equal(palette.Punctuation, probe.ForegroundOf("="));
            Assert.Equal(palette.Literal, probe.ForegroundOf("true"));
            Assert.Equal(palette.Key, probe.ForegroundOf("[editor]"));
            Assert.Equal(FontWeights.Bold, probe.FontWeightOf("[editor]"));
            Assert.Null(probe.FontWeightOf("font_family"));
            Assert.Equal(palette.Key, probe.ForegroundOf("font_family"));
            Assert.Equal(palette.String, probe.ForegroundOf("Cascadia Mono"));
            Assert.Equal(palette.Number, probe.ForegroundOf("14"));
            Assert.Equal(palette.Literal, probe.ForegroundOf("off"));
            Assert.Equal(palette.String, probe.ForegroundOf("1.2.3"));
            Assert.Equal(palette.String, probe.ForegroundOf("C:\\notes;D:\\more"));
            Assert.Equal(palette.Comment, probe.ForegroundOf("; inline comment"));
            Assert.Equal(palette.String, probe.ForegroundOf("\"NimPad \\\"x\\\"\""));
            Assert.Equal(palette.Comment, probe.ForegroundOf("# trailing"));
            Assert.Equal(palette.String, probe.ForegroundOf("'C:\\raw'"));
            Assert.Equal(palette.Number, probe.ForegroundOf("1979-05-27T07:32:00Z"));
            Assert.Equal(palette.Number, probe.ForegroundOf("8000"));
            Assert.Equal(palette.String, probe.ForegroundOf("\"x\""));
            Assert.Equal(palette.Key, probe.ForegroundOf("size", occurrence: 2)); // inline table key (1 is font_size)
            Assert.Equal(palette.Number, probe.ForegroundOf("12"));
            Assert.Equal(palette.String, probe.ForegroundOf("\"two\""));
            Assert.Equal(palette.Key, probe.ForegroundOf("[[tool.source]]"));
            Assert.Equal(palette.Key, probe.ForegroundOf("url"));
            Assert.Equal(palette.String, probe.ForegroundOf("https://example.com/a=b"));
        });
    }

    [Fact]
    public void JsonLooksLikeTheDeck()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            // Deck page 6: keys s-key, strings s-str, numbers s-num, true/false s-lit, braces and commas s-punc.
            const string Sample = """
                {
                  "name": "nimpad-sync",
                  "autosave": true,
                  "debounceMs": 400,
                  "devices": ["desktop", "laptop"],
                  "missing": null
                }
                """;
            var palette = SyntaxPalette.Dark;
            using var probe = new HighlightingProbe(new HighlightingProvider().Get(DocumentFormat.Json, dark: true)!, Sample);

            Assert.Equal(palette.Punctuation, probe.ForegroundOf("{"));
            Assert.Equal(palette.Key, probe.ForegroundOf("\"name\""));
            Assert.Equal(palette.String, probe.ForegroundOf("\"nimpad-sync\""));
            Assert.Equal(palette.Literal, probe.ForegroundOf("true"));
            Assert.Equal(palette.Number, probe.ForegroundOf("400"));
            Assert.Equal(palette.String, probe.ForegroundOf("\"laptop\""));
            Assert.Equal(palette.Literal, probe.ForegroundOf("null"));
            Assert.Equal(palette.Punctuation, probe.ForegroundOf(","));
            Assert.Null(probe.FontWeightOf("true"));
        });
    }

    [Fact]
    public void ThemedBuiltInsUseTheTokensInContext()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var provider = new HighlightingProvider();
            var palette = SyntaxPalette.Light;

            // C#: TODO's inline bold red is cleared, so it reads as part of the comment; keywords are not bold.
            using (var csharp = new HighlightingProbe(provider.Get(DocumentFormat.CSharp, dark: false)!, "// TODO fix\npublic int x = 42; /// <summary>Doc</summary>\n"))
            {
                Assert.Equal(palette.Comment, csharp.ForegroundOf("TODO"));
                Assert.Null(csharp.FontWeightOf("TODO"));
                Assert.Equal(palette.Key, csharp.ForegroundOf("public"));
                Assert.Null(csharp.FontWeightOf("public"));
                Assert.Equal(palette.Number, csharp.ForegroundOf("42"));
                Assert.Equal(palette.Comment, csharp.ForegroundOf("<summary>"));
            }

            // Markdown: bold heading, italic emphasis in the text color, accent links, C# in indented code.
            using (var markdown = new HighlightingProbe(provider.Get(DocumentFormat.Markdown, dark: false)!, "# Title\nSome *emphasis* and [a link](https://x.y)\n\n    var answer = 42;\n"))
            {
                Assert.Equal(palette.Key, markdown.ForegroundOf("# Title"));
                Assert.Equal(FontWeights.Bold, markdown.FontWeightOf("# Title"));
                Assert.Null(markdown.ForegroundOf("*emphasis*"));
                Assert.Equal(FontStyles.Italic, markdown.FontStyleOf("*emphasis*"));
                Assert.Equal(palette.Accent, markdown.ForegroundOf("[a link](https://x.y)"));
                Assert.Equal(palette.Key, markdown.ForegroundOf("var"));
                Assert.Equal(palette.Number, markdown.ForegroundOf("42"));
            }

            // Python: NimPad's copy colors numbers (AvalonEdit's own NumberLiteral rule never matches plain numbers).
            using (var python = new HighlightingProbe(provider.Get(DocumentFormat.Python, dark: false)!, "def f(x):\n    return x + 42 * 3.14 + 0x1F  # note\n"))
            {
                Assert.Equal(palette.Key, python.ForegroundOf("def"));
                Assert.Equal(palette.Number, python.ForegroundOf("42"));
                Assert.Equal(palette.Number, python.ForegroundOf("3.14"));
                Assert.Equal(palette.Number, python.ForegroundOf("0x1F"));
                Assert.Equal(palette.Comment, python.ForegroundOf("# note"));
            }

            // HTML imports the themed JavaScript.
            using (var html = new HighlightingProbe(provider.Get(DocumentFormat.Html, dark: false)!, "<!-- note -->\n<script>\nvar ok = true;\n</script>\n"))
            {
                Assert.Equal(palette.Comment, html.ForegroundOf("<!-- note -->"));
                Assert.Equal(palette.Key, html.ForegroundOf("<script>"));
                Assert.Equal(palette.Key, html.ForegroundOf("var"));
                Assert.Equal(palette.Literal, html.ForegroundOf("true"));
            }
        });
    }
}
