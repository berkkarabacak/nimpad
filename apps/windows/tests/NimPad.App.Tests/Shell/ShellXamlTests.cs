using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace NimPad.App.Tests.Shell;

/// <summary>
/// Source checks of the shell's XAML (MainWindow, Shell/, Views/) that the markup compiler does not make: every
/// DynamicResource key exists in the design system or in the same file, StaticResource is only used for keys defined
/// earlier in the same file (the views can be built before the design system is merged, as in these tests), and every
/// AutomationId of the UI Automation contract appears.
/// </summary>
public partial class ShellXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ContractIds =
    [
        "MainWindow", "TitleBar", "TabStrip", "NewTabButton", "MinimizeButton", "MaximizeButton", "CloseButton",
        "BannerHost", "ToastHost", "StatusBar", "StatusPosition", "StatusCharacters", "StatusEncoding", "StatusMode",
        "StatusZoom", "StatusSync", "SidebarToggle", "EmptyStateNewNote", "EmptyStateReopen", "ShortcutsDialog",
        "ShortcutsFilter", "ConfirmDialog", "ConfirmPrimary", "ConfirmCancel", "Editor", "ExplorerSearch", "ExplorerList",
    ];

    [Fact]
    public void EveryResourceReferenceResolves()
    {
        var app = FindAppDirectory();
        Assert.SkipWhen(app is null, "App sources not found next to the test binaries.");
        var designKeys = Directory.GetFiles(Path.Combine(app!, "Themes"), "*.xaml").SelectMany(file => KeysOf(XDocument.Load(file))).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var file in ShellXamlFiles(app!))
        {
            var document = XDocument.Load(file);
            var local = KeysOf(document).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    foreach (Match match in ResourceReference().Matches(attribute.Value))
                    {
                        var key = match.Groups[2].Value.Trim();
                        if (match.Groups[1].Value == "DynamicResource")
                        {
                            if (!designKeys.Contains(key) && !local.Contains(key))
                            {
                                problems.Add($"{Path.GetFileName(file)}: DynamicResource {key} is not defined");
                            }
                        }
                        else if (!seen.Contains(key))
                        {
                            problems.Add($"{Path.GetFileName(file)}: StaticResource {key} is not defined earlier in the same file");
                        }
                    }
                }

                if ((string?)element.Attribute(Xaml + "Key") is { } own)
                {
                    seen.Add(own);
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void EveryContractAutomationIdIsDeclared()
    {
        var app = FindAppDirectory();
        Assert.SkipWhen(app is null, "App sources not found next to the test binaries.");
        var text = string.Join('\n', ShellXamlFiles(app!).Select(File.ReadAllText).Concat(
            Directory.GetFiles(app!, "*.cs", SearchOption.AllDirectories).Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Select(File.ReadAllText)));
        var missing = ContractIds.Where(id => !text.Contains('"' + id + '"', StringComparison.Ordinal)).ToList();
        Assert.Empty(missing);
    }

    private static IEnumerable<string> ShellXamlFiles(string app) =>
        new[] { Path.Combine(app, "MainWindow.xaml") }
            .Concat(Directory.GetFiles(Path.Combine(app, "Shell"), "*.xaml"))
            .Concat(Directory.GetFiles(Path.Combine(app, "Views"), "*.xaml", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> KeysOf(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            if ((string?)element.Attribute(Xaml + "Key") is { } key)
            {
                yield return key;
            }
        }
    }

    private static string? FindAppDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var app = Path.Combine(dir.FullName, "apps", "windows", "src", "NimPad.App");
            if (File.Exists(Path.Combine(dir.FullName, "apps", "windows", "NimPad.slnx")) && Directory.Exists(Path.Combine(app, "Shell")))
            {
                return app;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\{(StaticResource|DynamicResource)\s+([^}]+?)\}")]
    private static partial Regex ResourceReference();
}
