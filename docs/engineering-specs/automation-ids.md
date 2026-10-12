# NimPad UI Automation contract

Every engineer building NimPad.App views MUST expose these AutomationIds (AutomationProperties.AutomationId)
exactly, and set AutomationProperties.Name as described. The FlaUI end-to-end tests (tests/e2e-windows) are
written against this contract before the UI exists, so a mismatch breaks CI. Ids are case-sensitive.

## Main window (shell)
| AutomationId | Element | Name / notes |
|---|---|---|
| MainWindow | the main Window | Window title "NimPad". AutomationProperties.ItemStatus = "theme:light" or "theme:dark" (the EFFECTIVE theme), updated live. |
| TitleBar | title bar container | |
| TabStrip | the tab strip list | |
| Tab_<n> | each tab item, n = 0-based visual index | Name = the tab title (e.g. "Untitled 1", "notes.txt"). ItemStatus = "unsaved" while waiting to save, "saved" otherwise, "error" on save failure. |
| TabClose_<n> | the close button inside tab n | |
| NewTabButton | new-note (+) button | |
| MinimizeButton, MaximizeButton, CloseButton | window controls | |
| BannerHost | container of banners | each banner's Name = its message |
| ToastHost | container of toasts | each toast's Name = its message |
| StatusBar | status bar | |
| StatusPosition | position text | Name = the visible text, e.g. "Ln 1, Col 1" |
| StatusCharacters | character count | Name = e.g. "12 characters" |
| StatusEncoding | encoding item | Name = e.g. "UTF-8", "UTF-16 LE" |
| StatusMode | mode item | Name = e.g. "Plain Text" |
| StatusZoom | zoom item | Name = e.g. "100%" |
| StatusSync | sync item | Name = "Local only" / "Not synced" / "Not saved · Retry" |
| SidebarToggle | status-bar sidebar toggle | |
| EmptyStateNewNote, EmptyStateReopen | buttons of the "No notes open" state | |
| ShortcutsDialog, ShortcutsFilter | keyboard shortcuts dialog and its filter box | |
| ConfirmDialog, ConfirmPrimary, ConfirmCancel | the confirm dialog and its buttons | |

## Editor
| AutomationId | Element | Notes |
|---|---|---|
| Editor | the AvalonEdit TextEditor | Its automation peer must expose the document text through the Value pattern (AvalonEdit's TextEditorAutomationPeer does; do not replace it). Keyboard focus lands here. |
| Breadcrumbs | breadcrumb line | each segment's Name = folder/note name |
| FindPanel | find & replace panel | |
| FindQuery, ReplaceQuery | text boxes | |
| FindMatchCase, FindWholeWord, FindRegex | toggles | |
| FindCount | count text | Name = "1 of 2" / "No results" |
| FindPrevious, FindNext, FindClose, ReplaceOne, ReplaceAll | buttons | |

## Explorer sidebar
| AutomationId | Element | Notes |
|---|---|---|
| ExplorerSearch | search box (its inner TextBox must accept typing; expose the Value pattern) | |
| ExplorerList | the virtualized ListBox | |
| ExplorerRow_<nodeId> | each tree/note row | Name = the node name (tests find rows by Name). Tree rows expose ExpandCollapse. |
| ExplorerRenameBox | the inline rename TextBox (only while renaming) | |
| ExplorerNewNote, ExplorerNewFolder, ExplorerSort, ExplorerTrash | toolbar/footer buttons | |
| TrashBack, TrashEmptyButton, RestoreButton, DeleteForeverButton | Trash view | |
| ExplorerSearchResult_<nodeId> | each search-result row | Name = the note name |

## Settings window
| AutomationId | Element |
|---|---|
| SettingsWindow | the window |
| SettingsNav | the section list |
| ThemeSegmented | theme selector; its items have Names "Light", "Dark", "System" |
| FontDropdown, FontSizeStepper, WordWrapSwitch, LineNumbersSwitch, TabSizeSegmented | appearance controls |
| StartWithWindowsSwitch, RestoreTabsSwitch, GlobalShortcutChange | general controls |

## Command line (App)
`NimPad.exe [--data-dir <path>] [--no-single-instance] [--no-hotkey] [--background] [files...]`
Tests always pass --data-dir <temp> --no-single-instance --no-hotkey.
