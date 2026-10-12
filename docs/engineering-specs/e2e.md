YOUR AREA: tests/e2e-windows/NimPad.E2E (you own it). These tests run ONLY on the GitHub Actions windows-latest
runner (see .github/workflows/ci.yml: env NIMPAD_EXE points at the built NimPad.exe, NIMPAD_E2E_ARTIFACTS at a
folder for screenshots). Locally (Linux) they cannot run — every test must call
Assert.SkipUnless(OperatingSystem.IsWindows(), ...) first, and the project must build on Linux
(`dotnet build apps/windows/NimPad.slnx`). Because you cannot run them, be extremely careful. The App UI is being built IN PARALLEL by other
engineers, so it may not exist yet in your tree: write the tests against the UI automation contract in
docs/engineering-specs/automation-ids.md (AutomationIds,
Names, ItemStatus values, command-line flags) — never invent ids outside it; use FlaUI 5 (FlaUI.Core + FlaUI.UIA3) APIs correctly (check the package's
public API in ~/.nuget/packages/flaui.core/5.0.0 via reflection-free reading of the XML docs if present), and use
Retry.WhileNull / Retry.WhileFalse with generous timeouts (10 s) instead of sleeps.

Harness (Infrastructure/): AppSession launches NIMPAD_EXE (fallback: locate apps/windows/src/NimPad.App/bin/
{Release,Debug}/net10.0-windows/NimPad.exe relative to the repo root found by walking up to global.json) with
`--data-dir <unique temp dir> --no-single-instance --no-hotkey`, waits for the main window (AutomationId
MainWindow), sets the window to 1280x800, exposes helpers (FindByAutomationId, Type into the editor via
Keyboard.Type, press shortcuts via Keyboard.TypeSimultaneously, read the editor text via the TextPattern or
ValuePattern of AvalonEdit's TextArea — check which pattern AvalonEdit's TextAreaAutomationPeer supports; it
implements IValueProvider in recent versions), Close() gracefully (Alt+F4 / CloseButton, then wait for exit),
Kill() for crash simulation, captures a screenshot + UIA tree dump into NIMPAD_E2E_ARTIFACTS on failure (wrap each
test body). Tests run serially (xunit.v3: [assembly: CollectionBehavior(DisableTestParallelization = true)]).

Tests (map each to the spec's acceptance scenarios in docs/product-spec.md §12 Phase 1 and §14 Scenario 1/8):
Organize the tests in separate classes/files so they can be merged in stages as the UI lands:
LaunchTests.cs (tests 1, 2, 4, 7 — need only the shell + editor), ExplorerTests.cs (test 3), ExternalFileTests.cs
(test 5), SettingsTests.cs (test 6). Replace the existing placeholder LaunchTests.Placeholder test entirely.
1) LaunchShowsWindowAndEmptyNote: first launch on an empty data dir shows the main window, one untitled tab, the
   status bar "Local only".
2) TypeAutosaveAndRestore: type text into the note, wait > 1.5 s, close gracefully, relaunch same data dir: the
   tab is restored with the exact text and the caret position.
3) HierarchyCreateMoveSearch: via the explorer (Ctrl+B, New folder button + inline rename "Projects", nested
   folder "NimPad" inside it, new note "Architecture" inside, type text), drag the note from NimPad to Projects
   (FlaUI Mouse.Drag between row elements by AutomationId ExplorerRow_<id> — get ids from AutomationId patterns,
   or select + use a keyboard/context-menu alternative if drag is unreliable; prefer real drag first and fall back
   with a clear note), search "Architecture" in the explorer search box and assert the result row appears; close
   and relaunch: tree and expansion restored.
4) CrashRecovery (Scenario 8 locally): type text, wait 1.5 s (autosave), Kill() the process, relaunch: text
   recovered.
5) ExternalFileOpenEditSave: create a temp .txt file with CRLF content in UTF-16 LE BOM, launch NimPad with the
   path as an argument, assert the tab title is the file name and the status bar shows the encoding ("UTF-16 LE")
   and "Not synced"; type, Ctrl+S, close; assert the file on disk kept UTF-16 LE BOM + CRLF line endings and
   contains the new text; assert the file was NOT imported into the workspace (relaunch: no managed note with that
   content appears in explorer search).
6) ThemeSwitchAndSettings: Ctrl+, opens the settings window, switch Theme to Dark via ThemeSegmented, close with
   Esc; relaunch: still dark (assert via a theme marker — e.g. the MainWindow exposes AutomationProperties.HelpText
   — the contract defines MainWindow ItemStatus "theme:light"/"theme:dark"; do NOT edit the App).
7) KeyboardOnlyFlow: Ctrl+N creates a tab, Ctrl+W closes, Ctrl+Shift+T reopens with its text, Ctrl+Tab cycles.
Write tests/e2e-windows/README.md explaining how to run them on Windows locally.
