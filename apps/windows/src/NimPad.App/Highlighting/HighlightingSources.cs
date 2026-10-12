using System.Collections.Frozen;
using System.IO;
using System.Reflection;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using NimPad.Core.Domain;

namespace NimPad.App.Highlighting;

/// <summary>The design token a highlighting color is mapped to.</summary>
internal enum SyntaxRole
{
    /// <summary><c>text</c>: no syntax color (identifiers, method calls, anything unmapped).</summary>
    Text,

    /// <summary><c>s-key</c>: keys, properties, keywords, types, tags, selectors.</summary>
    Key,

    /// <summary><c>s-str</c>: strings and characters.</summary>
    String,

    /// <summary><c>s-num</c>: numbers.</summary>
    Number,

    /// <summary><c>s-lit</c>: literals, constants, booleans, null, preprocessor directives, attributes.</summary>
    Literal,

    /// <summary><c>s-punc</c>: punctuation and operators.</summary>
    Punctuation,

    /// <summary><c>s-com</c>: comments.</summary>
    Comment,

    /// <summary><c>accent</c>: Markdown links and images.</summary>
    Accent,

    /// <summary>No foreground: the text keeps the surrounding color (Markdown emphasis changes only the style).</summary>
    Inherit,
}

/// <summary>How one named highlighting color is themed: its token and whether it is bold or italic.</summary>
internal readonly record struct SyntaxStyle(SyntaxRole Role, bool Bold = false, bool Italic = false)
{
    /// <summary>What an unmapped color name becomes.</summary>
    public static SyntaxStyle Fallback { get; } = new(SyntaxRole.Text);
}

/// <summary>
/// One highlighting definition NimPad themes: an AvalonEdit built-in (re-loaded from the AvalonEdit assembly's
/// embedded XSHD, never the shared instance) or one of NimPad's own XSHD files, with the mapping table from each of
/// its named colors to a design token.
/// </summary>
internal sealed class HighlightingSource
{
    public HighlightingSource(
        string name,
        Assembly assembly,
        string resourceName,
        bool isBuiltIn,
        IReadOnlyDictionary<string, SyntaxStyle> styles,
        Action<XshdSyntaxDefinition>? fixup = null)
    {
        Name = name;
        Assembly = assembly;
        ResourceName = resourceName;
        IsBuiltIn = isBuiltIn;
        Styles = styles.ToFrozenDictionary(StringComparer.Ordinal);
        Fixup = fixup;
    }

    /// <summary>
    /// The definition name: the XSHD <c>SyntaxDefinition name</c>, the name registered in
    /// <see cref="HighlightingManager.Instance"/> for built-ins, and the name other definitions import it by ("C#/").
    /// </summary>
    public string Name { get; }

    /// <summary>The assembly that embeds the XSHD.</summary>
    public Assembly Assembly { get; }

    /// <summary>The XSHD's manifest resource name.</summary>
    public string ResourceName { get; }

    /// <summary>True for AvalonEdit's built-in definitions.</summary>
    public bool IsBuiltIn { get; }

    /// <summary>Named color → token. Names that are missing fall back to <see cref="SyntaxStyle.Fallback"/>.</summary>
    public FrozenDictionary<string, SyntaxStyle> Styles { get; }

    /// <summary>Corrects a defect of the XSHD in NimPad's freshly parsed copy (never the shared built-in), or null.</summary>
    public Action<XshdSyntaxDefinition>? Fixup { get; }

    /// <summary>Opens a fresh stream over the XSHD.</summary>
    public Stream OpenXshd() =>
        Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The highlighting resource '{ResourceName}' was not found in {Assembly.GetName().Name}.");

    public override string ToString() => Name;
}

/// <summary>The definitions NimPad themes, their mapping tables, and the format → definition map.</summary>
internal static class HighlightingSources
{
    private const string BuiltInPrefix = "ICSharpCode.AvalonEdit.Highlighting.Resources.";
    private const string OwnPrefix = "NimPad.App.Highlighting.Definitions.";

    // Python numbers: 0x1F, 0o17, 0b101, 1_000, 3.14, .5, 1e-9, 2j.
    private const string PythonNumber =
        @"\b0[xX][0-9a-fA-F_]+\b|\b0[oO][0-7_]+\b|\b0[bB][01_]+\b|(?:\b[0-9][0-9_]*(?:\.[0-9_]*)?|\.[0-9][0-9_]*)(?:[eE][-+]?[0-9_]+)?[jJ]?\b";

    private static readonly SyntaxStyle Text = new(SyntaxRole.Text);
    private static readonly SyntaxStyle Key = new(SyntaxRole.Key);
    private static readonly SyntaxStyle String = new(SyntaxRole.String);
    private static readonly SyntaxStyle Number = new(SyntaxRole.Number);
    private static readonly SyntaxStyle Literal = new(SyntaxRole.Literal);
    private static readonly SyntaxStyle Punctuation = new(SyntaxRole.Punctuation);
    private static readonly SyntaxStyle Comment = new(SyntaxRole.Comment);
    private static readonly SyntaxStyle Accent = new(SyntaxRole.Accent);

    private static readonly Assembly AvalonEdit = typeof(HighlightingManager).Assembly;
    private static readonly Assembly Own = typeof(HighlightingSources).Assembly;

    /// <summary>Every themed definition, including <c>XmlDoc</c>, which only C# imports.</summary>
    public static IReadOnlyList<HighlightingSource> All { get; } =
    [
        BuiltIn("XmlDoc", "XmlDoc.xshd", new()
        {
            // C# /// doc comments: all comment-colored.
            ["XmlString"] = Comment,
            ["DocComment"] = Comment,
            ["XmlPunctuation"] = Comment,
            ["KnownDocTags"] = Comment,
        }),
        BuiltIn("C#", "CSharp-Mode.xshd", new()
        {
            ["Comment"] = Comment,
            ["String"] = String,
            ["StringInterpolation"] = Text,
            ["Char"] = String,
            ["Preprocessor"] = Literal,
            ["Punctuation"] = Punctuation,
            ["ValueTypeKeywords"] = Key,
            ["ReferenceTypeKeywords"] = Key,
            ["MethodCall"] = Text,
            ["NumberLiteral"] = Number,
            ["ThisOrBaseReference"] = Key,
            ["NullOrValueKeywords"] = Literal,
            ["Keywords"] = Key,
            ["GotoKeywords"] = Key,
            ["ContextKeywords"] = Key,
            ["ExceptionKeywords"] = Key,
            ["CheckedKeyword"] = Key,
            ["UnsafeKeywords"] = Key,
            ["OperatorKeywords"] = Key,
            ["ParameterModifiers"] = Key,
            ["Modifiers"] = Key,
            ["Visibility"] = Key,
            ["NamespaceKeywords"] = Key,
            ["GetSetAddRemove"] = Key,
            ["TrueFalse"] = Literal,
            ["TypeKeywords"] = Key,
            ["SemanticKeywords"] = Key,
        }),
        BuiltIn("JavaScript", "JavaScript-Mode.xshd", new()
        {
            ["Digits"] = Number,
            ["Comment"] = Comment,
            ["String"] = String,
            ["Character"] = String,
            ["Regex"] = String,
            ["JavaScriptKeyWords"] = Key,
            ["JavaScriptIntrinsics"] = Key,
            ["JavaScriptLiterals"] = Literal,
            ["JavaScriptGlobalFunctions"] = Text,
        }),
        BuiltIn("HTML", "HTML-Mode.xshd", new()
        {
            ["Comment"] = Comment,
            ["Digits"] = Number,
            ["ScriptTag"] = Key,
            ["JavaScriptTag"] = Key,
            ["JScriptTag"] = Key,
            ["VBScriptTag"] = Key,
            ["UnknownScriptTag"] = Key,
            ["HtmlTag"] = Key,
            ["Tags"] = Key,
            ["Attributes"] = Literal,
            ["Slash"] = Punctuation,
            ["Assignment"] = Punctuation,
            ["String"] = String,
            ["EntityReference"] = Number,
            ["Entities"] = Number,
            ["UnknownAttribute"] = Literal,
        }),
        BuiltIn("CSS", "CSS-Mode.xshd", new()
        {
            ["Comment"] = Comment,
            ["String"] = String,
            ["Selector"] = Key,
            ["Class"] = Key,
            ["Property"] = Key,
            ["Value"] = Text,
            ["CurlyBraces"] = Punctuation,
            ["Colon"] = Punctuation,
        }),
        BuiltIn("Json", "Json.xshd", new()
        {
            ["Bool"] = Literal,
            ["Number"] = Number,
            ["String"] = String,
            ["Null"] = Literal,
            ["FieldName"] = Key,
            ["Punctuation"] = Punctuation,
        }),
        BuiltIn("XML", "XML-Mode.xshd", new()
        {
            ["Comment"] = Comment,
            ["CData"] = String,
            ["DocType"] = Literal,
            ["XmlDeclaration"] = Literal,
            ["XmlTag"] = Key,
            ["AttributeName"] = Literal,
            ["AttributeValue"] = String,
            ["Entity"] = Number,
            // "&" without ";" is usually plain text (a & b): keep the surrounding color.
            ["BrokenEntity"] = new SyntaxStyle(SyntaxRole.Inherit),
        }),
        BuiltIn("MarkDown", "MarkDown-Mode.xshd", new()
        {
            ["Heading"] = new SyntaxStyle(SyntaxRole.Key, Bold: true),
            ["Emphasis"] = new SyntaxStyle(SyntaxRole.Inherit, Italic: true),
            ["StrongEmphasis"] = new SyntaxStyle(SyntaxRole.Inherit, Bold: true),
            ["Code"] = String,
            ["BlockQuote"] = Comment,
            ["Link"] = Accent,
            ["Image"] = Accent,
            ["LineBreak"] = Punctuation,
        }),
        BuiltIn(
            "Python",
            "Python-Mode.xshd",
            new()
            {
                ["Comment"] = Comment,
                ["String"] = String,
                ["MethodCall"] = Text,
                ["NumberLiteral"] = Number,
                ["Keywords"] = Key,
            },
            FixPythonNumbers),
        BuiltIn("PowerShell", "PowerShell.xshd", new()
        {
            ["Comment"] = Comment,
            ["String"] = String,
            ["Char"] = String,
            ["Punctuation"] = Punctuation,
            ["NumberLiteral"] = Number,
            ["Keywords"] = Key,
            ["Variable"] = Text,
            ["ExceptionKeywords"] = Key,
            ["GotoKeywords"] = Key,
            ["ReferenceTypes"] = Key,
            ["Command"] = Text,
            ["Operators"] = Punctuation,
        }),
        BuiltIn("TSQL", "TSQL-Mode.xshd", new()
        {
            ["Comment"] = Comment,
            ["Char"] = String,
            ["Keywords"] = Key,
        }),
        Custom("YAML", "YAML.xshd", new()
        {
            ["Comment"] = Comment,
            ["Key"] = Key,
            ["String"] = String,
            ["Number"] = Number,
            ["Literal"] = Literal,
            ["Anchor"] = Literal,
            ["Tag"] = Literal,
            ["Directive"] = Literal,
            ["DocumentMarker"] = Punctuation,
            ["Punctuation"] = Punctuation,
        }),
        Custom("Groovy", "Groovy.xshd", new()
        {
            ["Comment"] = Comment,
            ["String"] = String,
            ["Interpolation"] = Text,
            ["Number"] = Number,
            ["Keyword"] = Key,
            ["Literal"] = Literal,
            ["Annotation"] = Literal,
            ["Punctuation"] = Punctuation,
        }),
        Custom("INI", "INI.xshd", new()
        {
            ["Comment"] = Comment,
            // Section headers are the headings of the file: bold, like Markdown headings.
            ["Section"] = new SyntaxStyle(SyntaxRole.Key, Bold: true),
            ["Key"] = Key,
            ["String"] = String,
            ["Number"] = Number,
            ["Literal"] = Literal,
            ["Punctuation"] = Punctuation,
        }),
    ];

    private static readonly FrozenDictionary<string, HighlightingSource> ByName =
        All.ToFrozenDictionary(source => source.Name, StringComparer.Ordinal);

    private static readonly FrozenDictionary<DocumentFormat, HighlightingSource> ByFormat =
        new Dictionary<DocumentFormat, HighlightingSource>
        {
            [DocumentFormat.Markdown] = ByName["MarkDown"],
            [DocumentFormat.Json] = ByName["Json"],
            [DocumentFormat.Xml] = ByName["XML"],
            [DocumentFormat.Yaml] = ByName["YAML"],
            [DocumentFormat.Python] = ByName["Python"],
            [DocumentFormat.Groovy] = ByName["Groovy"],
            [DocumentFormat.JavaScript] = ByName["JavaScript"],
            // AvalonEdit has no TypeScript definition; JavaScript's keywords, strings and comments cover most of it.
            [DocumentFormat.TypeScript] = ByName["JavaScript"],
            [DocumentFormat.CSharp] = ByName["C#"],
            [DocumentFormat.Sql] = ByName["TSQL"],
            [DocumentFormat.PowerShell] = ByName["PowerShell"],
            [DocumentFormat.Ini] = ByName["INI"],
            [DocumentFormat.Css] = ByName["CSS"],
            [DocumentFormat.Html] = ByName["HTML"],
        }.ToFrozenDictionary();

    /// <summary>The definition for a format; null for <see cref="DocumentFormat.Plain"/> (one color, no highlighting).</summary>
    public static HighlightingSource? ForFormat(DocumentFormat format) => ByFormat.GetValueOrDefault(format);

    /// <summary>The definition with this <see cref="HighlightingSource.Name"/>, or null.</summary>
    public static HighlightingSource? FindByName(string name) => ByName.GetValueOrDefault(name);

    private static HighlightingSource BuiltIn(string name, string file, Dictionary<string, SyntaxStyle> styles, Action<XshdSyntaxDefinition>? fixup = null) =>
        new(name, AvalonEdit, BuiltInPrefix + file, isBuiltIn: true, styles, fixup);

    private static HighlightingSource Custom(string name, string file, Dictionary<string, SyntaxStyle> styles) =>
        new(name, Own, OwnPrefix + file, isBuiltIn: false, styles);

    /// <summary>
    /// AvalonEdit's Python <c>NumberLiteral</c> rule lacks the <c>|</c> between its hex and decimal alternatives, so it
    /// only matches a hex number directly followed by more digits: plain numbers were never colored. Our copy gets a
    /// working rule.
    /// </summary>
    private static void FixPythonNumbers(XshdSyntaxDefinition xshd)
    {
        var mainRuleSet = xshd.Elements.OfType<XshdRuleSet>().Single(ruleSet => ruleSet.Name is null);
        var numberRule = mainRuleSet.Elements.OfType<XshdRule>().Single(rule => rule.ColorReference.ReferencedElement == "NumberLiteral");
        numberRule.Regex = PythonNumber;
        numberRule.RegexType = XshdRegexType.Default;
    }
}
