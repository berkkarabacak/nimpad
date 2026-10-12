# NimPad design system (WPF)

The WPF implementation of the approved deck (`docs/design`, values in `docs/design/tokens.md`). Every view uses
these resources: never hard-code a color, size, radius or font. Open `Theming/DesignGalleryWindow` (internal) to see
every token, text style, icon and control in every state, in both themes.

## Contents

- [Setup and rules](#setup-and-rules)
- [Theming services](#theming-services)
- [Color tokens](#color-tokens)
- [Typography](#typography)
- [Metrics, radii and elevation](#metrics-radii-and-elevation)
- [Icons](#icons)
- [Attached properties (`c:Ui.*`)](#attached-properties-cui)
- [Focus visuals](#focus-visuals)
- [Window, dividers, links and tooltips](#window-dividers-links-and-tooltips)
- [Buttons](#buttons)
- [Inputs and selection](#inputs-and-selection)
- [Menus](#menus)
- [Lists and scrolling](#lists-and-scrolling)
- [Surfaces and status](#surfaces-and-status)
- [Custom controls reference](#custom-controls-reference)
- [Internal building blocks](#internal-building-blocks)
- [Decisions and deviations from the deck](#decisions-and-deviations-from-the-deck)

## Setup and rules

`App.xaml` already merges, in this order (the order matters):

```xml
<ResourceDictionary.MergedDictionaries>
    <themes:TokensLight />   <!-- FIRST: ThemeService swaps it for TokensDark / TokensLight -->
    <themes:DesignSystem />  <!-- Typography, Metrics, Icons, Controls.*.xaml -->
</ResourceDictionary.MergedDictionaries>
```

XAML namespaces used below:

```xml
xmlns:c="clr-namespace:NimPad.App.Controls"
xmlns:theming="clr-namespace:NimPad.App.Theming"
```

Rules:

1. **Colors are always `{DynamicResource ...}`** (`TextBrush`, `HoverBrush`, ...). They change when the theme
   changes; a `StaticResource` would freeze the first theme's value.
2. **Everything else** (text styles, metrics, icons, control styles) is theme-independent. Use `{StaticResource ...}`
   in views (cheaper), or `{DynamicResource ...}` if your view can be created before `App.xaml` resources exist (for
   example in a test without an `Application`).
3. Implicit styles already cover `TextBox`, `ComboBox`, `CheckBox`, `RadioButton`, `ContextMenu`, `MenuItem`, `Menu`,
   `ScrollViewer`, `ScrollBar`, `ListBox`, `ListBoxItem`, `ProgressBar`, `ToolTip`, `Separator`, `Hyperlink`,
   `Button` (secondary look) and all custom controls. To extend one, use
   `BasedOn="{StaticResource {x:Type TextBox}}"`.
4. Windows: `Style="{DynamicResource AppWindow}"` (implicit `Window` styles do not reach derived window classes).
5. Icon-only controls must set `AutomationProperties.Name`; use stable `AutomationProperties.AutomationId`s.
6. Status is never color alone: pair `Ok`/`Warn`/`Err` with an icon and a word.
7. Inside the `Themes/*.xaml` dictionaries, `StaticResource` is only used for keys defined earlier in the same file;
   cross-file references are `DynamicResource`. Keep it that way when adding styles (WPF resolves cross-file
   `StaticResource` inconsistently between the app, windows and tests).

Hosts without `App.xaml` (unit tests, tools): `ThemeService.InstallDesignSystem(resources, dark)` merges a token set
(first) and the design system into any `ResourceDictionary`.

## Theming services

All in `NimPad.App.Theming` (Windows-only, `[SupportedOSPlatform("windows")]`).

| Type / member | Purpose |
|---|---|
| `enum ThemeMode { System, Light, Dark }` | The user's choice. WPF 9+ also has `System.Windows.ThemeMode` (and `Window.ThemeMode`): alias ours where both are visible, `using ThemeMode = NimPad.App.Theming.ThemeMode;`. |
| `new ThemeService()` | Targets `Application.Current.Resources`, creates and owns a `SystemThemeWatcher`, sets `ThemeService.Current`. Create on the UI thread. |
| `new ThemeService(ResourceDictionary, SystemThemeWatcher)` | Targets any dictionary (tests, a window). Does not own the watcher. |
| `ThemeService.Apply(ThemeMode)` | Puts `TokensLight` / `TokensDark` at `MergedDictionaries[0]` (replacing the current token set, inserting one if missing). `System` follows Windows and keeps following it. |
| `ThemeService.Mode`, `ThemeService.IsDark` | Last applied mode; whether the dark tokens are applied. |
| `ThemeService.ThemeChanged` | Raised (UI thread) after the token set changed (and once on the first `Apply`). Use it for things that cannot use `DynamicResource`, e.g. AvalonEdit highlighting definitions. |
| `ThemeService.UpdateWindowFrames` | Default `true`: on change, calls `WindowThemeHelper.ApplyImmersiveDarkMode` for every open window. |
| `ThemeService.ApplyToWindow(Window)` | Matches one window's native frame to the current theme; call it for windows opened later (Settings). |
| `ThemeService.SystemTheme` | The `SystemThemeWatcher`. |
| `ThemeService.Current` (static) | The service created with the parameterless constructor, or null. |
| `ThemeService.CreateTokens(bool dark)` | A new `TokensDark` / `TokensLight` dictionary. |
| `ThemeService.IsTokenDictionary(ResourceDictionary)` | True for `TokensLight` / `TokensDark`. |
| `ThemeService.InstallDesignSystem(ResourceDictionary, bool dark)` | Merge tokens (first) + `DesignSystem` into any dictionary; idempotent. |
| `SystemThemeWatcher.IsDarkMode` / `.Changed` | Windows app mode: `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` (0 = dark; missing or unreadable = light), refreshed on `SystemEvents.UserPreferenceChanged`, marshalled to the creating dispatcher; unsubscribes on `Dispose`. **Same member names as `NimPad.Presentation`'s `ISystemThemeProvider`**: the composition root can add the interface to the class declaration or wrap it in a one-line adapter. |
| `SystemThemeWatcher.ReadSystemIsDark()` (static) | One-off registry read. |
| `SystemThemeWatcher.Refresh()` | Re-read now; raises `Changed` if it differs. |
| `WindowThemeHelper.ApplyImmersiveDarkMode(Window, bool)` | `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE = 20, falls back to 19)`. For standard-chrome windows (Settings); also tints the Windows 11 border of custom-chrome windows. Deferred to `SourceInitialized` when the window has no handle yet; safe no-op on failure. Overload taking an `IntPtr` HWND. |
| `ThemeName` (string resource) | `"Light"` or `"Dark"`: the active token set, e.g. `(string)FindResource("ThemeName")`. |

Composition sketch (App.xaml.cs): `var themes = new ThemeService(); themes.Apply(settings.Theme);` before the main
window is shown, then call `Apply` again whenever the setting changes; dispose on exit.

## Color tokens

Every token has a `Color` and a frozen `SolidColorBrush`. Values are verbatim from `tokens.md`; translucent tokens are
ARGB with alpha = round(opacity x 255), e.g. `#1D1D1B · 6%` = `#0F1D1D1B`. Always `{DynamicResource}`.

| Keys | Light | Dark | Use |
|---|---|---|---|
| `ChromeColor` / `ChromeBrush` | `#EFEFEC` | `#252527` | Frames the app: title bar, tab strip, sidebar |
| `SurfaceColor` / `SurfaceBrush` | `#FBFBFA` | `#1B1B1D` | The writing area and the selected tab |
| `RaisedColor` / `RaisedBrush` | `#FFFFFF` | `#2B2B2E` | Menus, dialogs, cards, toasts |
| `TextColor` / `TextBrush` | `#1D1D1B` | `#E8E7E3` | Primary text |
| `Text2Color` / `Text2Brush` | `#5B5B57` | `#A8A7A2` | Secondary text, unselected tab titles |
| `Text3Color` / `Text3Brush` | `#686863` | `#92918C` | Quietest text that still passes AA |
| `HoverColor` / `HoverBrush` | `#1D1D1B · 6%` | `#FFFFFF · 7%` | Hover wash on buttons and rows |
| `PressColor` / `PressBrush` | `#1D1D1B · 10%` | `#FFFFFF · 11%` | Pressed wash |
| `SepColor` / `SepBrush` | `#1D1D1B · 16%` | `#FFFFFF · 16%` | Tab separators |
| `LineColor` / `LineBrush` | `#1D1D1B · 10%` | `#FFFFFF · 9%` | Card edges |
| `Line2Color` / `Line2Brush` | `#1D1D1B · 26%` | `#FFFFFF · 22%` | Control outlines |
| `AccentColor` / `AccentBrush` | `#3055D8` | `#8CA2FF` | Primary button, focus ring, selected row, links, active-section marker |
| `AccentHoverColor` / `AccentHoverBrush` | `#2647BD` | `#A3B5FF` | Primary button hover |
| `AccentPressedColor` / `AccentPressedBrush` | `#1F3B9E` | `#7389EA` | Primary button pressed |
| `AccentSoftColor` / `AccentSoftBrush` | `#E6ECFC` | `#8CA2FF · 16%` | Selected rows, toggled icon buttons, accent pills |
| `OnAccentColor` / `OnAccentBrush` | `#FFFFFF` | `#0B1230` | Text and glyphs on an accent fill |
| `SelectionColor` / `SelectionBrush` | `#3055D8 · 18%` | `#8CA2FF · 30%` | Editor text selection |
| `CurrentLineColor` / `CurrentLineBrush` | `#1D1D1B · 4%` | `#FFFFFF · 4%` | Editor current-line wash |
| `MatchColor` / `MatchBrush` | `#F0B928 · 40%` | `#F0BE3C · 34%` | Find matches |
| `MatchCurrentColor` / `MatchCurrentBrush` | `#F09614 · 72%` | `#F0A028 · 60%` | Current find match |
| `AvatarBgColor` / `AvatarBgBrush` | `#DCE3FB` | `#33406F` | Account avatar fill |
| `AvatarFgColor` / `AvatarFgBrush` | `#273C99` | `#D5DEFF` | Account avatar initials |
| `OkColor` / `OkBrush` | `#26704A` | `#6FCB95` | Status: ok (always with an icon and a word) |
| `OkSoftColor` / `OkSoftBrush` | `#E3F2EA` | `#6FCB95 · 14%` | Status: ok fill |
| `WarnColor` / `WarnBrush` | `#8F5200` | `#EDB75A` | Status: needs attention |
| `WarnSoftColor` / `WarnSoftBrush` | `#FBEFD5` | `#EDB75A · 14%` | Status: needs-attention fill |
| `ErrColor` / `ErrBrush` | `#B3261E` | `#FF8F86` | Status: error |
| `ErrSoftColor` / `ErrSoftBrush` | `#FBE5E3` | `#FF8F86 · 14%` | Status: error fill |
| `DiffAddColor` / `DiffAddBrush` | `#289655 · 16%` | `#5AC882 · 18%` | Conflict diff: added line tint |
| `DiffDelColor` / `DiffDelBrush` | `#C8372D · 14%` | `#FF6E64 · 18%` | Conflict diff: removed line tint |
| `SyntaxKeyColor` / `SyntaxKeyBrush` | `#2B5FAE` | `#8DB4FF` | Syntax: keys, properties |
| `SyntaxStringColor` / `SyntaxStringBrush` | `#2A7A4B` | `#8FD1A5` | Syntax: strings |
| `SyntaxNumberColor` / `SyntaxNumberBrush` | `#A8530F` | `#F0B27A` | Syntax: numbers |
| `SyntaxLiteralColor` / `SyntaxLiteralBrush` | `#8A3FA8` | `#C9A0F0` | Syntax: literals (true, false, null) |
| `SyntaxPunctuationColor` / `SyntaxPunctuationBrush` | `#75756F` | `#8D8C87` | Syntax: punctuation |
| `SyntaxCommentColor` / `SyntaxCommentBrush` | `#75756F` | `#8A8984` | Syntax: comments |
| `CloseHoverColor` / `CloseHoverBrush` | `#C42B1C` | `#C42B1C` | Window close button hover (Windows close-button red) |
| `ScrimColor` / `ScrimBrush` | `#000000 · 40%` | `#000000 · 55%` | Dims the window behind a dialog |

Plus `ThemeName` (`"Light"` / `"Dark"`). Code that needs a brush: `element.SetResourceReference(prop, "TextBrush")`
(live) or `(Brush)element.FindResource("TextBrush")` (one-off; re-read on `ThemeService.ThemeChanged`).

## Typography

Fonts (no font files are bundled; Cascadia Mono ships with Windows 11):

| Key | Value |
|---|---|
| `UiFontFamily` | `Segoe UI Variable Text, Segoe UI` |
| `EditorFontFamily` | `Cascadia Mono, Consolas, Courier New` |

`TextBlock` styles set family, size, weight and an exact line box (`LineHeight` + `LineStackingStrategy=BlockLineHeight`),
never a color: set `Foreground="{DynamicResource Text2Brush}"` etc. Letter spacing from the deck is omitted (WPF has
no tracking). **`MicroText` is an all-caps label: pass uppercase text** ("YESTERDAY"); WPF cannot transform case.

| Style key | Size / line height | Weight | Typical use (suggested foreground) |
|---|---|---|---|
| `DisplayText` | 44 / 48 | 600 | Welcome headline only (`TextBrush`) |
| `PageTitleText` | 24 / 32 | 600 | Settings page heading, sheet titles |
| `DialogTitleText` | 20 / 26 | 600 | Dialog and panel headings |
| `SubtitleText` | 16 / 22 | 600 | Status headings, card titles |
| `BodyText` | 14 / 20 | 400 | General UI text |
| `BodyLabelText` | 14 / 20 | 500 | Settings labels |
| `ControlText` | 13.5 / 18 | 500 | Buttons, menu items, list names |
| `ControlNameText` | 13.5 / 18 | 600 | Note titles in rows |
| `TabText` | 13 / 18 | 400 | Tab titles (`Text2Brush`), chips, sidebar search |
| `TabSelectedText` | 13 / 18 | 500 | The selected tab's title (`TextBrush`) |
| `SecondaryText` | 12.5 / 17 | 400 | Setting descriptions, note previews (`Text2Brush`) |
| `CaptionText` | 12 / 16 | 400 | Status bar, helper text, footers (`Text2Brush` / `Text3Brush`) |
| `MicroText` | 11 / 14 | 600, CAPS | Sidebar group labels (`Text3Brush`) |
| `TimestampText` | 11.5 / 16 | 400 | Row timestamps (`Text3Brush`) |
| `EditorText` | 14 / 22 | 400, mono | Monospaced text outside the editor |
| `TextBase` | (UI family, block line height) | 400 | Base of the styles above; use as `BasedOn` |

Sizes as `Double` resources: `DisplayFontSize` 44, `PageTitleFontSize` 24, `DialogTitleFontSize` 20,
`SubtitleFontSize` 16, `BodyFontSize` 14, `ControlFontSize` 13.5, `TabFontSize` 13, `SecondaryFontSize` 12.5,
`CaptionFontSize` 12, `TimestampFontSize` 11.5, `MicroFontSize` 11, `EditorFontSize` 14 (the editor's default; the
user can change it).

Line heights as `Double` resources: `DisplayLineHeight` 48, `PageTitleLineHeight` 32, `DialogTitleLineHeight` 26,
`SubtitleLineHeight` 22, `BodyLineHeight` 20, `ControlLineHeight` 18, `TabLineHeight` 18, `SecondaryLineHeight` 17,
`CaptionLineHeight` 16, `TimestampLineHeight` 16, `MicroLineHeight` 14, `EditorLineHeight` 22.

## Metrics, radii and elevation

Spacing (4 px base), as `Double` (`Space<N>`) and uniform `Thickness` (`Thickness<N>`):

| N | `Space<N>` / `Thickness<N>` | Use |
|---|---|---|
| 4 | `Space4` / `Thickness4` | Icon to label, tight pairs |
| 8 | `Space8` / `Thickness8` | Inside controls, tab to close button |
| 12 | `Space12` / `Thickness12` | Sidebar and bar padding |
| 16 | `Space16` / `Thickness16` | Card padding, gaps between groups |
| 20 | `Space20` / `Thickness20` | Editor top padding |
| 24 | `Space24` / `Thickness24` | Page and sheet padding |
| 32 | `Space32` / `Thickness32` | Editor side padding |
| 40 | `Space40` / `Thickness40` | Title bar height, large gaps |
| 48 | `Space48` / `Thickness48` | Display line height |

Named paddings (`Thickness`): `BarPadding` 12,0 · `CardPadding` 16 · `DialogPadding` 24 · `PagePadding` 24 ·
`EditorPadding` 32,20,32,20 (wide) · `EditorPaddingMedium` 24,20,24,20 (medium/narrow).

Corner radius (`CornerRadius`): `Radius4` (tab close, key caps, menu items) · `Radius6` (buttons, inputs, rows) ·
`Radius8` (menus, dialogs, cards, window) · `RadiusFull` (999: avatar, switch, pills) · `TabTopRadius` (8,8,0,0).

Key metrics (`Double`):

| Key | Value | | Key | Value |
|---|---|---|---|---|
| `TitleBarHeight` | 40 | | `ControlHeight` | 32 |
| `TabHeight` | 34 | | `SmallControlHeight` | 26 |
| `TabMinWidth` | 96 | | `LargeControlHeight` | 44 |
| `TabMaxWidth` | 172 | | `IconButtonSize` | 28 |
| `PinnedTabWidth` | 104 | | `MenuRowHeight` | 32 |
| `NewTabButtonSize` | 30 | | `BannerMinHeight` | 36 |
| `AvatarSize` | 28 | | `IconSize` | 16 |
| `WindowControlWidth` | 46 | | `AppMarkSize` | 18 |
| `WindowControlHeight` | 40 | | `FocusRingThickness` | 2 |
| `StatusBarHeight` | 24 | | `ScrollBarIdleWidth` | 4 |
| `SidebarWidth` | 288 | | `ScrollBarHoverWidth` | 12 |
| `SettingsNavWidth` | 216 | | `DisabledOpacity` | 0.4 |
| `DefaultWindowWidth` | 1120 | | `MenuDisabledOpacity` | 0.45 |
| `DefaultWindowHeight` | 720 | | `WideLayoutMinWidth` | 720 (Wide is >= 720) |
| `MinWindowWidth` | 400 | | `MediumLayoutMinWidth` | 520 (Medium 520-719, Narrow 400-519) |
| `MinWindowHeight` | 300 | | | |

Elevation (frozen `DropShadowEffect`, black, `RenderingBias=Performance`):

| Key | Shadow | Use |
|---|---|---|
| `FlyoutShadow` | blur 16, depth 4, 16% | Menus, find panel, toasts, dropdowns |
| `DialogShadow` | blur 32, depth 8, 26% | Dialogs over a scrim |

Performance: never put an effect on an element whose content changes often. The card styles below draw the shadow on
a separate static layer behind the content; do the same in custom templates (see `FlyoutCard`).

## Icons

Frozen `Geometry` resources on a **24 x 24 grid**, outline style (round caps and joins). Draw them with the `Icon`
control, which scales the grid to `Size` and keeps strokes at `StrokeThickness` DIPs:

```xml
<c:Icon Data="{StaticResource Search}" />                                  <!-- 16 px, inherits Foreground -->
<c:Icon Data="{StaticResource Warning}" Foreground="{DynamicResource WarnBrush}" Size="20" />
<c:Icon Data="{StaticResource AppMark}" Filled="True" Size="18" Foreground="{DynamicResource AccentBrush}" />
<Image Source="{StaticResource AppMarkImage}" Width="18" Height="18" />    <!-- full-color logo -->
```

Buttons, pills, nav items and menu items take an icon through `c:Ui.Icon="{StaticResource Trash}"` instead.

| Key | Meaning |
|---|---|
| `AppMark` | NimPad logo, one color (lines knocked out of the tile). **Filled** |
| `AppMarkTile` | NimPad logo tile only (for two-color rendering). **Filled** |
| `AppMarkLines` | NimPad logo lines only (for two-color rendering). **Filled** |
| `Plus` | Add, new note, new tab |
| `Minus` | Decrease (stepper), collapse |
| `Close` | Close, dismiss, clear |
| `Pin` | Pin |
| `Unpin` | Unpin |
| `Search` | Search field, find |
| `SearchOff` | No search results |
| `ChevronRight` | Expand (collapsed tree row), submenu |
| `ChevronDown` | Expanded tree row, dropdown |
| `ChevronLeft` | Back, scroll tabs left |
| `ChevronUp` | Collapse, previous |
| `Folder` | Folder (closed) |
| `FolderOpen` | Folder (expanded) |
| `FolderAdd` | New folder |
| `Document` | Note / document |
| `DocumentCode` | Note recognized as code |
| `DocumentAdd` | New note (empty states) |
| `ExternalFile` | External file opened from disk |
| `Trash` | Trash, delete |
| `Restore` | Restore, undo, reopen closed tab (curved arrow) |
| `Redo` | Redo (mirror of Restore) |
| `More` | More actions (horizontal ellipsis) |
| `SidebarToggle` | Show or hide the notes sidebar |
| `Check` | Check mark, selected menu item |
| `CheckCircle` | Saved / synced / done |
| `Warning` | Warning (triangle) |
| `ErrorCircle` | Error (circle with exclamation) |
| `Info` | Information |
| `Computer` | Local only / This computer |
| `Sort` | Sort order |
| `Settings` | Settings (gear) |
| `Link` | Link |
| `ExternalLink` | Opens outside the app (arrow up-right) |
| `Copy` | Copy, duplicate |
| `Cut` | Cut |
| `Paste` | Paste |
| `Rename` | Rename (pencil) |
| `Minimize` | Window: minimize (10 px glyph at Size 16) |
| `Maximize` | Window: maximize (10 px glyph at Size 16) |
| `RestoreWindow` | Window: restore down (10 px glyph at Size 16) |
| `WindowClose` | Window: close (10 px glyph at Size 16) |
| `Keyboard` | Keyboard shortcuts |
| `ArrowUp` | Previous match |
| `ArrowDown` | Next match |
| `MatchCase` | Find option: match case (Aa) |
| `WholeWord` | Find option: whole word (ab with underline) |
| `Regex` | Find option: regular expression (.*) |
| `Replace` | Replace |
| `ReplaceAll` | Replace all |
| `Spinner` | Busy arc (the `Spinner` control draws its own; use this for a static busy glyph) |
| `Import` | Import a file |
| `Export` | Export to a file |
| `Sync` | Sync now, syncing |
| `CloudOff` | Offline |
| `Sun` | Settings: Appearance |
| `User` | Settings: Account |
| `Sliders` | Settings: General |
| `SignOut` | Sign out |

Logo extras (fixed brand colors, identical in both themes, as in the deck): `AppMarkImage` (`DrawingImage`, blue
tile + white lines), `AppMarkTileBrush` (`#3055D8`), `AppMarkLinesBrush` (`#FFFFFF`).

## Attached properties (`c:Ui.*`)

Class `NimPad.App.Controls.Ui`.

| Property | Type (default) | Read by |
|---|---|---|
| `Ui.Icon` | `Geometry` (null) | All buttons, `FilterChip`, pills, `NavItem`, `MenuItem`, `IconBadge` |
| `Ui.IconSize` | `double` (16) | Button templates |
| `Ui.IconStrokeThickness` | `double` (1.25) | Button templates (title bar buttons use 1) |
| `Ui.CornerRadius` | `CornerRadius` (6) | Button templates, cards, scrollbar thumb |
| `Ui.Placeholder` | `string` | `TextBox`, `ComboBox` (hint while empty / nothing selected) |
| `Ui.HasError` | `bool` (false) | `TextBox`: Err ring + message row |
| `Ui.ErrorText` | `string` | `TextBox`: the message under the box (Err, with an ErrorCircle icon) |
| `Ui.IsWindowActive` | `bool` (true), **inherited** | `TitleBarButton` / `CloseTitleBarButton` dim to Text3 when false. Set once: `<Window c:Ui.IsWindowActive="{Binding IsActive, RelativeSource={RelativeSource Self}}">` |
| `Ui.ForceHover` / `Ui.ForcePressed` | `bool` (false) | Every control style: shows hover / pressed without the mouse. **Snap Layouts:** the maximize button gets `WM_NCMOUSEMOVE` instead of WPF mouse events; set these from the WndProc hook. |
| `Ui.ForceFocused` | `bool` (false) | `TextBox`, `SearchBox`, `ComboBox`: show their in-template focus ring (previews only) |
| `Ui.LabelPlacement` | `Dock` (Right) | `ToggleSwitch`: `Left` puts the content before the pill ("On" / "Off" in settings rows) |

## Focus visuals

A 2 px Accent ring, only for keyboard focus (WPF `FocusVisualStyle`). The styles already set these; use them for
custom templates via `FocusVisualStyle="{DynamicResource ...}"`.

| Key | Ring |
|---|---|
| `FocusVisual` | Outside, 2 px gap, radius 10 (around radius-6 controls: buttons, dropdowns, stepper) |
| `FocusVisualSmall` | Outside, 2 px gap, radius 8 (link buttons, small icon buttons) |
| `FocusVisualPill` | Outside, 2 px gap, fully rounded (switches, chips) |
| `InsetFocusVisual` | Inside, radius 6 (rows, tabs, segments, nav items) |
| `InsetFocusVisualSmall` | Inside, radius 4 (menu items, dropdown rows) |

Text inputs (`TextBox`, `SearchBox`, editable `ComboBox`) instead turn their own border into the 2 px Accent ring while
focused (mouse or keyboard), as in the deck.

## Window, dividers, links and tooltips

| Key | Target | Description |
|---|---|---|
| `AppWindow` | `Window` | Surface background, Text foreground, UI font at 14, layout rounding. `Style="{DynamicResource AppWindow}"`. |
| `Divider` | `Separator` | Horizontal 1 px `LineBrush` line, no margin. Also the implicit `Separator` style. |
| `VerticalDivider` | `Border` | 1 x 16 `SepBrush` line (between unselected tabs, status bar groups). |
| implicit `Hyperlink` | `Hyperlink` | Accent text, AccentHover + underline on hover. |
| implicit `ToolTip` | `ToolTip` | Compact inverse chip: `TextBrush` fill, `SurfaceBrush` 12 px text, radius 4, padding 8,4, wraps at 320. Content convention: `"Close (Ctrl+W)"`. |

## Buttons

All button styles share one template: `[c:Ui.Icon][Content]`, 8 px apart, `c:Ui.CornerRadius` corners. States:
hover (`IsMouseOver` or `c:Ui.ForceHover`), pressed (`IsPressed` or `c:Ui.ForcePressed`), focused (outside ring),
disabled (40% opacity; WPF never focuses disabled elements). Text is 13.5 / 500 unless noted.

| Key | Target | Size | Look (default → hover → pressed) |
|---|---|---|---|
| `PrimaryButton` | `Button` | 32, padding 16, min width 64 | Accent → AccentHover → AccentPressed; OnAccent text |
| `SecondaryButton` | `Button` | 32, padding 16, min width 64 | Hover wash + 1 px Line inset → Press wash → Press + Line2 inset. Also the implicit `Button` style. |
| `QuietButton` | `Button` | 32, padding 16, min width 64 | Transparent → Hover → Press (dialog Cancel) |
| `DestructiveButton` | `Button` | 32, padding 16, min width 64 | Secondary shape, Err text/icon → ErrSoft → ErrSoft + Line2 inset |
| `SmallButton` | `Button` | 26, padding 10, 13 px, icon 14 | Secondary look (Replace, Restore) |
| `SmallPrimaryButton` | `Button` | 26 | Primary look |
| `SmallQuietButton` | `Button` | 26 | Quiet look |
| `SmallDestructiveButton` | `Button` | 26 | Destructive look ("Delete forever" in Trash rows) |
| `LargeButton` | `Button` | 44, padding 20, 14 / 600, min width 120 | Primary look (the one welcome button) |
| `LinkButton` | `Button` | auto height (min 20), padding 2,0, 600 | Accent text → AccentHover → AccentPressed ("Undo", "Resolve", "Retry", "Clear search") |
| `IconButton` | `Button` | 28 x 28, radius 6 | Transparent, Text2 glyph → Hover + Text glyph → Press |
| `SmallIconButton` | `Button` | 20 x 20, radius 4 | Like `IconButton` (tab close, search clear, banner dismiss) |
| `NewTabButton` | `Button` | 30 x 30 | `IconButton` with the `Plus` glyph preset; hit-testable in `WindowChrome` caption |
| `IconToggleButton` | `ToggleButton` | 28 x 28 | Like `IconButton`; **checked = AccentSoft fill + Accent glyph** (find options, sidebar toggle) |
| `FilterChip` | `ToggleButton` | 28, pill, 13 / 400 | 1 px Line2 outline → Hover → Press; **checked = AccentSoft + Accent text** |
| `TitleBarButton` | `Button` | 46 x 40, square | Transparent → Hover → Press; glyph stroke 1; not focusable / not a tab stop; dims to Text3 when `c:Ui.IsWindowActive` is false; hit-testable in `WindowChrome` caption |
| `CloseTitleBarButton` | `Button` | 46 x 40 | `TitleBarButton` with `WindowClose` preset; hover/press = `CloseHoverBrush` red + white glyph |

Examples:

```xml
<Button Style="{StaticResource PrimaryButton}" Content="New note" IsDefault="True" />
<Button Style="{StaticResource SmallDestructiveButton}" c:Ui.Icon="{StaticResource Trash}" Content="Delete forever" />
<Button Style="{StaticResource IconButton}" c:Ui.Icon="{StaticResource More}" AutomationProperties.Name="More actions" />
<ToggleButton Style="{StaticResource IconToggleButton}" c:Ui.Icon="{StaticResource MatchCase}"
              IsChecked="{Binding MatchCase}" AutomationProperties.Name="Match case" ToolTip="Match case (Alt+C)" />
<Button Style="{StaticResource TitleBarButton}" c:Ui.Icon="{StaticResource Minimize}" AutomationProperties.AutomationId="MinimizeButton" />
<Button Style="{StaticResource TitleBarButton}" c:Ui.Icon="{StaticResource Maximize}" />   <!-- swap to RestoreWindow when maximized -->
<Button Style="{StaticResource CloseTitleBarButton}" AutomationProperties.Name="Close" />
```

Busy button (deck p-29): keep the width, disable it, and put a `Spinner` with `Foreground="{DynamicResource Text2Brush}"`
before the verb in the content.

## Inputs and selection

| Key | Target | Description |
|---|---|---|
| implicit `TextBox` | `TextBox` | 32 px (`MinHeight`), Surface fill, radius 6, 14 px text, padding 10,0. Border Line2 → Text3 (hover) → 2 px Accent ring (focused). `c:Ui.Placeholder` hint. Error: `c:Ui.HasError="True"` + `c:Ui.ErrorText="..."` = Err ring and an Err message row under the box (do not give such a field a fixed `Height`; set `MinHeight` to change the box height, e.g. 24 for inline rename). Disabled 40%. Caret `TextBrush`, selection `SelectionBrush`. Multi-line: `AcceptsReturn="True" VerticalContentAlignment="Top"`. The red WPF validation adorner is disabled; map validation to `c:Ui.HasError` yourself. |
| `ChromelessTextBox` | `TextBox` | No border/fill/padding, kit caret/selection, `c:Ui.Placeholder`. For text fields inside custom chrome. |
| implicit `SearchBox` | `c:SearchBox` | See [SearchBox](#searchbox). |
| `ToggleSwitch` | `CheckBox` | 40 x 20 pill. Off: Line2 outline, Text2 knob; hover: Text2 outline; pressed: Hover fill and the knob widens to 16; on: Accent fill (AccentHover / AccentPressed), OnAccent knob. Content = label (13 px Text2), right of the pill, or left with `c:Ui.LabelPlacement="Left"`. Focus: `FocusVisualPill`. |
| implicit `CheckBox` | `CheckBox` | 18 px box, radius 4, Line2 → Text3 hover; checked = Accent + OnAccent check; `IsChecked=null` = Accent + dash. |
| implicit `RadioButton` | `RadioButton` | 18 px circle; checked = Accent with an OnAccent dot. |
| implicit `SegmentedControl` | `c:SegmentedControl` | See [SegmentedControl](#segmentedcontrol). Hover-wash track, radius 6, 2 px inset; selected segment = Raised + Line2 outline, Text; others Text2. |
| implicit `ComboBox` | `ComboBox` | 32 px dropdown: Surface, Line2 outline (Text3 hover), `ChevronDown`, 14 px text, padding 12,0,8,0; open = Hover fill. Popup: Raised card, radius 8, Line border, `FlyoutShadow`, 4 px padding, max 360 tall, virtualized. `c:Ui.Placeholder` while nothing is selected. `IsEditable` supported (Accent ring while focused). |
| implicit `ComboBoxItem` | `ComboBoxItem` | 32 px rows, radius 4, padding 12,0; highlighted = Hover; selected = Hover + 3 x 16 Accent marker at the left; inset focus ring; disabled 45%. |
| implicit `Stepper` | `c:Stepper` | See [Stepper](#stepper). 32 px outlined box (min width 140), − value +. |

## Menus

| Key | Description |
|---|---|
| implicit `ContextMenu` | Raised card, radius 8, 1 px Line border, 4 px padding, `FlyoutShadow`, min width 200, 13.5 / 500. The card sits inside a transparent 10,6,10,14 margin (shadow room); the style sets `HorizontalOffset=-10`, `VerticalOffset=-6` so the card's corner lands on the mouse point or under the placement target. With `Placement="Top"` also add about `VerticalOffset="14"`. |
| implicit `MenuItem` | 32 px rows (`MinHeight`), radius 4, padding 12,0; 24 px icon column (`MenuItem.Icon` element or `c:Ui.Icon` geometry; checked items show an Accent `Check` there); `InputGestureText` right-aligned in Text3 12 px (aligned across the menu); `ChevronRight` + child popup for submenus; hover/keyboard highlight = Hover, pressed = Press, keyboard focus = inset ring; disabled 45%. Menu-bar roles (`Menu`) have compact 28 px templates. |
| `MenuItem.SeparatorStyleKey` | 1 px Line with 4 px margins. A plain `<Separator />` inside a menu picks it up automatically. |
| implicit `Menu` | Transparent horizontal menu bar. |

```xml
<Button.ContextMenu>
    <ContextMenu>
        <MenuItem Header="Rename" InputGestureText="F2" c:Ui.Icon="{StaticResource Rename}" Command="{Binding RenameCommand}" />
        <MenuItem Header="Duplicate" InputGestureText="Ctrl+Shift+D" c:Ui.Icon="{StaticResource Copy}" />
        <Separator />
        <MenuItem Header="Last edited" IsCheckable="True" IsChecked="True" />
    </ContextMenu>
</Button.ContextMenu>
```

## Lists and scrolling

| Key | Target | Description |
|---|---|---|
| implicit `ScrollViewer` | `ScrollViewer` | Overlay bars: content uses the full area; bars float over the right/bottom edge. Applies everywhere, including AvalonEdit's internal `PART_ScrollViewer`. Keeps the standard PART names. |
| implicit `ScrollBar` | `ScrollBar` | 12 px hit area, no arrows, transparent track. Idle: 4 px `Line2Brush` thumb; bar hover: Hover rail (radius 6) + 6 px thumb; thumb hover / drag: `Text3Brush`. Track click pages. |
| `ChromelessListBox` | `ListBox` | Transparent, borderless, one tab stop, virtualized (recycling) with pixel scrolling. Also the implicit `ListBox` style. |
| `NoteRowItem` | `ListBoxItem` | Rows: radius 6, 2 px side margin, padding 10,0, min height 32; hover = Hover; selected = AccentSoft; inset focus ring; disabled 40%. Also the implicit `ListBoxItem` style. Rows have no pressed wash (selection happens on press). |
| `NavItem` | `ListBoxItem` | Settings navigation: 40 px, radius 6, padding 12,0, 14 px, optional `c:Ui.Icon`; hover = Hover; selected = Hover + 600 weight + 3 x 16 Accent bar at the left edge. `<ListBox ItemContainerStyle="{StaticResource NavItem}">`. |
| implicit `ProgressBar` | `ProgressBar` | 4 px rounded Hover track, Accent fill. Determinate only (for unknown durations use `Spinner`; `IsIndeterminate` shows a faint full bar). |

```xml
<ListBox ItemsSource="{Binding Rows}" ItemContainerStyle="{StaticResource NoteRowItem}"
         AutomationProperties.Name="Notes" />
```

## Surfaces and status

Card styles target `ContentControl` (not focusable): `<ContentControl Style="{StaticResource DialogCard}">...</ContentControl>`.
Elevated cards need room around them for the shadow (about 8 px at the sides, 16 px below).

| Key | Target | Description |
|---|---|---|
| `Card` | `ContentControl` | Flat: Raised, 1 px Line, radius 8 (`c:Ui.CornerRadius`), padding 16. Settings groups, account card. |
| `FlyoutCard` | `ContentControl` | Raised, 1 px Line, radius 8, `FlyoutShadow`, padding 12. Find panel, popovers. |
| `DialogCard` | `ContentControl` | Raised, 1 px Line, radius 8, `DialogShadow`, padding 24. Place it over a layer filled with `ScrimBrush`; focus the safe action; Esc cancels. |
| `ToastCard` | `ContentControl` | `FlyoutCard`, min height 44, padding 16,10, 13.5 px. Message + `LinkButton` action + shortcut caption + `SmallIconButton` close. |
| `CalloutCard` | `ContentControl` | Hover fill, no border, radius 6, padding 12, Text2 12.5 px. Tips inside panels. |
| `IconBadge` | `ContentControl` | 44 px Hover circle with a 20 px Text2 `c:Ui.Icon`. Empty states. |
| `StatusPill` | `ContentControl` | 22 px pill, 12 / 600, padding 8,0, optional `c:Ui.Icon` (14 px). Neutral: Hover + Text2. |
| `OkPill` | `ContentControl` | OkSoft + Ok ("Connected"). |
| `AccentPill` | `ContentControl` | AccentSoft + Accent ("Newer"). |
| `WarnPill` | `ContentControl` | WarnSoft + Warn ("1 conflict"). |
| `ErrPill` | `ContentControl` | ErrSoft + Err ("Sync paused"). |
| implicit `Banner` | `c:Banner` | See [Banner](#banner). |
| `InfoBanner` | `c:Banner` | `Kind=Info`: Hover fill, Text2 `Info` icon. |
| `WarningBanner` | `c:Banner` | `Kind=Warning`: WarnSoft fill, Warn `Warning` icon. |
| `ErrorBanner` | `c:Banner` | `Kind=Error`: ErrSoft fill, Err `ErrorCircle` icon. |
| implicit `KeyCap` | `c:KeyCap` | See [KeyCap and ShortcutKeys](#keycap-and-shortcutkeys). |
| implicit `ShortcutKeys` | `c:ShortcutKeys` | Gesture as key caps 4 px apart. |
| implicit `Avatar` | `c:Avatar` | See [Avatar](#avatar). |
| implicit `Spinner` | `c:Spinner` | Accent by default. |
| implicit `SkeletonLine` | `c:SkeletonLine` | 12 px Hover bar, not hit-testable. |

Dialog over a scrim:

```xml
<Grid>
    <Border Background="{DynamicResource ScrimBrush}" />
    <ContentControl Style="{StaticResource DialogCard}" Width="400" HorizontalAlignment="Center" VerticalAlignment="Center">
        <StackPanel>
            <TextBlock Style="{StaticResource DialogTitleText}" Text="Sign out of NimPad?" />
            <TextBlock Style="{StaticResource BodyText}" Foreground="{DynamicResource Text2Brush}" TextWrapping="Wrap" Margin="0,8,0,0" Text="..." />
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,24,0,0">
                <Button Style="{StaticResource QuietButton}" Content="Cancel" IsCancel="True" />
                <Button Style="{StaticResource PrimaryButton}" Content="Sign out" IsDefault="True" Margin="8,0,0,0" />
            </StackPanel>
        </StackPanel>
    </ContentControl>
</Grid>
```

## Custom controls reference

Namespace `NimPad.App.Controls` (`xmlns:c="clr-namespace:NimPad.App.Controls"`). Each has an implicit style in the
design system; none needs extra setup.

### Icon

`FrameworkElement` (no template; one cached frozen pen).

| Property | Type (default) | Notes |
|---|---|---|
| `Data` | `Geometry` | A key from [Icons](#icons) (24 x 24 grid). |
| `Size` | `double` (16) | Width and height in DIPs. |
| `Foreground` | `Brush` (inherited) | `TextElement.Foreground`; follows the parent control's state. |
| `StrokeThickness` | `double` (1.25) | DIPs at the rendered size. |
| `Filled` | `bool` (false) | Fill instead of stroke (only the `AppMark*` geometries). |
| `Icon.GridSize` | const 24 | |

### SearchBox

`Control`. 32 px, search icon, placeholder, clear button once there is text, **Esc clears** (Esc on an empty box is
not handled, so it bubbles, e.g. to close the sidebar). The box is not a tab stop; focus goes to its inner text field.
UI Automation: an `Edit` with the Value pattern carrying the SearchBox's own `AutomationProperties.Name` /
`AutomationId` (the placeholder is the fallback name).

| Member | Notes |
|---|---|
| `Text` (`string`, "") | Two-way by default, updates on every keystroke. |
| `Placeholder` (`string`) | e.g. "Search all notes". |
| `HasText` (`bool`, read-only) | |
| `ClearCommand` (`ICommand`) + `ClearCommandParameter` | Optional; executed after clearing (button, Esc or `Clear()`). |
| `Cleared` (routed event, bubbling) | Raised after clearing. |
| `Clear()` | Clears, raises `Cleared`, runs `ClearCommand`. |
| `FocusAndSelectAll()` | Focus the inner field and select its text (Ctrl+F style). |
| Parts | `PART_TextBox` (`TextBox`), `PART_ClearButton` (`ButtonBase`). |

```xml
<c:SearchBox Text="{Binding Query}" Placeholder="Search all notes" ClearCommand="{Binding ClearSearchCommand}"
             AutomationProperties.Name="Search notes" AutomationProperties.AutomationId="SidebarSearch" />
```

### SegmentedControl

`SegmentedControl : ListBox` (single selection) with `SegmentedControlItem : ListBoxItem` containers. 2 to 4 options.
Bind `ItemsSource` + `SelectedItem` / `SelectedValue` (+ `SelectedValuePath`), or declare items. One tab stop;
Left/Up, Right/Down, Home and End move the selection.

```xml
<c:SegmentedControl SelectedValue="{Binding Theme}" SelectedValuePath="Tag" AutomationProperties.Name="Theme">
    <c:SegmentedControlItem Content="Light" Tag="{x:Static vm:ThemeChoice.Light}" />
    <c:SegmentedControlItem Content="Dark" Tag="{x:Static vm:ThemeChoice.Dark}" />
    <c:SegmentedControlItem Content="System" Tag="{x:Static vm:ThemeChoice.System}" />
</c:SegmentedControl>
```

### Stepper

`Control`. "− 14 px +". One tab stop; Up/Right +Step, Down/Left −Step, PageUp/PageDown ±5 steps, Home/End =
Minimum/Maximum. The − and + parts repeat while held and disable at the limits. UI Automation: `Spinner` with the
RangeValue pattern.

| Member | Notes |
|---|---|
| `Value` (`double`, 0) | Two-way by default; clamped to [Minimum, Maximum]. |
| `Minimum` (0), `Maximum` (100), `Step` (1) | |
| `Suffix` (`string`) | `"px"` renders "14 px". |
| `DisplayText`, `CanDecrease`, `CanIncrease` | Read-only, for the template. |
| `ValueChanged` | `RoutedPropertyChangedEventHandler<double>`. |
| `Increase()`, `Decrease()` | |
| Parts | `PART_DecreaseButton`, `PART_IncreaseButton` (`ButtonBase`; the template uses `RepeatButton`s styled `StepperButton`). |

```xml
<c:Stepper Value="{Binding EditorFontSize}" Minimum="8" Maximum="32" Suffix="px" AutomationProperties.Name="Font size" />
```

### KeyCap and ShortcutKeys

`KeyCap : Control` with `Text` ("Ctrl"): radius 4, Line2 border with a 2 px bottom edge, Surface fill, mono 12 px
Text2, 22 px tall, min width 22. `ShortcutKeys : Control` with `Gesture` ("Ctrl+Shift+T") renders one `KeyCap` per key,
4 px apart; `Keys` (read-only) holds the parsed labels; `ShortcutKeys.ParseGesture(string)` is public ("Ctrl++" →
Ctrl, +). Both are not focusable; UI Automation reads the text / gesture once.

### Avatar

`Control`, 28 x 28 by default.

| Member | Notes |
|---|---|
| `Initials` (`string`) | "BK"; 11 / 600 AvatarFg on AvatarBg. |
| `Source` (`ImageSource`) | Optional photo; replaces the initials. |
| `Status` (`AvatarStatus`) | `None`, `Syncing` (Accent dot), `Offline` (Text3), `Conflict` (Warn), `Error` (Err). |
| `StatusRingBrush` (`Brush`) | Ring around the 10 px dot; default `ChromeBrush` (title bar). Set it to the surface behind the avatar. |

Larger avatar: set `Width`/`Height` and `FontSize` together (64 px → `FontSize="22"`). The avatar is not focusable:
wrap it in a `Button` (e.g. `QuietButton` with `Padding="0"`) when it opens the account menu, and name that button.

### Banner

`Banner : ContentControl`. Content is the message (strings wrap, 14 / 20).

| Member | Notes |
|---|---|
| `Kind` (`BannerKind`) | `Info`, `Warning`, `Error` (or use `InfoBanner` / `WarningBanner` / `ErrorBanner`). |
| `Icon` (`Geometry`) | Overrides the kind's icon (e.g. `CloudOff` for offline). |
| `ActionContent` + `ActionCommand` + `ActionCommandParameter` | The one link-style action; hidden when `ActionContent` is null. |
| `DismissCommand` + `DismissCommandParameter` | Shows a dismiss X (`SmallIconButton`) when set. |
| `CornerRadius` (6) | Set `0` for a full-bleed banner under the tab strip. |

```xml
<c:Banner Style="{StaticResource WarningBanner}" Content="“Meeting notes” changed on two computers."
          ActionContent="Resolve" ActionCommand="{Binding ResolveCommand}" CornerRadius="0" />
```

### Spinner

`FrameworkElement`. A 300° arc rotating once per 0.9 s; animates only while loaded, visible and `IsActive`, and stays
still when Windows animation effects are off (`SystemParameters.ClientAreaAnimation`). Removing it from the tree or
hiding it stops the clock entirely.

| Member | Notes |
|---|---|
| `Size` (16), `StrokeThickness` (1.5) | |
| `Foreground` (inherited; Accent from the style) | Text2 inside busy buttons. |
| `IsActive` (true) | Pause without removing it. |
| `IsSpinning` (read-only CLR) | For tests and diagnostics. |

### SkeletonLine

`FrameworkElement`: a fully rounded bar filled with `Fill` (`HoverBrush` from the style), height 12. Set `Width` to the
text it stands in for (deck p-29: placeholders between 300 and 600 ms). Static, no shimmer.

## Internal building blocks

Exposed so custom styles can reuse them (`BasedOn` / `Template`), otherwise not needed directly.

| Key | What |
|---|---|
| `KitButtonTemplate` | The shared `ButtonBase` template (fill, edge, icon, content). |
| `ButtonBaseStyle` | Base style of every button style (`TargetType=ButtonBase`). |
| `StepperButton` | `RepeatButton` style for the stepper's − / + parts. |
| `CloseTitleBarGlyphBrush` / `CloseTitleBarPressedBrush` | Fixed white glyph / 90% close red for the close button. |
| `ComboBoxToggle` | Invisible full-size `ToggleButton` behind a ComboBox. |
| `ScrollBarPageButton`, `ScrollBarThumb` | Scrollbar parts. |
| `VerticalScrollBarTemplate`, `HorizontalScrollBarTemplate` | Scrollbar templates. |
| `MenuItemSubmenuItemTemplate`, `MenuItemSubmenuHeaderTemplate`, `MenuItemTopLevelItemTemplate`, `MenuItemTopLevelHeaderTemplate` | MenuItem templates by role. |

## Decisions and deviations from the deck

- **Secondary and destructive button labels are mixed case.** The deck's pages 25-26 render them in spaced capitals,
  which other deck pages (Trash rows, banners, find panel) do not; mixed case was kept everywhere.
- **Tooltips are an inverse chip** (Text fill, Surface text) in both themes, as drawn on page 25.
- **The logo keeps the light accent (#3055D8) and white lines in both themes**, as on pages 1 and 2.
- **Text inputs show the Accent ring for mouse focus too** (it replaces the border, as in Windows); every other
  control shows its ring only for keyboard focus.
- **Rows have no pressed wash**: WPF list items select on mouse down.
- **Shadows are one theme-independent pair** (`FlyoutShadow`, `DialogShadow`); in the dark theme the 1 px Line border
  of raised surfaces carries most of the separation, as in Windows 11.
- **Scrollbars overlay the content** (no reserved gutter): 4 px idle, 12 px rail with a 6 px thumb on hover.
- **Font weights**: 500 resolves to Segoe UI Variable's Medium on Windows 11; with the plain Segoe UI fallback WPF
  picks the nearest available weight.
- **Typography letter-spacing** (Display −0.02em, Micro +0.06em) is omitted: WPF `TextBlock` has no tracking.
