using NimPad.Core.Domain;

namespace NimPad.Presentation.Integration;

/// <summary>One entry of the Open dialog's file type list.</summary>
/// <param name="Label">The name shown, without the patterns (for example <c>Markdown</c>).</param>
/// <param name="Extensions">The extensions it shows, with their dots (<c>.md</c>); empty for every file.</param>
/// <param name="Format">The syntax mode NimPad picks for these extensions, or <see langword="null"/> for mixed entries.</param>
public sealed record FileTypeFilter(string Label, IReadOnlyList<string> Extensions, DocumentFormat? Format)
{
    /// <summary>The patterns, for example <c>*.md;*.markdown</c> (<c>*.*</c> for every file).</summary>
    public string Patterns => Extensions.Count == 0 ? "*.*" : string.Join(';', Extensions.Select(extension => "*" + extension));

    /// <summary>The text the dialog shows, for example <c>Markdown (*.md;*.markdown)</c>.</summary>
    public string DisplayText => $"{Label} ({Patterns})";
}

/// <summary>
/// The Open dialog's (Ctrl+O) file types: All files first, then text, Markdown, JSON and every other syntax mode with
/// the extensions Core recognizes for it. The App's <c>IFileDialogService</c> sets
/// <c>OpenFileDialog.Filter = FileDialogFilters.Open</c> (and <c>FilterIndex = 1</c>).
/// </summary>
public static class FileDialogFilters
{
    /// <summary>The entries, in the order the dialog lists them.</summary>
    public static IReadOnlyList<FileTypeFilter> OpenFilters { get; } =
    [
        new("All files", [], null),
        new("Text files", [".txt", ".text", ".log", ".csv", ".tsv"], DocumentFormat.Plain),
        new("Markdown", [".md", ".markdown"], DocumentFormat.Markdown),
        new("JSON", [".json", ".jsonc"], DocumentFormat.Json),
        new("XML", [".xml", ".xaml", ".config", ".csproj", ".props", ".targets", ".resx", ".svg", ".xsd", ".xsl", ".xslt"], DocumentFormat.Xml),
        new("YAML", [".yml", ".yaml"], DocumentFormat.Yaml),
        new("INI and TOML", [".ini", ".cfg", ".conf", ".toml", ".editorconfig"], DocumentFormat.Ini),
        new("Python", [".py", ".pyw"], DocumentFormat.Python),
        new("Groovy and Gradle", [".groovy", ".gradle"], DocumentFormat.Groovy),
        new("JavaScript", [".js", ".mjs", ".cjs", ".jsx"], DocumentFormat.JavaScript),
        new("TypeScript", [".ts", ".mts", ".cts", ".tsx"], DocumentFormat.TypeScript),
        new("C#", [".cs", ".csx"], DocumentFormat.CSharp),
        new("SQL", [".sql"], DocumentFormat.Sql),
        new("PowerShell", [".ps1", ".psm1", ".psd1"], DocumentFormat.PowerShell),
        new("CSS", [".css"], DocumentFormat.Css),
        new("HTML", [".html", ".htm", ".xhtml"], DocumentFormat.Html),
    ];

    /// <summary>
    /// The <c>Filter</c> string for WPF's <c>OpenFileDialog</c>:
    /// <c>All files (*.*)|*.*|Text files (*.txt;…)|*.txt;…|Markdown (*.md;*.markdown)|…</c>.
    /// </summary>
    public static string Open { get; } = string.Join('|', OpenFilters.Select(filter => filter.DisplayText + "|" + filter.Patterns));

    /// <summary>The 1-based <c>FilterIndex</c> of All files, the default (NimPad opens any text file).</summary>
    public const int DefaultOpenFilterIndex = 1;
}
