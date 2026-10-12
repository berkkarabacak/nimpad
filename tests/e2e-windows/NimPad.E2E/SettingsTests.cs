using NimPad.E2E.Infrastructure;
using Xunit;

namespace NimPad.E2E;

/// <summary>The settings window and themes (spec §2.3, §12 Phase 1 "Light/dark themes").</summary>
public sealed class SettingsTests
{
    /// <summary>
    /// Ctrl+, opens the settings window; the theme switches live both ways; Esc closes the window; the choice survives
    /// a restart (the main window's ItemStatus carries the effective theme, the settings show the stored choice).
    /// </summary>
    [Fact]
    public void ThemeSwitchAndSettings()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Ui.WindowsOnly);
        Scenario.Run(scenario =>
        {
            var dataDirectory = scenario.CreateDataDirectory();

            var app = scenario.Launch(dataDirectory);
            var settings = SettingsWindowPage.Open(app);

            // Light first, so the switch to Dark is a real change whatever theme Windows itself uses.
            settings.ChooseTheme("Light");
            app.WaitForThemeMarker(ItemStatuses.ThemeLight);
            settings.ChooseTheme("Dark");
            app.WaitForThemeMarker(ItemStatuses.ThemeDark);
            settings.CloseWithEscape();
            Assert.Equal(ItemStatuses.ThemeDark, app.ThemeMarker);
            app.Close();

            var restored = scenario.Launch(dataDirectory);
            restored.WaitForThemeMarker(ItemStatuses.ThemeDark);
            var restoredSettings = SettingsWindowPage.Open(restored);
            Ui.WaitUntil(() => restoredSettings.IsThemeSelected("Dark"), "the settings window to show the stored theme choice (Dark)");
            restoredSettings.CloseWithEscape();
            Assert.Equal(ItemStatuses.ThemeDark, restored.ThemeMarker);
        });
    }
}
