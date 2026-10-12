using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace NimPad.E2E.Infrastructure;

/// <summary>The settings window (contract: SettingsWindow), opened with Ctrl+, and closed with Esc.</summary>
internal sealed class SettingsWindowPage
{
    private readonly AppSession _app;

    private SettingsWindowPage(AppSession app, AutomationElement window)
    {
        _app = app;
        Window = window;
    }

    /// <summary>The settings window element.</summary>
    public AutomationElement Window { get; }

    /// <summary>Presses Ctrl+, in the main window and waits for the settings window.</summary>
    public static SettingsWindowPage Open(AppSession app)
    {
        app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_COMMA);
        var window = Ui.WaitFor(
            () => app.FindWindow(AutomationIds.SettingsWindow) is { } found && Ui.IsShown(found) ? found : null,
            $"the settings window ({AutomationIds.SettingsWindow}) after Ctrl+,");
        return new SettingsWindowPage(app, window);
    }

    /// <summary>Waits for an option of the theme selector (contract: ThemeSegmented, items named Light, Dark, System).</summary>
    public AutomationElement ThemeOption(string name) =>
        Ui.WaitFor(() => FindThemeOption(name), $"the theme option {Ui.Describe(name)} in {AutomationIds.ThemeSegmented}");

    /// <summary>True when the theme option is the selected segment (SelectionItem pattern of the ListBoxItem).</summary>
    public bool IsThemeSelected(string name) =>
        FindThemeOption(name)?.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault ?? false;

    /// <summary>Clicks a theme option like a user and waits until it is the selected segment.</summary>
    public void ChooseTheme(string name)
    {
        ThemeOption(name).Click();
        Wait.UntilInputIsProcessed();
        Ui.WaitUntil(() => IsThemeSelected(name), $"the theme option {Ui.Describe(name)} to be selected");
    }

    /// <summary>Presses Esc in the settings window and waits for it to close.</summary>
    public void CloseWithEscape()
    {
        Window.Focus();
        _app.PressKeys(VirtualKeyShort.ESCAPE);
        Ui.WaitUntil(
            () => !Ui.IsShown(_app.FindWindow(AutomationIds.SettingsWindow)),
            $"the settings window ({AutomationIds.SettingsWindow}) to close on Esc");
    }

    private AutomationElement? FindThemeOption(string name)
    {
        var conditions = _app.Automation.ConditionFactory;
        if (Window.FindFirstDescendant(conditions.ByAutomationId(AutomationIds.ThemeSegmented)) is not { } selector)
        {
            return null;
        }

        // The segment itself is a ListItem; its text block carries the same Name, so prefer the item.
        var option = selector.FindFirstDescendant(conditions.ByControlType(ControlType.ListItem).And(conditions.ByName(name)))
            ?? selector.FindFirstDescendant(conditions.ByName(name));
        return option is not null && Ui.IsShown(option) ? option : null;
    }
}
