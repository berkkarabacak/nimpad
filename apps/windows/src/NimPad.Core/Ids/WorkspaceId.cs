using System.Diagnostics.CodeAnalysis;

namespace NimPad.Core.Ids;

/// <summary>Identifies a workspace. A UUID version 7 serialized as lowercase hyphenated text.</summary>
/// <param name="Value">The underlying UUID.</param>
public readonly record struct WorkspaceId(Guid Value) : IParsable<WorkspaceId>
{
    /// <summary>Creates a new time-ordered ID (UUIDv7).</summary>
    public static WorkspaceId New() => new(Guid.CreateVersion7());

    /// <summary>Returns the canonical lowercase hyphenated form.</summary>
    public override string ToString() => Value.ToString("D");

    /// <summary>Parses a hyphenated UUID.</summary>
    /// <exception cref="FormatException"><paramref name="s"/> is not a hyphenated UUID.</exception>
    public static WorkspaceId Parse(string s) => new(Uuid.Parse(s, nameof(WorkspaceId)));

    /// <inheritdoc cref="Parse(string)"/>
    public static WorkspaceId Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <summary>Tries to parse a hyphenated UUID.</summary>
    public static bool TryParse([NotNullWhen(true)] string? s, out WorkspaceId result)
    {
        var ok = Uuid.TryParse(s, out var guid);
        result = new WorkspaceId(guid);
        return ok;
    }

    /// <inheritdoc cref="TryParse(string?, out WorkspaceId)"/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out WorkspaceId result) =>
        TryParse(s, out result);
}
