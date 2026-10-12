YOUR AREA: apps/windows/src/NimPad.Presentation and apps/windows/tests/NimPad.Presentation.Tests (you own these).
NimPad.Core is finished (read apps/windows/src/NimPad.Core/README.md and the Core code first; reuse its services:
WorkspaceService, NodeService, DocumentService, AutosaveScheduler, SearchService, SessionService (tabs, recently
closed, UiState, AppSettings), ExternalFileService, LinkParser, BackupService, change notifications). If Core
needs a small addition to support the view models, you may add it (with tests) in apps/windows/src/NimPad.Core +
NimPad.Core.Tests, but keep Core's existing behavior intact.

Build the platform-neutral view-model layer (CommunityToolkit.Mvvm 8.4 source generators: [ObservableProperty],
[RelayCommand]; NO WPF references — this project must stay net10.0 and unit-testable on Linux). The WPF App (built
next by other engineers) binds to these view models, so give every public member clear XML docs and design the
API for binding (ObservableCollection, INotifyPropertyChanged, ICommand, simple CLR types). All time-based
behavior uses TimeProvider (tests use FakeTimeProvider); all UI-thread marshaling goes through IUiDispatcher.

PLATFORM ABSTRACTIONS (namespace NimPad.Presentation.Platform; the App implements them):
- IUiDispatcher { bool CheckAccess(); void Post(Action); Task InvokeAsync(Func<Task>) } (+ an immediate
  implementation for tests in the test project).
- IEditorBuffer: the live text of one open tab (the App wraps AvalonEdit's TextDocument): long Version;
  int TextLength; string GetSnapshotText() (thread-safe: the App snapshots on the UI thread); events optional.
  Make it compatible with Core's AutosaveScheduler source contract (adapt if needed).
- IClipboardService { void SetText(string) }.
- IFileDialogService { IReadOnlyList<string> PickFilesToOpen(); string? PickSaveFile(string suggestedName, string? initialDir) }.
- IDialogService { Task<bool> ConfirmAsync(ConfirmRequest) } (title, message, primary text, isDestructive,
  cancel text) — the design's dialogs focus the safe action and Esc cancels.
- ISystemThemeProvider { bool IsDarkMode; event Changed }, IAutostartService { bool IsEnabled; void Set(bool) },
  IGlobalHotkeyService { bool TryRegister(string gesture, out string? error); void Unregister() },
  IFontCatalog { IReadOnlyList<string> MonospaceFamilies }, IShellLauncher { void OpenUri(Uri) }.

VIEW MODELS (namespace NimPad.Presentation.*):
1) ShellViewModel (the root DataContext of the main window):
   - Children: Explorer, Tabs, Editor (the active tab's editor state), StatusBar, FindReplace, Toasts, Banners.
   - InitializeAsync(StartupOptions{ dataDir, filesToOpen[] }): ensure default workspace, load settings, load all
     node metadata, restore the session (tabs appear first in order with titles known; only the ACTIVE tab loads
     its text now; others load on first activation), open command-line files as external tabs, surface Core's
     StartupRecovery result and any external-file recovery buffers as banners ("NimPad recovered unsaved changes
     to N files" with a Review action that reopens them), purge expired trash, run the daily backup in the
     background. If there is no session or every tab was closed, show the empty state (no tabs) — but on a
     brand-new profile open one new untitled note (design: "Brand-new account: an empty note, no tour").
   - ShutdownAsync(): flush all autosaves (bounded wait), save session + UI state, checkpoint DB. Also a
     synchronous EmergencyFlush() for crash/session-end handlers (best effort, bounded time).
   - Layout: WindowWidth input -> LayoutMode Wide (>=720) / Medium (520-719) / Narrow (<520); IsSidebarOpen,
     SidebarWidth (default 288, min 200, max 480, persisted), IsSidebarOverlay (Medium/Narrow), ShowAppName (Wide),
     IsCharacterCountVisible (Wide), editor horizontal padding 32 (Wide) / 24 (Medium/Narrow).
   - Commands (gesture in comments; the App binds InputBindings): NewNote Ctrl+N, OpenFile Ctrl+O, Save Ctrl+S
     (external file: save in place; managed note: export to a file via IFileDialogService), SaveAs Ctrl+Shift+S,
     CloseTab Ctrl+W, ReopenClosedTab Ctrl+Shift+T, NextTab Ctrl+Tab, PreviousTab Ctrl+Shift+Tab, GoToTab(1..9)
     Ctrl+1..9 (9 = last tab), MoveTabLeft/Right Ctrl+Shift+Left/Right, RenameTab F2 (when tab strip focused),
     DuplicateNote Ctrl+Shift+D, ToggleSidebar Ctrl+B (opens with focus request to search), FocusSearch,
     ZoomIn Ctrl+Plus, ZoomOut Ctrl+Minus, ResetZoom Ctrl+0, Find Ctrl+F, Replace Ctrl+H, OpenSettings Ctrl+Comma,
     ShowShortcuts Ctrl+Slash, CycleRegion F6 / Shift+F6 (raises a FocusRequested event with the target region:
     TabStrip, Editor, Sidebar, StatusBar).
   - Events for the view: FocusRequested(region), OpenSettingsRequested, ShowShortcutsRequested,
     ScrollToRequested, etc. Keep these as plain .NET events or small request objects.
2) TabsViewModel + TabViewModel:
   - ObservableCollection<TabViewModel> Tabs, SelectedTab. Pinned tabs always sit at the left edge in pinned order.
   - Open(nodeId) activates an existing tab or opens a new one; OpenExternal(path) likewise (same path => same tab);
     NewUntitled() creates "Untitled N" (Core helper) at the workspace root and opens it at the end.
   - Close(tab): flush its autosave first; if it is a managed note that was created in this session, never edited
     and is still empty -> delete it permanently (it must not litter Trash). External tab with unsaved changes ->
     ConfirmAsync ("Save changes to X?" Save / Don't save / Cancel semantic — model it as a 3-way result). Closing
     pushes onto the recently-closed stack and raises a toast "Closed “X”" with Undo (Ctrl+Shift+T).
     CloseOthers, CloseToTheRight (skip pinned), Pin/Unpin, Move(tab, newIndex) (cannot cross the pinned
     boundary), Rename (managed: renames the node; validation errors surface inline), Duplicate (managed only).
     ReopenClosed restores the tab at its old index with its caret/scroll.
   - Next/Previous wrap around; GoTo(n).
   - TabViewModel: TabId, Kind Managed|External, NodeId?, FilePath?, Title (managed: node name, kept in sync with
     renames from the explorer), ToolTip (path/breadcrumb), IsPinned, IsSelected, LoadState NotLoaded|Loading|
     Loaded|Failed, SaveState Saved|Pending (accent dot)|Failed, IsRenaming/RenameText/CommitRename/CancelRename,
     Format, plus editor view state persisted per tab: CaretOffset, SelectionStart, SelectionLength,
     VerticalOffset, HorizontalOffset. LoadAsync() returns the initial text (managed: DocumentService; external:
     ExternalFileService with progress for >5 MB + cancellation). AttachBuffer(IEditorBuffer) + OnBufferChanged()
     wire the live document to the AutosaveScheduler (managed) or to dirty tracking + a debounced (2 s) recovery
     buffer write (external). External tabs: IsDirty compares buffer version to the last saved version; Save()
     writes via ExternalFileService and records the returned timestamp so the App can ignore its own
     file-watcher echo; OnFileChangedOnDisk() raises a banner "“X” changed on disk" with Reload (and keeps the
     user's text if they choose to keep it); Import() copies the file into the workspace and opens the managed
     note.
   - Session persistence: any change to tabs/selection/view state schedules a debounced (500 ms) SessionService
     save; the explicit save at shutdown is synchronous-safe.
3) EditorViewModel (state of the active tab's editor): ActiveTab, Breadcrumbs (ObservableCollection of
   {NodeId, Name}; empty for external files, which show the file path instead), Format + FormatDisplayName,
   settings-driven properties (FontFamily, FontSize * Zoom, WordWrap, ShowLineNumbers, TabSize, Zoom percent
   50-400 step 10), ShowEmptyHint (active managed note is empty and was never typed in), commands to
   navigate to a breadcrumb (reveals it in the explorer).
4) FindReplaceViewModel + TextSearchEngine (pure, in Presentation): IsOpen, IsReplaceMode, Query,
   Replacement, MatchCase, WholeWord, UseRegex, Matches (offset/length list), CurrentIndex, StatusText ("1 of 2",
   "No results", or the regex error, e.g. "Unmatched “(”"), HasError. FindNext/FindPrevious (wrap), Replace
   (current match), ReplaceAll (returns the list of edits so the view can apply them as ONE undo step), Close.
   The engine runs over a text snapshot from IEditorBuffer, recomputes on query/options change and (debounced
   150 ms) on document change, supports regex with timeouts (Regex match timeout 1 s) and whole-word semantics
   like Notepad++. Open() pre-fills the query from the current selection when it is a single line.
5) StatusBarViewModel: PositionText ("Ln 13, Col 31" or "Ln 2, Col 24 · 13 selected"), CharacterCountText
   ("312 characters" — use TextLength, never the full text), EncodingText ("UTF-8"; external files show the
   detected encoding, e.g. "UTF-8 with BOM", "UTF-16 LE", "Windows-1252"), ModeText ("Plain Text", "Markdown",
   "JSON", ...), ZoomText ("100%"), SyncText + SyncState: LocalOnly ("Local only") for managed notes,
   NotSynced ("Not synced") for external files, SaveFailed ("Not saved · Retry") when the autosave failed;
   commands: ChangeMode(format) (managed notes persist via DocumentService.SetFormatAsync; external files keep it
   per tab), ResetZoom, ToggleSidebar, RetrySave. Caret updates arrive from the view via UpdateCaret(line, col,
   selectionLength, textLength) and must be cheap.
6) ExplorerViewModel: ONE flattened row list (ObservableCollection<ExplorerRowViewModel>) for a single
   virtualized ListBox: section headers ("PINNED", "RECENT", "MY WORKSPACE") + rows. Modes: Tree (default),
   Search (when SearchQuery is non-empty; debounced 120 ms; results show title, path, snippet with highlight
   ranges, time; "N notes match “q”" header; empty => "No notes match “q”" + "Check the spelling or try fewer
   words." + Clear search), Trash (header "Trash" + "Empty trash" action, "Notes are deleted forever after 30
   days.", rows with title, preview, "Deleted today"/"12 days left", Restore / Delete forever; empty => "Trash is
   empty" / "Deleted notes wait here for 30 days, so nothing is lost by accident.").
   - Tree rows: NodeId, Kind, Name, Depth, IsExpanded, HasChildren, IsPinned, IsSelected, IsRenaming,
     RenameText, TimestampText (relative: "2:14 PM" today, "Sat" this week, "Oct 3" older — use the user's local
     time via TimeProvider.LocalTimeZone), Preview (documents). Pinned/Recent rows (Recent = 8 most recently
     content-edited documents) show title + preview + time like the design. Expand/collapse inserts/removes rows
     one at a time (WPF CollectionView rejects range changes). Expansion state + selected node + sort order persist
     via UiState.
   - Sort menu (design: three orders): Manual (default; drag to reorder), Last edited, Name A to Z — applies to
     siblings in the tree; Manual is the only order where drag-reordering within a folder is allowed (moving
     into another folder is always allowed). Remember the choice in UiState.
   - Commands: NewNote(parent?), NewFolder(parent?) (inline rename starts immediately), Rename (F2),
     Delete -> Trash with toast "Moved “X” to Trash" + Undo, Restore, DeleteForever (confirm), EmptyTrash
     (confirm; disabled when empty), Pin/Unpin, Duplicate, CopyLink (clipboard: nimpad://note/<id>), Open (Enter;
     opens or switches to its tab), Reveal(nodeId) (expand ancestors, select, scroll request), ToggleExpand,
     ExpandRow/CollapseRow (Right/Left keys), ClearSearch (Esc; second Esc returns focus to the editor via an
     event), ShowTrash/ShowTree.
   - Drag & drop API for the view: CanDrop(IReadOnlyList<NodeId> dragged, ExplorerRowViewModel target,
     DropPosition Before|Inside|After) — false for dropping into itself/descendants, into documents with
     Inside, onto header rows except the workspace header (= root), reordering when the sort isn't Manual;
     DropAsync(...) performs the move through NodeService. Files dropped from Windows Explorer are handled by
     the shell (open as external tabs), not here.
   - Live updates: subscribe to Core change notifications, marshal through IUiDispatcher, and update rows
     incrementally (renames, moves, new nodes, trash) without resetting scroll/selection. Must handle 10,000
     nodes: building the flattened list for a fully expanded 10k tree must take < 100 ms (test it).
   - Empty workspace: "No notes yet" / "Start typing in any tab. It is saved and synced automatically." with
     "New note Ctrl+N" — EXCEPT do not claim syncing in Phase 1: use "Start typing in any tab. It is saved
     automatically on this computer."
7) Toasts: ToastViewModel {Message, ActionText, ActionShortcutText, ActionCommand, Kind}; ToastService/host
   collection: at most 3 visible, auto-dismiss after 6 s (paused while hovered/focused: Pause()/Resume()),
   Dismiss. Banners: BannerViewModel {Kind Info|Warning|Error, Message, ActionText, ActionCommand, CanDismiss};
   banners dismiss themselves when their cause clears (expose Remove by key).
8) SettingsViewModel: Pages Appearance and General (Account and Synchronization arrive in Phase 2 — do not
   show them). Appearance: Theme (System|Light|Dark), EditorFontFamily (from IFontCatalog, default
   "Cascadia Mono"), FontSize stepper (8-32), WordWrap, ShowLineNumbers, TabSize (2|4|8), preview lines
   "The quick brown fox jumps over the lazy dog" / "0123456789 {}[]() => != ===". General: RestoreTabs,
   StartWithWindows (IAutostartService), GlobalShortcut (display "Ctrl Alt N" key caps + Change: capture a new
   gesture via the view; validate; register via IGlobalHotkeyService; report conflicts inline). Every change
   applies live (ShellViewModel/EditorViewModel react) and persists via SessionService.AppSettings immediately.
   Theme exposes EffectiveIsDark (System follows ISystemThemeProvider) for the App's ThemeService.
9) ShortcutsViewModel: the full list from the design (groups NOTES & TABS, EDITING, FIND, APP with exact labels
   and key caps from docs/design page 12 + 27), Filter text, filtered groups, empty state
   "No shortcuts match “zoomm”" / "Try “zoom” or a shorter word.".
TESTS (xunit.v3, FakeTimeProvider, real Core services on a temp data dir — no mocks of Core unless unavoidable):
tab lifecycle incl. pinned boundary, reopen-closed order, empty-untitled cleanup, Ctrl+9 semantics; session
round-trip (close shell, create a new one on the same data dir, tabs/selection/caret/scroll restored); explorer
flattening/expand/collapse/move/drop validation/live updates/search mode/trash mode/10k performance; find engine
(case, whole word, regex, invalid regex message, replace-all edit list, wrap); status texts; toasts timing and
pause; settings persistence; layout breakpoints; autosave integration through TabViewModel with a fake buffer.
Build the whole solution; `dotnet test --project apps/windows/tests/NimPad.Presentation.Tests` and the Core
tests must pass. Write apps/windows/src/NimPad.Presentation/README.md summarizing the view-model API for the
App engineers (bindable members and the platform interfaces they must implement).
