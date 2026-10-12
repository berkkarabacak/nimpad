using System.Diagnostics.CodeAnalysis;

namespace NimPad.Core.Ids;

/// <summary>
/// Identifies a folder or document. A UUID version 7 serialized as lowercase hyphenated text
/// (architecture.md, "IDs"); stored in SQLite as <c>TEXT</c>.
/// </summary>
/// <param name="Value">The underlying UUID.</param>
public readonly record struct NodeId(Guid Value) : IParsable<NodeId>
{
    /// <summary>Creates a new time-ordered ID (UUIDv7).</summary>
    public static NodeId New() => new(Guid.CreateVersion7());

    /// <summary>Returns the canonical lowercase hyphenated form, e.g. <c>0192f0c4-…</c>.</summary>
    public override string ToString() => Value.ToString("D");

    /// <summary>Parses a hyphenated UUID.</summary>
    /// <exception cref="FormatException"><paramref name="s"/> is not a hyphenated UUID.</exception>
    public static NodeId Parse(string s) => new(Uuid.Parse(s, nameof(NodeId)));

    /// <inheritdoc cref="Parse(string)"/>
    public static NodeId Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <summary>Tries to parse a hyphenated UUID.</summary>
    public static bool TryParse([NotNullWhen(true)] string? s, out NodeId result)
    {
        var ok = Uuid.TryParse(s, out var guid);
        result = new NodeId(guid);
        return ok;
    }

    /// <inheritdoc cref="TryParse(string?, out NodeId)"/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out NodeId result) =>
        TryParse(s, out result);
}
