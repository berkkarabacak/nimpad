using NimPad.Presentation.Integration;
using NimPad.Presentation.Settings;
using Xunit;

namespace NimPad.Presentation.Tests.Integration;

public sealed class HotkeyVirtualKeysTests
{
    // Every key a global shortcut may use, as HotkeyGesture normalizes it.
    public static TheoryData<string> AllKeys()
    {
        var keys = new TheoryData<string>();
        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys.Add(c.ToString());
        }

        for (var c = '0'; c <= '9'; c++)
        {
            keys.Add(c.ToString());
        }

        for (var f = 1; f <= 24; f++)
        {
            keys.Add("F" + f);
        }

        foreach (var named in new[] { "Space", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Pause" })
        {
            keys.Add(named);
        }

        return keys;
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void EveryGestureKeyRoundTripsThroughItsVirtualKey(string key)
    {
        Assert.True(HotkeyVirtualKeys.TryGetVirtualKey(key, out var virtualKey));
        Assert.Equal(key, HotkeyVirtualKeys.GetKeyName(virtualKey));
        Assert.False(HotkeyVirtualKeys.IsModifierKey(virtualKey));

        // Parse → format → capture → parse gives the same gesture (Shift avoids the reserved Ctrl+Alt+Delete).
        Assert.True(HotkeyGesture.TryParse("shift + ctrl + alt + " + key.ToLowerInvariant(), out var gesture, out var error), error);
        var captured = HotkeyVirtualKeys.ComposeGesture(ctrl: true, alt: true, shift: true, win: false, virtualKey, null);
        Assert.Equal(gesture.ToString(), captured);
        Assert.True(HotkeyGesture.TryParse(captured, out var again, out _));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("Ctrl+Alt+N", HotkeyVirtualKeys.ModControl | HotkeyVirtualKeys.ModAlt, 0x4E)]
    [InlineData("Shift+Win+F9", HotkeyVirtualKeys.ModShift | HotkeyVirtualKeys.ModWin, 0x78)]
    [InlineData("Ctrl+Shift+Space", HotkeyVirtualKeys.ModControl | HotkeyVirtualKeys.ModShift, 0x20)]
    [InlineData("Alt+0", HotkeyVirtualKeys.ModAlt, 0x30)]
    [InlineData("Win+Alt+Ctrl+Shift+PageDown", 0xF, 0x22)]
    [InlineData("Ctrl+Alt+F24", HotkeyVirtualKeys.ModControl | HotkeyVirtualKeys.ModAlt, 0x87)]
    public void MapsGesturesToRegisterHotKeyArguments(string text, int modifiers, int virtualKey)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture, out _));

        Assert.Equal((modifiers, virtualKey), HotkeyVirtualKeys.ToWin32(gesture));
    }

    [Fact]
    public void UnknownKeysHaveNoVirtualKeyAndViceVersa()
    {
        Assert.False(HotkeyVirtualKeys.TryGetVirtualKey("", out _));
        Assert.False(HotkeyVirtualKeys.TryGetVirtualKey("F25", out _));
        Assert.False(HotkeyVirtualKeys.TryGetVirtualKey("F0", out _));
        Assert.False(HotkeyVirtualKeys.TryGetVirtualKey("a", out _));
        Assert.False(HotkeyVirtualKeys.TryGetVirtualKey("Tab", out _));
        Assert.Null(HotkeyVirtualKeys.GetKeyName(0x09)); // Tab
        Assert.Null(HotkeyVirtualKeys.GetKeyName(0x60)); // NumPad0
        Assert.Null(HotkeyVirtualKeys.GetKeyName(0xBC)); // OemComma
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0xA0)]
    [InlineData(0xA3)]
    [InlineData(0xA5)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    public void ModifierKeysAloneDoNotCompleteACapture(int virtualKey)
    {
        Assert.True(HotkeyVirtualKeys.IsModifierKey(virtualKey));
        Assert.Null(HotkeyVirtualKeys.ComposeGesture(ctrl: true, alt: true, shift: false, win: false, virtualKey, "LeftCtrl"));
    }

    [Fact]
    public void CapturedChordsComposeInCanonicalOrderAndKeepUnusableKeysForTheError()
    {
        Assert.Equal("Ctrl+Alt+Shift+Win+K", HotkeyVirtualKeys.ComposeGesture(true, true, true, true, 0x4B, "K"));
        Assert.Equal("K", HotkeyVirtualKeys.ComposeGesture(false, false, false, false, 0x4B, "K"));
        Assert.Equal("Ctrl+NumPad5", HotkeyVirtualKeys.ComposeGesture(true, false, false, false, 0x65, "NumPad5"));
        Assert.Equal("Ctrl+OemPlus", HotkeyVirtualKeys.ComposeGesture(true, false, false, false, 0xBB, "Oem+Plus"));
        Assert.Equal("Alt+Key255", HotkeyVirtualKeys.ComposeGesture(false, true, false, false, 0xFF, null));

        // The settings view model turns these into the user-facing errors.
        Assert.False(HotkeyGesture.TryParse("K", out _, out var noModifier));
        Assert.Contains("Ctrl, Alt or Win", noModifier, StringComparison.Ordinal);
        Assert.False(HotkeyGesture.TryParse("Ctrl+NumPad5", out _, out var unusable));
        Assert.Equal("NumPad5 can't be part of a global shortcut.", unusable);
    }
}
