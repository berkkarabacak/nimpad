using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using NimPad.App.Shell;
using NimPad.App.Views.Editor;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;
using Xunit;
using AppThemeMode = NimPad.App.Theming.ThemeMode;

namespace NimPad.App.Tests.Shell;

/// <summary>
/// The real main window over a real shell view model on a temporary data folder: startup into a working editor, the UI
/// Automation contract, layouts, themes, dialogs and restart. These catch the runtime-only WPF failures (resource
/// lookups, templates, bindings, automation peers) that the Linux build cannot.
/// </summary>
public class MainWindowTests
{
    private static readonly string[] ContractIds =
    [
        "TitleBar", "TabStrip", "Tab_0", "TabClose_0", "NewTabButton", "MinimizeButton", "MaximizeButton", "CloseButton",
        "BannerHost", "ToastHost", "StatusBar", "StatusPosition", "StatusCharacters", "StatusEncoding", "StatusMode",
        "StatusZoom", "StatusSync", "SidebarToggle", "Editor", "ExplorerSearch", "ExplorerList",
    ];

    [Fact]
    public void FirstLaunchOpensOneEmptyFocusableUntitledNote()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                Assert.True(test.Started);
                Assert.True(test.Window.IsVisible);
                var tab = Assert.Single(test.Shell.Tabs.Tabs);
                Assert.Equal("Untitled 1", tab.Title);
                Assert.Equal("Ln 1, Col 1", test.Shell.StatusBar.PositionText);
                Assert.Equal("Local only", test.Shell.StatusBar.SyncText);

                var editor = test.Window.EditorView.TextEditor;
                Assert.Same(test.Window.EditorView.Host!.DocumentOf(tab), editor.Document);
                Assert.Equal(string.Empty, editor.Document.Text);
                Assert.False(editor.IsReadOnly);
                var buffer = Assert.IsType<EditorBuffer>(tab.Buffer);
                Assert.Same(editor.Document, buffer.Document);
                Assert.True(test.Shell.Editor.ShowEmptyHint);
                Assert.Equal(Visibility.Collapsed, test.Window.EmptyState.Visibility);
                Assert.Empty(test.Shell.Banners.Banners);
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Fact]
    public void ExposesTheAutomationContract()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                var window = test.Window;
                Assert.Equal("NimPad", window.Title);
                var peers = Find.Peers(window);
                var ids = peers.Select(peer => peer.GetAutomationId()).ToHashSet(StringComparer.Ordinal);
                Assert.Equal("MainWindow", peers[0].GetAutomationId());
                Assert.Equal("theme:light", peers[0].GetItemStatus());
                foreach (var id in ContractIds)
                {
                    Assert.True(ids.Contains(id), $"The automation tree has no {id}.");
                }

                AutomationPeer Peer(string id) => peers.First(peer => peer.GetAutomationId() == id);
                Assert.Equal("Untitled 1", Peer("Tab_0").GetName());
                Assert.Equal("saved", Peer("Tab_0").GetItemStatus());
                Assert.Equal("Ln 1, Col 1", Peer("StatusPosition").GetName());
                Assert.Equal("0 characters", Peer("StatusCharacters").GetName());
                Assert.Equal("UTF-8", Peer("StatusEncoding").GetName());
                Assert.Equal("Plain Text", Peer("StatusMode").GetName());
                Assert.Equal("100%", Peer("StatusZoom").GetName());
                Assert.Equal("Local only", Peer("StatusSync").GetName());

                // The editor's own peer carries the text (Value pattern).
                var editorPeer = Peer("Editor");
                Assert.IsAssignableFrom<IValueProvider>(editorPeer.GetPattern(PatternInterface.Value));
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Fact]
    public void TypedTextIsSavedAndComesBackWithTheCaretAfterARestart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var first = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                var editor = first.Window.EditorView.TextEditor;
                editor.Document.Insert(0, "hello world");
                editor.TextArea.Caret.Offset = 5;
                var tab = first.TabContainer(0);
                Assert.Equal("unsaved", System.Windows.Automation.AutomationProperties.GetItemStatus(tab));
                Assert.Equal("Ln 1, Col 6", first.Shell.StatusBar.PositionText);
                Assert.Equal("11 characters", first.Shell.StatusBar.CharacterCountText);
                await UiThread.WaitUntilAsync(() => System.Windows.Automation.AutomationProperties.GetItemStatus(tab) == "saved", "the autosave finished");
            }
            finally
            {
                await first.CloseAsync();
            }

            var second = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                var tab = Assert.Single(second.Shell.Tabs.Tabs);
                var editor = second.Window.EditorView.TextEditor;
                Assert.Equal("hello world", editor.Document.Text);
                await UiThread.WaitUntilAsync(() => editor.TextArea.Caret.Offset == 5, "the caret is restored");
                Assert.Equal(5, tab.CaretOffset);
                Assert.Equal("Ln 1, Col 6", second.Shell.StatusBar.PositionText);
            }
            finally
            {
                await second.CloseAsync();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutFollowsTheWindowWidth(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path, dark);
            try
            {
                var window = test.Window;
                Assert.Equal(dark ? "theme:dark" : "theme:light", window.ThemeStatus);
                var appName = Find.ById<TextBlock>(window, "AppName");
                var characters = Find.ById<TextBlock>(window, "StatusCharacters");
                var more = Find.ById<Button>(window, "StatusMore");
                var mode = Find.ById<Button>(window, "StatusMode");

                // Wide: everything shown, the sidebar docked at its width beside the editor.
                await test.ResizeAsync(1120);
                Assert.Equal(LayoutMode.Wide, test.Shell.LayoutMode);
                Assert.True(appName.IsVisible);
                Assert.True(characters.IsVisible);
                Assert.True(mode.IsVisible);
                Assert.False(more.IsVisible);
                Assert.True(test.Shell.IsSidebarOpen);
                Assert.True(window.SidebarHost.IsVisible);
                Assert.Equal(ShellViewModel.DefaultSidebarWidth, window.SidebarColumn.ActualWidth, precision: 0);
                Assert.Equal(40, window.TitleBar.ActualHeight, precision: 0);
                Assert.Equal(24, window.StatusBar.ActualHeight, precision: 0);
                Assert.True(window.EditorView.ActualWidth > 600);

                // Medium: the app name and the character count leave; the sidebar becomes an overlay (closed).
                await test.ResizeAsync(600);
                Assert.Equal(LayoutMode.Medium, test.Shell.LayoutMode);
                Assert.False(appName.IsVisible);
                Assert.False(characters.IsVisible);
                Assert.True(mode.IsVisible);
                Assert.False(test.Shell.IsSidebarOpen);
                Assert.False(window.SidebarHost.IsVisible);
                test.Shell.ToggleSidebarCommand.Execute(null);
                await UiThread.SettleAsync();
                Assert.True(window.SidebarHost.IsVisible);
                Assert.True(window.OverlayDismissLayer.IsVisible);
                Assert.Equal(0, window.SidebarColumn.ActualWidth, precision: 0);
                test.Shell.DismissSidebarOverlayCommand.Execute(null);
                await UiThread.SettleAsync();
                Assert.False(window.SidebarHost.IsVisible);

                // Narrow: encoding, mode and zoom move into the ⋯ menu.
                await test.ResizeAsync(450);
                Assert.Equal(LayoutMode.Narrow, test.Shell.LayoutMode);
                Assert.False(mode.IsVisible);
                Assert.True(more.IsVisible);
                Assert.True(Find.ById<TextBlock>(window, "StatusPosition").IsVisible);
                Assert.True(Find.ById<Button>(window, "StatusSync").IsVisible);
                Assert.True(Find.ById<Button>(window, "NewTabButton").IsVisible);
                Assert.True(window.TitleBar.TabStrip.ActualWidth >= TabStripPanel.MinTabWidth);

                // Back to wide: the docked sidebar comes back as it was.
                await test.ResizeAsync(1000);
                Assert.True(window.SidebarHost.IsVisible);
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Fact]
    public void TheEffectiveThemeIsReportedLive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path, dark: false);
            try
            {
                Assert.Equal("theme:light", test.Window.ThemeStatus);
                var light = test.Window.TitleBar.Root.Background;
                test.Themes.Apply(AppThemeMode.Dark);
                await UiThread.SettleAsync();
                Assert.Equal("theme:dark", test.Window.ThemeStatus);
                Assert.Equal("theme:dark", Find.Peers(test.Window)[0].GetItemStatus());
                Assert.NotEqual(light, test.Window.TitleBar.Root.Background);
                Assert.True(test.Window.EditorView.IsDarkTheme);
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Fact]
    public void TabsAreNumberedAndCarryTheirStates()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                await test.Shell.NewNoteCommand.ExecuteAsync(null);
                await test.Shell.NewNoteCommand.ExecuteAsync(null);
                await test.WaitForEditorAsync();
                Assert.Equal(3, test.Shell.Tabs.Tabs.Count);
                for (var i = 0; i < 3; i++)
                {
                    var container = test.TabContainer(i);
                    Assert.Equal("Tab_" + i, System.Windows.Automation.AutomationProperties.GetAutomationId(container));
                    Assert.Equal(test.Shell.Tabs.Tabs[i].Title, System.Windows.Automation.AutomationProperties.GetName(container));
                    Assert.Single(Find.AllById(container, "TabClose_" + i));
                }

                // The selected (last) tab is the one in the editor; the widths follow the design.
                Assert.Same(test.Shell.Tabs.Tabs[2], test.Shell.Tabs.SelectedTab);
                Assert.Equal(TabStripPanel.MaxTabWidth, test.TabContainer(0).ActualWidth, precision: 0);

                // Pinning moves the tab to the front at the pinned width; the IDs follow the visual order.
                var last = test.Shell.Tabs.Tabs[2];
                test.Shell.Tabs.TogglePinCommand.Execute(last);
                await UiThread.SettleAsync();
                Assert.Same(last, test.Shell.Tabs.Tabs[0]);
                var pinned = test.TabContainer(0);
                Assert.True(pinned.IsPinnedTab);
                Assert.Equal(TabStripPanel.PinnedTabWidth, pinned.ActualWidth, precision: 0);
                Assert.Equal(last.Title, System.Windows.Automation.AutomationProperties.GetName(pinned));

                // Closing renumbers.
                await test.Shell.Tabs.CloseAsync(test.Shell.Tabs.Tabs[1]);
                await UiThread.SettleAsync();
                Assert.Equal(2, test.Shell.Tabs.Tabs.Count);
                Assert.Equal("Tab_1", System.Windows.Automation.AutomationProperties.GetAutomationId(test.TabContainer(1)));
                Assert.Empty(Find.AllById(test.Window, "Tab_2"));
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Fact]
    public void ClosingEveryTabShowsTheEmptyState()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                test.Window.EditorView.TextEditor.Document.Insert(0, "keep me");
                await test.Shell.CloseTabCommand.ExecuteAsync(null);
                await UiThread.WaitUntilAsync(() => test.Shell.IsEmptyStateVisible, "every tab is closed");
                await UiThread.SettleAsync();
                var newNote = Find.ById<Button>(test.Window, "EmptyStateNewNote");
                var reopen = Find.ById<Button>(test.Window, "EmptyStateReopen");
                Assert.True(newNote.IsVisible);
                Assert.True(reopen.IsVisible);
                Assert.True(reopen.IsEnabled);
                Assert.True(test.Window.EditorView.TextEditor.IsReadOnly);

                ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(reopen).GetPattern(PatternInterface.Invoke)).Invoke();
                await UiThread.WaitUntilAsync(() => test.Shell.Tabs.Tabs.Count == 1, "the closed tab is back");
                await test.WaitForEditorAsync();
                Assert.Equal("keep me", test.Window.EditorView.TextEditor.Document.Text);
                Assert.False(newNote.IsVisible);
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Fact]
    public void KeyboardShortcutsDialogOpensOverAScrimAndCloses()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                test.Shell.ShowShortcutsCommand.Execute(null);
                await UiThread.SettleAsync();
                Assert.True(test.Window.Modal.IsOpen);
                var dialog = Find.ById<ShortcutsDialog>(test.Window, "ShortcutsDialog");
                Assert.True(dialog.IsVisible);
                Assert.Single(Find.AllById(dialog, "ShortcutsFilter"));
                Assert.NotEmpty(test.Shell.Shortcuts.Groups);
                Find.Peer(test.Window, "ShortcutsFilter");

                test.Shell.Shortcuts.Filter = "zoomm";
                await UiThread.SettleAsync();
                Assert.True(dialog.EmptyState.IsVisible);

                test.Window.Modal.CancelCurrent();
                await UiThread.SettleAsync();
                Assert.False(test.Window.Modal.IsOpen);
                Assert.Empty(Find.AllById(test.Window, "ShortcutsDialog"));
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Theory]
    [InlineData(false, "ConfirmPrimary", true)]
    [InlineData(true, "ConfirmCancel", false)]
    public void ConfirmDialogReturnsTheChosenButton(bool destructive, string button, bool expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                var answer = test.Dialogs.ConfirmAsync(new ConfirmRequest("Delete “Old agenda” forever?", "It can't be restored.", "Delete forever", destructive));
                await UiThread.SettleAsync();
                var dialog = Find.ById<ConfirmDialog>(test.Window, "ConfirmDialog");
                Assert.Equal("Delete “Old agenda” forever?", System.Windows.Automation.AutomationProperties.GetName(dialog));
                Assert.Single(Find.AllById(dialog, "ConfirmPrimary"));
                Assert.Single(Find.AllById(dialog, "ConfirmCancel"));
                var target = Find.ById<Button>(dialog, button);
                ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(target).GetPattern(PatternInterface.Invoke)).Invoke();
                Assert.Equal(expected, await answer.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(test.Window.Modal.IsOpen);
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Fact]
    public void SidebarToggleFollowsTheViewModel()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                var toggle = Find.ById<CommandToggleButton>(test.Window, "SidebarToggle");
                Assert.True(test.Shell.IsSidebarOpen);
                Assert.True(toggle.IsChecked);

                ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                await UiThread.SettleAsync();
                Assert.False(test.Shell.IsSidebarOpen);
                Assert.False(toggle.IsChecked);
                Assert.False(test.Window.SidebarHost.IsVisible);

                test.Shell.ToggleSidebarCommand.Execute(null);
                await UiThread.SettleAsync();
                Assert.True(toggle.IsChecked);
                Assert.True(test.Window.SidebarHost.IsVisible);
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Fact]
    public void AStartupFailureShowsABannerInsteadOfCrashing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        using var data = new TempDataDir();

        // The profile folder cannot be created: a file is in the way.
        File.WriteAllText(Path.Combine(data.Path, "profiles"), "not a folder");
        UiThread.Run(async () =>
        {
            var test = await ShellTestWindow.OpenAsync(data.Path);
            try
            {
                Assert.False(test.Started);
                Assert.True(test.Window.IsVisible);
                var banner = Assert.Single(test.Shell.Banners.Banners);
                Assert.Equal(MainWindow.StartupFailedBannerKey, banner.Key);
                await UiThread.SettleAsync();
                var host = (UIElement)Find.ById<FrameworkElement>(test.Window, "BannerHost");
                Assert.Contains(Find.Peers(host), peer => peer.GetAutomationId() == "Banner" && peer.GetName() == banner.Message);
                Assert.True(test.Window.EditorView.TextEditor.IsReadOnly);
            }
            finally
            {
                await test.CloseAsync();
            }
        });
    }

    [Theory]
    [InlineData(TabSaveState.Saved, false, "saved")]
    [InlineData(TabSaveState.Saved, true, "unsaved")]
    [InlineData(TabSaveState.Pending, true, "unsaved")]
    [InlineData(TabSaveState.Failed, true, "error")]
    public void TabItemStatusFollowsTheContract(TabSaveState state, bool dirty, string expected) =>
        Assert.Equal(expected, ShellConverters.ItemStatusOf(state, dirty));
}
