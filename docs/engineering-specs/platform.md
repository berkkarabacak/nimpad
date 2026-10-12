YOUR AREA: settings UI and OS integration of the NimPad WPF app. You own ONLY:
apps/windows/src/NimPad.App/Views/Settings/** (SettingsWindow), apps/windows/src/NimPad.App/Integration/**
(single instance, global hotkey, autostart, external-file watching, restart registration), and
apps/windows/tests/NimPad.App.Tests/Settings/** + Integration/**. The application shell (App.xaml.cs, MainWindow,
Platform/**) is being built IN PARALLEL by another engineer and is NOT in your tree: do NOT create or edit
App.xaml(.cs), MainWindow or anything outside your paths. Instead provide Integration/IntegrationBootstrapper — a
small class the shell engineer (or the orchestrator) will call from App.xaml.cs, e.g.
`IntegrationBootstrapper.TryBecomePrimaryInstance(args, singleInstanceEnabled)` before the window is created (returns
false after forwarding args to the running instance) and `bootstrapper.Attach(Window mainWindow, ShellViewModel shell,
IntegrationOptions options)` after it is shown (registers the hotkey, starts the pipe server, wires the file
watchers to the tabs, calls RegisterApplicationRestart), plus `Dispose()` on exit — and document the exact wiring
lines in Integration/README.md. Also provide `SettingsWindowLauncher.Show(Window owner, SettingsViewModel vm)` (owner
modal-less, single instance of the window, Esc closes) for the shell to call on ShellViewModel.OpenSettingsRequested.
Minimal tested additions to apps/windows/src/NimPad.Presentation/** are allowed only if a view model genuinely
lacks something.

Read first: App.xaml.cs, Presentation README (SettingsViewModel, ShellViewModel, TabsViewModel external-file APIs,
platform interfaces IAutostartService, IGlobalHotkeyService, IFontCatalog), Themes/README.md (SegmentedControl,
ToggleSwitch, ComboBox, Stepper, KeyCap, buttons, WindowThemeHelper.ApplyImmersiveDarkMode), deck renders in
docs/design/renders/ (p-18 Appearance,
p-21 General, p-27 "Settings: opens in its own small window; Ctrl+, opens; Esc closes; Up/Down move between
sections").

BUILD:
1) SettingsWindow (its own small window, standard Windows chrome with immersive dark mode matching the theme,
   ~760x560, owner = main window, Esc closes, remembers nothing else): left nav 216 px (Appearance, General — the
   Account and Synchronization pages arrive in Phase 2; do not show them), selected item has the 3 px accent bar
   (p-26 "Settings nav item"), Up/Down move between sections; right pane with PageTitle and rows: label (Body/500)
   + description (Secondary, Text2) on the left, control on the right, separated by 16 px gaps, exactly like p-18
   and p-21. Appearance: live preview card (two mono lines with line numbers "1 The quick brown fox jumps over the
   lazy dog" / "2 0123456789 {}[]() => != ===" rendered with the selected font/size/theme), Theme segmented
   (Light / Dark / System — "Follow Windows or pick one."), Editor font dropdown (IFontCatalog monospace families),
   Font size stepper ("14 px"), Word wrap switch ("Wrap long lines instead of scrolling sideways."), Line numbers
   switch, Tab size segmented (2 4 8). General: Start with Windows switch ("Open NimPad in the background when you
   sign in to Windows."), Restore previous tabs switch ("Reopen your tabs, in order, exactly as you left them."),
   Global shortcut (KeyCaps "Ctrl Alt N" + "Change" button that enters a capture mode: the next key chord with at
   least one modifier becomes the new gesture; Esc cancels; conflicts/registration failures shown inline in Err
   text). Omit "Minimize to system tray" (deferred). Everything applies live and persists through the view model.
2) Integration/SingleInstance: per-user mutex + named pipe "NimPad-<user SID>" whose PipeSecurity grants only the
   current user (PipeAccessRule for WindowsIdentity.GetCurrent().User). A second launch forwards its args (file
   paths, or nothing) as a small JSON message and exits; the first instance opens the files as external tabs and
   activates its window (restore if minimized, bring to front with the usual SetForegroundWindow dance:
   AllowSetForegroundWindow from the second process or AttachThreadInput fallback). Disabled by
   --no-single-instance (used by tests). Handle pipe errors without crashing; log them.
3) Integration/GlobalHotkey: RegisterHotKey/UnregisterHotKey on the main window's HWND (HwndSource hook for
   WM_HOTKEY); gesture parsing/formatting ("Ctrl+Alt+N") as a pure class (unit-testable in Presentation if you put
   it there); pressing it brings NimPad to the front (or hides it if already foreground? — no: just activate and
   focus the editor). Disabled by --no-hotkey. Implements IGlobalHotkeyService.
4) Integration/AutostartService (IAutostartService): HKCU\Software\Microsoft\Windows\CurrentVersion\Run value
   "NimPad" = "\"<exe path>\" --background" (document in Integration/README.md that the App must honor --background
   by starting minimized; the orchestrator wires it). Portable builds should still work (the path is whatever the exe is).
5) Integration/ExternalFileWatcher: one FileSystemWatcher per open external file's directory (filter by file
   name), debounced (300 ms), ignoring events caused by NimPad's own saves (compare LastWriteTimeUtc/length with the
   value returned by the save), raising TabViewModel.OnFileChangedOnDisk on the UI thread; handles deletion/rename
   (banner "“x” was moved or deleted" — keep the text so the user can Save As). Dispose watchers when tabs close.
6) Crash/restart: call RegisterApplicationRestart (kernel32) at startup with "--restarted" so Windows restarts
   NimPad after a crash/update; ensure the App shows the recovered-changes banner path (ShellViewModel handles
   it). Verify Ctrl+O open-file dialog filters ("All files (*.*)", "Text files", "Markdown", "JSON", ...).
Tests (Windows-only skip where WPF/Win32 is needed): settings window constructs/measures in both themes and
contains every control by AutomationId (SettingsNav, ThemeSegmented, FontDropdown, FontSizeStepper, WordWrapSwitch,
LineNumbersSwitch, TabSizeSegmented, StartWithWindowsSwitch, RestoreTabsSwitch, GlobalShortcutChange); gesture
parsing round-trips; single-instance message encoding (pure) and an integration test where two in-process
"instances" exchange args over a uniquely named pipe; autostart writes/removes the Run value (use a test-specific
value name so the real one is never touched); file-watcher self-save suppression logic (pure part on Linux).
`dotnet build apps/windows/NimPad.slnx` green on Linux; Core + Presentation tests green.

UI AUTOMATION CONTRACT (mandatory): expose exactly the AutomationIds, Names and ItemStatus values listed for your
area in docs/engineering-specs/automation-ids.md — the
FlaUI end-to-end tests are written against it. Where this spec names a different id, the contract wins.

END-TO-END EXPECTATIONS (the FlaUI tests on Windows CI check these):
- Ctrl+, opens the SettingsWindow (AutomationId "SettingsWindow"); Esc closes it. ThemeSegmented's items are named
  "Light", "Dark", "System" and expose SelectionItem; selecting "Dark" switches the app theme live and the choice
  persists across relaunch (MainWindow ItemStatus "theme:dark").
- External file tabs are restored on relaunch.
