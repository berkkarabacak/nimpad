# NimPad design

The approved visual design is the deck `NimPad — Cloud Notepad for Windows.pdf` (the `.html` file is the same deck
as an interactive bundle). [tokens.md](tokens.md) transcribes every color, type style, spacing step, radius and
metric from it. Use the token names in code, never raw values.

## Page index

| Page | Content |
|---|---|
| 1–2 | Welcome / sign-in screen, light and dark (Phase 2) |
| 3 | Sign-in states: ready, waiting for Google, error, offline on first launch (Phase 2) |
| 4–5 | Main window: tab strip, editor and status bar, light and dark |
| 6 | Editor with line numbers and JSON syntax highlighting, many tabs |
| 7 | Notes sidebar open beside the editor (dark) |
| 8 | Notes sidebar states: search results, sort menu, Trash, empty Trash, no notes yet, no results |
| 9 | Find and replace panel |
| 10 | Tab context menu and the "Closed … · Undo" toast |
| 11 | Sync conflict dialog: side by side / changes only (Phase 2) |
| 12 | Keyboard shortcuts dialog |
| 13 | Sync states in the status bar, avatar and banners (Phase 2; Phase 1 shows "Local only") |
| 14, 16–17 | Tabs restoring on a new computer, progress and completion toast (Phase 2) |
| 15 | Waiting for Google, full window (Phase 2) |
| 18–21 | Settings: Appearance, Account, Synchronization, General |
| 22 | Color tokens, light and dark |
| 23 | Type, spacing, radii, elevation and key metrics |
| 24 | Window chrome: title bar, tab states, new-note button, avatar, window controls, scrollbar, status bar |
| 25 | Controls and surfaces: buttons, text fields, selection controls, menus, tooltip, toasts, banners, dialog |
| 26 | Interaction states for every control, light and dark |
| 27 | Keyboard interaction map |
| 28 | Responsive window sizes: wide, medium, narrow |
| 29 | Empty and loading states, and loading timing rules |

## Adaptations made during implementation

- **Sidebar.** The deck draws a flat, date-grouped note list. The product spec requires a hierarchical Explorer, so
  the sidebar is tree-first:
  - The search field stays at the top.
  - Pinned and Recent follow, using the deck's row style (title, preview line, timestamp).
  - The workspace folder tree comes next.
  - Trash stays at the bottom.

  The deck's search-result highlighting, sort menu and Trash states are kept. The All/Pinned/Untitled filter
  chips are dropped.
- **Breadcrumbs.** The spec asks for breadcrumbs above the editor. They are drawn as one quiet caption line in
  `text-3`, inside the editor's top padding.
- **Status bar in Phase 1.** Until cloud sync exists, the sync slot reads "Local only", or "Not synced" for
  external files. It never shows a "Synced" state that isn't real.
