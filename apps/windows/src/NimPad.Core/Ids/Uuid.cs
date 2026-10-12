using System.Diagnostics.CodeAnalysis;

namespace NimPad.Core.Ids;

/// <summary>Shared parsing and formatting for the UUID-based identifiers.</summary>
internal static class Uuid
{
    /// <summary>The length of the hyphenated form.</summary>
    public const int HyphenatedLength = 36;

    /// <summary>Parses the hyphenated 8-4-4-4-12 form (hex digits in any letter case).</summary>
    public static Guid Parse(string s, string typeName)
    {
        ArgumentNullException.ThrowIfNull(s);
        return TryParse(s, out var guid)
            ? guid
            : throw new FormatException($"'{s}' is not a valid {typeName}: expected a hyphenated UUID.");
    }

    /// <summary>Tries to parse the hyphenated 8-4-4-4-12 form (hex digits in any letter case).</summary>
    public static bool TryParse([NotNullWhen(true)] string? s, out Guid guid)
    {
        if (s is not null && TryParse(s.AsSpan(), out guid))
        {
            return true;
        }

        guid = Guid.Empty;
        return false;
    }

    /// <inheritdoc cref="TryParse(string?, out Guid)"/>
    public static bool TryParse(ReadOnlySpan<char> s, out Guid guid)
    {
        // Guid.TryParseExact(…, "D") alone also accepts a '+' sign or a "0x" prefix inside a group.
        if (IsHyphenated(s, lowercaseOnly: false) && Guid.TryParseExact(s, "D", out guid))
        {
            return true;
        }

        guid = Guid.Empty;
        return false;
    }

    /// <summary>
    /// True when <paramref name="s"/> is exactly 32 ASCII hex digits grouped 8-4-4-4-12 by hyphens, in lowercase
    /// only when <paramref name="lowercaseOnly"/> (the contracts' canonical form).
    /// </summary>
    public static bool IsHyphenated(ReadOnlySpan<char> s, bool lowercaseOnly)
    {
        if (s.Length != HyphenatedLength)
        {
            return false;
        }

        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            var ok = i is 8 or 13 or 18 or 23
                ? ch == '-'
                : char.IsAsciiDigit(ch) || ch is >= 'a' and <= 'f' || (!lowercaseOnly && ch is >= 'A' and <= 'F');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="s"/> is a canonical (lowercase hyphenated) UUID of version 7 with the RFC 9562 variant.</summary>
    public static bool IsCanonicalV7(string? s) =>
        s is not null && IsHyphenated(s, lowercaseOnly: true) && s[14] == '7' && s[19] is '8' or '9' or 'a' or 'b';

    /// <summary>The canonical text form: lowercase, hyphenated.</summary>
    public static string Format(Guid guid) => guid.ToString("D");
}
