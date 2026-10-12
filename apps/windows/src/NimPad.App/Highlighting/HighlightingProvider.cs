using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using NimPad.Core.Domain;

namespace NimPad.App.Highlighting;

/// <summary>
/// Theme-aware syntax highlighting definitions for the editor, one per (format, theme), cached and thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// Every definition is a private copy: the XSHD is parsed fresh (from AvalonEdit's embedded resources for the
/// built-ins, from <c>Highlighting/Definitions</c> for YAML, Groovy and INI), so the shared instances in
/// <see cref="HighlightingManager.Instance"/> are never touched. Definitions that import others (Markdown → C#,
/// C# → XmlDoc, HTML → JavaScript) import the themed copies of the same theme.
/// </para>
/// <para>
/// Every named color is recolored to a <see cref="SyntaxPalette"/> color through an explicit mapping table
/// (<c>HighlightingSources</c>); a name missing from its table falls back to <see cref="SyntaxPalette.Text"/> and is
/// reported by <see cref="GetUnmappedColorNames"/>. Backgrounds, underlines, fonts and sizes are cleared, code is
/// never bold (only Markdown headings and strong emphasis and INI section headers are), and inline colors without a
/// name (C#'s TODO markers) are cleared so they keep the surrounding color. The result: no AvalonEdit default color
/// can leak. All colors are then frozen, so the cached definitions can be shared by every editor and thread.
/// </para>
/// </remarks>
public sealed class HighlightingProvider
{
    private static readonly Lazy<HighlightingProvider> DefaultProvider = new(() => new HighlightingProvider());

    private readonly Func<bool, SyntaxPalette> _palettes;
    private readonly Dictionary<(string Source, bool Dark), IHighlightingDefinition> _cache = [];
    private readonly HashSet<(string Source, bool Dark)> _building = [];
    private readonly object _gate = new();

    /// <summary>Uses the design tokens: <see cref="SyntaxPalette.Light"/> and <see cref="SyntaxPalette.Dark"/>.</summary>
    public HighlightingProvider()
    {
        _palettes = SyntaxPalette.For;
    }

    /// <summary>Uses the given palettes (for example read from a custom token dictionary).</summary>
    public HighlightingProvider(SyntaxPalette light, SyntaxPalette dark)
    {
        ArgumentNullException.ThrowIfNull(light);
        ArgumentNullException.ThrowIfNull(dark);
        _palettes = isDark => isDark ? dark : light;
    }

    /// <summary>The application-wide provider (design-token palettes, one shared cache).</summary>
    public static HighlightingProvider Default => DefaultProvider.Value;

    /// <summary>
    /// The highlighting definition for a format in a theme, or null for <see cref="DocumentFormat.Plain"/> (plain
    /// text stays one color). The same instance is returned for the same (format, theme); light and dark are
    /// different instances. Safe to call from any thread; the first call per definition parses its XSHD (a few
    /// milliseconds). Callers should not modify the returned definition (its colors are frozen).
    /// </summary>
    /// <param name="format">The document's format.</param>
    /// <param name="dark">True for the dark theme (<c>ThemeService.IsDark</c>).</param>
    /// <exception cref="HighlightingDefinitionInvalidException">A definition failed to load (a build defect).</exception>
    public IHighlightingDefinition? Get(DocumentFormat format, bool dark)
    {
        var source = HighlightingSources.ForFormat(format);
        return source is null ? null : GetOrBuild(source, dark);
    }

    /// <summary>The palette this provider themes with for a theme.</summary>
    public SyntaxPalette GetPalette(bool dark) => _palettes(dark);

    /// <summary>
    /// Diagnostic: the named colors of every themed definition that have no entry in its mapping table and therefore
    /// fall back to <see cref="SyntaxPalette.Text"/>, as <c>"Definition/Color"</c> (for example <c>"C#/Comment"</c>).
    /// Empty when the tables are complete; tests assert that.
    /// </summary>
    public static IReadOnlyList<string> GetUnmappedColorNames() =>
        [.. HighlightingSources.All.SelectMany(source => LoadColorNames(source)
            .Where(name => !source.Styles.ContainsKey(name))
            .Select(name => source.Name + "/" + name))];

    /// <summary>Diagnostic: mapping-table entries that name a color the definition does not have (stale entries).</summary>
    internal static IReadOnlyList<string> GetUnusedMappingNames() =>
        [.. HighlightingSources.All.SelectMany(source =>
        {
            var names = LoadColorNames(source).ToHashSet(StringComparer.Ordinal);
            return source.Styles.Keys.Where(name => !names.Contains(name)).Select(name => source.Name + "/" + name);
        })];

    /// <summary>
    /// Every color a definition can paint with: its named colors and the colors of every rule and span reachable from
    /// its main rule set (including colors of the definitions it imports).
    /// </summary>
    internal static IReadOnlyCollection<HighlightingColor> EnumerateColors(IHighlightingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var colors = new HashSet<HighlightingColor>(ReferenceEqualityComparer.Instance);
        foreach (var color in definition.NamedHighlightingColors)
        {
            colors.Add(color);
        }

        CollectRuleSetColors(definition.MainRuleSet, colors, new HashSet<HighlightingRuleSet>(ReferenceEqualityComparer.Instance));
        return colors;
    }

    private IHighlightingDefinition GetOrBuild(HighlightingSource source, bool dark)
    {
        var key = (source.Name, dark);

        // One lock for every build: builds are rare (once per definition and theme) and recursive (imports re-enter
        // on the same thread, which Monitor allows), and it keeps the XSHD schema validation single-threaded.
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (!_building.Add(key))
            {
                throw new InvalidOperationException($"The highlighting definition '{source.Name}' imports itself.");
            }

            try
            {
                var definition = Build(LoadXshd(source), source.Styles, _palettes(dark), new ThemedResolver(this, dark));
                _cache.Add(key, definition);
                return definition;
            }
            finally
            {
                _building.Remove(key);
            }
        }
    }

    /// <summary>
    /// Creates a fresh definition from <paramref name="xshd"/> and themes it: every named color through
    /// <paramref name="styles"/> (missing names become <see cref="SyntaxStyle.Fallback"/>), unnamed inline colors
    /// cleared, everything owned frozen.
    /// </summary>
    internal static IHighlightingDefinition Build(
        XshdSyntaxDefinition xshd,
        IReadOnlyDictionary<string, SyntaxStyle> styles,
        SyntaxPalette palette,
        IHighlightingDefinitionReferenceResolver? resolver)
    {
        var definition = HighlightingLoader.Load(xshd, resolver);
        var brushes = new Dictionary<Color, HighlightingBrush>();
        var named = new HashSet<HighlightingColor>(ReferenceEqualityComparer.Instance);
        foreach (var color in definition.NamedHighlightingColors)
        {
            var style = styles.TryGetValue(color.Name, out var mapped) ? mapped : SyntaxStyle.Fallback;
            Apply(color, style, palette, brushes);
            named.Add(color);
        }

        // Also walk the named rule sets the main rule set does not reach: other definitions may import them.
        var owned = new HashSet<HighlightingColor>(EnumerateColors(definition), ReferenceEqualityComparer.Instance);
        var visited = new HashSet<HighlightingRuleSet>(ReferenceEqualityComparer.Instance);
        foreach (var ruleSet in xshd.Elements.OfType<XshdRuleSet>().Where(ruleSet => ruleSet.Name is not null))
        {
            CollectRuleSetColors(definition.GetNamedRuleSet(ruleSet.Name), owned, visited);
        }

        foreach (var color in owned)
        {
            // Frozen colors belong to an imported definition, which is already themed.
            if (color.IsFrozen)
            {
                continue;
            }

            if (!named.Contains(color))
            {
                // An inline color without a name (e.g. C#'s bold red TODO): keep the surrounding color instead.
                Clear(color);
            }

            color.Freeze();
        }

        return definition;
    }

    private static void Apply(HighlightingColor color, SyntaxStyle style, SyntaxPalette palette, Dictionary<Color, HighlightingBrush> brushes)
    {
        Clear(color);
        if (palette.Resolve(style.Role) is { } foreground)
        {
            if (!brushes.TryGetValue(foreground, out var brush))
            {
                brush = new SimpleHighlightingBrush(foreground);
                brushes.Add(foreground, brush);
            }

            color.Foreground = brush;
        }

        color.FontWeight = style.Bold ? FontWeights.Bold : null;
        color.FontStyle = style.Italic ? FontStyles.Italic : null;
    }

    private static void Clear(HighlightingColor color)
    {
        color.Foreground = null;
        color.Background = null;
        color.FontWeight = null;
        color.FontStyle = null;
        color.FontFamily = null;
        color.FontSize = null;
        color.Underline = null;
        color.Strikethrough = null;
    }

    private static void CollectRuleSetColors(HighlightingRuleSet? ruleSet, HashSet<HighlightingColor> colors, HashSet<HighlightingRuleSet> visited)
    {
        if (ruleSet is null || !visited.Add(ruleSet))
        {
            return;
        }

        foreach (var rule in ruleSet.Rules)
        {
            AddColor(rule.Color, colors);
        }

        foreach (var span in ruleSet.Spans)
        {
            AddColor(span.StartColor, colors);
            AddColor(span.SpanColor, colors);
            AddColor(span.EndColor, colors);
            CollectRuleSetColors(span.RuleSet, colors, visited);
        }
    }

    private static void AddColor(HighlightingColor? color, HashSet<HighlightingColor> colors)
    {
        if (color is not null)
        {
            colors.Add(color);
        }
    }

    /// <summary>Parses (and schema-validates) a fresh copy of the source's XSHD.</summary>
    internal static XshdSyntaxDefinition LoadXshd(HighlightingSource source)
    {
        using var stream = source.OpenXshd();
        using var reader = XmlReader.Create(stream);
        var xshd = HighlightingLoader.LoadXshd(reader);
        if (xshd.Name != source.Name)
        {
            throw new InvalidOperationException($"'{source.ResourceName}' defines '{xshd.Name}', expected '{source.Name}'.");
        }

        source.Fixup?.Invoke(xshd);
        return xshd;
    }

    private static IEnumerable<string> LoadColorNames(HighlightingSource source) =>
        LoadXshd(source).Elements.OfType<XshdColor>().Select(color => color.Name).Where(name => name is not null);

    /// <summary>Resolves imports ("C#/", "XmlDoc/DocCommentSet") to the themed copies of the same theme.</summary>
    private sealed class ThemedResolver(HighlightingProvider provider, bool dark) : IHighlightingDefinitionReferenceResolver
    {
        public IHighlightingDefinition? GetDefinition(string name) =>
            HighlightingSources.FindByName(name) is { } source ? provider.GetOrBuild(source, dark) : null;
    }
}
