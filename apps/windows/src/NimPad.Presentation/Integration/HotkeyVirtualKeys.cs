using System.Collections.Frozen;
using System.Globalization;
using NimPad.Presentation.Settings;

namespace NimPad.Presentation.Integration;

/// <summary>
/// Maps <see cref="HotkeyGesture"/>s to the Win32 <c>RegisterHotKey</c> modifiers and virtual-key codes, and captured
/// key presses back to gesture text. Pure, so it is testable on any OS; the App's <c>GlobalHotkeyService</c> and the
/// Settings window's shortcut capture use it.
/// </summary>
public static class HotkeyVirtualKeys
{
    /// <summary><c>MOD_ALT</c>.</summary>
    public const int ModAlt = 0x0001;

    /// <summary><c>MOD_CONTROL</c>.</summary>
    public const int ModControl = 0x0002;

    /// <summary><c>MOD_SHIFT</c>.</summary>
    public const int ModShift = 0x0004;

    /// <summary><c>MOD_WIN</c>.</summary>
    public const int ModWin = 0x0008;

    /// <summary><c>MOD_NOREPEAT</c>: holding the keys down does not repeat the hotkey.</summary>
    public const int ModNoRepeat = 0x4000;

    private static readonly FrozenDictionary<string, int> NamedKeys = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Pause"] = 0x13,
        ["Space"] = 0x20,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<int, string> NamesByKey =
        NamedKeys.ToFrozenDictionary(pair => pair.Value, pair => pair.Key);

    // Shift, Ctrl and Alt (generic, left and right) and the two Windows keys.
    private static readonly FrozenSet<int> ModifierKeys =
        new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }.ToFrozenSet();

    /// <summary>The virtual-key code of a gesture key (<see cref="HotkeyGesture.Key"/>): <c>N</c> is 0x4E.</summary>
    /// <returns><see langword="false"/> for a key a global shortcut cannot use.</returns>
    public static bool TryGetVirtualKey(string key, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (key.Length == 1 && (char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0])))
        {
            // 'A'–'Z' are 0x41–0x5A and '0'–'9' are 0x30–0x39, the same as their characters.
            virtualKey = key[0];
            return true;
        }

        if (key.Length is 2 or 3 && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 24)
        {
            virtualKey = 0x70 + number - 1;
            return true;
        }

        return NamedKeys.TryGetValue(key, out virtualKey);
    }

    /// <summary>The <c>RegisterHotKey</c> modifier flags of a gesture (without <see cref="ModNoRepeat"/>).</summary>
    public static int GetModifiers(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var flags = 0;
        foreach (var modifier in gesture.Modifiers)
        {
            flags |= modifier switch
            {
                "Ctrl" => ModControl,
                "Alt" => ModAlt,
                "Shift" => ModShift,
                "Win" => ModWin,
                _ => 0,
            };
        }

        return flags;
    }

    /// <summary>The modifiers and virtual key to pass to <c>RegisterHotKey</c>.</summary>
    /// <exception cref="ArgumentException">The gesture's key has no virtual key (a parsed gesture always has one).</exception>
    public static (int Modifiers, int VirtualKey) ToWin32(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        return TryGetVirtualKey(gesture.Key, out var virtualKey)
            ? (GetModifiers(gesture), virtualKey)
            : throw new ArgumentException($"{gesture.Key} has no virtual key.", nameof(gesture));
    }

    /// <summary>The gesture key name of a virtual key (<c>0x4E</c> is <c>N</c>), or <see langword="null"/> when a global shortcut cannot use it.</summary>
    public static string? GetKeyName(int virtualKey) => virtualKey switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => string.Create(CultureInfo.InvariantCulture, $"F{virtualKey - 0x70 + 1}"),
        _ => NamesByKey.GetValueOrDefault(virtualKey),
    };

    /// <summary>True for Shift, Ctrl, Alt and the Windows keys (pressed alone they do not complete a gesture).</summary>
    public static bool IsModifierKey(int virtualKey) => ModifierKeys.Contains(virtualKey);

    /// <summary>
    /// The gesture text for a key pressed while the Settings window captures a new shortcut, for example
    /// <c>Ctrl+Alt+K</c>; the view model validates it (<see cref="SettingsViewModel.CompleteShortcutCapture"/>). Keys
    /// a shortcut cannot use keep <paramref name="fallbackKeyName"/> so the error can name them.
    /// </summary>
    /// <returns><see langword="null"/> while only modifiers are pressed (keep waiting for the key).</returns>
    public static string? ComposeGesture(bool ctrl, bool alt, bool shift, bool win, int virtualKey, string? fallbackKeyName)
    {
        if (IsModifierKey(virtualKey))
        {
            return null;
        }

        var key = GetKeyName(virtualKey)
            ?? (string.IsNullOrWhiteSpace(fallbackKeyName) ? string.Create(CultureInfo.InvariantCulture, $"Key{virtualKey}") : fallbackKeyName.Replace("+", string.Empty, StringComparison.Ordinal));
        var parts = new List<string>(5);
        if (ctrl)
        {
            parts.Add("Ctrl");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        if (win)
        {
            parts.Add("Win");
        }

        parts.Add(key);
        return string.Join('+', parts);
    }
}
