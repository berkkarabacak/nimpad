namespace NimPad.Core.Domain;

/// <summary>The two node kinds. Only folders have children.</summary>
public enum NodeKind
{
    /// <summary>A folder (<c>folder</c>).</summary>
    Folder,

    /// <summary>A managed text document (<c>document</c>).</summary>
    Document,
}

/// <summary>Conversions between <see cref="NodeKind"/> and its wire/storage name.</summary>
public static class NodeKinds
{
    /// <summary>The storage and wire name: <c>folder</c> or <c>document</c>.</summary>
    public static string ToWireName(this NodeKind kind) => kind switch
    {
        NodeKind.Folder => "folder",
        NodeKind.Document => "document",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown node kind."),
    };

    /// <summary>Parses a wire name.</summary>
    /// <exception cref="FormatException">The name is not <c>folder</c> or <c>document</c>.</exception>
    public static NodeKind Parse(string name) => name switch
    {
        "folder" => NodeKind.Folder,
        "document" => NodeKind.Document,
        _ => throw new FormatException($"Unknown node kind '{name}'."),
    };
}
