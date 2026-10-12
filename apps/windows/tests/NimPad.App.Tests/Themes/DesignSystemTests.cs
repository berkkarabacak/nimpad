using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NimPad.App.Controls;
using NimPad.App.Theming;
using Xunit;

namespace NimPad.App.Tests.Themes;

/// <summary>The design system loads in both themes and the gallery lays out every control.</summary>
public class DesignSystemTests
{
    private static readonly string[] TextStyles =
    [
        "TextBase", "DisplayText", "PageTitleText", "DialogTitleText", "SubtitleText", "BodyText", "BodyLabelText",
        "ControlText", "ControlNameText", "TabText", "TabSelectedText", "SecondaryText", "CaptionText", "MicroText",
        "TimestampText", "EditorText",
    ];

    private static readonly string[] Doubles =
    [
        "Space4", "Space8", "Space12", "Space16", "Space20", "Space24", "Space32", "Space40", "Space48",
        "TitleBarHeight", "TabHeight", "TabMinWidth", "TabMaxWidth", "PinnedTabWidth", "NewTabButtonSize", "AvatarSize",
        "WindowControlWidth", "WindowControlHeight", "StatusBarHeight", "SidebarWidth", "SettingsNavWidth",
        "ControlHeight", "SmallControlHeight", "LargeControlHeight", "IconButtonSize", "MenuRowHeight",
        "BannerMinHeight", "IconSize", "AppMarkSize", "FocusRingThickness", "ScrollBarIdleWidth", "ScrollBarHoverWidth",
        "DisabledOpacity", "MenuDisabledOpacity", "DisplayFontSize", "BodyFontSize", "EditorFontSize",
        "EditorLineHeight",
    ];

    /// <summary>The icon list from the design-system brief (plus Minus, used by the stepper).</summary>
    private static readonly string[] Icons =
    [
        "AppMark", "Plus", "Minus", "Close", "Pin", "Unpin", "Search", "ChevronRight", "ChevronDown", "ChevronLeft",
        "ChevronUp", "Folder", "FolderOpen", "Document", "DocumentCode", "Trash", "Restore", "More", "SidebarToggle",
        "Check", "Warning", "ErrorCircle", "Info", "Computer", "Sort", "Settings", "Link", "Copy", "Rename", "Minimize",
        "Maximize", "RestoreWindow", "Keyboard", "ArrowUp", "ArrowDown", "MatchCase", "WholeWord", "Regex", "Replace",
        "ReplaceAll", "Spinner", "Import", "Export", "ExternalFile",
    ];

    private static readonly string[] KeyedStyles =
    [
        "FocusVisual", "FocusVisualSmall", "FocusVisualPill", "InsetFocusVisual", "InsetFocusVisualSmall", "AppWindow",
        "Divider", "VerticalDivider", "ButtonBaseStyle", "PrimaryButton", "SecondaryButton", "QuietButton",
        "DestructiveButton", "SmallButton", "SmallPrimaryButton", "SmallQuietButton", "SmallDestructiveButton",
        "LargeButton", "LinkButton", "IconButton", "SmallIconButton", "NewTabButton", "IconToggleButton", "FilterChip",
        "TitleBarButton", "CloseTitleBarButton", "StepperButton", "ChromelessTextBox", "ToggleSwitch", "ComboBoxToggle",
        "ScrollBarPageButton", "ScrollBarThumb", "ChromelessListBox", "NoteRowItem", "NavItem", "Card", "FlyoutCard",
        "DialogCard", "ToastCard", "CalloutCard", "IconBadge", "StatusPill", "OkPill", "AccentPill", "WarnPill",
        "ErrPill", "InfoBanner", "WarningBanner", "ErrorBanner",
    ];

    private static readonly Type[] ImplicitStyles =
    [
        typeof(Button), typeof(TextBox), typeof(ComboBox), typeof(ComboBoxItem), typeof(CheckBox), typeof(RadioButton),
        typeof(ContextMenu), typeof(MenuItem), typeof(Menu), typeof(ScrollViewer), typeof(ScrollBar), typeof(ListBox),
        typeof(ListBoxItem), typeof(ProgressBar), typeof(ToolTip), typeof(Separator), typeof(System.Windows.Documents.Hyperlink),
        typeof(SearchBox), typeof(SegmentedControl), typeof(SegmentedControlItem), typeof(Stepper), typeof(KeyCap),
        typeof(ShortcutKeys), typeof(Avatar), typeof(Spinner), typeof(SkeletonLine), typeof(Banner),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesignSystemLoadsEveryDocumentedResource(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var resources = new ResourceDictionary();
            ThemeService.InstallDesignSystem(resources, dark);
            Assert.True(ThemeService.IsTokenDictionary(resources.MergedDictionaries[0]));

            Assert.IsType<FontFamily>(resources["UiFontFamily"]);
            Assert.IsType<FontFamily>(resources["EditorFontFamily"]);
            foreach (var key in TextStyles)
            {
                var style = Assert.IsType<Style>(resources[key]);
                Assert.Equal(typeof(TextBlock), style.TargetType);
            }

            foreach (var key in Doubles)
            {
                Assert.IsType<double>(resources[key]);
            }

            Assert.Equal(new CornerRadius(6), resources["Radius6"]);
            Assert.Equal(new Thickness(32, 20, 32, 20), resources["EditorPadding"]);
            Assert.IsType<DropShadowEffect>(resources["FlyoutShadow"]);
            Assert.IsType<DropShadowEffect>(resources["DialogShadow"]);

            foreach (var key in Icons)
            {
                var geometry = Assert.IsAssignableFrom<Geometry>(resources[key]);
                Assert.True(geometry.IsFrozen, $"{key} must be frozen");
                Assert.False(geometry.Bounds.IsEmpty, $"{key} is empty");
                Assert.True(geometry.Bounds.Right <= 24.01 && geometry.Bounds.Bottom <= 24.01, $"{key} leaves the 24 grid");
            }

            Assert.IsType<DrawingImage>(resources["AppMarkImage"]);

            foreach (var key in KeyedStyles)
            {
                Assert.True(resources[key] is Style, $"missing style {key}");
            }

            foreach (var type in ImplicitStyles)
            {
                Assert.True(resources[type] is Style, $"missing implicit style for {type.Name}");
            }

            Assert.True(resources[MenuItem.SeparatorStyleKey] is Style, "missing menu separator style");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GalleryMeasuresAndArrangesInBothThemes(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var window = new DesignGalleryWindow(dark);
            try
            {
                window.Measure(new Size(1240, 860));
                window.Arrange(new Rect(0, 0, 1240, 860));
                window.UpdateLayout();

                Assert.Equal(dark, window.IsDark);
                Assert.Equal(dark ? "Dark" : "Light", window.FindResource("ThemeName"));
                Assert.True(window.DesiredSize.Width > 0 && window.DesiredSize.Height > 0);

                // Every measured, templated control in the gallery got its template from the design system.
                var elements = Descendants(window).ToList();
                foreach (var control in elements.OfType<Control>())
                {
                    if (control.Visibility == Visibility.Visible && control.IsMeasureValid && control.Template is not null)
                    {
                        Assert.True(
                            VisualTreeHelper.GetChildrenCount(control) > 0,
                            $"{control.GetType().Name} has no template content");
                    }
                }

                // The custom controls are present and their implicit styles supplied a template.
                Type[] templated =
                [
                    typeof(SearchBox), typeof(Stepper), typeof(SegmentedControl), typeof(SegmentedControlItem),
                    typeof(KeyCap), typeof(ShortcutKeys), typeof(Avatar), typeof(Banner),
                ];
                foreach (var type in templated)
                {
                    var instances = elements.OfType<Control>().Where(type.IsInstanceOfType).ToList();
                    Assert.True(instances.Count > 0, $"no {type.Name} in the gallery");
                    Assert.All(instances, c => Assert.True(c.Template is not null && VisualTreeHelper.GetChildrenCount(c) > 0,
                        $"{type.Name} has no template"));
                }

                Assert.Contains(elements, e => e is Spinner);
                Assert.Contains(elements, e => e is SkeletonLine);

                // The ICONS section draws every icon key at 16 and 24 px, all resolved through DynamicResource.
                var drawnIcons = elements.OfType<Icon>().Count(icon => icon.Data is not null);
                Assert.True(drawnIcons >= DesignGalleryWindow.IconKeys.Count * 2, $"only {drawnIcons} icons have data");

                // Theme-dependent values are live: a primary button follows the token swap.
                var primaryStyle = (Style)window.FindResource("PrimaryButton");
                var primary = elements.OfType<Button>().First(b => ReferenceEquals(b.Style, primaryStyle) && b.IsEnabled &&
                    !Ui.GetForceHover(b) && !Ui.GetForcePressed(b));
                Assert.Same(window.FindResource("AccentBrush"), primary.Background);

                window.SetTheme(!dark);
                window.UpdateLayout();
                Assert.Equal(dark ? "Light" : "Dark", window.FindResource("ThemeName"));
                Assert.Same(window.FindResource("AccentBrush"), primary.Background);
                var expectedAccent = TokenTests.ParseToken(dark
                    ? TokenTests.Expected["Accent"].Light
                    : TokenTests.Expected["Accent"].Dark);
                Assert.Equal(expectedAccent, ((SolidColorBrush)primary.Background).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ControlsBehave()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            Assert.Equal(new[] { "Ctrl", "Shift", "T" }, ShortcutKeys.ParseGesture("Ctrl+Shift+T"));
            Assert.Equal(new[] { "Ctrl", "+" }, ShortcutKeys.ParseGesture("Ctrl++"));
            Assert.Equal(new[] { "Ctrl", "-" }, ShortcutKeys.ParseGesture("Ctrl+-"));
            Assert.Empty(ShortcutKeys.ParseGesture(" "));

            var stepper = new Stepper { Minimum = 8, Maximum = 32, Value = 14, Suffix = "px" };
            Assert.Equal("14 px", stepper.DisplayText);
            stepper.Increase();
            Assert.Equal(15d, stepper.Value);
            stepper.Value = 100;
            Assert.Equal(32d, stepper.Value);
            Assert.False(stepper.CanIncrease);
            stepper.Value = -5;
            Assert.Equal(8d, stepper.Value);
            Assert.False(stepper.CanDecrease);

            var search = new SearchBox { Text = "beta" };
            var cleared = 0;
            search.Cleared += (_, _) => cleared++;
            Assert.True(search.HasText);
            search.Clear();
            Assert.Equal(string.Empty, search.Text);
            Assert.False(search.HasText);
            Assert.Equal(1, cleared);

            var spinner = new Spinner();
            Assert.False(spinner.IsSpinning); // not loaded: no clock running
        });
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
