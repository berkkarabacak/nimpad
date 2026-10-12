using System.Diagnostics.CodeAnalysis;

namespace NimPad.Core.Ids;

/// <summary>
/// Groups a node moved to the Trash with the live subtree that went with it, so they are restored as a unit (one Trash
/// item). A UUID version 7 serialized
/// as lowercase hyphenated text.
/// </summary>
/// <param name="Value">The underlying UUID.</param>
public readonly record struct TrashBatchId(Guid Value) : IParsable<TrashBatchId>
{
    /// <summary>Creates a new time-ordered ID (UUIDv7).</summary>
    public static TrashBatchId New() => new(Guid.CreateVersion7());

    /// <summary>Returns the canonical lowercase hyphenated form.</summary>
    public override string ToString() => Value.ToString("D");

    /// <summary>Parses a hyphenated UUID.</summary>
    /// <exception cref="FormatException"><paramref name="s"/> is not a hyphenated UUID.</exception>
    public static TrashBatchId Parse(string s) => new(Uuid.Parse(s, nameof(TrashBatchId)));

    /// <inheritdoc cref="Parse(string)"/>
    public static TrashBatchId Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <summary>Tries to parse a hyphenated UUID.</summary>
    public static bool TryParse([NotNullWhen(true)] string? s, out TrashBatchId result)
    {
        var ok = Uuid.TryParse(s, out var guid);
        result = new TrashBatchId(guid);
        return ok;
    }

    /// <inheritdoc cref="TryParse(string?, out TrashBatchId)"/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TrashBatchId result) =>
        TryParse(s, out result);
}
