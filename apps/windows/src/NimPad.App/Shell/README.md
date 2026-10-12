# NimPad shell (WPF)

The application shell: composition root, main window, title bar and tab strip, status bar, banners, toasts, the modal
layer with the confirm and keyboard-shortcuts dialogs, the WPF platform services and the startup arguments and log.
It binds to `NimPad.Presentation` (see its README for the view-model contract) and draws with the design system
(`Themes/README.md`).

| Folder / file | What |
|---|---|
| `App.xaml.cs` | Composition root (below). |
| `MainWindow.xaml(.cs)` | WindowChrome window: title bar (40) / banners / content / status bar (24) + modal layer. |
| `Shell/TitleBarView` | App mark, `TabStrip`, new-note button, account slot (hidden in Phase 1), window controls. |
| `Shell/TabStrip`, `TabStripItem`, `TabStripPanel`, `TitleTabsLayout` | The tab strip (states, keyboard, drag to reorder, widths 172 → 96 then scrolling with chevrons). |
| `Shell/StatusBarView` | Sidebar toggle, position, characters, encoding, mode menu, zoom, sync slot, ⋯ menu (narrow). |
| `Shell/BannerHostView`, `ToastHostView`, `EmptyStateView` | Banners, toasts (bottom-left, pause on hover/focus, polite live region), "No notes open". |
| `Shell/ModalLayer`, `ConfirmDialog`, `ShortcutsDialog` | Dialogs over a scrim inside the window (`IModalDialog`). |
| `Shell/WindowChromeHook` | Snap Layouts (`HTMAXBUTTON`), maximized work-area fit, right-click tab-bar menu. |
| `Shell/WindowPlacementStore` | Window bounds in `profiles/local/window-placement.json` (clamped to a visible monitor). |
| `Shell/ShellStartup` | The startup path shared by `App` and the UI tests. |
| `Shell/RegionBorder`, `CommandToggleButton`, `BalancedColumnsPanel`, `ShellConverters` | Building blocks. |
| `Platform/` | `WpfUiDispatcher`, `ClipboardService`, `FileDialogService`, `DialogService`, `FontCatalog`, `ShellLauncher`, `SystemThemeProvider`, `AppIntegrations` (+ defaults). |
| `Services/StartupArgs`, `Services/FileLog` | Command line; rolling daily log (14 days, never note text). |
| `Views/Editor/*`, `Views/Explorer/*` | **Stubs** to be replaced (contract below). |

## Startup

`NimPad.exe [--data-dir <path>] [--no-single-instance] [--no-hotkey] [--background] [files...]` (also `--name=value`,
`--` ends options; unknown options are logged and ignored).

1. Crash handlers are hooked first (`DispatcherUnhandledException`, `AppDomain.UnhandledException`,
   `TaskScheduler.UnobservedTaskException`): log, `ShellViewModel.EmergencyFlush()`, an error banner, and the app keeps
   running unless the error is fatal or repeats (more than 5 in 30 s).
2. `ConfigureIntegrations` (partial method) fills `AppIntegrations`; single instance runs unless `--no-single-instance`.
3. Log (`<data>/logs/nimpad-yyyy-MM-dd.log`; `Trace` output goes there too), then `ThemeService.Apply(System)`.
4. `ShellViewModel` over the WPF platform services, then `MainWindow`.
5. `ShellStartup.RunAsync`: starts `InitializeAsync`, waits at most 400 ms so the window appears with its theme and tabs,
   shows the window (never a splash), awaits the data. A failure (for example a locked database) becomes the
   `startup-failed` error banner; the window stays usable.
6. Closing the window: `ConfirmCloseAsync` (unsaved files), placement saved, `ShutdownAsync` (bounded by 12 s), then the
   window closes. `SessionEnding` flushes; `Exit` runs `ShutdownAsync` with an 8 s bound if the window did not.

## Seams for other features

Implement in a separate file, without editing the shell:

```csharp
namespace NimPad.App;

public partial class App
{
    partial void ConfigureIntegrations(AppIntegrations integrations)
    {
        integrations.Activation = new SingleInstanceActivation();          // IAppActivation
        integrations.GlobalHotkey = args => new GlobalHotkeyService();     // not called with --no-hotkey
        integrations.Autostart = () => new RunKeyAutostartService();
        integrations.SettingsWindow = new SettingsWindowLauncher();       // ISettingsWindowLauncher (Ctrl+Comma)
    }
}
```

Defaults when unset: no single-instance handling; `DisabledGlobalHotkeyService` (registers nothing, reports success,
logs it); `UnavailableAutostartService` (off; turning it on throws, so the Settings view model reverts with a toast);
Settings requests are logged. `--background` hides the window only when an `Activation` can show it later.

Other dialogs can use the window's modal layer: implement `IModalDialog` on a `FrameworkElement` and
`await mainWindow.Modal.ShowAsync(dialog)` (one at a time, queued; Esc cancels; window shortcuts are suppressed
while it shows).

## Replacing the stub views

`MainWindow` uses these members; keep them (or adjust `MainWindow.xaml.cs` in the same change):

| View | Members used by the shell (and its tests) |
|---|---|
| `Views/Editor/EditorView` (DataContext = `ShellViewModel`) | `FocusEditor()`, `SelectedText` (Ctrl+F / Ctrl+H prefill), `IsDarkTheme` (set on theme change), `Host` (`EditorHost`: `DocumentShown`, `IsShowingActiveDocument`, `DocumentOf(tab)`, `Dispose()`), `TextEditor` |
| `Views/Explorer/ExplorerView` (DataContext = `ExplorerViewModel`) | `FocusSearch()` (Ctrl+B, F6) |

`EditorHost` implements the document lifecycle of the Presentation README (one `TextDocument` per tab, `EditorBuffer`,
view-state restore at `Loaded` priority, `ReplaceTextRequested`, release on `TabClosed`, read-only while loading and
shutting down) and can be reused as is. `EditorBuffer.GetSnapshotText()` uses `TextDocument.CreateSnapshot()`, which
AvalonEdit documents as safe from any thread, so no marshaling (and no deadlock with `EmergencyFlush`).

## Window chrome

`WindowChrome`: caption 40, resize border `SystemParameters.WindowResizeBorderThickness`, glass `0,0,0,1` (Windows 11
keeps the shadow and rounded corners), no Aero caption buttons. Everything that is not a control is caption: drag,
double-click maximizes, right-click on the app mark shows the system menu. A right-click on the empty tab-bar space
(the tabs' column) opens the deck's tab-bar menu (New note, Reopen closed tab); the new-note button has the same menu.
Snap Layouts: the maximize button answers `HTMAXBUTTON` (except on the top resize edge of a restored window) and is
driven by non-client messages. Maximized windows fit the work area, 1 px short of an auto-hide taskbar.

## Keyboard

Window bindings for every `ShellViewModel` gesture (Ctrl+N/O/S/Shift+S/W/Shift+T/Shift+D/B/F/H/Comma/Slash,
Ctrl+Plus/Minus/0, Ctrl+1…9, Ctrl+Shift+Left/Right, F6/Shift+F6). Ctrl+Tab / Ctrl+Shift+Tab are handled in
`PreviewKeyDown` (the editor takes Tab). Tab stays inside a region (title bar, sidebar, status bar); F6 moves between
regions. Tab strip: one tab stop; Left/Right/Home/End, Enter (editor), F2, Delete, Ctrl+Shift+Left/Right, Menu or
Shift+F10. Esc closes an overlaid sidebar.

## UI Automation

Every id of the contract for the shell is exposed (`MainWindow` with ItemStatus `theme:light` / `theme:dark`,
`TitleBar`, `TabStrip`, `Tab_<n>` with Name = title and ItemStatus `unsaved` / `saved` / `error`, `TabClose_<n>`,
`NewTabButton`, window controls, `BannerHost` / `ToastHost` (children named by their message), status items with their
visible text as Name, `SidebarToggle` (Toggle pattern), `EmptyStateNewNote`, `EmptyStateReopen`, `ShortcutsDialog`,
`ShortcutsFilter`, `ConfirmDialog`, `ConfirmPrimary`, `ConfirmCancel`). The editor stub exposes `Editor` on the
`TextEditor` (Value pattern) and on its text area (which takes keyboard focus). Containers that WPF does not expose
by themselves use `RegionBorder`.

## Known gaps

- The encoding item is text only: changing a file's save encoding needs a `TabViewModel` operation that does not exist.
- Window bounds live in a JSON file next to the database: Core's `UiState` has no window bounds.
- Find and replace, breadcrumbs and the full explorer belong to the editor and explorer views.
