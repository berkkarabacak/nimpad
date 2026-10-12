using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NimPad.App.Controls;
using NimPad.App.Themes;
using IconElement = NimPad.App.Controls.Icon;

namespace NimPad.App.Theming;

/// <summary>
/// The design gallery (see the XAML header). Sections are generated here so every control can be shown in each
/// of the six interaction states: default, hover (<see cref="Ui.ForceHoverProperty"/>), pressed
/// (<see cref="Ui.ForcePressedProperty"/>), focused (the kit's ring drawn statically, or
/// <see cref="Ui.ForceFocusedProperty"/> for text inputs), selected/on and disabled.
/// </summary>
internal partial class DesignGalleryWindow : Window
{
    /// <summary>Every token name (each has a "&lt;Name&gt;Color" and a "&lt;Name&gt;Brush" resource).</summary>
    public static IReadOnlyList<string> TokenNames { get; } =
    [
        "Chrome", "Surface", "Raised", "Text", "Text2", "Text3", "Hover", "Press", "Sep", "Line", "Line2", "Accent",
        "AccentHover", "AccentPressed", "AccentSoft", "OnAccent", "Selection", "CurrentLine", "Match", "MatchCurrent",
        "AvatarBg", "AvatarFg", "Ok", "OkSoft", "Warn", "WarnSoft", "Err", "ErrSoft", "DiffAdd", "DiffDel",
        "SyntaxKey", "SyntaxString", "SyntaxNumber", "SyntaxLiteral", "SyntaxPunctuation", "SyntaxComment",
        "CloseHover", "Scrim",
    ];

    /// <summary>Every icon geometry key in Themes/Icons.xaml.</summary>
    public static IReadOnlyList<string> IconKeys { get; } =
    [
        "AppMark", "AppMarkTile", "AppMarkLines", "Plus", "Minus", "Close", "Pin", "Unpin", "Search", "SearchOff",
        "ChevronRight", "ChevronDown", "ChevronLeft", "ChevronUp", "Folder", "FolderOpen", "FolderAdd", "Document",
        "DocumentCode", "DocumentAdd", "ExternalFile", "Trash", "Restore", "Redo", "More", "SidebarToggle", "Check",
        "CheckCircle", "Warning", "ErrorCircle", "Info", "Computer", "Sort", "Settings", "Link", "ExternalLink", "Copy",
        "Cut", "Paste", "Rename", "Minimize", "Maximize", "RestoreWindow", "WindowClose", "Keyboard", "ArrowUp",
        "ArrowDown", "MatchCase", "WholeWord", "Regex", "Replace", "ReplaceAll", "Spinner", "Import", "Export", "Sync",
        "CloudOff", "Sun", "User", "Sliders", "SignOut",
    ];

    private static readonly (string Key, string Spec, string Sample)[] TypeRamp =
    [
        ("DisplayText", "Display 44 / 48, 600", "Your notes."),
        ("PageTitleText", "Page title 24 / 32, 600", "Synchronization"),
        ("DialogTitleText", "Dialog title 20 / 26, 600", "Keyboard shortcuts"),
        ("SubtitleText", "Subtitle 16 / 22, 600", "Everything is up to date"),
        ("BodyText", "Body 14 / 20, 400", "Restore previous tabs"),
        ("BodyLabelText", "Body label 14 / 20, 500", "Start with Windows"),
        ("ControlText", "Control 13.5 / 18, 500", "Replace all"),
        ("ControlNameText", "Control name 13.5 / 18, 600", "Meeting notes"),
        ("TabText", "Tab 13 / 18, 400", "config.json"),
        ("TabSelectedText", "Tab selected 13 / 18, 500", "Meeting notes"),
        ("SecondaryText", "Secondary 12.5 / 17, 400", "Open NimPad in the background when you sign in."),
        ("CaptionText", "Caption 12 / 16, 400", "Ln 13, Col 31 · 312 characters · UTF-8"),
        ("MicroText", "Micro 11 / 14, 600, caps", "YESTERDAY"),
        ("TimestampText", "Timestamp 11.5 / 16, 400", "2:14 PM"),
        ("EditorText", "Editor 14 / 22, mono", "- Ship the beta on Thursday"),
    ];

    private static readonly string[] StateColumns =
        ["DEFAULT", "HOVER", "PRESSED", "FOCUSED", "SELECTED / ON", "DISABLED"];

    private bool _dark;

    public DesignGalleryWindow()
        : this(dark: null)
    {
    }

    /// <param name="dark">The theme to show; null follows <see cref="ThemeService.Current"/> (light without one).</param>
    public DesignGalleryWindow(bool? dark)
    {
        InitializeComponent();
        _dark = dark ?? ThemeService.Current?.IsDark ?? false;
        Resources.MergedDictionaries.Insert(0, ThemeService.CreateTokens(_dark));
        Resources.MergedDictionaries.Insert(1, new DesignSystem());
        ThemeSwitch.SelectedIndex = _dark ? 1 : 0;
        ThemeSwitch.SelectionChanged += (_, _) => SetTheme(ThemeSwitch.SelectedIndex == 1);
        WindowThemeHelper.ApplyImmersiveDarkMode(this, _dark);
        BuildSections();
    }

    private enum FocusLook
    {
        Outer,
        Inset,
        InTemplate,
        None,
    }

    /// <summary>True while the dark token set is shown.</summary>
    public bool IsDark => _dark;

    /// <summary>Swaps the gallery's own token set (the first merged dictionary).</summary>
    public void SetTheme(bool dark)
    {
        if (dark == _dark)
        {
            return;
        }

        _dark = dark;
        Resources.MergedDictionaries[0] = ThemeService.CreateTokens(dark);
        ThemeSwitch.SelectedIndex = dark ? 1 : 0;
        WindowThemeHelper.ApplyImmersiveDarkMode(this, dark);
    }

    private void BuildSections()
    {
        Sections.Children.Add(Section("COLOR TOKENS", BuildSwatches()));
        Sections.Children.Add(Section("TYPE", BuildTypeRamp()));
        Sections.Children.Add(Section("ICONS", BuildIcons()));
        Sections.Children.Add(Section("BUTTONS", BuildButtonMatrix()));
        Sections.Children.Add(Section("INPUTS AND SELECTION", BuildInputMatrix()));
        Sections.Children.Add(Section("ROWS AND MENU ITEMS", BuildRowMatrix()));
        Sections.Children.Add(Section("WINDOW CHROME", BuildChrome()));
        Sections.Children.Add(Section("SURFACES", BuildSurfaces()));
        Sections.Children.Add(Section("STATUS, LOADING AND SCROLLING", BuildStatus()));
    }

    // ------------------------------------------------------------------ sections

    private static StackPanel BuildSwatches()
    {
        var panel = new WrapPanel();
        foreach (var name in TokenNames)
        {
            var chip = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
            };
            chip.SetResourceReference(Border.BackgroundProperty, name + "Brush");
            chip.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            var label = Text(name, "CaptionText", "Text2Brush");
            label.Margin = new Thickness(8, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            var item = new StackPanel { Orientation = Orientation.Horizontal, Width = 196, Margin = new Thickness(0, 0, 8, 8) };
            item.Children.Add(chip);
            item.Children.Add(label);
            panel.Children.Add(item);
        }

        var stack = new StackPanel();
        stack.Children.Add(panel);
        return stack;
    }

    private static Grid BuildTypeRamp()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < TypeRamp.Length; i++)
        {
            var (key, spec, sample) = TypeRamp[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Text($"{key} · {spec}", "CaptionText", "Text3Brush");
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 6, 16, 6);
            var text = Text(sample, key, "TextBrush");
            text.Margin = new Thickness(0, 6, 0, 6);
            Grid.SetRow(label, i);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            grid.Children.Add(label);
            grid.Children.Add(text);
        }

        return grid;
    }

    private static WrapPanel BuildIcons()
    {
        var panel = new WrapPanel();
        foreach (var key in IconKeys)
        {
            var filled = key.StartsWith("AppMark", StringComparison.Ordinal);
            var cell = new StackPanel { Width = 104, Margin = new Thickness(0, 0, 4, 12) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var size in new[] { 16.0, 24.0 })
            {
                var icon = new IconElement { Size = size, Filled = filled, Margin = new Thickness(6, 0, 6, 0) };
                icon.SetResourceReference(IconElement.DataProperty, key);
                icon.SetResourceReference(IconElement.ForegroundProperty, filled ? "AccentBrush" : "TextBrush");
                icon.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(icon);
            }

            var label = Text(key, "CaptionText", "Text2Brush");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 6, 0, 0);
            cell.Children.Add(row);
            cell.Children.Add(label);
            panel.Children.Add(cell);
        }

        var logo = new StackPanel { Width = 104, Margin = new Thickness(0, 0, 4, 12) };
        var image = new Image { Width = 24, Height = 24 };
        image.SetResourceReference(Image.SourceProperty, "AppMarkImage");
        var logoLabel = Text("AppMarkImage", "CaptionText", "Text2Brush");
        logoLabel.HorizontalAlignment = HorizontalAlignment.Center;
        logoLabel.Margin = new Thickness(0, 6, 0, 0);
        logo.Children.Add(image);
        logo.Children.Add(logoLabel);
        panel.Children.Add(logo);
        return panel;
    }

    private static Grid BuildButtonMatrix() => Matrix(
        new MatrixRow("Primary", "Accent → hover → pressed", () => Btn("PrimaryButton", "Sync now", "Sync"),
            FocusLook.Outer, 6),
        new MatrixRow("Secondary", "Hover wash + 1 px inset", () => Btn("SecondaryButton", "Sync now", "Sync"),
            FocusLook.Outer, 6),
        new MatrixRow("Quiet", "Text until hovered", () => Btn("QuietButton", "Cancel"), FocusLook.Outer, 6),
        new MatrixRow("Destructive", "Err text, ErrSoft wash",
            () => Btn("DestructiveButton", "Delete forever", "Trash"), FocusLook.Outer, 6),
        new MatrixRow("Small", "26 px", () => Btn("SmallButton", "Replace", "Restore"), FocusLook.Outer, 6),
        new MatrixRow("Small primary", "26 px", () => Btn("SmallPrimaryButton", "Replace"), FocusLook.Outer, 6),
        new MatrixRow("Large", "44 px, welcome", () => Btn("LargeButton", "Continue"), FocusLook.Outer, 6),
        new MatrixRow("Link", "Accent text", () => Btn("LinkButton", "Undo"), FocusLook.Outer, 4),
        new MatrixRow("Icon", "28 x 28, radius 6", () => Btn("IconButton", null, "ChevronUp", "Collapse"),
            FocusLook.Outer, 6),
        new MatrixRow("Small icon", "20 x 20, radius 4", () => Btn("SmallIconButton", null, "Close", "Close"),
            FocusLook.Outer, 4),
        new MatrixRow("Icon toggle", "Checked = AccentSoft",
            () => Toggle("IconToggleButton", null, "MatchCase", "Match case"), FocusLook.Outer, 6,
            e => ((ToggleButton)e).IsChecked = true),
        new MatrixRow("Filter chip", "Checked = AccentSoft", () => Toggle("FilterChip", "Pinned 2", null, null),
            FocusLook.Outer, 14, e => ((ToggleButton)e).IsChecked = true),
        new MatrixRow("New tab", "30 x 30", () => Btn("NewTabButton", null, null, "New note"), FocusLook.Outer, 6),
        new MatrixRow("Window control", "46 x 40, not a tab stop",
            () => Btn("TitleBarButton", null, "Maximize", "Maximize"), FocusLook.None, 0),
        new MatrixRow("Close window", "Close turns red",
            () => Btn("CloseTitleBarButton", null, null, "Close"), FocusLook.None, 0));

    private static Grid BuildInputMatrix() => Matrix(
        new MatrixRow("Text field", "Line2 → Text3 → Accent", () =>
        {
            var box = new TextBox { Width = 170 };
            Ui.SetPlaceholder(box, "Replace with");
            AutomationProperties.SetName(box, "Replace with");
            return box;
        }, FocusLook.InTemplate, 6),
        new MatrixRow("Text field, error", "c:Ui.HasError + ErrorText", () =>
        {
            var box = new TextBox { Width = 170, Text = "beta(" };
            Ui.SetHasError(box, true);
            Ui.SetErrorText(box, "Unmatched \"(\"");
            return box;
        }, FocusLook.InTemplate, 6),
        new MatrixRow("Search box", "Icon inside, clear button", () => new SearchBox
        {
            Width = 190,
            Placeholder = "Search all notes",
        }, FocusLook.InTemplate, 6, e => ((SearchBox)e).Text = "beta"),
        new MatrixRow("Dropdown", "32 px, raised popup", () =>
        {
            var combo = new ComboBox { Width = 170, ItemsSource = new[] { "Cascadia Mono", "Consolas", "JetBrains Mono" } };
            combo.SelectedIndex = 0;
            AutomationProperties.SetName(combo, "Editor font");
            return combo;
        }, FocusLook.Outer, 6),
        new MatrixRow("Stepper", "Arrow keys step", () => new Stepper
        {
            Minimum = 8,
            Maximum = 32,
            Value = 14,
            Suffix = "px",
        }, FocusLook.Outer, 6),
        new MatrixRow("Switch", "Knob widens while pressed", () => Styled(new CheckBox(), "ToggleSwitch"),
            FocusLook.Outer, 10, e => ((CheckBox)e).IsChecked = true),
        new MatrixRow("Switch, label left", "c:Ui.LabelPlacement=Left", () =>
        {
            var toggle = Styled(new CheckBox { Content = "Off" }, "ToggleSwitch");
            Ui.SetLabelPlacement(toggle, Dock.Left);
            return toggle;
        }, FocusLook.Outer, 10, e =>
        {
            var toggle = (CheckBox)e;
            toggle.IsChecked = true;
            toggle.Content = "On";
        }),
        new MatrixRow("Check box", "18 px, radius 4", () => new CheckBox { Content = "Line numbers" },
            FocusLook.Outer, 4, e => ((CheckBox)e).IsChecked = true),
        new MatrixRow("Radio button", "18 px circle", () => new RadioButton { Content = "Restore tabs", GroupName = Guid.NewGuid().ToString() },
            FocusLook.Outer, 9, e => ((RadioButton)e).IsChecked = true),
        new MatrixRow("Segmented", "2 to 4 options", () => Segmented(), FocusLook.Outer, 6, null)
        {
            Customize = static (state, element) =>
            {
                var segmented = (SegmentedControl)element;
                if (state is 1 or 2 && segmented.Items[0] is SegmentedControlItem first)
                {
                    // Items are their own containers, so the unselected first segment can show the state now.
                    Ui.SetForceHover(segmented, false);
                    Ui.SetForcePressed(segmented, false);
                    if (state == 1)
                    {
                        Ui.SetForceHover(first, true);
                    }
                    else
                    {
                        Ui.SetForcePressed(first, true);
                    }
                }
            },
        });

    private static Grid BuildRowMatrix() => Matrix(
        new MatrixRow("Note row", "Selected = AccentSoft", NoteRow, FocusLook.Inset, 6,
            e => ((ListBoxItem)e).IsSelected = true),
        new MatrixRow("Nav item", "Selected = 3 px Accent bar", () =>
        {
            var item = Styled(new ListBoxItem { Content = "Appearance", Width = 180 }, "NavItem");
            item.SetResourceReference(Ui.IconProperty, "Sun");
            return item;
        }, FocusLook.Inset, 6, e => ((ListBoxItem)e).IsSelected = true),
        new MatrixRow("Menu item", "32 px, radius 4", () =>
        {
            var item = new MenuItem { Header = "Rename", InputGestureText = "F2", Width = 180 };
            item.SetResourceReference(Ui.IconProperty, "Rename");
            return item;
        }, FocusLook.Inset, 4, e =>
        {
            var item = (MenuItem)e;
            item.Header = "Last edited";
            item.InputGestureText = string.Empty;
            item.IsCheckable = true;
            item.IsChecked = true;
        }),
        new MatrixRow("Dropdown row", "Selected = Accent marker",
            () => new ComboBoxItem { Content = "Cascadia Mono", Width = 180 }, FocusLook.Inset, 4,
            e => ((ComboBoxItem)e).IsSelected = true));

    private static StackPanel BuildChrome()
    {
        var stack = new StackPanel();
        stack.Children.Add(TitleBarSample(active: true));
        var inactive = TitleBarSample(active: false);
        inactive.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(inactive);
        var caption = Text("Second strip: c:Ui.IsWindowActive=False dims the glyphs and the app name.",
            "CaptionText", "Text3Brush");
        caption.Margin = new Thickness(0, 6, 0, 16);
        stack.Children.Add(caption);
        stack.Children.Add(StatusBarSample());
        return stack;
    }

    private static WrapPanel BuildSurfaces()
    {
        var panel = new WrapPanel();
        panel.Children.Add(Tile("Menu (FlyoutCard + MenuItems)", MenuSample()));
        panel.Children.Add(Tile("Tooltip", TooltipSample()));
        panel.Children.Add(Tile("Toasts (ToastCard)", ToastSamples()));
        panel.Children.Add(Tile("Banners (InfoBanner, WarningBanner, ErrorBanner)", BannerSamples()));
        panel.Children.Add(Tile("Dialog (DialogCard over ScrimBrush)", DialogSample()));
        panel.Children.Add(Tile("Cards", CardSamples()));
        panel.Children.Add(Tile("Key caps, chips and pills", SmallParts()));
        panel.Children.Add(Tile("Avatar", AvatarSamples()));
        return panel;
    }

    private static WrapPanel BuildStatus()
    {
        var panel = new WrapPanel();
        panel.Children.Add(Tile("Spinner (> 600 ms) and busy button", SpinnerSamples()));
        panel.Children.Add(Tile("Skeleton lines (300-600 ms) and progress", SkeletonSamples()));
        panel.Children.Add(Tile("Empty state (IconBadge)", EmptyStateSample()));
        panel.Children.Add(Tile("Scrollbars: idle 4 px, hover 12 px", ScrollSamples()));
        return panel;
    }

    // ------------------------------------------------------------------ sample builders

    private static FrameworkElement NoteRow()
    {
        var title = Text("Meeting notes", "ControlNameText", "TextBrush");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var time = Text("2:14 PM", "TimestampText", "Text3Brush");
        time.Margin = new Thickness(8, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(time, Dock.Right);
        row.Children.Add(time);
        row.Children.Add(title);
        return Styled(new ListBoxItem { Content = row, Width = 180 }, "NoteRowItem");
    }

    private static SegmentedControl Segmented()
    {
        var segmented = new SegmentedControl();
        foreach (var option in new[] { "System", "Light", "Dark" })
        {
            segmented.Items.Add(new SegmentedControlItem { Content = option });
        }

        segmented.SelectedIndex = 1;
        AutomationProperties.SetName(segmented, "Theme");
        return segmented;
    }

    private static Border TitleBarSample(bool active)
    {
        var bar = new DockPanel { Height = 40, LastChildFill = true };
        Ui.SetIsWindowActive(bar, active);

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Right);
        controls.Children.Add(Btn("TitleBarButton", null, "Minimize", "Minimize"));
        controls.Children.Add(Btn("TitleBarButton", null, "Maximize", "Maximize"));
        controls.Children.Add(Btn("CloseTitleBarButton", null, null, "Close"));
        bar.Children.Add(controls);

        var avatar = new Avatar { Initials = "BK", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(avatar, Dock.Right);
        bar.Children.Add(avatar);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        var mark = new Image { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(Image.SourceProperty, "AppMarkImage");
        left.Children.Add(mark);
        var name = Text("NimPad", "CaptionText", active ? "Text2Brush" : "Text3Brush");
        name.FontWeight = FontWeights.SemiBold;
        name.Margin = new Thickness(8, 0, 16, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(name);

        var tab = new Border
        {
            Height = 34,
            Width = 172,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(12, 0, 8, 0),
        };
        tab.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        var tabContent = new DockPanel();
        var close = Btn("SmallIconButton", null, "Close", "Close tab");
        DockPanel.SetDock(close, Dock.Right);
        tabContent.Children.Add(close);
        var tabTitle = Text("Meeting notes", "TabSelectedText", "TextBrush");
        tabTitle.VerticalAlignment = VerticalAlignment.Center;
        tabContent.Children.Add(tabTitle);
        tab.Child = tabContent;
        left.Children.Add(tab);

        var other = Text("config.json", "TabText", "Text2Brush");
        other.Margin = new Thickness(20, 0, 20, 0);
        other.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(other);
        var divider = Styled(new Border(), "VerticalDivider");
        left.Children.Add(divider);
        var newTab = Btn("NewTabButton", null, null, "New note");
        newTab.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(newTab);
        bar.Children.Add(left);

        var frame = new Border { Child = bar };
        frame.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
        return frame;
    }

    private static Border StatusBarSample()
    {
        var bar = new DockPanel { Height = 24, Margin = new Thickness(4, 0, 12, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        foreach (var label in new[] { "UTF-8", "Plain Text", "100%" })
        {
            var text = Text(label, "CaptionText", "Text2Brush");
            text.Margin = new Thickness(0, 0, 16, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(text);
        }

        var sync = new IconElement { Size = 14, VerticalAlignment = VerticalAlignment.Center };
        sync.SetResourceReference(IconElement.DataProperty, "Computer");
        sync.SetResourceReference(IconElement.ForegroundProperty, "Text2Brush");
        right.Children.Add(sync);
        var syncText = Text("Local only", "CaptionText", "Text2Brush");
        syncText.Margin = new Thickness(6, 0, 0, 0);
        syncText.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(syncText);
        bar.Children.Add(right);

        var toggle = Toggle("IconToggleButton", null, "SidebarToggle", "Toggle notes sidebar");
        toggle.Width = 24;
        toggle.Height = 20;
        Ui.SetIconSize(toggle, 14);
        bar.Children.Add(toggle);
        DockPanel.SetDock(toggle, Dock.Left);
        var position = Text("Ln 13, Col 31     312 characters", "CaptionText", "Text2Brush");
        position.Margin = new Thickness(10, 0, 0, 0);
        position.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(position);

        var frame = new Border { Child = bar };
        frame.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
        return frame;
    }

    private static FrameworkElement MenuSample()
    {
        var items = new StackPanel();
        Grid.SetIsSharedSizeScope(items, true);
        items.Children.Add(MenuEntry("Rename", "F2", "Rename"));
        items.Children.Add(MenuEntry("Duplicate", "Ctrl+Shift+D", "Copy"));
        var pin = MenuEntry("Pin tab", string.Empty, "Pin");
        Ui.SetForceHover(pin, true);
        items.Children.Add(pin);
        var separator = new Separator();
        separator.SetResourceReference(StyleProperty, MenuItem.SeparatorStyleKey);
        items.Children.Add(separator);
        items.Children.Add(MenuEntry("Close", "Ctrl+W", "Close"));
        var others = MenuEntry("Close others", string.Empty, null);
        others.IsEnabled = false;
        items.Children.Add(others);
        var more = MenuEntry("Move to", string.Empty, "Folder");
        more.Items.Add(new MenuItem { Header = "Inbox" });
        more.Items.Add(new MenuItem { Header = "Archive" });
        items.Children.Add(more);

        var card = Styled(new ContentControl { Content = items, Width = 260, Padding = new Thickness(4) }, "FlyoutCard");
        var trigger = Btn("SecondaryButton", "Right-click for a real menu", "More");
        trigger.Margin = new Thickness(0, 16, 0, 0);
        trigger.HorizontalAlignment = HorizontalAlignment.Left;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "New note", InputGestureText = "Ctrl+N" });
        menu.Items.Add(new MenuItem { Header = "Reopen closed tab", InputGestureText = "Ctrl+Shift+T" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Sort by name", IsCheckable = true, IsChecked = true });
        ((MenuItem)menu.Items[0]).SetResourceReference(Ui.IconProperty, "Plus");
        ((MenuItem)menu.Items[1]).SetResourceReference(Ui.IconProperty, "Restore");
        trigger.ContextMenu = menu;

        var stack = new StackPanel();
        stack.Children.Add(card);
        stack.Children.Add(trigger);
        return stack;
    }

    private static StackPanel TooltipSample()
    {
        var chip = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
        chip.SetResourceReference(Border.BackgroundProperty, "TextBrush");
        chip.Child = Text("Close (Ctrl+W)", "CaptionText", "SurfaceBrush");
        var real = Btn("IconButton", null, "Close", "Close");
        real.ToolTip = "Close (Ctrl+W)";
        real.HorizontalAlignment = HorizontalAlignment.Left;
        real.Margin = new Thickness(0, 12, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(chip);
        stack.Children.Add(real);
        return stack;
    }

    private static StackPanel ToastSamples()
    {
        var stack = new StackPanel { Width = 380 };

        var first = new DockPanel();
        var restore = new IconElement { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        restore.SetResourceReference(IconElement.DataProperty, "Restore");
        DockPanel.SetDock(restore, Dock.Left);
        first.Children.Add(restore);
        var gesture = Text("Ctrl+Shift+T", "CaptionText", "Text3Brush");
        gesture.VerticalAlignment = VerticalAlignment.Center;
        gesture.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(gesture, Dock.Right);
        first.Children.Add(gesture);
        var undo = Btn("LinkButton", "Undo");
        undo.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(undo, Dock.Right);
        first.Children.Add(undo);
        var closed = Text("Closed “packing-list.md”", "ControlText", "TextBrush");
        closed.FontWeight = FontWeights.Normal;
        closed.VerticalAlignment = VerticalAlignment.Center;
        first.Children.Add(closed);
        stack.Children.Add(Styled(new ContentControl { Content = first, Margin = new Thickness(0, 0, 0, 12) }, "ToastCard"));

        var second = new DockPanel();
        var ok = new IconElement { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        ok.SetResourceReference(IconElement.DataProperty, "CheckCircle");
        ok.SetResourceReference(IconElement.ForegroundProperty, "OkBrush");
        DockPanel.SetDock(ok, Dock.Left);
        second.Children.Add(ok);
        var dismiss = Btn("SmallIconButton", null, "Close", "Dismiss");
        dismiss.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(dismiss, Dock.Right);
        second.Children.Add(dismiss);
        var restored = Text("Restored 5 notes from your other computer", "ControlText", "TextBrush");
        restored.FontWeight = FontWeights.Normal;
        restored.TextWrapping = TextWrapping.Wrap;
        restored.VerticalAlignment = VerticalAlignment.Center;
        second.Children.Add(restored);
        stack.Children.Add(Styled(new ContentControl { Content = second }, "ToastCard"));
        return stack;
    }

    private static StackPanel BannerSamples()
    {
        var stack = new StackPanel { Width = 380 };
        var info = Styled(new Banner { Content = "Offline. Saved on this computer and will sync when you’re back." }, "InfoBanner");
        info.SetResourceReference(Banner.IconProperty, "CloudOff");
        stack.Children.Add(info);
        var warning = Styled(new Banner
        {
            Content = "“Meeting notes” changed on two computers.",
            ActionContent = "Resolve",
            ActionCommand = new GalleryCommand(),
            Margin = new Thickness(0, 8, 0, 0),
        }, "WarningBanner");
        stack.Children.Add(warning);
        var error = Styled(new Banner
        {
            Content = "Sync paused. Your notes are safe here.",
            ActionContent = "Retry",
            ActionCommand = new GalleryCommand(),
            DismissCommand = new GalleryCommand(),
            Margin = new Thickness(0, 8, 0, 0),
        }, "ErrorBanner");
        stack.Children.Add(error);
        return stack;
    }

    private static Border DialogSample()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(Btn("QuietButton", "Cancel"));
        var primary = Btn("PrimaryButton", "Sign out");
        primary.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(primary);

        var body = new StackPanel();
        body.Children.Add(Text("Sign out of NimPad?", "DialogTitleText", "TextBrush"));
        var message = Text("Your notes stay in your Google account. They’ll be removed from this computer.", "BodyText", "Text2Brush");
        message.TextWrapping = TextWrapping.Wrap;
        message.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(message);
        body.Children.Add(buttons);

        var dialog = Styled(new ContentControl { Content = body, Width = 340, Margin = new Thickness(24, 16, 24, 32) }, "DialogCard");
        var scrim = new Border { Child = dialog, CornerRadius = new CornerRadius(8), Width = 400 };
        scrim.SetResourceReference(Border.BackgroundProperty, "ScrimBrush");
        return scrim;
    }

    private static StackPanel CardSamples()
    {
        var stack = new StackPanel { Width = 300 };
        var title = Text("Card", "SubtitleText", "TextBrush");
        var description = Text("Raised fill, 1 px Line border, radius 8, 16 px padding.", "SecondaryText", "Text2Brush");
        description.TextWrapping = TextWrapping.Wrap;
        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(description);
        stack.Children.Add(Styled(new ContentControl { Content = content }, "Card"));
        var callout = Styled(new ContentControl
        {
            Content = new TextBlock
            {
                Text = "In the editor, Tab types a tab character. F6 and Ctrl+Tab are the ways out.",
                TextWrapping = TextWrapping.Wrap,
            },
            Margin = new Thickness(0, 12, 0, 0),
        }, "CalloutCard");
        stack.Children.Add(callout);
        return stack;
    }

    private static StackPanel SmallParts()
    {
        var stack = new StackPanel { Width = 380 };
        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        keys.Children.Add(new KeyCap { Text = "Ctrl", Margin = new Thickness(0, 0, 4, 0) });
        keys.Children.Add(new KeyCap { Text = "Shift", Margin = new Thickness(0, 0, 4, 0) });
        keys.Children.Add(new KeyCap { Text = "N", Margin = new Thickness(0, 0, 16, 0) });
        keys.Children.Add(new ShortcutKeys { Gesture = "Ctrl++" });
        stack.Children.Add(keys);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var all = Toggle("FilterChip", "All 24", null, null);
        all.IsChecked = true;
        chips.Children.Add(all);
        var pinned = Toggle("FilterChip", "Pinned 2", null, null);
        pinned.Margin = new Thickness(8, 0, 0, 0);
        chips.Children.Add(pinned);
        stack.Children.Add(chips);

        var pills = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        pills.Children.Add(Pill("OkPill", "Connected", "Check"));
        pills.Children.Add(Pill("AccentPill", "Newer", null));
        pills.Children.Add(Pill("WarnPill", "1 conflict", "Warning"));
        pills.Children.Add(Pill("ErrPill", "Sync paused", "ErrorCircle"));
        pills.Children.Add(Pill("StatusPill", "Local only", "Computer"));
        stack.Children.Add(pills);
        return stack;
    }

    private static StackPanel AvatarSamples()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var status in Enum.GetValues<AvatarStatus>())
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var avatar = new Avatar { Initials = "BK", Status = status };
            avatar.SetResourceReference(Avatar.StatusRingBrushProperty, "RaisedBrush");
            cell.Children.Add(avatar);
            var label = Text(status.ToString(), "CaptionText", "Text3Brush");
            label.Margin = new Thickness(0, 6, 0, 0);
            cell.Children.Add(label);
            row.Children.Add(cell);
        }

        var large = new Avatar { Initials = "BK", Width = 64, Height = 64, FontSize = 22 };
        row.Children.Add(large);
        return row;
    }

    private static StackPanel SpinnerSamples()
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new Spinner { VerticalAlignment = VerticalAlignment.Center });
        var busyContent = new StackPanel { Orientation = Orientation.Horizontal };
        var spinner = new Spinner { Margin = new Thickness(0, 0, 8, 0) };
        spinner.SetResourceReference(Spinner.ForegroundProperty, "Text2Brush");
        busyContent.Children.Add(spinner);
        busyContent.Children.Add(new TextBlock { Text = "Syncing…" });
        var busy = Btn("SecondaryButton", null);
        busy.Content = busyContent;
        busy.IsEnabled = false;
        busy.Margin = new Thickness(16, 0, 0, 0);
        stack.Children.Add(busy);
        return stack;
    }

    private static StackPanel SkeletonSamples()
    {
        var stack = new StackPanel { Width = 300 };
        foreach (var width in new[] { 220.0, 296.0, 170.0 })
        {
            stack.Children.Add(new SkeletonLine { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) });
        }

        stack.Children.Add(new ProgressBar { Value = 40, Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) });
        return stack;
    }

    private static StackPanel EmptyStateSample()
    {
        var stack = new StackPanel { Width = 240 };
        var badge = Styled(new ContentControl { HorizontalAlignment = HorizontalAlignment.Center }, "IconBadge");
        badge.SetResourceReference(Ui.IconProperty, "DocumentAdd");
        stack.Children.Add(badge);
        var title = Text("No notes yet", "BodyLabelText", "TextBrush");
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 12, 0, 4);
        stack.Children.Add(title);
        var hint = Text("Start typing in any tab. It is saved automatically.", "SecondaryText", "Text2Brush");
        hint.TextAlignment = TextAlignment.Center;
        hint.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(hint);
        var action = Btn("PrimaryButton", "New note");
        action.HorizontalAlignment = HorizontalAlignment.Center;
        action.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(action);
        return stack;
    }

    private static StackPanel ScrollSamples()
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hover in new[] { false, true })
        {
            var bar = new ScrollBar { Orientation = Orientation.Vertical, Height = 120, Maximum = 100, ViewportSize = 30, Value = 20, Margin = new Thickness(0, 0, 16, 0) };
            Ui.SetForceHover(bar, hover);
            stack.Children.Add(bar);
        }

        var lines = new StackPanel();
        for (var i = 1; i <= 20; i++)
        {
            lines.Children.Add(Text($"Line {i}: scroll me", "BodyText", "Text2Brush"));
        }

        var viewer = new ScrollViewer { Height = 120, Width = 200, Content = lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        stack.Children.Add(viewer);
        return stack;
    }

    // ------------------------------------------------------------------ matrix

    private static Grid Matrix(params MatrixRow[] rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        foreach (var _ in StateColumns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < StateColumns.Length; c++)
        {
            var header = Text(StateColumns[c], "MicroText", "Text3Brush");
            header.Margin = new Thickness(8, 0, 8, 8);
            Grid.SetColumn(header, c + 1);
            grid.Children.Add(header);
        }

        foreach (var row in rows)
        {
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(line, grid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(line, StateColumns.Length + 1);
            grid.Children.Add(line);

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var r = grid.RowDefinitions.Count - 1;
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 8, 10) };
            label.Children.Add(Text(row.Name, "BodyLabelText", "TextBrush"));
            label.Children.Add(Text(row.Note, "CaptionText", "Text3Brush"));
            Grid.SetRow(label, r);
            grid.Children.Add(label);

            for (var state = 0; state < StateColumns.Length; state++)
            {
                var cell = BuildCell(row, state);
                cell.Margin = new Thickness(8, 10, 8, 10);
                cell.VerticalAlignment = VerticalAlignment.Center;
                cell.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, state + 1);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    private static FrameworkElement BuildCell(MatrixRow row, int state)
    {
        switch (state)
        {
            case 3 when row.Focus == FocusLook.None:
            case 4 when row.Select is null:
                return Dash();
        }

        var element = row.Create();
        switch (state)
        {
            case 1:
                Ui.SetForceHover(element, true);
                break;
            case 2:
                Ui.SetForcePressed(element, true);
                break;
            case 4:
                row.Select!(element);
                break;
            case 5:
                element.IsEnabled = false;
                break;
        }

        row.Customize?.Invoke(state, element);
        if (state != 3)
        {
            return element;
        }

        switch (row.Focus)
        {
            case FocusLook.InTemplate:
                Ui.SetForceFocused(element, true);
                return element;
            case FocusLook.Outer:
                return Ring(element, row.Radius + 4, -4);
            default:
                return Ring(element, row.Radius, 0);
        }
    }

    /// <summary>Draws the kit's focus ring statically (FocusVisualStyle only appears for real keyboard focus).</summary>
    private static Grid Ring(FrameworkElement element, double radius, double offset)
    {
        var host = new Grid();
        host.Children.Add(element);
        var ring = new Border
        {
            Margin = new Thickness(offset),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(radius),
            IsHitTestVisible = false,
        };
        ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        host.Children.Add(ring);
        return host;
    }

    // ------------------------------------------------------------------ small helpers

    private static T Styled<T>(T element, string styleKey)
        where T : FrameworkElement
    {
        element.SetResourceReference(StyleProperty, styleKey);
        return element;
    }

    private static TextBlock Text(string text, string styleKey, string brushKey)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(StyleProperty, styleKey);
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    private static TextBlock Dash()
    {
        var dash = Text("–", "BodyText", "Text3Brush");
        dash.Margin = new Thickness(8, 0, 8, 0);
        dash.VerticalAlignment = VerticalAlignment.Center;
        return dash;
    }

    private static Button Btn(string styleKey, object? content, string? iconKey = null, string? automationName = null)
    {
        var button = Styled(new Button { Content = content }, styleKey);
        if (iconKey is not null)
        {
            button.SetResourceReference(Ui.IconProperty, iconKey);
        }

        if (automationName is not null)
        {
            AutomationProperties.SetName(button, automationName);
        }

        return button;
    }

    private static ToggleButton Toggle(string styleKey, object? content, string? iconKey, string? automationName)
    {
        var toggle = Styled(new ToggleButton { Content = content }, styleKey);
        if (iconKey is not null)
        {
            toggle.SetResourceReference(Ui.IconProperty, iconKey);
        }

        if (automationName is not null)
        {
            AutomationProperties.SetName(toggle, automationName);
        }

        return toggle;
    }

    private static MenuItem MenuEntry(string header, string gesture, string? iconKey)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        if (iconKey is not null)
        {
            item.SetResourceReference(Ui.IconProperty, iconKey);
        }

        return item;
    }

    private static ContentControl Pill(string styleKey, string text, string? iconKey)
    {
        var pill = Styled(new ContentControl { Content = text, Margin = new Thickness(0, 0, 8, 8) }, styleKey);
        if (iconKey is not null)
        {
            pill.SetResourceReference(Ui.IconProperty, iconKey);
        }

        return pill;
    }

    private static StackPanel Tile(string title, UIElement content)
    {
        var tile = new StackPanel { Margin = new Thickness(0, 0, 32, 24), VerticalAlignment = VerticalAlignment.Top };
        var header = Text(title, "CaptionText", "Text3Brush");
        header.Margin = new Thickness(0, 0, 0, 10);
        tile.Children.Add(header);
        tile.Children.Add(content);
        return tile;
    }

    private static ContentControl Section(string title, UIElement body)
    {
        var stack = new StackPanel();
        var header = Text(title, "MicroText", "Text3Brush");
        header.Margin = new Thickness(0, 0, 0, 14);
        stack.Children.Add(header);
        stack.Children.Add(body);
        return Styled(new ContentControl { Content = stack, Margin = new Thickness(0, 0, 0, 16) }, "Card");
    }

    private sealed record MatrixRow(
        string Name,
        string Note,
        Func<FrameworkElement> Create,
        FocusLook Focus,
        double Radius,
        Action<FrameworkElement>? Select = null)
    {
        /// <summary>Extra per-state tweaks (state index as in <see cref="StateColumns"/>).</summary>
        public Action<int, FrameworkElement>? Customize { get; init; }
    }

    /// <summary>A no-op command so action and dismiss buttons render as they do in the app.</summary>
    private sealed class GalleryCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}
