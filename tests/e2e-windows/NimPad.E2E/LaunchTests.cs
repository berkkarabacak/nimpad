using System.Diagnostics;
using FlaUI.Core.WindowsAPI;
using NimPad.E2E.Infrastructure;
using Xunit;

namespace NimPad.E2E;

/// <summary>
/// The shell and the editor: first launch, autosave and restore, crash recovery (spec §14 Scenarios 1 and 8, §12
/// Phase 1 "autosave, crash recovery, tabs and keyboard shortcuts").
/// </summary>
public sealed class LaunchTests
{
    /// <summary>A brand-new profile opens the main window with one empty untitled note that is stored only locally.</summary>
    [Fact]
    public void LaunchShowsWindowAndEmptyNote()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Ui.WindowsOnly);
        Scenario.Run(scenario =>
        {
            var app = scenario.Launch(scenario.CreateDataDirectory());

            Assert.Equal("NimPad", app.MainWindow.Name);
            app.WaitForTabTitles(["Untitled 1"]);
            app.WaitForName(AutomationIds.StatusSync, "Local only");
            app.WaitForName(AutomationIds.StatusPosition, "Ln 1, Col 1");

            // The empty-note hint is drawn over the editor and is never part of the document.
            Assert.Equal(string.Empty, app.ReadEditorText());
        });
    }

    /// <summary>
    /// Typed text is autosaved without any explicit save; after a graceful close and relaunch the tab comes back with
    /// the exact text and the caret where it was.
    /// </summary>
    [Fact]
    public void TypeAutosaveAndRestore()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Ui.WindowsOnly);
        Scenario.Run(scenario =>
        {
            const string Text = "Hello NimPad\nSecond line here";
            var dataDirectory = scenario.CreateDataDirectory();

            var app = scenario.Launch(dataDirectory);
            app.WaitForTabTitles(["Untitled 1"]);
            app.FocusEditor();
            app.TypeText(Text);
            var sinceEdit = Stopwatch.StartNew();
            app.WaitForEditorText(Text);

            // Leave the caret somewhere distinctive: line 2, column 7 (after "Second").
            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
            app.PressKeys(VirtualKeyShort.DOWN);
            app.PressKeys(Enumerable.Repeat(VirtualKeyShort.RIGHT, 6).ToArray());
            app.WaitForName(AutomationIds.StatusPosition, "Ln 2, Col 7");

            app.WaitForAutosave("Untitled 1", sinceEdit);
            app.Close();

            var restored = scenario.Launch(dataDirectory);
            restored.WaitForTabTitles(["Untitled 1"]);
            restored.WaitForEditorText(Text);
            restored.WaitForName(AutomationIds.StatusPosition, "Ln 2, Col 7");
        });
    }

    /// <summary>
    /// Scenario 8 on one machine: text typed moments before the process dies (killed, no shutdown code runs) is
    /// recovered on the next launch.
    /// </summary>
    [Fact]
    public void CrashRecovery()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Ui.WindowsOnly);
        Scenario.Run(scenario =>
        {
            const string Text = "Typed just before the crash 8";
            var dataDirectory = scenario.CreateDataDirectory();

            var app = scenario.Launch(dataDirectory);
            app.WaitForTabTitles(["Untitled 1"]);
            app.FocusEditor();
            app.TypeText(Text);
            var sinceEdit = Stopwatch.StartNew();
            app.WaitForEditorText(Text);
            app.WaitForAutosave("Untitled 1", sinceEdit);
            app.Kill();

            var recovered = scenario.Launch(dataDirectory);
            recovered.WaitForTabTitles(["Untitled 1"]);
            recovered.WaitForEditorText(Text);
            recovered.WaitForName(AutomationIds.StatusSync, "Local only");
        });
    }

    /// <summary>
    /// The tab shortcuts work from the keyboard alone: Ctrl+N creates a note tab, Ctrl+W closes it, Ctrl+Shift+T
    /// reopens it with its text, and Ctrl+Tab cycles tabs even while the editor (where Tab is a character) has focus.
    /// </summary>
    [Fact]
    public void KeyboardOnlyFlow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Ui.WindowsOnly);
        Scenario.Run(scenario =>
        {
            const string FirstText = "First note body";
            const string SecondText = "Second note body";

            var app = scenario.Launch(scenario.CreateDataDirectory());
            app.WaitForTabTitles(["Untitled 1"]);
            app.FocusEditor();
            app.TypeText(FirstText);
            app.WaitForEditorText(FirstText);

            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_N);
            app.WaitForTabTitles(["Untitled 1", "Untitled 2"]);
            app.WaitForEditorText(string.Empty);
            app.FocusEditor();
            app.TypeText(SecondText);
            app.WaitForEditorText(SecondText);

            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
            app.WaitForTabTitles(["Untitled 1"]);
            app.WaitForEditorText(FirstText);

            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_T);
            app.WaitForTabTitles(["Untitled 1", "Untitled 2"]);
            app.WaitForEditorText(SecondText);

            app.FocusEditor();
            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
            app.WaitForEditorText(FirstText);
            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
            app.WaitForEditorText(SecondText);
        });
    }
}
