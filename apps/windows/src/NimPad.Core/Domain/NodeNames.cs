using System.Globalization;

namespace NimPad.Core.Domain;

/// <summary>Validation and generation of node names.</summary>
public static class NodeNames
{
    /// <summary>Longest allowed name, in UTF-16 code units (the same unit as the contracts' <c>NodeName</c>).</summary>
    public const int MaxLength = 255;

    /// <summary>The base of generated document names: <c>Untitled 1</c>, <c>Untitled 2</c>, …</summary>
    public const string UntitledBase = "Untitled";

    /// <summary>
    /// Trims <paramref name="name"/> and checks it: not empty, at most <see cref="MaxLength"/> characters, no
    /// control characters (C0 or DEL, so no line breaks or tabs) and no unpaired surrogates (which UTF-8 cannot carry).
    /// </summary>
    /// <returns>The trimmed name to store.</returns>
    /// <exception cref="InvalidNodeNameException">The name is not acceptable; the message is user-facing.</exception>
    public static string Normalize(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new InvalidNodeNameException(name, "A name can't be empty.");
        }

        if (trimmed.Length > MaxLength)
        {
            throw new InvalidNodeNameException(name, $"A name can't be longer than {MaxLength} characters.");
        }

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch < '\u0020' || ch == '\u007f')
            {
                throw new InvalidNodeNameException(name, "A name can't contain line breaks, tabs or other control characters.");
            }

            if (char.IsSurrogate(ch))
            {
                // A lone surrogate cannot be encoded as UTF-8, so it could never be synced.
                if (!char.IsHighSurrogate(ch) || i + 1 >= trimmed.Length || !char.IsLowSurrogate(trimmed[i + 1]))
                {
                    throw new InvalidNodeNameException(name, "A name can't contain invalid Unicode characters.");
                }

                i++;
            }
        }

        return trimmed;
    }

    /// <summary>True when <see cref="Normalize"/> would accept <paramref name="name"/>.</summary>
    public static bool IsValid(string? name)
    {
        try
        {
            Normalize(name);
            return true;
        }
        catch (InvalidNodeNameException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> is a valid name in its stored (and wire) form: <see cref="Normalize"/>
    /// accepts it unchanged, so it is also already trimmed (contracts: <c>NodeName</c>).
    /// </summary>
    public static bool IsStoredForm(string? name)
    {
        try
        {
            return name is not null && string.Equals(Normalize(name), name, StringComparison.Ordinal);
        }
        catch (InvalidNodeNameException)
        {
            return false;
        }
    }

    /// <summary>
    /// The lowest-numbered <c>Untitled N</c> (N ≥ 1) not present in <paramref name="existingNames"/>, compared
    /// case-insensitively.
    /// </summary>
    public static string NextUntitledName(IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        var used = new HashSet<int>();
        foreach (var name in existingNames)
        {
            if (TryParseUntitledNumber(name, out var number))
            {
                used.Add(number);
            }
        }

        var candidate = 1;
        while (used.Contains(candidate))
        {
            candidate++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{UntitledBase} {candidate}");
    }

    /// <summary>
    /// The name for a copy placed among <paramref name="siblingNames"/>: the original name when no sibling uses it,
    /// otherwise <c>X (copy)</c>, then <c>X (copy 2)</c>, <c>X (copy 3)</c>, … Names compare case-insensitively.
    /// For documents the suffix goes before the extension (<c>config (copy).json</c>) so the format is kept. An
    /// existing copy suffix is not repeated: copying <c>X (copy)</c> yields <c>X (copy 2)</c>. A long name is shortened
    /// to fit <see cref="MaxLength"/> without splitting a surrogate pair; when the extension leaves no room, the whole
    /// name is shortened instead.
    /// </summary>
    /// <param name="originalName">The name of the node being copied.</param>
    /// <param name="kind">The node kind; only documents keep their extension last.</param>
    /// <param name="siblingNames">Names of the live siblings at the destination.</param>
    public static string CopyName(string originalName, NodeKind kind, IEnumerable<string> siblingNames)
    {
        ArgumentNullException.ThrowIfNull(originalName);
        ArgumentNullException.ThrowIfNull(siblingNames);
        var taken = new HashSet<string>(siblingNames, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(originalName))
        {
            return originalName;
        }

        var (stem, extension) = kind == NodeKind.Document ? SplitExtension(originalName) : (originalName, string.Empty);
        stem = StripCopySuffix(stem);
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? " (copy)" : string.Create(CultureInfo.InvariantCulture, $" (copy {n})");
            var candidate = FitCopyName(stem, suffix, extension);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string FitCopyName(string stem, string suffix, string extension)
    {
        var fitted = ShortenTo(stem, MaxLength - suffix.Length - extension.Length);
        if (fitted.Length == 0)
        {
            // The extension leaves no room for the stem: shorten the whole name instead.
            fitted = ShortenTo(stem + extension, MaxLength - suffix.Length);
            extension = string.Empty;
        }

        return fitted + suffix + extension;
    }

    // At most room code units, never ending in the first half of a surrogate pair, without trailing white space.
    private static string ShortenTo(string text, int room)
    {
        if (room < 1)
        {
            return string.Empty;
        }

        if (text.Length <= room)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[room - 1]) ? room - 1 : room;
        return text[..cut].TrimEnd();
    }

    /// <summary>True when <paramref name="name"/> is a generated document name: <c>Untitled N</c> (N ≥ 1, any letter case).</summary>
    public static bool IsUntitledName(string? name) => name is not null && TryParseUntitledNumber(name, out _);

    private static bool TryParseUntitledNumber(string name, out int number)
    {
        number = 0;
        if (!name.StartsWith(UntitledBase + " ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = name.AsSpan(UntitledBase.Length + 1);
        return digits.Length is > 0 and <= 9
            && digits[0] != '0'
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static (string Stem, string Extension) SplitExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        // A leading dot (".gitignore") or a trailing one is part of the stem, not an extension.
        return dot <= 0 || dot == name.Length - 1 ? (name, string.Empty) : (name[..dot], name[dot..]);
    }

    private static string StripCopySuffix(string stem)
    {
        if (stem.EndsWith(" (copy)", StringComparison.OrdinalIgnoreCase))
        {
            return stem[..^" (copy)".Length];
        }

        const string Marker = " (copy ";
        var start = stem.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (start > 0 && stem.EndsWith(')'))
        {
            var digits = stem.AsSpan(start + Marker.Length, stem.Length - start - Marker.Length - 1);
            if (digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return stem[..start];
            }
        }

        return stem;
    }
}
