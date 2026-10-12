using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NimPad.App.Tests.Themes;

/// <summary>
/// Source-level checks of the design-system XAML that neither the markup compiler nor WPF report (a missing
/// DynamicResource key silently renders nothing). Pure XML: no WPF needed.
/// </summary>
internal sealed partial class XamlResourceAudit
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly string _appDirectory;
    private readonly List<(string File, XDocument Document)> _documents;

    private XamlResourceAudit(string appDirectory)
    {
        _appDirectory = appDirectory;
        _documents = Directory.GetFiles(Path.Combine(appDirectory, "Themes"), "*.xaml")
            .Concat(Directory.GetFiles(Path.Combine(appDirectory, "Theming"), "*.xaml"))
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), XDocument.Load(path)))
            .ToList();
    }

    /// <summary>Finds apps/windows/src/NimPad.App by walking up from <paramref name="start"/>; null if absent.</summary>
    public static XamlResourceAudit? TryCreate(string? start = null)
    {
        for (var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var app = Path.Combine(dir.FullName, "apps", "windows", "src", "NimPad.App");
            if (File.Exists(Path.Combine(dir.FullName, "apps", "windows", "NimPad.slnx")) &&
                Directory.Exists(Path.Combine(app, "Themes")))
            {
                return new XamlResourceAudit(app);
            }
        }

        return null;
    }

    /// <summary>Keys defined in Themes/*.xaml (string keys and implicit-style "{x:Type T}" keys).</summary>
    public HashSet<string> DefinedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, document) in _documents.Where(d => !d.File.StartsWith("DesignGallery", StringComparison.Ordinal)))
        {
            foreach (var element in document.Descendants())
            {
                if (KeyOf(element) is { } key)
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// DynamicResource keys that are defined nowhere, StaticResource keys not defined earlier in the same file
    /// (the design system never relies on cross-file StaticResource), StaticResource in the gallery, token sets that
    /// differ, and merged dictionary sources that do not exist.
    /// </summary>
    public List<string> FindReferenceProblems()
    {
        var problems = new List<string>();
        var defined = DefinedKeys();
        foreach (var (file, document) in _documents)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var match = ResourceReference().Match(attribute.Value.Trim());
                    if (!match.Success)
                    {
                        continue;
                    }

                    var key = NormalizeKey(match.Groups[2].Value.Trim());
                    if (match.Groups[1].Value == "DynamicResource")
                    {
                        if (!defined.Contains(key))
                        {
                            problems.Add($"{file}: DynamicResource {key} is not defined");
                        }
                    }
                    else if (file.StartsWith("DesignGallery", StringComparison.Ordinal))
                    {
                        problems.Add($"{file}: use DynamicResource in the gallery ({key})");
                    }
                    else if (!seen.Contains(key))
                    {
                        problems.Add($"{file}: StaticResource {key} is not defined earlier in the same file");
                    }
                }

                if (KeyOf(element) is { } own)
                {
                    seen.Add(own);
                }
            }
        }

        var light = KeysOf("Tokens.Light.xaml");
        var dark = KeysOf("Tokens.Dark.xaml");
        if (!light.SetEquals(dark))
        {
            problems.Add("Tokens.Light.xaml and Tokens.Dark.xaml define different keys: " +
                string.Join(", ", light.Except(dark).Concat(dark.Except(light))));
        }

        var designSystem = _documents.Single(d => d.File == "DesignSystem.xaml").Document;
        foreach (var source in designSystem.Descendants().Select(e => (string?)e.Attribute("Source")).OfType<string>())
        {
            const string prefix = "/NimPad;component/";
            if (!source.StartsWith(prefix, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(_appDirectory, source[prefix.Length..])))
            {
                problems.Add($"DesignSystem.xaml: merged source {source} does not exist");
            }
        }

        return problems;
    }

    /// <summary>String keys of Themes/*.xaml that Themes/README.md does not mention in backticks.</summary>
    public List<string> FindUndocumentedKeys()
    {
        var readme = File.ReadAllText(Path.Combine(_appDirectory, "Themes", "README.md"));
        var documented = BacktickedName().Matches(readme).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return DefinedKeys()
            .Where(key => !key.StartsWith('{') && !documented.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private HashSet<string> KeysOf(string file) =>
        _documents.Single(d => d.File == file).Document.Descendants()
            .Select(KeyOf).OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static string? KeyOf(XElement element)
    {
        var key = (string?)element.Attribute(Xaml + "Key");
        if (key is not null)
        {
            return key.StartsWith('{') ? NormalizeKey(key) : key;
        }

        // Implicit style: keyed by its TargetType.
        if (element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") is { } target)
        {
            return NormalizeKey("{x:Type " + target.Replace("{x:Type ", string.Empty, StringComparison.Ordinal).TrimEnd('}') + "}");
        }

        return null;
    }

    private static string NormalizeKey(string key) => WhitespaceRun().Replace(key, " ");

    [GeneratedRegex(@"^\{(StaticResource|DynamicResource)\s+(.+)\}$")]
    private static partial Regex ResourceReference();

    [GeneratedRegex(@"`([A-Za-z][A-Za-z0-9.]*)`")]
    private static partial Regex BacktickedName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
