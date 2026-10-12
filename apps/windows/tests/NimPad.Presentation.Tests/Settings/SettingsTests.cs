using NimPad.Core.Domain;
using NimPad.Presentation.Settings;
using NimPad.Presentation.Shortcuts;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Settings;

public sealed class SettingsTests
{
    [Fact]
    public async Task OnlyAppearanceAndGeneralArePagesInPhaseOne()
    {
        await using var harness = await ShellHarness.StartAsync();
        Assert.Equal([SettingsPage.Appearance, SettingsPage.General], harness.Shell.Settings.Pages);
    }

    [Fact]
    public async Task ChangesApplyLiveAndPersistImmediately()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var settings = harness.Shell.Settings;
            var editor = harness.Shell.Editor;
            Assert.Equal("Cascadia Mono", settings.EditorFontFamily);
            Assert.Equal(["Cascadia Mono", "Consolas", "Courier New"], settings.FontFamilies);
            Assert.Equal("14 px", settings.FontSizeText);
            Assert.Equal(["The quick brown fox jumps over the lazy dog", "0123456789 {}[]() => != ==="], settings.PreviewLines);
            Assert.Equal(["Light", "Dark", "System"], settings.ThemeOptions.Select(option => option.Label));

            settings.EditorFontFamily = "Consolas";
            settings.IncreaseFontSizeCommand.Execute(null);
            settings.WordWrap = true;
            settings.ShowLineNumbers = false;
            settings.TabSize = 2;
            settings.Theme = ThemePreference.Dark;

            Assert.Equal("Consolas", editor.FontFamily);
            Assert.Equal(15, editor.FontSize);
            Assert.True(editor.WordWrap);
            Assert.False(editor.ShowLineNumbers);
            Assert.Equal(2, editor.TabSize);
            Assert.True(settings.EffectiveIsDark);

            await harness.SettingsStore().WhenSavedAsync();
            var stored = await harness.Core.Settings.LoadAsync();
            Assert.Equal(("Consolas", 15, true, false, 2, ThemePreference.Dark), (stored.EditorFontFamily, stored.EditorFontSize, stored.WordWrap, stored.ShowLineNumbers, stored.TabSize, stored.Theme));

            await harness.RestartAsync();
            Assert.Equal("Consolas", harness.Shell.Settings.EditorFontFamily);
            Assert.Equal(15, harness.Shell.Editor.FontSize);
            Assert.Equal(2, harness.Shell.Settings.TabSize);
        });
    }

    [Fact]
    public async Task TheFontSizeStepperStaysBetween8And32()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(() =>
        {
            var settings = harness.Shell.Settings;
            settings.FontSize = 32;
            Assert.False(settings.IncreaseFontSizeCommand.CanExecute(null));
            settings.FontSize = 8;
            Assert.False(settings.DecreaseFontSizeCommand.CanExecute(null));
            settings.DecreaseFontSizeCommand.Execute(null);
            Assert.Equal(8, settings.FontSize);

            settings.TabSize = 3;
            Assert.Equal(4, harness.SettingsStore().Current.TabSize);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SystemThemeFollowsWindows()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(() =>
        {
            var settings = harness.Shell.Settings;
            Assert.Equal(ThemePreference.System, settings.Theme);
            Assert.False(settings.EffectiveIsDark);

            harness.Platform.SystemTheme.Set(dark: true);
            Assert.True(settings.EffectiveIsDark);

            settings.Theme = ThemePreference.Light;
            Assert.False(settings.EffectiveIsDark);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StartWithWindowsGoesThroughTheAutostartService()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var settings = harness.Shell.Settings;
            settings.StartWithWindows = true;
            Assert.True(harness.Platform.Autostart.IsEnabled);
            await harness.SettingsStore().WhenSavedAsync();
            Assert.True((await harness.Core.Settings.LoadAsync()).StartWithWindows);

            harness.Platform.Autostart.Fail = true;
            settings.StartWithWindows = false;

            Assert.True(settings.StartWithWindows);
            Assert.True(harness.Platform.Autostart.IsEnabled);
            Assert.Equal("NimPad couldn't stop starting with Windows.", harness.Shell.Toasts.Toasts[^1].Message);
        });
    }

    [Fact]
    public async Task TheGlobalShortcutIsRegisteredAtStartupAndCanBeChanged()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var settings = harness.Shell.Settings;
            var hotkeys = harness.Platform.Hotkey;
            Assert.Equal("Ctrl+Alt+N", hotkeys.Registered);
            Assert.Equal(["Ctrl", "Alt", "N"], settings.GlobalShortcutKeys);

            settings.BeginShortcutCaptureCommand.Execute(null);
            Assert.True(settings.IsCapturingShortcut);

            Assert.False(settings.CompleteShortcutCapture("Shift+K"));
            Assert.Equal("Add Ctrl, Alt or Win, so the shortcut doesn't get in the way of typing.", settings.ShortcutError);
            Assert.False(settings.CompleteShortcutCapture("Ctrl+Alt"));
            Assert.Equal("Add a key to the shortcut, for example N.", settings.ShortcutError);
            Assert.False(settings.CompleteShortcutCapture("Ctrl+Alt+Delete"));
            Assert.Equal("Ctrl+Alt+Delete is reserved by Windows.", settings.ShortcutError);

            hotkeys.TakenByOtherApps.Add("Ctrl+Alt+K");
            Assert.False(settings.CompleteShortcutCapture("alt + control + k"));
            Assert.Equal("Ctrl+Alt+K is already in use.", settings.ShortcutError);
            Assert.True(settings.IsCapturingShortcut);
            Assert.Equal("Ctrl+Alt+N", hotkeys.Registered);

            // A failing registration (not a conflict) is reported inline too, never thrown into the key handler.
            hotkeys.Fail = true;
            Assert.False(settings.CompleteShortcutCapture("Ctrl+Alt+J"));
            Assert.Equal("Ctrl+Alt+J couldn't be registered. Try a different shortcut.", settings.ShortcutError);
            Assert.True(settings.IsCapturingShortcut);
            Assert.Equal("Ctrl+Alt+N", settings.GlobalShortcut);
            hotkeys.Fail = false;

            Assert.True(settings.CompleteShortcutCapture("Win+Shift+F9"));
            Assert.False(settings.IsCapturingShortcut);
            Assert.Null(settings.ShortcutError);
            Assert.Equal("Shift+Win+F9", settings.GlobalShortcut);
            Assert.Equal(["Shift", "Win", "F9"], settings.GlobalShortcutKeys);
            Assert.Equal("Shift+Win+F9", hotkeys.Registered);
            await harness.SettingsStore().WhenSavedAsync();
            Assert.Equal("Shift+Win+F9", (await harness.Core.Settings.LoadAsync()).GlobalHotkey);
            Assert.Equal(["Shift", "Win", "F9"], harness.Shell.Shortcuts.AllGroups.Single(group => group.Title == "APP").Shortcuts.Single(row => row.Label == "Open NimPad from anywhere").Keys);
        });
    }

    [Fact]
    public async Task AShortcutTakenByAnotherAppAtStartupShowsABanner()
    {
        await using var harness = await ShellHarness.StartAsync(beforeInitialize: h =>
        {
            h.Platform.Hotkey.TakenByOtherApps.Add("Ctrl+Alt+N");
            return Task.CompletedTask;
        });
        await harness.RunAsync(async () =>
        {
            var banner = harness.Shell.Banners.Find(NimPad.Presentation.Shell.ShellViewModel.HotkeyBannerKey)!;
            Assert.Equal("Ctrl+Alt+N is used by another app, so it doesn't open NimPad. You can pick another shortcut in Settings.", banner.Message);
            var opened = 0;
            harness.Shell.OpenSettingsRequested += (_, _) => opened++;

            await banner.ActionCommand.ExecuteAsync(null);

            Assert.Equal(1, opened);
            Assert.Equal(SettingsPage.General, harness.Shell.Settings.SelectedPage);
        });
    }

    [Fact]
    public void HotkeyGesturesNormalizeModifiersAndKeys()
    {
        Assert.True(HotkeyGesture.TryParse("shift+ctrl+f12", out var gesture, out _));
        Assert.Equal("Ctrl+Shift+F12", gesture.ToString());
        Assert.True(HotkeyGesture.TryParse("Control+Alt+Space", out gesture, out _));
        Assert.Equal(["Ctrl", "Alt", "Space"], gesture.Keys);
        Assert.False(HotkeyGesture.TryParse("Ctrl+A+B", out _, out var error));
        Assert.Equal("Use one key together with Ctrl, Alt or Win.", error);
        Assert.False(HotkeyGesture.TryParse("Ctrl+F25", out _, out error));
        Assert.Equal("F25 can't be part of a global shortcut.", error);
        Assert.False(HotkeyGesture.TryParse(null, out _, out _));
    }

    [Fact]
    public async Task TheShortcutListFiltersAndExplainsAnEmptyResult()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(() =>
        {
            var shortcuts = harness.Shell.Shortcuts;
            Assert.Equal(["NOTES & TABS", "EDITING", "FIND", "APP"], shortcuts.Groups.Select(group => group.Title));
            var notes = shortcuts.Groups[0].Shortcuts;
            Assert.Equal(["Ctrl", "Shift", "T"], notes.Single(row => row.Label == "Reopen closed tab").Keys);
            Assert.Equal(["Ctrl", "1…9"], notes.Single(row => row.Label == "Go to tab 1–9").Keys);
            Assert.Equal(["F2"], notes.Single(row => row.Label == "Rename tab").Keys);
            Assert.Equal(["Ctrl", "S"], shortcuts.Groups[1].Shortcuts.Single(row => row.Label == "Export note to file").Keys);
            Assert.Equal(["Shift", "Enter"], shortcuts.Groups[2].Shortcuts.Single(row => row.Label == "Previous match").Keys);
            Assert.Equal(["Ctrl", "/"], shortcuts.Groups[3].Shortcuts.Single(row => row.Label == "Keyboard shortcuts").Keys);
            Assert.DoesNotContain("sync", ShortcutsViewModel.FooterText, StringComparison.OrdinalIgnoreCase);

            shortcuts.Filter = "zoom";
            var zoom = Assert.Single(shortcuts.Groups);
            Assert.Equal("EDITING", zoom.Title);
            Assert.Equal(["Zoom in", "Zoom out", "Reset zoom"], zoom.Shortcuts.Select(row => row.Label));
            Assert.True(shortcuts.HasResults);

            shortcuts.Filter = "ctrl+shift+t";
            Assert.Equal("Reopen closed tab", Assert.Single(Assert.Single(shortcuts.Groups).Shortcuts).Label);

            shortcuts.Filter = "zoomm";
            Assert.Empty(shortcuts.Groups);
            Assert.False(shortcuts.HasResults);
            Assert.Equal("No shortcuts match “zoomm”", shortcuts.EmptyTitle);
            Assert.Equal("Try “zoom” or a shorter word.", shortcuts.EmptyMessage);

            shortcuts.Filter = "qq";
            Assert.Equal("Try a shorter word.", shortcuts.EmptyMessage);

            harness.Shell.ShowShortcutsCommand.Execute(null);
            Assert.Equal(string.Empty, shortcuts.Filter);
            Assert.Equal(4, shortcuts.Groups.Count);
            return Task.CompletedTask;
        });
    }
}
