# NimPad end-to-end UI tests (Windows)

`NimPad.E2E` drives the real `NimPad.exe` through UI Automation with [FlaUI](https://github.com/FlaUI/FlaUI) 5
(UIA3): real keyboard and mouse input, a fresh temporary profile per launch, and assertions on what the UI exposes.
The tests are written against the UI Automation contract (AutomationIds, Names and ItemStatus values that the app's
views must expose); every id they use is listed in `NimPad.E2E/Infrastructure/AutomationIds.cs`.

They only run on Windows. On Linux and macOS the project still builds (CI checks that), and every test starts with
`Assert.SkipUnless(OperatingSystem.IsWindows(), …)`. The test host itself needs the Windows Desktop runtime, so
`dotnet test` cannot start it elsewhere.

## What is covered

| Test | Scenario (docs/product-spec.md) |
|---|---|
| `LaunchTests.LaunchShowsWindowAndEmptyNote` | First launch on an empty profile: main window "NimPad", one tab "Untitled 1", status bar "Local only", empty editor. |
| `LaunchTests.TypeAutosaveAndRestore` | §14 Scenario 1: type, wait 1.5 s (autosave, no explicit save), close, relaunch: same tab, exact text, same caret (Ln 2, Col 7). |
| `LaunchTests.CrashRecovery` | §14 Scenario 8 locally: type, wait 1.5 s, kill the process, relaunch: the text is recovered. |
| `LaunchTests.KeyboardOnlyFlow` | Ctrl+N new tab, Ctrl+W close, Ctrl+Shift+T reopen with its text, Ctrl+Tab cycles from inside the editor. |
| `ExplorerTests.HierarchyCreateMoveSearch` | §12 Phase 1 / §14 Scenario 3: Ctrl+B, New folder + inline rename "Projects", "NimPad" inside it and note "Architecture" inside that (context menus), type, drag the note from NimPad onto Projects (same id, breadcrumbs follow), search finds it, relaunch: tree, expansion and tab restored. |
| `ExternalFileTests.ExternalFileOpenEditSave` | §3 Local Files: a UTF-16 LE (BOM) CRLF `.txt` passed on the command line opens as a tab titled with the file name, shows "UTF-16 LE" and "Not synced", Ctrl+S saves in place keeping BOM, encoding and CRLF; after a relaunch the text is not in the workspace (search with a positive control note). |
| `SettingsTests.ThemeSwitchAndSettings` | Ctrl+, opens Settings, Light then Dark apply live (MainWindow ItemStatus `theme:light`/`theme:dark`), Esc closes, Dark survives a relaunch. |

The classes are independent, so they can be enabled as the matching parts of the UI land.

### Contract elements each test needs

Every launch needs `MainWindow` (a top-level window of the process) and, to know startup has finished, a
`Tab_<n>` (or `EmptyStateNewNote` when no tab is open). `Close()` invokes `CloseButton` (Alt+F4 if it is missing).

| Test | AutomationIds, Names and ItemStatus values |
|---|---|
| LaunchShowsWindowAndEmptyNote | `MainWindow` Name "NimPad"; `Tab_<n>` Names; `StatusSync` "Local only"; `StatusPosition` "Ln 1, Col 1"; `Editor` (Value pattern) |
| TypeAutosaveAndRestore | `Tab_<n>` Name and ItemStatus `saved`; `Editor` (focus, Value); `StatusPosition` "Ln 2, Col 7"; `CloseButton` |
| CrashRecovery | `Tab_<n>` Name and ItemStatus `saved`; `Editor`; `StatusSync` "Local only" |
| KeyboardOnlyFlow | `Tab_<n>` Names; `Editor` |
| HierarchyCreateMoveSearch | `ExplorerSearch` (Value, focus, Esc); `ExplorerList`; `ExplorerNewFolder` (Invoke); `ExplorerRenameBox` (Value); `ExplorerRow_<nodeId>` (Name, ExpandCollapse on folders, same id after the move and the restart); `ExplorerSearchResult_<nodeId>` (Name, same node id); `Breadcrumbs` (segment Names); `Tab_<n>` Name and ItemStatus `saved`; `Editor`; `CloseButton`. Also the folder context-menu items named "New folder" and "New note" (explorer design, not AutomationIds). |
| ExternalFileOpenEditSave | `Tab_<n>` Name "notes.txt" and ItemStatus `saved`; `StatusEncoding` "UTF-16 LE"; `StatusSync` "Not synced"; `Editor`; `CloseButton`; `ExplorerSearch`; `ExplorerList`; `ExplorerSearchResult_<nodeId>` and `ExplorerRow_<nodeId>` Names |
| ThemeSwitchAndSettings | `MainWindow` ItemStatus `theme:light` / `theme:dark`; `SettingsWindow`; `ThemeSegmented` items named "Light" and "Dark" (SelectionItem); `CloseButton` |

## Run them on Windows

Prerequisites: Windows 10 or 11, the .NET SDK from `global.json`, and an interactive desktop session (not a locked
screen or a service). Use 100 % display scaling if you can, and a screen of at least 1280×800 (the harness sizes the
window to 1280×800 and clamps it to the screen, so smaller screens work too).

```powershell
# From the repository root
dotnet build apps/windows/NimPad.slnx -c Release
dotnet test --project tests/e2e-windows/NimPad.E2E -c Release --no-build

# One class or one test
dotnet test --project tests/e2e-windows/NimPad.E2E -c Release --no-build --filter-class NimPad.E2E.LaunchTests
dotnet test --project tests/e2e-windows/NimPad.E2E -c Release --no-build --filter-method NimPad.E2E.LaunchTests.CrashRecovery
```

Leave the mouse and keyboard alone while the tests run: they type and click on the real desktop, and NimPad must
stay in front. The tests run one at a time (`[assembly: Parallelization(Mode = ParallelMode.None)]`).

### Which NimPad.exe

- `NIMPAD_EXE`, when set, is the full path of the executable under test (CI sets it to the Release build).
- Otherwise the newest of `apps/windows/src/NimPad.App/bin/{Release,Debug}/net10.0-windows/NimPad.exe` is used,
  found from the repository root (the first folder above the test binaries that contains `global.json`).
- A missing executable fails the tests; it never skips them.

Every launch passes `--data-dir <new temp folder> --no-single-instance --no-hotkey`, so the tests never read or
change your real profile, never forward to a NimPad you have open, and never register the global shortcut. The temp
folders (under `%TEMP%\NimPad.E2E`) are deleted after each test.

## When a test fails

The failure is written to `NIMPAD_E2E_ARTIFACTS\<test>-<UTC time>\` (default: `e2e-artifacts` next to the test
binaries):

- `error.txt`: the exception;
- `screen.png`: the whole desktop at the moment of failure;
- `session<N>-uia-tree.txt`: the UI Automation tree of every window of each running NimPad (control type,
  AutomationId, Name, ItemStatus, bounds, focus), launch N of the test;
- `data<N>-logs\`: NimPad's own logs from each profile.

CI uploads that folder as the `e2e-artifacts` artifact when the job fails.

Waits never sleep blindly: they poll with FlaUI's `Retry` (10 s for UI reactions, 60 s for startup, 30 s for exit)
and report the last value seen, e.g. `Timed out … waiting for StatusSync to read "Local only". Last value: "<null>"`.
`<null>` usually means the element with that AutomationId is missing: compare the tree dump with the contract.

## How the harness works

- `Infrastructure/Scenario.cs`: one test run; owns the temp folders and the launched apps, captures artifacts on
  failure, cleans up.
- `Infrastructure/AppSession.cs`: one NimPad process: launch and wait until started, window size and foreground,
  `FindByAutomationId`/`WaitForAutomationId`, keyboard (`TypeText`, `PressShortcut`, `PressKeys`), editor text
  through the Value pattern of AvalonEdit's `TextEditorAutomationPeer`, tabs, breadcrumbs, status bar, `Close()`
  (CloseButton, else Alt+F4, then waits for exit) and `Kill()`.
- `Infrastructure/ExplorerPane.cs`, `SettingsWindowPage.cs`: the sidebar and the settings window.
- `Infrastructure/Ui.cs`: timeouts and polling.

Notes on FlaUI behavior the harness relies on:

- `Keyboard.Type(string)` maps characters through `VkKeyScan`, which turns `\n` into Ctrl+Enter, so `TypeText` sends
  line breaks as the Enter key.
- `Mouse.Drag` jumps straight from start to end; the drag in `ExplorerPane.DragOnto` uses `Mouse.Down`, several
  `Mouse.MoveTo` steps (which interpolate) and a pause over the target before `Mouse.Up`, so WPF's drag threshold and
  DragOver run as they do for a person.
- UI Automation `SetFocus` on AvalonEdit's TextEditor moves focus to its TextArea, but WPF can report that as a
  failure (`UIElement.Focus()` returns false when focus is handed on); the harness checks where focus landed instead
  of trusting the call, and clicks into the editor only if focus did not arrive.
- The test executable is per-monitor DPI aware (`app.manifest`), so UI Automation bounds and mouse coordinates agree
  on scaled displays.
