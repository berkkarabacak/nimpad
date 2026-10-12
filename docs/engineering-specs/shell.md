YOUR AREA: the NimPad WPF application shell (apps/windows/src/NimPad.App). You own: App.xaml(.cs) composition
root, MainWindow.xaml(.cs), Shell/** (title bar, tab strip, status bar, toast host, banner host, layout),
Platform/** (WPF implementations of NimPad.Presentation.Platform interfaces that are not owned elsewhere:
UiDispatcher, ClipboardService, FileDialogService, DialogService + the confirm dialog window, FontCatalog,
ShellLauncher), Services/StartupArgs.cs, and apps/windows/tests/NimPad.App.Tests/Shell/**.
You also create STUB views with these exact names, which other engineers will replace in parallel afterwards:
Views/Explorer/ExplorerView.xaml(.cs) (UserControl, DataContext = ExplorerViewModel) and
Views/Editor/EditorView.xaml(.cs) (UserControl hosting the editor; DataContext = ShellViewModel; for now a plain
AvalonEdit TextEditor bound to the active tab via a minimal EditorHost that creates one TextDocument per tab,
swaps editor.Document on tab change, wraps it as IEditorBuffer and calls TabViewModel.AttachBuffer/
OnBufferChanged, and restores caret/scroll). Keep the stubs functional but minimal; put real effort into the
shell.

Read first: apps/windows/src/NimPad.Presentation/README.md and the view models (ShellViewModel, TabsViewModel,
StatusBarViewModel, Toasts, Banners, Settings, Shortcuts), apps/windows/src/NimPad.App/Themes/README.md (the
design system: tokens, text styles, icons, control styles — USE IT; never hard-code colors/sizes), docs/design/
README.md and the deck page renders at docs/design/renders/p-NN.png
(p-04/p-05 main window, p-06 many tabs, p-10 tab menu + toast, p-13 status/banners (Phase 1 shows "Local only"),
p-24 window chrome + tab states + status bar anatomy, p-25 controls, p-26 states, p-27 keyboard, p-28 responsive,
p-29 empty/loading states).

BUILD:
1) Composition (App.xaml.cs): parse args (--data-dir <path>, --no-single-instance, --no-hotkey, file paths) into
   StartupArgs; resolve AppPaths; open LocalDatabase + Core services; build the ShellViewModel with WPF platform
   implementations; apply the theme (ThemeService) BEFORE the window shows; show the MainWindow quickly (tabs and
   chrome first; initialize data asynchronously without blocking the UI thread; never a blocking splash); hook
   Application.DispatcherUnhandledException, AppDomain.UnhandledException and
   TaskScheduler.UnobservedTaskException -> log + ShellViewModel.EmergencyFlush() + an error banner where the app
   can continue; SessionEnding / WM_QUERYENDSESSION -> flush; Exit -> ShutdownAsync with a bounded wait. A
   simple rolling file logger (Services/FileLog.cs: one file per day under AppPaths.LogsDir, keep 14 days, never
   log note contents) is yours too. (Single-instance, global hotkey, autostart and the settings window belong to
   another engineer — leave clean seams: e.g. an IAppActivation hook in App.xaml.cs they can implement.)
2) MainWindow: WindowChrome custom title bar (CaptionHeight 40, ResizeBorderThickness from
   SystemParameters.WindowResizeBorderThickness, GlassFrameThickness 0,0,0,1 so Windows 11 keeps the shadow and
   rounded corners, UseAeroCaptionButtons=false), default 1120x720, min 400x300, restores its last bounds
   (persist in UiState; clamp to a visible monitor). Layout rows: title bar 40 (Chrome background) / banner host
   / content (sidebar column + splitter + editor) / status bar 24.
   - Title bar: AppMark 18 px + "NimPad" (12/600, hidden in Medium/Narrow), tab strip, new-tab button (30x30,
     Ctrl+N), drag area (everything that is not a control drags; double-click maximizes; right-click shows the
     system menu — WindowChrome provides this when hit-test visible areas are correct), account avatar slot
     (HIDDEN in Phase 1), window controls (46x40; close turns red). All interactive elements set
     WindowChrome.IsHitTestVisibleInChrome="True".
   - Snap Layouts: handle WM_NCHITTEST in a WndProc hook and return HTMAXBUTTON when the cursor (physical pixels
     -> convert with PresentationSource.CompositionTarget.TransformFromDevice) is over the maximize button; since
     the button then gets no WPF mouse events, drive its hover/pressed visuals from WM_NCMOUSEMOVE/
     WM_NCMOUSELEAVE/WM_NCLBUTTONDOWN/WM_NCLBUTTONUP and perform maximize/restore on button-up. Handle
     WM_GETMINMAXINFO so a maximized window fits the monitor work area (no 8 px overhang; leave 1 px for an
     auto-hide taskbar). Toggle Maximize/Restore glyph on state change. Inactive window: dim title text/glyphs.
   - Tab strip (Shell/TabStrip): an ItemsControl/ListBox of TabViewModel with the exact p-24 states: default
     (Text2), hover wash + close button, selected (Surface background shared with the editor, top corners 8,
     500 weight, close always visible), keyboard focus (inset ring; ONE tab stop, Left/Right move + select,
     Home/End, Enter sends focus to the editor, F2 rename, Delete/Ctrl+W close, Ctrl+Shift+Left/Right move,
     Menu/Shift+F10 opens the tab menu), pinned (104 px, pin icon, no close, left edge), waiting-to-sync/unsaved
     (accent dot replaces the close icon until saved; close shows on hover), save failed (Err dot), loading
     (muted title + spinner), renaming (inline TextBox, text preselected, Enter saves, Esc cancels), dragging
     (lifted with flyout shadow; an accent insertion marker; reorder via TabsViewModel.Move; respect the pinned
     boundary). Widths shrink from 172 toward 96, then the strip scrolls horizontally (mouse wheel scrolls;
     selected tab is brought into view). Separators (Sep) between unselected tabs. Middle-click closes.
     Tab context menu exactly as p-10: Rename F2, Duplicate Ctrl+Shift+D, Pin tab/Unpin tab, Close Ctrl+W, Close
     others, Close tabs to the right; empty-strip context menu: New note Ctrl+N, Reopen closed tab Ctrl+Shift+T.
     Tooltips show the full title/path.
   - Status bar (Shell/StatusBar, 24 px, Caption style): sidebar toggle (highlighted while open), position text,
     character count (hidden in Medium/Narrow), then right-aligned: encoding (menu for external files: UTF-8,
     UTF-8 with BOM, UTF-16 LE, Windows-1252 — changes the save encoding), mode (menu listing every format),
     zoom (click resets to 100%; Ctrl+wheel over the editor zooms — coordinate with EditorView via the
     view model), sync slot with icon + word ("Local only" with the Computer icon; "Not synced" for external
     files; "Not saved · Retry" in Err for save failures — status is never color alone). In Narrow layout the
     encoding/mode/zoom collapse into a ⋯ menu.
   - Sidebar host: ExplorerView in a column 288 px wide (resizable with a GridSplitter, min 200 / max 480,
     persisted), fully collapsible (Ctrl+B); in Medium/Narrow it overlays the editor (Popup-free: an overlay panel
     in the content grid with a Flyout shadow), Esc or a click outside closes it. The editor must remain fully
     usable with the sidebar collapsed.
   - Toast host: bottom-left of the editor, Raised card with flyout shadow, message + optional action button
     ("Undo" + KeyCap-styled shortcut text "Ctrl+Shift+T"), close X, auto-dismiss handled by the view model;
     pause on hover/keyboard focus; toasts never take focus; announce via an AutomationProperties.LiveSetting=
     Polite text.
   - Banner host: under the tab strip, 36 px min, icon + message + one action (Info/Warning/Error variants),
     dismiss X when allowed.
   - Empty state when no tabs are open (p-29 "All tabs closed"): centered "No notes open" + buttons "New note" and
     "Reopen closed tab" — never blocks the window.
   - Responsive: bind ShellViewModel.WindowWidth from ActualWidth (SizeChanged); apply Wide/Medium/Narrow
     visual changes exactly as tokens.md "Responsive window layouts".
   - Input: KeyBindings for every ShellViewModel command gesture (Window.InputBindings); F6/Shift+F6 region
     cycling via FocusRequested (TabStrip, Editor, Sidebar, StatusBar); Ctrl+Tab must work even when the editor has
     focus (AvalonEdit consumes Tab — handle in PreviewKeyDown). Drag & drop of files from Windows Explorer onto
     the window opens them as external tabs (ShellViewModel API).
   - Keyboard shortcuts dialog (Ctrl+/, p-12): a modal Raised dialog over a scrim with a filter field and the
     ShortcutsViewModel groups rendered with KeyCaps; Esc closes. (Settings window itself belongs to another
     engineer; just raise/handle OpenSettingsRequested through an ISettingsWindowLauncher seam they implement.)
   - Confirm dialog (DialogService): p-25 dialog: title, message, Cancel (focused by default — the safe action)
     + primary (Destructive style when destructive); Esc cancels, Enter confirms the primary.
3) Accessibility: AutomationProperties.Name + AutomationId on every control (AutomationIds are used by the UI
   tests — use stable ids: TitleBar, TabStrip, Tab_<index>, NewTabButton, MinimizeButton, MaximizeButton,
   CloseButton, StatusBar, StatusPosition, StatusCharacters, StatusEncoding, StatusMode, StatusZoom, StatusSync,
   SidebarToggle, ToastHost, BannerHost, EmptyStateNewNote, ShortcutsDialog), tab order per the keyboard map.
4) Tests (App.Tests/Shell, STA, Windows-only via Assert.SkipUnless(OperatingSystem.IsWindows())): construct
   MainWindow against a ShellViewModel on a temp data dir, Measure/Arrange at Wide/Medium/Narrow widths in both
   themes, assert key elements exist by AutomationId and layout rules (app name hidden in Medium, char count
   hidden in Medium). Also keep `dotnet build apps/windows/NimPad.slnx` green on Linux (XAML compiles there) and
   `dotnet test` for Core + Presentation green.
Be meticulous with XAML resource keys (runtime-only failures on Windows): only use keys documented in
Themes/README.md, and DynamicResource for anything theme-dependent.

UI AUTOMATION CONTRACT (mandatory): expose exactly the AutomationIds, Names and ItemStatus values listed for your
area in docs/engineering-specs/automation-ids.md — the
FlaUI end-to-end tests are written against it. Where this spec names a different id, the contract wins.

END-TO-END EXPECTATIONS (the FlaUI tests on Windows CI check these; see tests/e2e-windows/README.md once merged):
- First launch on an empty data dir: exactly one tab named "Untitled 1", StatusPosition "Ln 1, Col 1", StatusSync
  "Local only", the editor empty and focused.
- The session (tabs, order, active tab, caret/selection/scroll per tab) is persisted within ~1 s of any change
  (debounced), so a process KILL ~1.5 s after the last change still restores the same tabs and caret on relaunch.
- Tab titles follow renames made in the explorer, live.
- Each tab exposes ItemStatus "unsaved" / "saved" / "error" per the contract.
- Ctrl+B toggles the sidebar (open: focus goes to the explorer search box; pressed again: closes and focus returns
  to the editor).
- The main window's ItemStatus is "theme:light"/"theme:dark" (effective theme), updated live.
