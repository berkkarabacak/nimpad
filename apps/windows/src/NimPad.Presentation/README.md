# NimPad.Presentation

The view models of the desktop app. The project targets `net10.0`, references NimPad.Core and CommunityToolkit.Mvvm
only (no WPF), and is tested on any OS (`dotnet test --project apps/windows/tests/NimPad.Presentation.Tests`). The WPF
App binds to these classes; this page is the contract for the App engineers.

## Rules for the App

- **Threading.** Create `ShellViewModel` on the UI thread. Every view-model member is used on the UI thread. Core
  events (raised on background threads) and timers reach the view models through `IUiDispatcher`, which the App
  implements over `Dispatcher`.
- **Time.** All timing (autosave, debounces, toasts, loading placeholders) uses the `TimeProvider` passed to
  `ShellViewModel`; the App passes nothing (the system clock).
- **Errors.** Commands never throw: failures become an error toast (`Toasts`) or banner (`Banners`). Public `…Async`
  methods of `TabsViewModel` may throw storage errors; call them from commands or catch.
- **Collections.** Every `ObservableCollection` changes one item at a time (insert, remove, move). `Explorer.Rows`
  reuses its row objects and moves only the rows that moved (one `Move` per moved row) in the tree, the search
  results and the Trash alike; it uses `Reset` only to replace its whole content: switching modes, or a real reorder
  (more than 128 moves, such as a new sort order on a large tree). Bind ListBoxes directly; WPF's CollectionView
  accepts both.
- **Texts.** User-facing strings (empty states, banners, toasts, status texts) come from the view models and follow the
  design deck. Phase 1 never claims cloud sync: the status bar says "Local only" (notes) or "Not synced" (files).

## Platform interfaces (namespace `NimPad.Presentation.Platform`)

The App implements these and passes them in a `PlatformServices`:

| Interface | Members | Notes |
|---|---|---|
| `IUiDispatcher` | `CheckAccess()`, `Post(Action)`, `InvokeAsync(Func<Task>)` | Over WPF's `Dispatcher` (`BeginInvoke` for `Post`). |
| `IEditorBuffer` | `Version`, `TextLength`, `GetSnapshotText()` | One per open tab, over AvalonEdit's `TextDocument`: `Version` is a counter incremented in `TextDocument.Changed` (read with `Interlocked`), `GetSnapshotText()` returns `CreateSnapshot().Text`, marshaling to the UI thread when called elsewhere (autosave timers call it from the thread pool). Use a bounded wait when marshaling: `EmergencyFlush` may hold the UI thread. |
| `IClipboardService` | `SetText` | |
| `IFileDialogService` | `PickFilesToOpen()`, `PickSaveFile(suggestedName, initialDirectory)` | Empty list / `null` when cancelled. |
| `IDialogService` | `ConfirmAsync(ConfirmRequest)` → `bool`, `ChooseAsync(ConfirmRequest)` → `DialogChoice` | `ConfirmRequest(Title, Message, PrimaryText, IsDestructive, CancelText, SecondaryText)`. Focus the safe action (Cancel when destructive), Esc cancels. `ChooseAsync` is the three-way Save / Don't save / Cancel. |
| `ISystemThemeProvider` | `IsDarkMode`, `Changed` | `Theming.SystemThemeWatcher` already has these members. |
| `IAutostartService` | `IsEnabled`, `Set(bool)` | HKCU Run key. `Set` may throw; the view model reverts and shows a toast. |
| `IGlobalHotkeyService` | `TryRegister(gesture, out error)`, `Unregister()` | Gestures arrive normalized (`Ctrl+Alt+N`, `Shift+Win+F9`). A failed registration keeps the previous one. |
| `IFontCatalog` | `MonospaceFamilies` | Installed monospace families; "Cascadia Mono" is added if missing. |
| `IShellLauncher` | `OpenUri(Uri)` | Web links and `file:` folders. |

## ShellViewModel (namespace `NimPad.Presentation.Shell`) — the main window's DataContext

Lifecycle:
- `new ShellViewModel(platform, clock?, culture?)`, then `await InitializeAsync(new StartupOptions { DataDirectory, FilesToOpen })`.
  It opens the database (off the UI thread), loads settings, UI state and every node's metadata, restores the session
  (titles at once; only the active tab loads its text), opens the command-line files, shows recovery banners
  (damaged database, recovered unsaved files with **Review**, global shortcut taken), purges expired Trash and runs the
  daily backup in the background. A brand-new profile gets one empty untitled note. It throws only when the database
  cannot be opened (for example another process locks it).
- Window closing: `await ConfirmCloseAsync()` (false = cancel closing; asks about unsaved external files), then
  `await ShutdownAsync()` (flushes autosaves first, with a 5 s bound, then writes recovery buffers, saves session and
  UI state, checkpoints and closes the database; each step runs even when an earlier one failed). Make the editors
  read-only while it runs: bind `TextEditor.IsReadOnly` to `IsShuttingDown` (or hide the window). Text typed after the
  final flush is not saved; edits reported after the database closed are ignored.
- Crash and session-end handlers: `EmergencyFlush(timeout?)` (synchronous, bounded, never throws).
- `OpenFiles(paths)` for single-instance forwarding and files dropped from Windows Explorer.

Children: `Explorer`, `Tabs`, `Editor`, `StatusBar`, `FindReplace`, `Toasts`, `Banners`, `Settings`, `Shortcuts`,
`SettingsStore` (current `AppSettings`, `Changed`).

State: `IsInitialized`, `IsShuttingDown`, `IsEmptyStateVisible` + `EmptyStateTitle` ("No notes open"; actions `NewNoteCommand` and
`ReopenClosedTabCommand`, enabled per `Tabs.CanReopenClosedTab`), `Paths`.

Layout: set `WindowWidth` on resize → `LayoutMode` (Wide ≥ 720, Medium 520–719, Narrow < 520), `IsSidebarOpen`,
`SidebarWidth` (two-way; 200–480, default 288, remembered), `IsSidebarOverlay` (Medium/Narrow; Esc or a click outside
runs `DismissSidebarOverlayCommand`), `ShowAppName`, `IsCharacterCountVisible`, `IsStatusBarCompact` (Narrow: encoding,
mode and zoom go into the "···" menu), `EditorHorizontalPadding` (32/24).

Commands (bind the gestures with InputBindings): `NewNoteCommand` Ctrl+N, `OpenFileCommand` Ctrl+O, `SaveCommand`
Ctrl+S (file: save in place; note: export to a file), `SaveAsCommand` Ctrl+Shift+S, `CloseTabCommand` Ctrl+W,
`ReopenClosedTabCommand` Ctrl+Shift+T, `NextTabCommand` Ctrl+Tab, `PreviousTabCommand` Ctrl+Shift+Tab,
`GoToTabCommand` Ctrl+1…9 (CommandParameter "1"–"9"; 9 = last tab), `MoveTabLeftCommand` / `MoveTabRightCommand`
Ctrl+Shift+Left/Right, `RenameTabCommand` F2 (tab strip focused), `DuplicateNoteCommand` Ctrl+Shift+D,
`ToggleSidebarCommand` Ctrl+B, `FocusSearchCommand`, `ZoomInCommand` Ctrl+Plus, `ZoomOutCommand` Ctrl+Minus,
`ResetZoomCommand` Ctrl+0, `FindCommand` Ctrl+F and `ReplaceCommand` Ctrl+H (CommandParameter = the editor's selected
text), `OpenSettingsCommand` Ctrl+Comma, `ShowShortcutsCommand` Ctrl+Slash, `NextRegionCommand` F6,
`PreviousRegionCommand` Shift+F6, `ImportFileCommand`, `DismissSidebarOverlayCommand`.

Events: `FocusRequested` (`FocusRegion`: TabStrip, Editor, Sidebar = its search field, StatusBar),
`OpenSettingsRequested`, `ShowShortcutsRequested`. Call `ReportFocus(region)` when a region gets keyboard focus.

## Tabs (namespace `NimPad.Presentation.Tabs`)

`TabsViewModel`: `Tabs` (pinned first), `SelectedTab` (two-way), `HasTabs`, `PinnedCount`, `CanReopenClosedTab`,
event `TabClosed` (release the tab's `TextDocument`). Methods: `OpenAsync(nodeId)`, `OpenExternal(path)` (same path =
same tab), `NewUntitledAsync()`, `CloseAsync(tab)`, `CloseOthersAsync(tab)`, `CloseToTheRightAsync(tab)` (both skip
pinned tabs), `ReopenClosedAsync()`, `Pin`/`Unpin`, `Move(tab, index)` (never crosses the pinned boundary),
`NextTab`/`PreviousTab` (wrap), `GoTo(1..9)`, `DuplicateAsync(tab)`, `SaveSessionAsync()`. Commands for the tab
context menu take the tab as CommandParameter (null = selected tab): `CloseTabCommand`, `CloseOtherTabsCommand`,
`CloseTabsToTheRightCommand`, `TogglePinCommand`, `RenameTabCommand`, `DuplicateTabCommand`, `MoveTabLeftCommand`,
`MoveTabRightCommand`, `ImportTabCommand`, `ReopenClosedTabCommand`, `NextTabCommand`, `PreviousTabCommand`,
`GoToTabCommand`. The menu commands report when they cannot act (show those items disabled): `CloseOtherTabsCommand`
(no other unpinned tab), `CloseTabsToTheRightCommand` (no unpinned tab to the right), `RenameTabCommand` and
`DuplicateTabCommand` (files), `ImportTabCommand` (notes); they raise `CanExecuteChanged` when the tabs, pins or
selection change.

Closing a note flushes its autosave first (if that save fails, the tab stays open with an error toast); an untouched,
empty `Untitled N` note is deleted for good (no Trash, no toast) when its tab was opened by New note in this session or
carried over from the last session, never when the user opened it from the sidebar (Core's `DiscardIfPristineAsync`
decides). A file with unsaved changes asks Save / Don't save / Cancel. Other closes push onto the reopen stack and show
"Closed “X”" with Undo (Ctrl+Shift+T); closing several tabs shows "Closed N tabs" whose Undo reopens all of them (no
shortcut shown: Ctrl+Shift+T reopens one). Reopening a tab retires a visible "Closed …" toast. Trashing a note closes
its tab after its last edits are stored (if they cannot be, the tab stays open); restoring it reopens the tab where it
was. Any change of tabs, selection or view state saves the session 500 ms later.

`TabViewModel`:
- Identity: `TabId`, `Kind` (`Managed`/`External`), `IsManaged`, `IsExternal`, `NodeId`, `FilePath`, `Title`,
  `ToolTip` (`My Workspace / Folder / Note`, or the path), `IsPinned`, `IsSelected`, `Format`, `Encoding`,
  `LineEnding`.
- Loading: `LoadState` (`NotLoaded`, `Loading`, `Loaded`, `Failed`), `IsLoading` (true for the whole load: use it
  for logic or a muted title, not for the spinner, which would flash on fast loads), `LoadError`,
  `IsLoadingPlaceholderVisible` (after 300 ms: the editor's skeleton lines), `IsLoadingSpinnerVisible` (after 600 ms:
  bind the tab's spinner to it),
  `IsLoadProgressVisible` + `LoadProgress` + `LoadingText` ("Opening “server.log”") + `LoadingSizeText` ("12.4 MB")
  for files over 5 MB, `CancelLoadCommand` (closes the tab), `RetryLoadCommand`.
- Saving: `SaveState` (`Saved`, `Pending` = accent dot, `Failed`), `ShowsPendingDot`, `IsDirty`, `HasBeenEdited`,
  `LastSavedWriteTimeUtc`.
- Rename: `CanRename`, `IsRenaming`, `RenameText` (two-way), `RenameError` (inline), `BeginRename()`,
  `CommitRenameCommand` (Enter), `CancelRenameCommand` (Esc).
- View state: `CaretOffset`, `SelectionStart`, `SelectionLength`, `VerticalOffset`, `HorizontalOffset`; report them
  with `UpdateViewState(...)`, restore them when the tab's document is shown (at `DispatcherPriority.Loaded`).
- **Document lifecycle (App):** when a tab without a document becomes active (or `LoadState` becomes `Loaded` while it
  has none), `var text = await tab.LoadAsync()` (the same task while a load runs), create the `TextDocument`, wrap it
  in an `IEditorBuffer`, `tab.AttachBuffer(buffer)`, restore the view state. On every `TextDocument.Changed` call
  `tab.OnBufferChanged()`. Handle `ReplaceTextRequested` by replacing the document's text (reload from disk, a note
  kept as a new note). A replacement document for the same tab may be attached again; its versions continue.
  `Buffer` raises `PropertyChanged` when a document is attached (find and replace searches the new text).
- External files: `SaveAsync()`, `SaveAsAsync(path)`, `OnFileChangedOnDisk(lastWriteUtc?)` from the
  `FileSystemWatcher` (ignores NimPad's own save; otherwise a "“X” changed on disk." banner with Reload; dismissing
  keeps the user's text), `OnFileMissingOnDisk()` (moved, renamed or deleted: "“X” was moved or deleted." with
  Save as; the text stays, `IsMissingOnDisk` and `IsDirty` are true until it is saved or the file is back),
  `ReloadFromDiskAsync()`, `ImportAsync()` (copies the file, with unsaved edits, into the workspace and opens the
  note). Unsaved edits go to a recovery buffer 2 s after the last change. The App's `ExternalFileWatcher` (below)
  makes these calls.
- Crash recovery: at startup every recovery buffer is applied to the tab of its file, whether restored, opened from
  the command line or opened later, so the tab starts dirty with the recovered text (and new edits extend it); the
  "NimPad recovered unsaved changes to N files" banner's **Review** opens the files that are not open yet. If the file
  changed on disk after the crash, the tab also shows the "changed on disk" banner.

## Editor and status bar (namespace `NimPad.Presentation.Editor`)

`EditorViewModel`: `ActiveTab`, `Breadcrumbs` (`BreadcrumbViewModel(NodeId, Name, IsLast)`; empty for files),
`FilePathText` (files), `Format`, `FormatDisplayName`, `FontFamily`, `FontSize` (setting × zoom), `WordWrap`,
`ShowLineNumbers`, `TabSize`, `Zoom` (50–400, step 10), `HorizontalPadding`, `ShowEmptyHint` + `EmptyHint`
("Start typing. It saves on its own."; never saved), `ZoomInCommand`, `ZoomOutCommand`, `ResetZoomCommand`,
`NavigateToBreadcrumbCommand` (reveals the node in the explorer), event `RevealRequested`.

`StatusBarViewModel`: `HasActiveTab`, `PositionText` ("Ln 13, Col 31", "Ln 2, Col 24 · 13 selected"),
`CharacterCountText` ("312 characters"), `EncodingText` ("UTF-8", "UTF-8 with BOM", "UTF-16 LE", "Windows-1252"),
`ModeText`, `ZoomText` ("100%"), `SyncState` (`LocalOnly`, `NotSynced`, `SaveFailed`) + `SyncText` + `IsSaveFailed`,
`AvailableModes` (`FormatInfo`), `ChangeModeCommand` (a `DocumentFormat`, `FormatInfo` or wire name; notes store it,
files keep it per tab), `ResetZoomCommand`, `ToggleSidebarCommand`, `RetrySaveCommand`. Report the caret with
`UpdateCaret(line, column, selectionLength, textLength)` (cheap; pass `TextDocument.TextLength`, never the text).

## Find and replace (namespace `NimPad.Presentation.Find`)

`FindReplaceViewModel`: `IsOpen`, `IsReplaceMode`, `Query`, `Replacement`, `MatchCase`, `WholeWord`, `UseRegex`,
`Matches` (`TextMatch(Offset, Length)`, for highlighting), `MatchCount`, `HasMatches` (dim previous/next),
`CurrentIndex`, `CurrentMatch` (match-cur color), `StatusText` ("1 of 2", "No results", "Unmatched “(”"), `HasError`,
`IsSearching`. Commands: `FindNextCommand` (Enter), `FindPreviousCommand` (Shift+Enter), `ReplaceCommand` (Enter in
the replace field), `ReplaceAllCommand` (Ctrl+Enter; replaces every match, also beyond the 100,000 the count shows,
off the UI thread for texts over 1,000,000 characters), `CloseCommand` (Esc), `ToggleMatchCaseCommand` (Alt+C),
`ToggleWholeWordCommand` (Alt+W), `ToggleRegexCommand` (Alt+R). Events: `SelectMatchRequested` (select and scroll),
`ApplyEditsRequested` (apply `TextEdit`s from last to first inside one `BeginUpdate`/`EndUpdate` undo group),
`FocusRequested`, `Closed` (focus back to the editor). Matches are recomputed when the query or an option changes, when
the active tab changes or its document is attached, and 150 ms after the active document changes. `TextSearchEngine`
is the pure engine (Notepad++ whole-word semantics, 1 s regex timeout, `$1` substitutions; in CRLF text `$` matches
before the `\r\n` and `.` matches neither line-break character).

## Explorer (namespace `NimPad.Presentation.Explorer`)

`ExplorerViewModel`: `Rows` (one virtualized ListBox; pick templates by type), `SelectedRow` (two-way),
`SelectedNodeId`, `Mode` (`Tree`, `Search`, `Trash`) + `IsTreeMode`/`IsSearchMode`/`IsTrashMode`, `SearchQuery`
(two-way; 120 ms debounce), `IsSearching`, `SearchSummaryText` ("3 notes match “beta”"), `IsSearchEnabled`,
`SortOrder` + `SortOptions` + `SetSortOrderCommand` (Manual, Last edited, Name A to Z), `EmptyState`
(`ExplorerEmptyState(Kind, Title, Message, ActionText, ActionShortcutText, ActionCommand)`), `TrashCount` (the
sidebar's Trash entry, "Trash 3") and `HasTrashItems`, both current in every mode, `TrashNotice`, `IsLoading`. Events:
`ScrollIntoViewRequested`, `FocusEditorRequested` (second Esc, or a new note at the root; the shell moves focus to the
editor and closes an overlaid sidebar), `RenameStarted` (focus the row's text box), `StateChanged`.

Rows:
- `ExplorerHeaderRowViewModel`: `Section`, `Title` ("PINNED", "RECENT", "MY WORKSPACE"), `IsWorkspaceRoot`. Make
  header containers non-focusable so the keyboard skips them.
- `ExplorerNodeRowViewModel`: `Section`, `Id`, `Kind`, `IsFolder`, `Name`, `Depth`, `IsExpanded`, `HasChildren`,
  `IsPinned`, `ShowsDetails` (Pinned/Recent rows show title, preview and time), `TimestampText` ("2:14 PM", "Sat",
  "Oct 3"), `Preview`, `IsSelected`, `IsRenaming`, `RenameText`, `RenameError`.
- `ExplorerSearchResultRowViewModel`: `Id`, `Kind`, `Title` + `TitleSegments`, `PathText`, `Snippet` +
  `SnippetSegments` (`HighlightSegment(Text, IsMatch)` for match highlighting), `TimestampText`.
- `ExplorerTrashRowViewModel`: `Id`, `Kind`, `Title`, `Preview`, `StatusText` ("Deleted today", "12 days left"),
  `DescendantCount`.

Commands take a row as CommandParameter (null = selected row): `OpenCommand` (Enter: open note / switch to its tab,
toggle folder, restore a Trash item), `NewNoteCommand` / `NewFolderCommand` (parameter: a row or `NodeId`; inside its
folder, inline rename starts), `RenameCommand` (F2), `CommitRenameCommand`, `CancelRenameCommand`, `DeleteCommand`
(Delete: to Trash with an Undo toast; in the Trash: delete forever after a confirmation), `RestoreCommand`,
`DeleteForeverCommand`, `EmptyTrashCommand` (confirms; disabled when empty), `UndoTrashCommand` (bind Ctrl+Z on the
sidebar list: undoes the last Delete while its "Moved “X” to Trash" toast shows; the toast shows the shortcut),
`PinCommand`, `UnpinCommand`, `TogglePinCommand`, `DuplicateCommand`, `CopyLinkCommand` (`nimpad://note/<id>`; a busy
clipboard shows an error toast), `ExportCommand` (disabled for folders and headers),
`ToggleExpandCommand`, `ExpandRowCommand` (Right), `CollapseRowCommand` (Left), `ClearSearchCommand` (Esc),
`ShowTrashCommand`, `ShowTreeCommand`. Also `Reveal(nodeId)` and `SetExpanded(folderId, bool)`.

Drag and drop: `CanDrop(draggedIds, targetRow, DropPosition.Before|Inside|After)` for the adorner and
`DropAsync(...)` to move. Rules: not into itself or a descendant, not inside a note, onto headers only the workspace
header (the root), within a folder only in the Manual order (moving to another folder always works), Pinned rows
reorder among themselves. Files dropped from Windows Explorer go to `ShellViewModel.OpenFiles`.

The rows follow Core's change notifications incrementally (renames, moves, new notes, Trash) without resetting scroll
or selection; a fully expanded tree of 10,000 nodes loads and flattens in about 20–30 ms, and one live change applies in
about 10–25 ms (debug build; the test asserts < 100 ms for the fastest of several runs, in each sort order).

## Feedback (namespace `NimPad.Presentation.Feedback`)

`ToastHostViewModel.Toasts` (at most 3; each disappears after 6 s): `ToastViewModel` — `Message`, `Kind` (`Neutral`,
`Success`, `Error`), `ActionText`, `ActionShortcutText`, `HasAction`, `ActionCommand`, `DismissCommand`, `IsPaused`;
call `Pause()` on pointer enter / keyboard focus and `Resume()` when they leave. Toasts never take focus.

`BannerHostViewModel.Banners`: `BannerViewModel` — `Key`, `Kind` (`Info`, `Warning`, `Error`, as the App's
`BannerKind`), `Message`, `ActionText`, `HasAction`, `ActionCommand`, `CanDismiss`, `DismissCommand`. Banners remove
themselves when their cause clears.

## Settings and shortcuts

`SettingsViewModel` (namespace `NimPad.Presentation.Settings`): `Pages` (Appearance, General — Account and
Synchronization arrive in Phase 2), `SelectedPage`; Appearance: `Theme` + `ThemeOptions` (Light, Dark, System),
`EffectiveIsDark` (drive `ThemeService` from it), `EditorFontFamily` + `FontFamilies`, `FontSize` (8–32) +
`FontSizeText` + `IncreaseFontSizeCommand` / `DecreaseFontSizeCommand`, `WordWrap`, `ShowLineNumbers`, `TabSize` +
`TabSizes`, `PreviewLines`; General: `RestoreTabs`, `StartWithWindows`, `GlobalShortcut` + `GlobalShortcutKeys`
(key caps), `BeginShortcutCaptureCommand` (Change), `CompleteShortcutCapture(gesture)` (the view sends the captured
chord, for example `Ctrl+Alt+K`), `CancelShortcutCaptureCommand`, `IsCapturingShortcut`, `ShortcutError` (inline).
Every change applies live and is stored at once.

`ShortcutsViewModel` (namespace `NimPad.Presentation.Shortcuts`): `Filter`, `Groups` (`ShortcutGroupViewModel(Title,
Shortcuts)` with `ShortcutViewModel(Label, Keys)`), `HasResults`, `EmptyTitle` ("No shortcuts match “zoomm”"),
`EmptyMessage` ("Try “zoom” or a shorter word."), `FooterText`.

## OS integration logic (namespace `NimPad.Presentation.Integration`)

Platform-neutral parts of the App's Windows integration (`NimPad.App/Integration/README.md` has the whole picture):
`AppCommandLine` (`Parse`, `ResolveFiles`, `BuildRestartArguments`) and `WindowsCommandLine` (`Quote`, `Split`);
`SingleInstanceProtocol` + `SingleInstanceMessage` (pipe message format and framing, instance names);
`HotkeyVirtualKeys` (`HotkeyGesture` ↔ `RegisterHotKey` modifiers and virtual keys, `ComposeGesture` for the shortcut
capture); `AutostartCommand` (the Run value); `FileDialogFilters` (the Open dialog's filter string);
`ExternalFileChanges` (what settled file events mean) and `ExternalFileWatcher(shell)` (one debounced
`FileSystemWatcher` per external tab, ignoring NimPad's own saves; create it on the UI thread after initialization).

## Core additions made for the view models

`NodeService.GetNodesAsync(ids)`, `DocumentService.GetPreviewsAsync(workspaceId | ids)`,
`ExternalFileService.ImportTextAsync(fileName, text, …)`, and the reopen stack keeps a tab's `Order`.

## Tests

`apps/windows/tests/NimPad.Presentation.Tests` drives a real `ShellViewModel` over real Core services in a temp data
directory, with a fake clock (`FakeTimeProvider`), a single-threaded test UI thread (`Support/TestUiThread`, which
behaves like WPF's dispatcher) and fake platform services. The clock is advanced from a thread-pool thread, as the
system clock fires timers in the App, and `Support/UiThreadGuard` fails a test when a view model raises a change off the
UI thread. Storage failures are real: `Support/DatabaseFaults` makes Core's writer fail through a trigger in the test
database. Suites: tab lifecycle (pinned boundary, reopen order,
untitled cleanup, Ctrl+9), autosave through `TabViewModel`, external files (dirty state, save, disk changes, recovery),
session round trip, explorer (flattening, expand/collapse, sort, drop rules, live updates, search, Trash, a 10,000-node
performance test), find engine and panel, status bar and editor, toasts and banners, settings, shortcuts and layout.
