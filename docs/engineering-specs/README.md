# Engineering specs (desktop app)

These are the detailed task specifications the desktop work was built from. Each was given to one implementation
agent and then checked by a three-lens review (correctness, spec and design compliance, test adequacy) followed by a
verify-and-fix pass. That flow is scripted in [`implement-review-fix.workflow.js`](implement-review-fix.workflow.js).
They are kept so the remaining work can resume from the same instructions.

| Spec | Area | State |
|---|---|---|
| [context-desktop.md](context-desktop.md) | Shared context given to every desktop agent | — |
| [presentation.md](presentation.md) | View models (`NimPad.Presentation`) | Done, reviewed |
| [design-system.md](design-system.md) | WPF tokens, styles, controls, theming | Done; verified on Windows CI |
| [highlighting.md](highlighting.md) | Theme-aware syntax highlighting | Done; verified on Windows CI |
| [shell.md](shell.md) | App startup, main window chrome, tab strip, status bar, platform services | Implemented; **review not yet run** |
| [platform.md](platform.md) | Settings window, single instance, global hotkey, autostart, file watcher | Implemented; **not yet wired into App startup or reviewed** |
| [explorer.md](explorer.md) | Explorer sidebar view (replaces the shell's stub) | **Not started** |
| [editor.md](editor.md) | Editor view, find and replace, links (replaces the shell's stub) | **Not started** |
| [e2e.md](e2e.md) | FlaUI end-to-end tests | Written; not yet run against the finished UI |
| [automation-ids.md](automation-ids.md) | The UI automation contract every view must implement | Authoritative |

The specs refer to design-deck page images as `docs/design/renders/p-NN.png`. Regenerate them (they are not
committed) with:

```sh
mkdir -p docs/design/renders
pdftoppm -r 110 -png "docs/design/NimPad — Cloud Notepad for Windows.pdf" docs/design/renders/p
```

Where a spec says "/home/user/nimpad", read it as the repository root.
