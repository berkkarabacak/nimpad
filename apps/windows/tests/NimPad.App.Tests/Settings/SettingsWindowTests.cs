using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NimPad.App.Controls;
using NimPad.App.Integration;
using NimPad.App.Tests.Themes;
using NimPad.App.Theming;
using NimPad.App.Views.Settings;
using NimPad.Core.Domain;
using NimPad.Presentation.Settings;
using NimPad.Presentation.Shell;
using Xunit;

namespace NimPad.App.Tests.Settings;

/// <summary>The Settings window against the UI automation contract and its view model, in both themes.</summary>
public sealed class SettingsWindowTests
{
    private static readonly string[] AppearanceIds =
        ["ThemeSegmented", "FontDropdown", "FontSizeStepper", "WordWrapSwitch", "LineNumbersSwitch", "TabSizeSegmented"];

    private static readonly string[] GeneralIds = ["StartWithWindowsSwitch", "RestoreTabsSwitch", "GlobalShortcutChange"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaysOutInBothThemesAndExposesEveryControlOfTheContract(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var shell = new ShellViewModel(new TestPlatform().Services);
            var settings = shell.Settings;
            var window = CreateWindow(settings, dark);
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal("SettingsWindow", AutomationProperties.GetAutomationId(window));
                Assert.Equal("Settings", window.Title);
                Assert.Equal(dark ? "Dark" : "Light", window.FindResource("ThemeName"));
                Assert.True(window.ActualWidth > 0 && window.ActualHeight > 0, "the window did not lay out");

                var nav = Find<ListBox>(window, "SettingsNav");
                Assert.Equal(["Appearance", "General"], nav.Items.Cast<ListBoxItem>().Select(item => (string)item.Content));
                Assert.Equal(216, ((FrameworkElement)nav.Parent).ActualWidth);
                Assert.Equal(SettingsPage.Appearance, settings.SelectedPage);
                Assert.Equal(0, nav.SelectedIndex);
                AssertShown(window, AppearanceIds);
                AssertHidden(window, GeneralIds);
                AssertTemplated(window);

                // What UI Automation clients (FlaUI) see: three named items with the SelectionItem pattern.
                var theme = Find<SegmentedControl>(window, "ThemeSegmented");
                var segments = ItemPeers(theme);
                Assert.Equal(["Light", "Dark", "System"], segments.Select(peer => peer.GetName()));
                Assert.All(segments, peer => Assert.NotNull(peer.GetPattern(PatternInterface.SelectionItem)));
                Assert.True(((ISelectionItemProvider)segments[2].GetPattern(PatternInterface.SelectionItem)).IsSelected);
                Assert.Equal(["2", "4", "8"], ItemPeers(Find<SegmentedControl>(window, "TabSizeSegmented")).Select(peer => peer.GetName()));
                Assert.Equal(["Appearance", "General"], ItemPeers(nav).Select(peer => peer.GetName()));

                settings.SelectedPage = SettingsPage.General;
                window.UpdateLayout();

                Assert.Equal(1, nav.SelectedIndex);
                AssertShown(window, GeneralIds);
                AssertHidden(window, AppearanceIds);
                AssertTemplated(window);
                Assert.Equal(["Ctrl", "Alt", "N"], Find<ShortcutKeys>(window, "GlobalShortcutKeys").Keys);

                // The section list drives the page too.
                nav.SelectedIndex = 0;
                window.UpdateLayout();
                Assert.Equal(SettingsPage.Appearance, settings.SelectedPage);
                AssertShown(window, AppearanceIds);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SelectingDarkThroughUiAutomationSwitchesTheThemeLive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var shell = new ShellViewModel(new TestPlatform().Services);
            var settings = shell.Settings;
            var window = CreateWindow(settings, dark: false);
            try
            {
                window.Show();
                window.UpdateLayout();
                var theme = Find<SegmentedControl>(window, "ThemeSegmented");
                var dark = ItemPeers(theme)[1];
                Assert.Equal("Dark", dark.GetName());

                ((ISelectionItemProvider)dark.GetPattern(PatternInterface.SelectionItem)).Select();

                Assert.Equal(ThemePreference.Dark, settings.Theme);
                Assert.True(settings.EffectiveIsDark);
                Assert.Equal(ThemePreference.Dark, shell.SettingsStore.Current.Theme);

                ((SegmentedControlItem)theme.Items[0]).IsSelected = true;
                Assert.Equal(ThemePreference.Light, settings.Theme);
                Assert.False(settings.EffectiveIsDark);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EveryControlAppliesItsSettingLiveAndThePreviewFollows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var platform = new TestPlatform();
            var shell = new ShellViewModel(platform.Services);
            var settings = shell.Settings;
            var window = CreateWindow(settings, dark: false);
            try
            {
                window.Show();
                window.UpdateLayout();
                var preview = Find<Border>(window, "SettingsPreview");
                var previewText = Descendants(preview).OfType<TextBlock>().ToList();
                Assert.Equal(["1", "The quick brown fox jumps over the lazy dog", "2", "0123456789 {}[]() => != ==="], previewText.Select(t => t.Text));

                var wrap = Find<CheckBox>(window, "WordWrapSwitch");
                Assert.Equal("Off", wrap.Content);
                wrap.IsChecked = true;
                Assert.True(settings.WordWrap);
                Assert.True(shell.SettingsStore.Current.WordWrap);
                Assert.Equal("On", wrap.Content);
                Assert.Equal(TextWrapping.Wrap, previewText[1].TextWrapping);

                var lineNumbers = Find<CheckBox>(window, "LineNumbersSwitch");
                Assert.True(lineNumbers.IsChecked);
                lineNumbers.IsChecked = false;
                window.UpdateLayout();
                Assert.False(settings.ShowLineNumbers);
                Assert.Equal(Visibility.Collapsed, previewText[0].Visibility);

                var size = Find<Stepper>(window, "FontSizeStepper");
                Assert.Equal(14d, size.Value);
                Assert.Equal("14 px", size.DisplayText);
                size.Increase();
                Assert.Equal(15, settings.FontSize);
                Assert.Equal(15d, previewText[1].FontSize);

                var fonts = Find<ComboBox>(window, "FontDropdown");
                Assert.Contains("Consolas", fonts.Items.Cast<string>());
                fonts.SelectedItem = "Consolas";
                window.UpdateLayout();
                Assert.Equal("Consolas", settings.EditorFontFamily);
                Assert.Equal("Consolas", previewText[1].FontFamily.Source);

                var tabSize = Find<SegmentedControl>(window, "TabSizeSegmented");
                Assert.Equal(4, tabSize.SelectedValue);
                tabSize.SelectedIndex = 2;
                Assert.Equal(8, settings.TabSize);
                Assert.Equal(8, shell.SettingsStore.Current.TabSize);

                settings.SelectedPage = SettingsPage.General;
                window.UpdateLayout();
                var autostart = Find<CheckBox>(window, "StartWithWindowsSwitch");
                Assert.False(autostart.IsChecked);
                autostart.IsChecked = true;
                Assert.True(platform.Autostart.IsEnabled);
                Assert.True(settings.StartWithWindows);

                var restore = Find<CheckBox>(window, "RestoreTabsSwitch");
                Assert.True(restore.IsChecked);
                restore.IsChecked = false;
                Assert.False(shell.SettingsStore.Current.RestoreTabs);

                // Changes made elsewhere show up in the window.
                settings.WordWrap = false;
                Assert.False(wrap.IsChecked);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheShortcutCaptureTakesTheNextChordAndShowsProblemsInline()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var platform = new TestPlatform();
            platform.Hotkeys.TakenByOtherApps.Add("Ctrl+Alt+J");
            var shell = new ShellViewModel(platform.Services);
            var settings = shell.Settings;
            settings.SelectedPage = SettingsPage.General;
            var window = CreateWindow(settings, dark: true);
            try
            {
                window.Show();
                window.UpdateLayout();
                var change = Find<Button>(window, "GlobalShortcutChange");
                var error = Find<TextBlock>(window, "GlobalShortcutError");
                Assert.Equal("Change", change.Content);
                Assert.Equal("Change global shortcut", AutomationProperties.GetName(change));
                Assert.False(window.HandleCaptureKey(Key.K, ModifierKeys.Control | ModifierKeys.Alt));

                change.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();
                Assert.True(settings.IsCapturingShortcut);
                Assert.Equal("Cancel", change.Content);
                Assert.False(Find<ShortcutKeys>(window, "GlobalShortcutKeys").IsVisible);

                // A modifier alone keeps waiting.
                Assert.True(window.HandleCaptureKey(Key.LeftCtrl, ModifierKeys.Control));
                Assert.True(settings.IsCapturingShortcut);
                Assert.Null(settings.ShortcutError);

                // Taken by another app: the error shows inline and capturing goes on.
                Assert.True(window.HandleCaptureKey(Key.J, ModifierKeys.Control | ModifierKeys.Alt));
                window.UpdateLayout();
                Assert.True(settings.IsCapturingShortcut);
                Assert.Equal("Another app already uses Ctrl+Alt+J. Try a different shortcut.", settings.ShortcutError);
                Assert.True(error.IsVisible);
                Assert.Equal(settings.ShortcutError, error.Text);

                // No modifier: rejected with a reason.
                Assert.True(window.HandleCaptureKey(Key.K, ModifierKeys.None));
                Assert.Contains("Ctrl, Alt or Win", settings.ShortcutError, StringComparison.Ordinal);
                Assert.Equal("Ctrl+Alt+N", settings.GlobalShortcut);

                // A chord with a modifier completes the capture.
                Assert.True(window.HandleCaptureKey(Key.K, ModifierKeys.Control | ModifierKeys.Alt));
                window.UpdateLayout();
                Assert.False(settings.IsCapturingShortcut);
                Assert.Null(settings.ShortcutError);
                Assert.False(error.IsVisible);
                Assert.Equal("Ctrl+Alt+K", settings.GlobalShortcut);
                Assert.Equal("Ctrl+Alt+K", platform.Hotkeys.Registered);
                Assert.Equal("Ctrl+Alt+K", shell.SettingsStore.Current.GlobalHotkey);
                Assert.Equal(["Ctrl", "Alt", "K"], Find<ShortcutKeys>(window, "GlobalShortcutKeys").Keys);
                Assert.Equal("Change", change.Content);

                // Esc cancels without touching the shortcut; Cancel on the button does too.
                settings.BeginShortcutCapture();
                Assert.True(window.HandleCaptureKey(Key.Escape, ModifierKeys.None));
                Assert.False(settings.IsCapturingShortcut);
                change.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                change.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.False(settings.IsCapturingShortcut);
                Assert.Equal("Ctrl+Alt+K", settings.GlobalShortcut);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscClosesTheWindowAndTheLauncherKeepsOneWindowPerOwner()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var shell = new ShellViewModel(new TestPlatform().Services);
            var owner = new Window
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                Width = 800,
                Height = 600,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            owner.Show();
            try
            {
                var first = SettingsWindowLauncher.Show(owner, shell.Settings);
                ThemeService.InstallDesignSystem(first.Resources, dark: false);
                Assert.Same(first, SettingsWindowLauncher.Current);
                Assert.Same(owner, first.Owner);
                Assert.True(first.IsVisible);
                Assert.False(first.ShowInTaskbar);
                Assert.Same(shell.Settings, first.DataContext);

                Assert.Same(first, SettingsWindowLauncher.Show(owner, shell.Settings));

                first.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first), 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                });

                Assert.False(first.IsVisible);
                Assert.Null(SettingsWindowLauncher.Current);
                var again = SettingsWindowLauncher.Show(owner, shell.Settings);
                Assert.NotSame(first, again);
                again.Close();
                Assert.Null(SettingsWindowLauncher.Current);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    // The item peers a UI Automation client walks (the list's children), not the items' own wrapper peers.
    private static List<AutomationPeer> ItemPeers(ListBox list)
    {
        var peers = UIElementAutomationPeer.CreatePeerForElement(list).GetChildren();
        Assert.NotNull(peers);
        return peers;
    }

    internal static SettingsWindow CreateWindow(SettingsViewModel settings, bool dark)
    {
        // Shown off-screen, unactivated and out of the taskbar: the real layout path without disturbing the desktop.
        var window = new SettingsWindow(settings)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        ThemeService.InstallDesignSystem(window.Resources, dark);
        return window;
    }

    internal static T Find<T>(DependencyObject root, string automationId)
        where T : DependencyObject
    {
        var element = WindowActivator.FindByAutomationId(root, automationId);
        Assert.True(element is not null, $"no element with AutomationId {automationId}");
        return Assert.IsAssignableFrom<T>(element);
    }

    private static void AssertShown(Window window, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            var element = Find<FrameworkElement>(window, id);
            Assert.True(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, $"{id} is not shown");
        }
    }

    private static void AssertHidden(Window window, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            Assert.False(Find<FrameworkElement>(window, id).IsVisible, $"{id} should be on the other page");
        }
    }

    // Every shown control got its template from the design system (a missing style key would leave it empty).
    private static void AssertTemplated(Window window)
    {
        foreach (var control in Descendants(window).OfType<Control>().Where(control => control.IsVisible))
        {
            Assert.True(control.Template is not null && VisualTreeHelper.GetChildrenCount(control) > 0, $"{control.GetType().Name} has no template content");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
