using NimPad.Core.Domain;

namespace NimPad.Presentation.Editor;

/// <summary>A document format as the status bar's mode picker shows it.</summary>
/// <param name="Format">The format.</param>
/// <param name="DisplayName">The name shown, for example <c>Plain Text</c> or <c>JSON</c>.</param>
/// <param name="DefaultExtension">The extension suggested when exporting a note whose name has none (with the dot).</param>
public sealed record FormatInfo(DocumentFormat Format, string DisplayName, string DefaultExtension)
{
    /// <summary>Every format, in the order the mode picker lists them (Plain Text first, then by name).</summary>
    public static IReadOnlyList<FormatInfo> All { get; } =
    [
        new(DocumentFormat.Plain, "Plain Text", ".txt"),
        new(DocumentFormat.CSharp, "C#", ".cs"),
        new(DocumentFormat.Css, "CSS", ".css"),
        new(DocumentFormat.Groovy, "Groovy", ".groovy"),
        new(DocumentFormat.Html, "HTML", ".html"),
        new(DocumentFormat.Ini, "INI", ".ini"),
        new(DocumentFormat.JavaScript, "JavaScript", ".js"),
        new(DocumentFormat.Json, "JSON", ".json"),
        new(DocumentFormat.Markdown, "Markdown", ".md"),
        new(DocumentFormat.PowerShell, "PowerShell", ".ps1"),
        new(DocumentFormat.Python, "Python", ".py"),
        new(DocumentFormat.Sql, "SQL", ".sql"),
        new(DocumentFormat.TypeScript, "TypeScript", ".ts"),
        new(DocumentFormat.Xml, "XML", ".xml"),
        new(DocumentFormat.Yaml, "YAML", ".yaml"),
    ];

    /// <summary>The entry for <paramref name="format"/>.</summary>
    public static FormatInfo For(DocumentFormat format) =>
        All.FirstOrDefault(info => info.Format == format) ?? All[0];

    /// <summary>The display name of <paramref name="format"/>, for example <c>Markdown</c>.</summary>
    public static string DisplayNameOf(DocumentFormat format) => For(format).DisplayName;

    /// <summary>
    /// The file name suggested when exporting a note: its name, plus the format's extension when the name has no
    /// extension of its own; characters Windows does not allow in file names become <c>_</c>.
    /// </summary>
    public static string SuggestFileName(string noteName, DocumentFormat format)
    {
        ArgumentNullException.ThrowIfNull(noteName);
        var invalid = new HashSet<char>(['<', '>', ':', '"', '/', '\\', '|', '?', '*']);
        var chars = noteName.Trim().Select(ch => ch < ' ' || invalid.Contains(ch) ? '_' : ch).ToArray();
        var name = new string(chars).TrimEnd('.', ' ');
        if (name.Length == 0)
        {
            name = "Note";
        }

        return Path.HasExtension(name) && DocumentFormats.FromName(name) == format ? name : name + For(format).DefaultExtension;
    }
}
