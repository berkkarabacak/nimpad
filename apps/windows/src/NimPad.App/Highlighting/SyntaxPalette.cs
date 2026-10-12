using System.Windows;
using System.Windows.Media;
using NimPad.App.Theming;

namespace NimPad.App.Highlighting;

/// <summary>
/// The colors a themed highlighting definition may use: the six syntax tokens (<c>s-key</c>, <c>s-str</c>,
/// <c>s-num</c>, <c>s-lit</c>, <c>s-punc</c>, <c>s-com</c>) plus <c>accent</c> (Markdown links) and <c>text</c>
/// (everything else), read from the design-token dictionaries so the values have one source of truth
/// (<c>Themes/Tokens.Light.xaml</c> / <c>Themes/Tokens.Dark.xaml</c>). Immutable and thread-safe.
/// </summary>
public sealed class SyntaxPalette
{
    /// <summary>Resource key of the <c>s-key</c> token: keys, properties, keywords, tags.</summary>
    public const string KeyColorKey = "SyntaxKeyColor";

    /// <summary>Resource key of the <c>s-str</c> token: strings.</summary>
    public const string StringColorKey = "SyntaxStringColor";

    /// <summary>Resource key of the <c>s-num</c> token: numbers.</summary>
    public const string NumberColorKey = "SyntaxNumberColor";

    /// <summary>Resource key of the <c>s-lit</c> token: literals (true, false, null).</summary>
    public const string LiteralColorKey = "SyntaxLiteralColor";

    /// <summary>Resource key of the <c>s-punc</c> token: punctuation and operators.</summary>
    public const string PunctuationColorKey = "SyntaxPunctuationColor";

    /// <summary>Resource key of the <c>s-com</c> token: comments.</summary>
    public const string CommentColorKey = "SyntaxCommentColor";

    /// <summary>Resource key of the <c>accent</c> token (Markdown links and images).</summary>
    public const string AccentColorKey = "AccentColor";

    /// <summary>Resource key of the <c>text</c> token (code that has no syntax color).</summary>
    public const string TextColorKey = "TextColor";

    private static readonly Lazy<SyntaxPalette> LightPalette = new(() => FromTokens(dark: false));
    private static readonly Lazy<SyntaxPalette> DarkPalette = new(() => FromTokens(dark: true));

    private SyntaxPalette(
        Color key,
        Color @string,
        Color number,
        Color literal,
        Color punctuation,
        Color comment,
        Color accent,
        Color text)
    {
        Key = key;
        String = @string;
        Number = number;
        Literal = literal;
        Punctuation = punctuation;
        Comment = comment;
        Accent = accent;
        Text = text;
        AllColors = [key, @string, number, literal, punctuation, comment, accent, text];
    }

    /// <summary>The light theme's palette (from <c>TokensLight</c>), loaded on first use.</summary>
    public static SyntaxPalette Light => LightPalette.Value;

    /// <summary>The dark theme's palette (from <c>TokensDark</c>), loaded on first use.</summary>
    public static SyntaxPalette Dark => DarkPalette.Value;

    /// <summary><c>s-key</c>: keys, properties, keywords, types, tags, selectors.</summary>
    public Color Key { get; }

    /// <summary><c>s-str</c>: strings and characters.</summary>
    public Color String { get; }

    /// <summary><c>s-num</c>: numbers (and entities, dates).</summary>
    public Color Number { get; }

    /// <summary><c>s-lit</c>: literals (true, false, null), constants, preprocessor directives, attributes.</summary>
    public Color Literal { get; }

    /// <summary><c>s-punc</c>: punctuation and operators.</summary>
    public Color Punctuation { get; }

    /// <summary><c>s-com</c>: comments.</summary>
    public Color Comment { get; }

    /// <summary><c>accent</c>: Markdown links and images.</summary>
    public Color Accent { get; }

    /// <summary><c>text</c>: code tokens that have no syntax color (identifiers, method calls, ...).</summary>
    public Color Text { get; }

    /// <summary>
    /// Every color of this palette. A themed definition never uses any other foreground color (see
    /// <see cref="HighlightingProvider"/>).
    /// </summary>
    public IReadOnlyList<Color> AllColors { get; }

    /// <summary>The palette for a theme: <see cref="Dark"/> or <see cref="Light"/>.</summary>
    public static SyntaxPalette For(bool dark) => dark ? Dark : Light;

    /// <summary>
    /// Reads the palette from a resource dictionary holding the design tokens (a <c>TokensLight</c> /
    /// <c>TokensDark</c> instance, or any dictionary that merges one, such as <c>Application.Current.Resources</c>).
    /// The colors are copied: later changes to the dictionary do not affect the palette.
    /// </summary>
    /// <param name="resources">Dictionary that resolves the <c>*ColorKey</c> keys of this class to <see cref="Color"/> values.</param>
    /// <exception cref="InvalidOperationException">A key is missing or is not a <see cref="Color"/>.</exception>
    public static SyntaxPalette FromResources(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return new SyntaxPalette(
            Read(resources, KeyColorKey),
            Read(resources, StringColorKey),
            Read(resources, NumberColorKey),
            Read(resources, LiteralColorKey),
            Read(resources, PunctuationColorKey),
            Read(resources, CommentColorKey),
            Read(resources, AccentColorKey),
            Read(resources, TextColorKey));
    }

    /// <summary>True when <paramref name="color"/> is one of <see cref="AllColors"/>.</summary>
    public bool Contains(Color color) => AllColors.Contains(color);

    /// <summary>The foreground for a role; null for <see cref="SyntaxRole.Inherit"/> (keep the surrounding color).</summary>
    internal Color? Resolve(SyntaxRole role) => role switch
    {
        SyntaxRole.Text => Text,
        SyntaxRole.Key => Key,
        SyntaxRole.String => String,
        SyntaxRole.Number => Number,
        SyntaxRole.Literal => Literal,
        SyntaxRole.Punctuation => Punctuation,
        SyntaxRole.Comment => Comment,
        SyntaxRole.Accent => Accent,
        SyntaxRole.Inherit => null,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown syntax role."),
    };

    private static SyntaxPalette FromTokens(bool dark) => FromResources(ThemeService.CreateTokens(dark));

    private static Color Read(ResourceDictionary resources, string key) =>
        resources[key] is Color color
            ? color
            : throw new InvalidOperationException($"The token dictionary has no Color resource '{key}'.");
}
