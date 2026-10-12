using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace NimPad.Core.Domain;

/// <summary>
/// The supported document formats (architecture.md, "Formats"). The format is detected from the note name's
/// extension, else <see cref="Plain"/>, and can be overridden by the user.
/// </summary>
public enum DocumentFormat
{
    /// <summary><c>plain</c>: plain text, one color.</summary>
    Plain,

    /// <summary><c>markdown</c>.</summary>
    Markdown,

    /// <summary><c>json</c>.</summary>
    Json,

    /// <summary><c>xml</c>.</summary>
    Xml,

    /// <summary><c>yaml</c>.</summary>
    Yaml,

    /// <summary><c>python</c>.</summary>
    Python,

    /// <summary><c>groovy</c>.</summary>
    Groovy,

    /// <summary><c>javascript</c>.</summary>
    JavaScript,

    /// <summary><c>typescript</c>.</summary>
    TypeScript,

    /// <summary><c>csharp</c>.</summary>
    CSharp,

    /// <summary><c>sql</c>.</summary>
    Sql,

    /// <summary><c>powershell</c>.</summary>
    PowerShell,

    /// <summary><c>ini</c>.</summary>
    Ini,

    /// <summary><c>css</c>.</summary>
    Css,

    /// <summary><c>html</c>.</summary>
    Html,
}

/// <summary>Wire names and extension-based detection for <see cref="DocumentFormat"/>.</summary>
public static class DocumentFormats
{
    private static readonly FrozenDictionary<DocumentFormat, string> WireNames = new Dictionary<DocumentFormat, string>
    {
        [DocumentFormat.Plain] = "plain",
        [DocumentFormat.Markdown] = "markdown",
        [DocumentFormat.Json] = "json",
        [DocumentFormat.Xml] = "xml",
        [DocumentFormat.Yaml] = "yaml",
        [DocumentFormat.Python] = "python",
        [DocumentFormat.Groovy] = "groovy",
        [DocumentFormat.JavaScript] = "javascript",
        [DocumentFormat.TypeScript] = "typescript",
        [DocumentFormat.CSharp] = "csharp",
        [DocumentFormat.Sql] = "sql",
        [DocumentFormat.PowerShell] = "powershell",
        [DocumentFormat.Ini] = "ini",
        [DocumentFormat.Css] = "css",
        [DocumentFormat.Html] = "html",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, DocumentFormat> ByWireName =
        WireNames.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    // Extensions are matched case-insensitively and include the leading dot.
    private static readonly FrozenDictionary<string, DocumentFormat> ByExtension = new Dictionary<string, DocumentFormat>
    {
        [".md"] = DocumentFormat.Markdown,
        [".markdown"] = DocumentFormat.Markdown,
        [".json"] = DocumentFormat.Json,
        [".jsonc"] = DocumentFormat.Json,
        [".xml"] = DocumentFormat.Xml,
        [".xaml"] = DocumentFormat.Xml,
        [".csproj"] = DocumentFormat.Xml,
        [".props"] = DocumentFormat.Xml,
        [".targets"] = DocumentFormat.Xml,
        [".svg"] = DocumentFormat.Xml,
        [".xsd"] = DocumentFormat.Xml,
        [".xsl"] = DocumentFormat.Xml,
        [".xslt"] = DocumentFormat.Xml,
        [".resx"] = DocumentFormat.Xml,
        [".config"] = DocumentFormat.Xml,
        [".yml"] = DocumentFormat.Yaml,
        [".yaml"] = DocumentFormat.Yaml,
        [".py"] = DocumentFormat.Python,
        [".pyw"] = DocumentFormat.Python,
        [".groovy"] = DocumentFormat.Groovy,
        [".gradle"] = DocumentFormat.Groovy,
        [".js"] = DocumentFormat.JavaScript,
        [".mjs"] = DocumentFormat.JavaScript,
        [".cjs"] = DocumentFormat.JavaScript,
        [".jsx"] = DocumentFormat.JavaScript,
        [".ts"] = DocumentFormat.TypeScript,
        [".mts"] = DocumentFormat.TypeScript,
        [".cts"] = DocumentFormat.TypeScript,
        [".tsx"] = DocumentFormat.TypeScript,
        [".cs"] = DocumentFormat.CSharp,
        [".csx"] = DocumentFormat.CSharp,
        [".sql"] = DocumentFormat.Sql,
        [".ps1"] = DocumentFormat.PowerShell,
        [".psm1"] = DocumentFormat.PowerShell,
        [".psd1"] = DocumentFormat.PowerShell,
        [".ini"] = DocumentFormat.Ini,
        [".cfg"] = DocumentFormat.Ini,
        [".toml"] = DocumentFormat.Ini,
        [".conf"] = DocumentFormat.Ini,
        [".editorconfig"] = DocumentFormat.Ini,
        [".css"] = DocumentFormat.Css,
        [".html"] = DocumentFormat.Html,
        [".htm"] = DocumentFormat.Html,
        [".xhtml"] = DocumentFormat.Html,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Extensions that mean "plain text" on purpose; such files are never content-sniffed.
    private static readonly FrozenSet<string> PlainExtensions =
        new[] { ".txt", ".text", ".log", ".csv", ".tsv", ".lst" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Well-known extensionless file names.
    private static readonly FrozenDictionary<string, DocumentFormat> ByFileName = new Dictionary<string, DocumentFormat>
    {
        ["Jenkinsfile"] = DocumentFormat.Groovy,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every format, in declaration order.</summary>
    public static IReadOnlyList<DocumentFormat> All { get; } = Enum.GetValues<DocumentFormat>();

    /// <summary>The storage and wire name, e.g. <c>csharp</c>.</summary>
    public static string ToWireName(this DocumentFormat format) =>
        WireNames.TryGetValue(format, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown document format.");

    /// <summary>Parses a wire name such as <c>markdown</c> (exact, lowercase).</summary>
    /// <exception cref="FormatException">The name is not a supported format.</exception>
    public static DocumentFormat Parse(string name) =>
        TryParse(name, out var format) ? format : throw new FormatException($"Unknown document format '{name}'.");

    /// <summary>Tries to parse a wire name such as <c>markdown</c> (exact, lowercase).</summary>
    public static bool TryParse([NotNullWhen(true)] string? name, out DocumentFormat format)
    {
        if (name is not null && ByWireName.TryGetValue(name, out format))
        {
            return true;
        }

        format = DocumentFormat.Plain;
        return false;
    }

    /// <summary>
    /// Detects the format from a note or file name's extension (<c>config.json</c> → <see cref="DocumentFormat.Json"/>),
    /// case-insensitively; <see cref="DocumentFormat.Plain"/> when the extension is unknown or missing.
    /// </summary>
    public static DocumentFormat FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DocumentFormat.Plain;
        }

        var trimmed = name.Trim();
        if (ByFileName.TryGetValue(trimmed, out var byName))
        {
            return byName;
        }

        var dot = trimmed.LastIndexOf('.');
        if (dot < 0 || dot == trimmed.Length - 1)
        {
            return DocumentFormat.Plain;
        }

        return ByExtension.TryGetValue(trimmed[dot..], out var format) ? format : DocumentFormat.Plain;
    }

    /// <summary>
    /// Detects the format of a file being imported: from the name's extension (see <see cref="FromName"/>) and, when
    /// the name has no known extension (and is not a plain-text one such as <c>.txt</c>), from the content (see <see cref="Sniff"/>).
    /// </summary>
    public static DocumentFormat Detect(string? name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var byName = FromName(name);
        if (byName != DocumentFormat.Plain)
        {
            return byName;
        }

        var extension = Path.GetExtension(name?.Trim() ?? string.Empty);
        return PlainExtensions.Contains(extension) ? DocumentFormat.Plain : Sniff(text);
    }

    /// <summary>
    /// Guesses a format from the start of a text: an XML declaration, an HTML doctype or root, a script's
    /// <c>#!</c> interpreter line (Python, PowerShell, Node, Groovy), or a whole text that parses as a JSON object or
    /// array. Anything else is <see cref="DocumentFormat.Plain"/>.
    /// </summary>
    public static DocumentFormat Sniff(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.AsSpan().TrimStart('\uFEFF').TrimStart();
        if (start.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFormat.Xml;
        }

        if (start.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFormat.Html;
        }

        if (start.StartsWith("#!"))
        {
            var lineEnd = start.IndexOfAny('\r', '\n');
            var shebang = (lineEnd < 0 ? start : start[..lineEnd]).ToString();
            return shebang switch
            {
                _ when shebang.Contains("python", StringComparison.OrdinalIgnoreCase) => DocumentFormat.Python,
                _ when shebang.Contains("pwsh", StringComparison.OrdinalIgnoreCase)
                    || shebang.Contains("powershell", StringComparison.OrdinalIgnoreCase) => DocumentFormat.PowerShell,
                _ when shebang.Contains("node", StringComparison.OrdinalIgnoreCase) => DocumentFormat.JavaScript,
                _ when shebang.Contains("groovy", StringComparison.OrdinalIgnoreCase) => DocumentFormat.Groovy,
                _ => DocumentFormat.Plain,
            };
        }

        if (start.Length > 0 && start[0] is '{' or '[' && text.Length <= MaxSniffedJsonLength)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(
                    text.AsMemory().TrimStart('\uFEFF'),
                    new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
                return DocumentFormat.Json;
            }
            catch (System.Text.Json.JsonException)
            {
                return DocumentFormat.Plain;
            }
        }

        return DocumentFormat.Plain;
    }

    // Larger texts starting with a brace are not parsed just to guess a format.
    private const int MaxSniffedJsonLength = 4 * 1024 * 1024;
}
