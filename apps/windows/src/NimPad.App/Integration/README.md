# OS integration and the Settings window

How NimPad plugs into Windows: single instance, the global shortcut, start with Windows, external-file watching and
restart after a crash, plus the Settings window (`Views/Settings`). The App's composition root (`App.xaml.cs`) wires
it with the lines in [Wiring](#wiring); nothing else in the App needs to know the details.

## Where things are

| Part | Type | Notes |
|---|---|---|
| Composition | `Integration/IntegrationBootstrapper` | The one object `App.xaml.cs` talks to. |
| Options | `Integration/IntegrationOptions` | `FromCommandLine(AppCommandLine)` gives the App's defaults. |
| Single instance | `Integration/SingleInstance` | Per-user mutex + named pipe with a current-user-only ACL. |
| Global shortcut | `Integration/GlobalHotkeyService` | `IGlobalHotkeyService`; `RegisterHotKey` on its own message-only window. |
| Start with Windows | `Integration/AutostartService` | `IAutostartService`; `HKCU\…\Run` value `NimPad`. |
| Restart after a crash | `Integration/RestartRegistration` | `RegisterApplicationRestart("--restarted …")`. |
| Bring to front | `Integration/WindowActivator` | Restore, activate, `AttachThreadInput` fallback, focus the `Editor`. |
| Settings window | `Views/Settings/SettingsWindow`, `SettingsWindowLauncher` | One modeless window owned by the main window. |
| Pure logic (any OS) | `NimPad.Presentation/Integration/*` | Command line, pipe message format and names, hotkey ↔ virtual keys, Run value rules, file-change decisions, the file watcher, Open dialog filters. Tested in `NimPad.Presentation.Tests/Integration`. |

## Wiring

`App.xaml.cs` (the shell engineer's `PlatformServices` members are elided as `…`):

```csharp
using NimPad.App.Integration;
using NimPad.App.Views.Settings;
using NimPad.Presentation.Integration;

private IntegrationBootstrapper? _integration;

protected override async void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    var commandLine = AppCommandLine.Parse(e.Args);

    // 1. Before any window: a second launch forwards its files to the running NimPad and exits.
    _integration = new IntegrationBootstrapper(commandLine);
    if (!_integration.TryBecomePrimaryInstance())
    {
        _integration.Dispose();
        _integration = null;
        Shutdown();
        return;
    }

    // 2. The integration supplies two of the platform services.
    var platform = new PlatformServices
    {
        …,
        Autostart = _integration.Autostart,
        GlobalHotkey = _integration.GlobalHotkey,
    };
    var shell = new ShellViewModel(platform);
    var window = new MainWindow { DataContext = shell };
    MainWindow = window;

    // 3. --background (the start-with-Windows entry) starts minimized and does not take focus.
    if (commandLine.Background)
    {
        window.WindowState = WindowState.Minimized;
        window.ShowActivated = false;
    }

    // 4. Ctrl+, (and the "Open settings" action of the shortcut banner) opens the Settings window.
    shell.OpenSettingsRequested += (_, _) => SettingsWindowLauncher.Show(window, shell.Settings);

    window.Show();
    await shell.InitializeAsync(new StartupOptions
    {
        DataDirectory = commandLine.DataDirectory,
        FilesToOpen = commandLine.ResolveFiles(Environment.CurrentDirectory),
    });

    // 5. After the window is shown and the shell is initialized: shortcut → bring forward, forwarded files → tabs,
    //    external-file watchers, RegisterApplicationRestart.
    _integration.Attach(window, shell);
}

protected override void OnExit(ExitEventArgs e)
{
    // 6. On the UI thread (it releases the mutex taken in OnStartup).
    _integration?.Dispose();
    base.OnExit(e);
}
```

Notes for the composition root:

- `Attach` may also be called before `InitializeAsync` completes; the parts that need the initialized shell (file
  watching, delivering forwarded files) wait for `ShellViewModel.IsInitialized`. Forwarded launches that arrive
  earlier are queued, never dropped.
- The App's own `IFileDialogService` should use `FileDialogFilters.Open` (`NimPad.Presentation.Integration`) as the
  Open dialog's `Filter` with `FilterIndex = FileDialogFilters.DefaultOpenFilterIndex` ("All files (\*.\*)", "Text
  files", "Markdown", "JSON", then every other syntax mode with the extensions Core recognizes).
- Theme: drive `ThemeService.Apply` from `shell.Settings.EffectiveIsDark` / `Theme` (the shell's part). The Settings
  window frame follows by itself (and `ThemeService.UpdateWindowFrames` covers every open window).
- Logging: this code writes to `System.Diagnostics.Trace` (warnings for pipe, watcher, registry and hotkey trouble);
  the App's log listener picks it up. Nothing here throws into the dispatcher.
- `--restarted` needs no handling: after a crash the shell finds the recovery buffers and shows "NimPad recovered
  unsaved changes to N files" with **Review**; unsaved notes were autosaved.

## Command line

`NimPad.exe [--data-dir <path>] [--no-single-instance] [--no-hotkey] [--background] [--restarted] [files...]`
(`AppCommandLine.Parse`; case-insensitive options, `--data-dir=<path>` works, `--` ends options, unknown options are
collected in `UnknownOptions` and never fail the launch).

| Option | Effect |
|---|---|
| `--data-dir <path>` | Another profile; also gives the single-instance pipe its own name, so different data never forwards. |
| `--no-single-instance` | No mutex, no pipe: every launch runs (FlaUI tests). |
| `--no-hotkey` | The global shortcut is remembered (Settings shows and stores it) but never registered with Windows. |
| `--background` | Start minimized (the App honors it, see Wiring step 3). A forwarded `--background` launch does not activate the running window. |
| `--restarted` | Set by `RegisterApplicationRestart`; informational. |

## Single instance

- Names: `NimPad-<user SID>` for the default profile; portable and `--data-dir` profiles append `-<16 hex digits of
  the data root's SHA-256>`. The mutex is `Local\` + that name; the pipe is the name itself.
- The first launch owns the mutex (an abandoned mutex left by a crash counts as owned) and listens at once. The pipe's
  security descriptor has the current user as the only (Allow, full control) entry, with inheritance blocked, and the
  token's default owner as owner (what a `CurrentUserOnly` client verifies); the first instance is created with `FILE_FLAG_FIRST_PIPE_INSTANCE` and the next listening instance is
  created before a connected one is served, so the name is never free for another process to squat on.
- A later launch connects with `PipeOptions.CurrentUserOnly` (verifies the server's user), calls
  `AllowSetForegroundWindow(<server pid>)`, sends one message — 4-byte little-endian length + UTF-8 JSON
  `{"version":1,"files":["C:\\full\\path.txt"],"activate":true}` — and waits for the 0x06 acknowledgement (10 s). Files
  are resolved to full paths against the launching process's current directory. If the primary does not answer and
  has exited meanwhile, the launch takes over; otherwise it exits.
- The primary validates every message (version, at most 4,096 fully qualified paths, at most 1 MB, read within 5 s),
  opens the files as external tabs (`ShellViewModel.OpenFiles`) and, unless `activate` is false, restores and
  activates the window and focuses the editor. Invalid messages and broken connections are logged and ignored.

## Global shortcut

`GlobalHotkeyService` registers with `RegisterHotKey(MOD_* | MOD_NOREPEAT, VK)` on a message-only window it owns
(`HwndSource` with `HWND_MESSAGE` parent) and raises `Pressed` on `WM_HOTKEY`. Decision: not the main window's HWND,
because `ShellViewModel.InitializeAsync` registers the saved shortcut before the main window necessarily has a handle,
and a handle of its own survives the main window recreating its HWND. A new registration replaces the old one only
after it succeeded (a conflict keeps the old shortcut; the error says "Another app already uses Ctrl+Alt+K. Try a
different shortcut."). Pressing it brings NimPad to the front and focuses the editor. While the Settings window
captures a new shortcut, pressing the current one (which Windows delivers as the hotkey, not as keys) keeps it.

## Start with Windows

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `NimPad` = `"<exe path>" --background`, where the exe is
`Environment.ProcessPath` (or `NimPad.exe` next to the app under the `dotnet` host), so portable copies register
their own path. `IsEnabled` is true only when the value starts this executable and Task Manager has not turned the
entry off (`Explorer\StartupApproved\Run`, odd first byte); `Set` also clears that Task Manager mark. Registry errors
propagate to the view model, which reverts the switch and shows a toast.

## External files

`ExternalFileWatcher` (`NimPad.Presentation.Integration`, created by `Attach`): one `FileSystemWatcher` per open
external tab on the file's folder, filtered by the file name; events are debounced 300 ms, then the file is read once
off the UI thread and `ExternalFileChanges.Evaluate` decides on the UI thread:

| Settled state | Tab call |
|---|---|
| Write time equals `TabViewModel.LastSavedWriteTimeUtc` (NimPad's own atomic save) | nothing |
| Other write time | `OnFileChangedOnDisk(time)`: "“x” changed on disk." with Reload |
| File missing (moved, renamed, deleted) | `OnFileMissingOnDisk()`: "“x” was moved or deleted." with Save as; the text stays and the tab counts as unsaved (Save writes it back) |
| Back after missing | `OnFileChangedOnDisk(time)`: the notice clears; unchanged files are clean again |

Watchers follow Save as, stop when a tab closes and are released by `Dispose`. Folders that cannot be watched (gone,
some network shares) are logged; the tab works without a watcher.

## Settings window

`SettingsWindowLauncher.Show(owner, shell.Settings)`: one modeless window per UI thread, owned by the main window, not
in the taskbar; a second request activates it. Standard frame with immersive dark mode following
`SettingsViewModel.EffectiveIsDark`. Sections Appearance and General (Account and Synchronization arrive in Phase 2;
"Minimize to system tray" is deferred). Keyboard: focus starts in the section list (Up/Down switch sections), Tab
moves through the rows, Esc closes (an open dropdown takes Esc first). Global shortcut: **Change** starts capturing —
the next key chord with Ctrl, Alt or Win becomes the shortcut; a modifier alone keeps waiting; Esc or **Cancel**
stops; invalid, reserved or taken chords show their reason inline (Err text with an icon) and capturing continues;
switching to another window cancels.

UI automation ids (contract): `SettingsWindow`, `SettingsNav` (items `SettingsNavAppearance`, `SettingsNavGeneral`),
`ThemeSegmented` (items named Light, Dark, System, with SelectionItem), `FontDropdown`, `FontSizeStepper`,
`WordWrapSwitch`, `LineNumbersSwitch`, `TabSizeSegmented` (items 2, 4, 8), `StartWithWindowsSwitch`,
`RestoreTabsSwitch`, `GlobalShortcutChange`, plus `SettingsPreview`, `GlobalShortcutKeys` and `GlobalShortcutError`.
Only the selected section's controls are visible: select `SettingsNavGeneral` (or press Down in `SettingsNav`) before
using the General controls.

## Tests

- `NimPad.Presentation.Tests/Integration` (any OS): command line and quoting, pipe message encoding and framing (also
  over a real pipe), instance names, hotkey ↔ virtual-key round trips and capture composition, Run value rules, file
  dialog filters, file-change decisions, and the file watcher over real files (debounce, own-save suppression,
  delete/rename/recreate, Save as, tab close). `Tabs/ExternalFileMissingTests` covers the moved/deleted behavior.
- `NimPad.App.Tests/Integration` and `Settings` (Windows only, skipped elsewhere): two in-process instances exchanging
  files over a uniquely named pipe, the pipe ACL, crash takeover, real `RegisterHotKey` conflicts and `WM_HOTKEY`,
  the Run value under a test-only name, `RegisterApplicationRestart`, the bootstrapper end to end with a real shell,
  and the Settings window in both themes against the automation contract.
