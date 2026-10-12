using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace NimPad.Presentation.Settings;

/// <summary>
/// A global shortcut such as <c>Ctrl+Alt+N</c>: one or more modifiers (Ctrl, Alt, Shift, Win; at least one of Ctrl, Alt
/// or Win) and one key. <see cref="ToString"/> gives the normalized form stored in the settings and passed to
/// <c>IGlobalHotkeyService</c>; <see cref="Keys"/> gives the key caps.
/// </summary>
public sealed record HotkeyGesture
{
    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];

    private static readonly Dictionary<string, string> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = "Ctrl",
        ["Control"] = "Ctrl",
        ["Alt"] = "Alt",
        ["Shift"] = "Shift",
        ["Win"] = "Win",
        ["Windows"] = "Win",
        ["Meta"] = "Win",
    };

    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = "Space",
        ["Insert"] = "Insert",
        ["Ins"] = "Insert",
        ["Delete"] = "Delete",
        ["Del"] = "Delete",
        ["Home"] = "Home",
        ["End"] = "End",
        ["PageUp"] = "PageUp",
        ["PgUp"] = "PageUp",
        ["PageDown"] = "PageDown",
        ["PgDn"] = "PageDown",
        ["Up"] = "Up",
        ["Down"] = "Down",
        ["Left"] = "Left",
        ["Right"] = "Right",
        ["Pause"] = "Pause",
    };

    private HotkeyGesture(IReadOnlyList<string> modifiers, string key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    /// <summary>The modifiers in canonical order (Ctrl, Alt, Shift, Win).</summary>
    public IReadOnlyList<string> Modifiers { get; }

    /// <summary>The key: <c>A</c>–<c>Z</c>, <c>0</c>–<c>9</c>, <c>F1</c>–<c>F24</c> or a named key such as <c>Space</c>.</summary>
    public string Key { get; }

    /// <summary>The key caps: <c>["Ctrl", "Alt", "N"]</c>.</summary>
    public IReadOnlyList<string> Keys => [.. Modifiers, Key];

    /// <summary>The normalized text, for example <c>Ctrl+Alt+N</c>.</summary>
    public override string ToString() => string.Join('+', Keys);

    /// <summary>Value equality on the normalized form.</summary>
    public bool Equals(HotkeyGesture? other) => other is not null && ToString() == other.ToString();

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    /// <summary>Parses and validates a gesture typed as <c>ctrl + alt + n</c> or captured by the view.</summary>
    /// <param name="text">The gesture.</param>
    /// <param name="gesture">The normalized gesture.</param>
    /// <param name="error">A user-facing reason when the gesture is not usable.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture, [NotNullWhen(false)] out string? error)
    {
        gesture = null;
        var parts = (text ?? string.Empty).Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        string? key = null;
        foreach (var part in parts)
        {
            if (ModifierAliases.TryGetValue(part, out var modifier))
            {
                modifiers.Add(modifier);
                continue;
            }

            if (key is not null)
            {
                error = "Use one key together with Ctrl, Alt or Win.";
                return false;
            }

            if (NormalizeKey(part) is not { } normalized)
            {
                error = $"{part} can't be part of a global shortcut.";
                return false;
            }

            key = normalized;
        }

        if (key is null)
        {
            error = "Add a key to the shortcut, for example N.";
            return false;
        }

        if (!modifiers.Contains("Ctrl") && !modifiers.Contains("Alt") && !modifiers.Contains("Win"))
        {
            error = "Add Ctrl, Alt or Win, so the shortcut doesn't get in the way of typing.";
            return false;
        }

        var ordered = ModifierOrder.Where(modifiers.Contains).ToList();
        var candidate = new HotkeyGesture(ordered, key);
        if (IsReserved(candidate))
        {
            error = $"{candidate} is reserved by Windows.";
            return false;
        }

        gesture = candidate;
        error = null;
        return true;
    }

    private static string? NormalizeKey(string part)
    {
        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
        {
            return part.ToUpperInvariant();
        }

        if (part.Length is >= 2 and <= 3 && (part[0] is 'F' or 'f')
            && int.TryParse(part.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 24)
        {
            return string.Create(CultureInfo.InvariantCulture, $"F{number}");
        }

        return NamedKeys.GetValueOrDefault(part);
    }

    private static bool IsReserved(HotkeyGesture gesture)
    {
        var text = gesture.ToString();
        return text is "Ctrl+Alt+Delete" or "Ctrl+Shift+Escape" or "Alt+F4" or "Alt+Tab" or "Win+L"
            || (gesture.Modifiers is ["Alt"] && gesture.Key is "Space");
    }
}
