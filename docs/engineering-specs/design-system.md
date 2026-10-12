YOUR AREA: the WPF design system of the NimPad desktop app. You own these NEW paths only:
apps/windows/src/NimPad.App/Themes/**, apps/windows/src/NimPad.App/Controls/** (custom controls),
apps/windows/src/NimPad.App/Theming/** (ThemeService + native helpers), and you may edit
apps/windows/src/NimPad.App/App.xaml (only to merge your dictionaries) and add tests under
apps/windows/tests/NimPad.App.Tests/Themes/**. Do NOT build views/windows for app features (other engineers own
the shell, explorer, editor, settings) — but DO create a small internal "DesignGallery" Window
(Theming/DesignGalleryWindow.xaml) that shows every control in every state, so the App.Tests can load it and
humans can inspect the kit.

SOURCE OF TRUTH: docs/design/tokens.md (exact values), docs/design/README.md (page index). Rendered deck pages are
PNG images you can view with your Read tool: docs/design/renders/p-NN.png
(look at p-22 colors, p-23 type/metrics, p-24 window chrome, p-25 controls & surfaces, p-26 interaction states,
p-08 sidebar rows, p-09 find panel, p-10 menus/toast, p-12 dialog, p-18..21 settings controls).

DELIVER:
1) Themes/Tokens.Light.xaml and Themes/Tokens.Dark.xaml: for EVERY token in tokens.md a Color resource
   "<Name>Color" and a SolidColorBrush "<Name>Brush" (frozen), names in PascalCase: Chrome, Surface, Raised, Text,
   Text2, Text3, Hover, Press, Sep, Line, Line2, Accent, AccentHover, AccentPressed, AccentSoft, OnAccent,
   Selection, CurrentLine, Match, MatchCurrent, AvatarBg, AvatarFg, Ok, OkSoft, Warn, WarnSoft, Err, ErrSoft,
   DiffAdd, DiffDel, SyntaxKey, SyntaxString, SyntaxNumber, SyntaxLiteral, SyntaxPunctuation, SyntaxComment,
   plus CloseHover (#C42B1C, Windows close-button red) and Scrim (#000000 at 40% light / 55% dark). Translucent
   tokens use the ARGB of the listed ink at the listed opacity (e.g. #1D1D1B·6% => #0F1D1D1B). Every consumer must
   use {DynamicResource ...} so theme switching works live.
2) Themes/Typography.xaml: FontFamily resources UiFontFamily ("Segoe UI Variable Text, Segoe UI") and
   EditorFontFamily ("Cascadia Mono, Consolas, Courier New" — Cascadia Mono ships with Windows 11; do NOT bundle
   font files), sizes and TextBlock styles for Display, PageTitle, DialogTitle, Subtitle, Body, BodyLabel,
   Control, ControlName, Tab, TabSelected, Secondary, Caption, Micro (caps via Typography.Capitals="AllSmallCaps"
   is NOT the same — render Micro text uppercased by the consumer or a converter; letter spacing via
   TextBlock has no tracking: approximate +0.06em is acceptable to omit), LineHeight with
   LineStackingStrategy=BlockLineHeight exactly as tokens.md.
3) Themes/Metrics.xaml: Thickness/Double/CornerRadius resources for spacing (4..48), radii (4, 6, 8, full), key
   metrics (TitleBarHeight 40, TabHeight 34, TabMinWidth 96, TabMaxWidth 172, PinnedTabWidth 104, NewTabButton 30,
   AvatarSize 28, WindowControlWidth 46, StatusBarHeight 24, SidebarWidth 288, SettingsNavWidth 216,
   ControlHeight 32, SmallControlHeight 26, LargeControlHeight 44, MenuRowHeight 32, BannerMinHeight 36), and
   DropShadowEffect resources FlyoutShadow and DialogShadow (soft, Win11-like; keep blur moderate for
   performance; RenderOptions caching where sensible).
4) Themes/Icons.xaml: Geometry resources (24x24 design grid, 1.5 px stroke look, rounded joins) for: AppMark
   (the NimPad logo: rounded square with three text lines, the last shorter, with a small dot — see p-01/p-24),
   Plus, Close, Pin, Unpin, Search, ChevronRight, ChevronDown, ChevronLeft, ChevronUp, Folder, FolderOpen,
   Document, DocumentCode, Trash, Restore (curved undo arrow), More (horizontal ellipsis), SidebarToggle,
   Check, Warning (triangle), ErrorCircle, Info, Computer (for "Local only"/"This computer"), Sort, Settings
   (gear), Link, Copy, Rename (pencil), Minimize, Maximize, RestoreWindow, Keyboard, ArrowUp, ArrowDown,
   MatchCase ("Aa"), WholeWord ("ab" with underline), Regex (".*"), Replace, ReplaceAll, Spinner (arc), Import,
   Export, ExternalFile. Plus an IconControl (Controls/Icon.cs: a lightweight FrameworkElement or Path-based
   control with Geometry, Size (default 16), Foreground inherited, StrokeThickness) used everywhere.
5) Themes/Controls.xaml (+ split files if large, merged via Themes/DesignSystem.xaml which App.xaml merges):
   implicit or keyed styles matching p-25/p-26 in BOTH themes with all six states (default, hover, pressed,
   focused, selected/on, disabled at 40-45% opacity, not focusable when disabled):
   - FocusVisual: 2 px accent ring with 2 px offset for standalone controls; InsetFocusVisual for rows, tabs,
     menu items.
   - Buttons (keyed): PrimaryButton, SecondaryButton (wash + 1 px inset line), QuietButton, DestructiveButton
     (err color text/icon, err-soft wash), SmallButton (26 tall), LargeButton (44, welcome), IconButton (28x28,
     radius 6), IconToggleButton (ToggleButton; checked uses AccentSoft + Accent icon), TitleBarButton
     (46x40 window controls; CloseTitleBarButton turns CloseHover red with white glyph; inactive-window dimmed
     state via a WindowActive attached property or binding), NewTabButton (30x30).
   - TextBox (default border Line2 -> hover Text3 -> focus Accent 2px-look, error state via an attached
     property Validation-free "HasError" + message, disabled), SearchBox custom control (Controls/SearchBox:
     32 tall, search icon inside, clear button visible when Text non-empty, Esc clears — expose ClearCommand and
     a Cleared event), PlaceholderText attached property for TextBox.
   - ToggleSwitch style for CheckBox (40x20 pill, knob widens while pressed, accent fill when on, "On"/"Off" label
     optional), Segmented control (Controls/SegmentedControl: a ListBox-based or ItemsControl of RadioButtons
     with the p-25 look; 2-4 options), ComboBox (dropdown 32 tall with raised popup, radius 8, flyout shadow,
     32 px rows), Stepper custom control (Controls/Stepper: − value + with Value/Minimum/Maximum/Step/Suffix
     "px"; arrow keys work).
   - ContextMenu, MenuItem (32 px rows, radius 4, icon column, gesture text right-aligned in Text3, separator,
     disabled 45%, submenu chevron, hover wash, keyboard focus inset ring), ToolTip (dark chip like p-25:
     "Close (Ctrl+W)").
   - ScrollBar + ScrollViewer: thin overlay-style bar: 4 px idle thumb, expands to 12 px on hover (p-24), no
     arrows, rounded thumb in Line2/Text3, track transparent; must work for ListBox, ScrollViewer and AvalonEdit's
     internal ScrollViewer (implicit style).
   - ListBoxItem styles: NoteRowItem (selected = AccentSoft, hover wash, inset focus ring, radius 6, 2 px
     horizontal margin), plain ListBox style without chrome.
   - Card/Dialog surfaces: DialogCard (Raised, radius 8, DialogShadow, padding 24), Scrim brush, FlyoutCard
     (Raised, radius 8, FlyoutShadow, 1 px Line border), ToastCard, Banner styles (Info = Hover-ish neutral,
     Warning = WarnSoft + Warn icon, Error = ErrSoft + Err icon; 36 px min, one action link button),
     KeyCap (Controls/KeyCap: radius 4, 1 px Line2 border, Caption mono-ish text — renders "Ctrl", "Shift", "N"),
     Chip/Pill (filter chip, status pill e.g. "Connected"/"Newer"), Avatar (28 px circle, AvatarBg/AvatarFg
     initials; status dot variants: syncing accent, offline Text3, conflict Warn, error Err).
   - Spinner custom control (Controls/Spinner: 16 px arc rotating via a lightweight storyboard; stops animating
     when not visible).
   - SkeletonLine (placeholder bar with Hover fill) for loading states.
6) Theming/ThemeService: Apply(ThemeMode System|Light|Dark) swaps the tokens dictionary in
   Application.Current.Resources.MergedDictionaries (keep it the FIRST merged dictionary; other dictionaries
   reference tokens dynamically); System mode reads HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\
   Personalize\AppsUseLightTheme and listens to Microsoft.Win32.SystemEvents.UserPreferenceChanged (marshal to UI
   thread; unsubscribe on dispose); exposes IsDark + ThemeChanged event; also implements
   NimPad.Presentation.Platform.ISystemThemeProvider if that interface exists by the time you finish (another
   engineer is writing the Presentation layer in parallel; if it does not exist yet, keep ThemeService
   self-contained and note it). Theming/NativeMethods + WindowThemeHelper.ApplyImmersiveDarkMode(Window, bool)
   via DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE=20) for standard-chrome windows (Settings); safe no-op
   on failure.
7) Docs: apps/windows/src/NimPad.App/Themes/README.md listing every resource key (tokens, text styles, metrics,
   icons, control styles) and how to use them — the other App engineers will rely on it.
8) Tests (apps/windows/tests/NimPad.App.Tests/Themes): on an STA thread (Windows only; Assert.SkipUnless
   OperatingSystem.IsWindows()), load each ResourceDictionary in both themes and assert every token key exists in
   both with the exact color values from tokens.md (parse expected values from a table in the test — this guards
   regressions); instantiate DesignGalleryWindow in Light and Dark and call Measure/Arrange (no Show needed);
   also a cross-platform test project cannot run WPF, so ALSO make sure the XAML compiles on Linux:
   `dotnet build apps/windows/NimPad.slnx` must succeed (XAML compile errors surface there; runtime resource
   errors only surface on Windows, so be meticulous: every StaticResource/DynamicResource key must exist, pack
   URIs must match file paths and casing exactly).
QUALITY BAR: this kit must look like the deck (Windows 11 calm, flat, precise spacing), be fast (frozen brushes,
no heavy effects on frequently re-rendered elements, no per-frame animations except the spinner), and be fully
keyboard accessible (focus visuals everywhere, AutomationProperties.Name on icon-only controls via their
consumers — expose AutomationProperties passthrough where custom controls wrap content).
