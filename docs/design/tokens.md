# NimPad design tokens

Transcribed from the approved design deck (`NimPad — Cloud Notepad for Windows.pdf`, pages "Color and tokens",
"Type, spacing, radii and metrics", "Window chrome", "Controls and surfaces", "Interaction states").
Every screen reads from these named tokens, never from raw values, so a theme is simply a second set of values.
The WPF resource keys use the same names (`chrome`, `text-2`, … → `Chrome`, `Text2`, …).

## Color

Translucent tokens are "ink" laid over whatever surface is underneath: `#1D1D1B · 6%` means color `#1D1D1B` at
6% opacity.

### Surfaces & text

| Token | Light | Dark | Use |
|---|---|---|---|
| chrome | `#EFEFEC` | `#252527` | Frames the app: title bar, tab strip, sidebar |
| surface | `#FBFBFA` | `#1B1B1D` | The writing area and the selected tab |
| raised | `#FFFFFF` | `#2B2B2E` | Menus, dialogs, cards, toasts |
| text | `#1D1D1B` | `#E8E7E3` | Primary text |
| text-2 | `#5B5B57` | `#A8A7A2` | Secondary text, unselected tab titles |
| text-3 | `#686863` | `#92918C` | Quietest text that still passes AA |

### Interaction

| Token | Light | Dark | Use |
|---|---|---|---|
| hover | `#1D1D1B · 6%` | `#FFFFFF · 7%` | Hover wash on buttons and rows |
| press | `#1D1D1B · 10%` | `#FFFFFF · 11%` | Pressed wash |
| sep | `#1D1D1B · 16%` | `#FFFFFF · 16%` | Tab separators |
| line | `#1D1D1B · 10%` | `#FFFFFF · 9%` | Card edges |
| line-2 | `#1D1D1B · 26%` | `#FFFFFF · 22%` | Control outlines |

### Accent

One blue, used sparingly: the primary button, focus ring, selected row, links and the active-section marker.

| Token | Light | Dark |
|---|---|---|
| accent | `#3055D8` | `#8CA2FF` |
| accent-h (hover) | `#2647BD` | `#A3B5FF` |
| accent-p (pressed) | `#1F3B9E` | `#7389EA` |
| accent-soft | `#E6ECFC` | `#8CA2FF · 16%` |
| on-accent | `#FFFFFF` | `#0B1230` |

### Editor & identity

| Token | Light | Dark | Use |
|---|---|---|---|
| sel | `#3055D8 · 18%` | `#8CA2FF · 30%` | Text selection |
| cur-line | `#1D1D1B · 4%` | `#FFFFFF · 4%` | Current-line wash |
| match | `#F0B928 · 40%` | `#F0BE3C · 34%` | Find matches |
| match-cur | `#F09614 · 72%` | `#F0A028 · 60%` | Current find match |
| avatar-bg | `#DCE3FB` | `#33406F` | Account avatar fill |
| avatar-fg | `#273C99` | `#D5DEFF` | Account avatar initials |

### Status

Status is never color alone: every state also carries an icon and a word. `add`/`del` tint conflict-diff lines only.

| Token | Light | Dark |
|---|---|---|
| ok | `#26704A` | `#6FCB95` |
| ok-soft | `#E3F2EA` | `#6FCB95 · 14%` |
| warn | `#8F5200` | `#EDB75A` |
| warn-soft | `#FBEFD5` | `#EDB75A · 14%` |
| err | `#B3261E` | `#FF8F86` |
| err-soft | `#FBE5E3` | `#FF8F86 · 14%` |
| add | `#289655 · 16%` | `#5AC882 · 18%` |
| del | `#C8372D · 14%` | `#FF6E64 · 18%` |

### Syntax

Used only when a note is recognized as code (JSON, Markdown, config files). Plain text stays one color.

| Token | Light | Dark |
|---|---|---|
| s-key | `#2B5FAE` | `#8DB4FF` |
| s-str | `#2A7A4B` | `#8FD1A5` |
| s-num | `#A8530F` | `#F0B27A` |
| s-lit | `#8A3FA8` | `#C9A0F0` |
| s-punc | `#75756F` | `#8D8C87` |
| s-com | `#75756F` | `#8A8984` |

### Contrast (WCAG 2.1 AA)

| Pair | Light | Dark |
|---|---|---|
| text on surface | 16.3:1 | 13.9:1 |
| text-2 on surface | 6.6:1 | 7.1:1 |
| text-3 on surface | 5.4:1 | 5.5:1 |
| text-3 on chrome | 4.9:1 | 4.9:1 |
| accent on surface | 5.9:1 | 7.2:1 |
| on-accent on accent | 6.2:1 | 7.6:1 |

## Type

All sizes are in pixels at 100% scale. Interface font: **Segoe UI Variable** (fallback Segoe UI). Editor font:
**Cascadia Mono** (the only monospaced text; size is user-adjustable).

| Style | Size / line height | Weight | Use |
|---|---|---|---|
| Display | 44 / 48 | 600, −0.02em | Welcome headline only |
| Page title | 24 / 32 | 600 | Settings page heading, sheet titles |
| Dialog title | 20 / 26 | 600 | Dialog and panel headings |
| Subtitle | 16 / 22 | 600 | Status headings, card titles |
| Body | 14 / 20 | 400 · 500 for labels | Settings labels and general UI text |
| Control | 13.5 / 18 | 500 · 600 for names | Buttons, menu items, list names |
| Tab | 13 / 18 | 400 · 500 when selected | Tab titles, chips, sidebar search |
| Secondary | 12.5 / 17 | 400 | Setting descriptions, note previews |
| Caption | 12 / 16 | 400 | Status bar, helper text, footers |
| Micro | 11 / 14 | 600 · caps · +0.06em | Sidebar group labels (timestamps use 11.5 / 16 regular) |
| Editor | 14 / 22 | 400 · mono | Editor text |

## Spacing (4 px base)

| px | Use |
|---|---|
| 4 | Icon to label, tight pairs |
| 8 | Inside controls, tab to close button |
| 12 | Sidebar and bar padding |
| 16 | Card padding, gaps between groups |
| 20 | Editor top padding |
| 24 | Page and sheet padding |
| 32 | Editor side padding |
| 40 | Title bar height, large gaps |
| 48 | Display line height |

## Corner radius

| px | Use |
|---|---|
| 4 | Tab close button, key caps, menu items |
| 6 | Buttons, inputs, rows |
| 8 | Menus, dialogs, cards, window, tab top corners |
| Full | Avatar, switch, pills |

## Elevation

| Level | Use |
|---|---|
| Flat | Tabs, rows, bars |
| Flyout | Menus, find panel, toasts (soft shadow) |
| Dialog | Dialogs, over a scrim (stronger shadow) |

## Key metrics

| Element | Value |
|---|---|
| Title / tab bar | 40 |
| Tab height | 34 |
| Tab width | 96 to 172 |
| Pinned tab | 104 (pin icon, no close button) |
| New-tab button | 30 × 30 |
| Account avatar | 28 |
| Window controls | 46 × 40 each (close turns red on hover) |
| Status bar | 24 |
| Sidebar | 288 |
| Settings nav | 216 |
| Editor padding | 20 top / 32 sides (24 sides in medium layout) |
| Default window | 1120 × 720 |
| Minimum window | 400 × 300 |
| Focus ring | 2 px accent, offset 2 (inset on rows, tabs, menu items) |
| Controls | 32 tall (26 inline actions, 44 the welcome button), radius 6 |
| Menu rows | 32 tall, radius 4, disabled at 45% |
| Banner | 36 minimum, under the tab strip, one action at most |
| Scrollbar | 4 px idle, 12 px on hover |

## Interaction states

Every interactive part has the same six states in both themes.

- **Hover:** a translucent wash (6 to 7%). The primary button steps to `accent-h`.
- **Pressed:** a stronger wash (10 to 11%). The primary button steps to `accent-p`. Nothing moves.
- **Focused:** a 2 px accent ring. On standalone controls it sits outside with a 2 px offset; on rows, tabs and
  menu items it sits inside.
- **Selected:**
  - Tabs: the editor surface.
  - Rows: `accent-soft`.
  - Navigation: a 3 px accent bar.
  - Switches: an accent fill.
- **Disabled:** 40 to 45% opacity, no hover, not focusable. Always paired with a reason nearby when it is not
  obvious.

## Responsive window layouts

| Layout | Width | Behavior |
|---|---|---|
| Wide | ≥ 720 | Sidebar docks beside the editor at 288 px. Editor padding 20 / 32. Tabs shrink from 172 toward 96 before scrolling. |
| Medium | 520 – 719 | App name hides. Sidebar overlays the editor; Esc or a click outside closes it. Editor padding 20 / 24. Character count leaves the status bar. |
| Narrow | 400 – 519 | Tabs scroll with chevrons; the account moves into the sync popover. Status bar keeps position and sync; a ⋯ menu holds encoding, mode and zoom. Find spans the full width. |

## Loading timing

| Elapsed | Behavior |
|---|---|
| 0 – 300 ms | Show nothing |
| 300 – 600 ms | Placeholders at final size (skeleton lines or rows) |
| > 600 ms | A spinner where the result will land |
| > 2 s | Say what is happening with a count or percentage; offer Cancel when safe |
