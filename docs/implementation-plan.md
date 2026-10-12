# Implementation plan

This is the engineering plan for the current phases. The product phases themselves are defined in
[product-spec.md §12](product-spec.md). Progress against this plan is tracked in
[implementation-status.md](implementation-status.md).


## Context

The repo `berkkarabacak/nimpad` (branch `claude/vibrant-wright-a3yuns`) contains only the approved design deck
(`NimPad — Cloud Notepad for Windows.pdf/.html`, 29 pages: welcome/sign-in, editor chrome, sidebar states,
find/replace, tab menu, conflict dialog, shortcuts, sync states, settings, full light/dark tokens, type/metrics,
interaction states, keyboard map, responsive layouts, empty/loading states). The user supplied the full product
spec and asked to start with Phase 0, then Phase 1, while keeping the architecture ready for sync, version
history, MCP and multi-agent features.

**Environment constraints:**
- The dev box is Linux, with Node 22, pnpm, PostgreSQL 16 binaries and Docker. It has no .NET.
- I'll install the .NET 10 SDK 10.0.401 to `~/.dotnet` with `dotnet-install.sh`; it and NuGet are reachable.
- WPF cannot run here. The cross-platform C# projects build and test on Linux, and the WPF project compiles with
  `EnableWindowsTargeting=true`.
- Running the app, the XAML resource tests and the FlaUI end-to-end tests happens only on GitHub Actions
  `windows-latest`. I'll report exactly what was verified where.

**Decisions already made:**
- **Sidebar (user deferred to me): tree-first.** Top to bottom: search, Pinned, Recent (design row style: title,
  preview line, timestamp), the workspace folder tree, and Trash at the bottom. The design's tokens, rows,
  match highlighting, sort menu and Trash states are kept; the All/Untitled chips are dropped.
- **Phase 1 runs in "local only" mode with no sign-in.** The status bar says "Local only", never "Synced". The
  welcome/sign-in screen, the account pages and the sync UI arrive in Phase 2.
- **Server migrations are per phase.** The spec's agent, task, approval, webhook and agent_run tables land in the
  phases that use them, not in 0001.

## Repository layout (Phase 0)

```
global.json                      .NET SDK 10.0.401 (rollForward latestFeature), MTP test runner
package.json, pnpm-workspace.yaml, biome.json, tsconfig.base.json, .editorconfig, .gitattributes
docker-compose.yml               postgres:16 for local dev
.env.example                     DATABASE_URL, DATABASE_URL_MIGRATE, SUPABASE_URL/ANON_KEY/JWKS_URL,
                                 GOOGLE_CLIENT_ID (placeholders; never secrets)
scripts/setup.sh, setup.ps1      prerequisites check, pnpm install, dotnet restore, db up + migrate
apps/windows/
  NimPad.slnx, Directory.Build.props, Directory.Packages.props (central versions, warnings as errors)
  src/NimPad.Core                net10.0: domain, SQLite storage, services, op queue, external file IO
  src/NimPad.Presentation        net10.0: view models (CommunityToolkit.Mvvm 8.4.2), no WPF references;
                                 abstractions IDispatcher, IClipboard, IFileDialogs, IEditorBuffer, TimeProvider
  src/NimPad.App                 net10.0-windows WPF + AvalonEdit 6.3.1.120
  tests/NimPad.Core.Tests, tests/NimPad.Presentation.Tests     xunit.v3 4.x on Microsoft.Testing.Platform
  tests/NimPad.App.Tests         Windows-only: STA test loading every ResourceDictionary/Window
services/api                     Fastify 5: /healthz, /readyz, zod env config, error envelope, pg pool with
                                 withUserTx(userId, fn) using set_config('app.user_id', $1, true)
services/mcp                     @modelcontextprotocol/sdk 1.32 server factory; no note tools until Phase 4
services/worker                  job-loop skeleton with graceful shutdown
packages/contracts               zod 4 schemas (Node, DocumentContent, PendingOperation, ApiError), the
                                 fractional-index algorithm, the content checksum, shared JSON fixtures
database/migrations              plain SQL; database/migrate.ts runner (advisory lock, per-file transaction,
                                 checksums in schema_migrations)
tests/database                   vitest against real Postgres: migrations, RLS isolation, move race, event seq
tests/e2e-windows                FlaUI 5 UIA3 end-to-end tests (Windows CI only)
docs/                            all docs required by the spec; design deck moved to docs/design/
.github/workflows/ci.yml         jobs: backend (ubuntu + postgres service), dotnet-linux, windows
```

**TypeScript tooling:**
- TypeScript 7 (native compiler), Biome 2.5, vitest 5 and tsx.
- `tsc --noEmit` runs as its own CI step.
- tsconfig: `module`/`moduleResolution: nodenext`, `verbatimModuleSyntax`, `erasableSyntaxOnly`.
- zod is deduplicated with a pnpm `overrides` entry so the MCP SDK and our code share one copy.

## Server schema (Phase 0, real but unused by clients until Phase 2)

**Shared rules:**
- All tables live in a `nimpad` schema.
- RLS uses `ENABLE` + `FORCE ROW LEVEL SECURITY`, with policies through `nimpad.current_user_id()`, which reads
  `current_setting('app.user_id', true)`. It does not use `auth.uid()`, so the same SQL works on plain Postgres
  and Supabase.
- The migrations create a non-owner `nimpad_app` role without BYPASSRLS. They revoke access from
  `anon`/`authenticated` when those roles exist, so Supabase's Data API cannot expose the tables.

**`0001_core.sql`:**
- `users`: id = auth subject, no FK to `auth.users`.
- `workspaces`: carries `event_seq bigint`.
- `workspace_members`.
- `nodes`:
  - Composite FK `(workspace_id, parent_id)` → `(workspace_id, id)`.
  - Trigger: the parent must be a folder.
  - Folder-cycle prevention trigger, plus `pg_advisory_xact_lock(workspace)` in the move path.
  - Soft delete via `deleted_at` + `trash_batch_id`.
  - Fractional `position` text.
- `document_contents`: `content_rev`, `meta_rev`, checksum.
- `document_revisions`: immutable; `actor_kind` user|agent|system plus `actor_id`; unique `(node_id, revision)`.
- `document_links`, `activity_events`, `audit_log`.
- Indexes on membership, parent, revisions and timestamps.

**`0002_sync.sql`:**
- `devices`, `device_sessions`.
- `sync_events`: `(workspace_id, seq)` unique. Seq comes from
  `UPDATE workspaces SET event_seq = event_seq + 1 RETURNING` in the same transaction, which makes it a gap-safe
  cursor.
- `idempotency_records`: unique `(user_id, op_id)`, stored response, TTL purge.

## Desktop Core (NimPad.Core)

**Storage:**
- Location: `%LOCALAPPDATA%\NimPad\profiles\local\nimpad.db`. It is overridable with `--data-dir`, and a
  `portable.txt` marker next to the exe switches to portable mode.
- Pragmas: `journal_mode=WAL`, `synchronous=FULL`, `foreign_keys=ON`.
- Startup runs `quick_check`. On failure, the DB is moved aside, the newest backup is restored and a banner
  explains it.
- A daily `VACUUM INTO` backup is kept (7 retained). Exit runs a checkpoint and `PRAGMA optimize`.
- Its own `schema_version` migrations.

**Threading:**
- One dedicated writer connection, fed by a `Channel<Func<…>>` on a background thread. Each mutation is a single
  `BEGIN IMMEDIATE` transaction containing the domain change and its pending operation.
- Reads use pooled read connections and never run on the UI thread for anything non-trivial.

**Local tables:**
- `device` holds a device_id created on first run. `workspaces` holds a single "My Workspace" in Phase 1; the
  schema allows many.
- `nodes`:
  - Identity and tree: rowid kept, `id` TEXT UUIDv7 lowercase, workspace_id, parent_id, kind folder|document,
    name, position.
  - Pinning: is_pinned, pin_position.
  - Timestamps: created_at, updated_at, content_updated_at.
  - Trash: deleted_at, trash_batch_id.
  - Sync revisions: meta_server_rev, content_server_rev.
- `document_contents`: node_id, text, format, checksum, char_count, preview, fts_dirty.
- `document_sync_base`: node_id, server_content_rev, text, checksum. Empty in Phase 1; it is the base for
  Phase 2's three-way merge.
- `document_links`: source_id, target_id.
- `notes_fts`: FTS5 over name and body, `unicode61 remove_diacritics 2`, mapped by rowid.
- `pending_operations`: `seq` autoincrement, op_id, workspace_id, node_id, kind
  create|update_content|rename|move|pin|delete|restore|purge, payload JSON, base revisions, device_id,
  actor_kind, created_at, attempts, last_error.
- `session_tabs`, `recently_closed`, `ui_state` (tree expansion, sidebar width/collapsed, sort order).
- `settings` (key/JSON).
- `external_file_buffers`: unsaved external edits for crash recovery; never synced.

**Op-queue coalescing:**
- One pending `update_content` per node, replaced in place. It keeps its original base revision and stores a
  checksum, not the text; the current content is read at push time.
- Updates fold into a still-pending `create`.
- A create followed by a delete of a never-synced node cancels both.
- No drain in Phase 1, and therefore no fake sync.

**Content canonicalization:**
- Workspace notes are normalized to LF.
- Checksum = SHA-256 of the UTF-8 LF text, defined in contracts and verified by shared fixtures in both C# and
  TS. The fractional-index algorithm gets the same treatment, with ties broken by id.

**Services:**
- `NodeService`:
  - Create, rename, move with a recursive-CTE cycle check, and deep copy with new IDs.
  - Pin/unpin; trash a subtree under one batch; restore; delete forever; empty trash; purge items older than
    30 days at startup.
- `DocumentService`: save with format detection from the name extension or content sniff, preview, links
  extraction, FTS update. Documents over 256 KB set `fts_dirty`, and a background indexer catches up.
- `AutosaveScheduler`: TimeProvider-driven, 400 ms debounce with a 1000 ms max-wait. Dirty state is tracked by
  buffer version, not a bool. A write failure keeps the note dirty, retries with back-off and raises an error
  banner.
- `SearchService`: user tokens are quoted with a prefix `*`, ranked by bm25, with highlighted snippets and
  cancellation.
- `SessionService`: tabs, order, pinned, active, caret/selection/scroll per tab, and layout.
- `ExternalFileService`:
  - Encoding detection: BOM, UTF-8 validation, then a fallback to 1252.
  - Line-ending preservation.
  - Atomic save via temp file + `File.Replace`, falling back to a direct write.
  - Recovery buffers.
  - Import into the workspace.
- Internal links: `nimpad://note/{uuid}` in the text, so moves never break them; `document_links` is kept current
  on save.
- Empty, never-edited untitled notes are hard-deleted when their tab closes.
- Ctrl+N creates the note at the workspace root; "New note" on a folder creates it inside that folder.

## Presentation (view models, unit-tested on Linux)

- `ShellViewModel`: regions, layout breakpoints, sidebar state, toasts and banners.
- `TabsViewModel`:
  - Open, close, reopen stack, pin, rename, duplicate, reorder.
  - Ctrl+1..9 and Ctrl+Tab semantics.
  - Tab states: dirty accent dot, loading.
- `ExplorerViewModel`:
  - One flattened, virtualized row list with header rows (Pinned, Recent, Workspace).
  - All node metadata is loaded at startup; content loads on demand.
  - Expand/collapse emits one collection change per row (WPF rejects range changes).
  - Move/reorder API used by drag-and-drop; search-results mode; Trash mode.
- `EditorViewModel`: breadcrumbs, mode, zoom, find/replace state. `StatusBarViewModel`: "Ln 13, Col 31 · 4
  selected", char count from `TextLength`, encoding, mode, zoom, sync state.
- `SettingsViewModel`, `ShortcutsViewModel` (filterable).

## WPF App (NimPad.App)

**Theme:**
- `Tokens.Light.xaml` and `Tokens.Dark.xaml` hold the deck's exact token values.
- `Controls.xaml` restyles every control: buttons (primary/secondary/quiet/destructive/small/icon), text fields,
  search field, switch, segmented control, dropdown, stepper, menus, tooltip, scrollbar (4→12 px), dialog over a
  scrim, toasts and banners.
- "System" follows the Windows registry and `SystemEvents`.
- Icons are `Path` geometries. Cascadia Mono (OFL) is bundled. UI font chain: `Segoe UI Variable Text, Segoe UI`.

**Shell:**
- `WindowChrome` with a 40 px caption: app mark, tab strip, new-tab button and window controls (46×40, close
  turns red). The account avatar is hidden until Phase 2.
- Interactive elements use `IsHitTestVisibleInChrome`.
- Snap Layouts: `WM_NCHITTEST` returns `HTMAXBUTTON` with DPI-correct coordinates, and NC mouse messages drive
  the hover/press visuals.
- `WM_GETMINMAXINFO` fixes the maximized overhang. `GlassFrameThickness` is kept non-zero for the Win11 shadow
  and rounded corners. The manifest declares PerMonitorV2.
- Settings and dialogs use DWM immersive dark mode.
- Window: default 1120×720, minimum 400×300.

**Responsive layouts:**
- Wide (≥720): docked 288 px sidebar.
- Medium (520–719): the sidebar overlays, the app name hides, editor padding is 20/24 and the char count drops.
- Narrow: the status bar trims, and tabs shrink then scroll. Chevrons are deferred.

**Tab strip:**
- Tabs are 34 px tall and 96–172 px wide; pinned tabs are 104 px with no close button.
- Visual states: hover, selected (shares the editor surface), keyboard focus (inset ring), waiting dot, loading
  spinner, rename-in-place (F2).
- Drag reorder with a lifted look and an accent marker; Ctrl+Shift+←/→ also moves tabs.
- Context menus on tabs and on empty strip space.
- "Closed X · Undo" toast.

**Explorer:**
- Virtualized ListBox with indentation and chevrons. Keyboard per the deck: Ctrl+B focuses search, ↑/↓,
  Home/End, Enter, F2, Delete, Esc.
- Context menus: New note, New folder, Rename, Duplicate, Pin, Copy link, Export, Move to Trash.
- Drag-and-drop:
  - Private DataFormat, drag threshold, before/inside/after adorner.
  - Auto-scroll at the edges and hover-to-expand.
  - Files dropped from Explorer open as external files.
- Custom AutomationPeer exposing level and expand/collapse. Trash view: Restore, Delete forever (with confirm),
  Empty trash.
- Empty/no-results states use the design copy.

**Editor:**
- A single AvalonEdit `TextEditor`, with one `TextDocument` per tab swapped in.
- Caret, selection and scroll are restored at `DispatcherPriority.Loaded`. Restored tabs load text lazily on
  first activation.
- Large files are built off-thread and handed over with `SetOwnerThread`; files over 5 MB show progress with
  Cancel.
- Highlighting and wrap turn off above a line-length threshold.
- Breadcrumb line above the text, in caption style with clickable segments.
- Empty-note hint "Start typing. It saves and syncs on its own." (shown, never saved).
- Line numbers, word wrap, tab size, current-line wash, selection and caret colors from tokens.
- Zoom with Ctrl+±/0 and Ctrl+wheel.

**Syntax highlighting:**
- Token values are substituted into the `.xshd` text, and one definition is cached per (mode, theme).
- Built-in definitions, remapped: Markdown, JSON, XML/HTML, Python, JavaScript, C#, PowerShell, SQL, CSS.
- Custom definitions: YAML, Groovy, INI.
- Plain text stays one color.

**Find/replace:**
- Custom floating panel, top right: Aa, whole word, regex, "1 of 2", Replace, Replace all. The deck's key
  bindings and tab order apply.
- Renderers use the match and match-cur colors. "No results" is spelled out in words.

**Links:**
- The built-in hyperlink handling is off. A custom generator handles `https?://` and `nimpad://` with Ctrl+click.

**External files:**
- Opened via Ctrl+O, drag-drop or command-line args. Single instance via a per-user named pipe with a
  current-user ACL that forwards args.
- Ctrl+S saves an external file in place; for a managed note, Ctrl+S exports it to a file.
- A FileSystemWatcher ignores our own saves and shows a "Changed on disk · Reload" banner.
- The status bar shows "Not synced" for external files. "Import into workspace" copies the file in; nothing is
  ever uploaded silently.

**Settings window:**
- Appearance: theme, font, size, wrap, line numbers, tab size, with a live preview.
- General: restore tabs, start with Windows (HKCU Run), global shortcut Ctrl+Alt+N (`RegisterHotKey`).
- The keyboard shortcuts dialog opens with Ctrl+/.
- F6 / Shift+F6 cycle between regions.

**Reliability:**
- Dispatcher, AppDomain and TaskScheduler exception handlers all flush pending saves synchronously.
- `SessionEnding`/`WM_QUERYENDSESSION` also flush, and `RegisterApplicationRestart` is set.
- A rolling log is written to `%LOCALAPPDATA%\NimPad\logs`. Test flags: `--data-dir`, `--no-single-instance`,
  `--no-hotkey`.

**Deferred and documented in implementation-status:**
- The right context panel (arrives with Version History in Phase 3).
- Narrow-layout tab chevrons, tray mode, split editor.
- The sign-in/account/sync UI and the Daily Notes, Agent Activity and Tasks sections; each lands in its own phase,
  and none is faked.

## Commit sequence

1. **Scaffold.** global.json, Directory.*.props, .editorconfig, pnpm workspace, Biome, tsconfig.base. Move the
   design deck to `docs/design/` and add `tokens.md` extracted from it. Add `docs/product-spec.md` (the spec
   verbatim), architecture.md and implementation-plan.md.
2. **TypeScript services.** contracts (schemas, fractional index, checksum, fixtures), api/mcp/worker skeletons
   with vitest, and the CI backend job.
3. **Database.** Migration runner, 0001/0002, docker-compose, `tests/database` (idempotent migrate, RLS
   isolation as `nimpad_app`, concurrent move race, event_seq), setup scripts.
4. **.NET skeleton.** The 3 projects plus tests, an empty WindowChrome shell, and the CI dotnet-linux and windows
   jobs. The windows job builds, tests, runs the FlaUI "window opens" test and uploads a portable zip artifact.
5. **Core storage.** Paths/profile, pragmas, migrations, backups/quick_check, writer, NodeService, op queue.
6. **Core documents.** DocumentService, autosave, FTS + search, links, session/UI state.
7. **Core external files.** ExternalFileService (encoding, EOL, atomic save, recovery buffers).
8. **Presentation.** View models with tests.
9. **App shell.** Tokens/themes, control styles, icons, title bar and Snap Layouts, tab strip, status bar,
   toasts/banners, responsive states.
10. **App explorer.** Tree view, DnD, context menus, search and Trash views, automation peer.
11. **App editor.** Editor host, grammars, find/replace, links, breadcrumbs, restore of caret/scroll.
12. **App files and reliability.** External-files UI, single instance, watcher banner, import, crash/shutdown
    flush.
13. **App settings and acceptance.** Settings, shortcuts dialog, hotkey, autostart, logging, the Phase 1
    acceptance FlaUI test, and the docs: README, synchronization, mcp-integration (planned), deployment,
    testing, implementation-status.

After every commit: build, run the relevant tests, and fix failures before moving on. Push to
`claude/vibrant-wright-a3yuns` and open one ready-for-review PR. I'll subscribe to it and fix Windows CI failures
until it is green, since Windows CI is the only place the XAML actually runs.

## Verification

**Linux, every commit:**
- `pnpm biome ci`, `pnpm -r typecheck`, `pnpm -r test`, and `pnpm db:migrate` against local Postgres 16, run
  twice to prove it is idempotent.
- `dotnet build apps/windows/NimPad.slnx -warnaserror` (the WPF project builds with
  `EnableWindowsTargeting=true`) and `dotnet test` for Core and Presentation.

**Key automated tests:**
- **Database and storage:** the FTS5 compile option and pragmas are present; local migrations are idempotent.
- **Node operations:**
  - Cycle rejection.
  - Deep copy.
  - Trash/restore by batch, and the 30-day purge.
  - Pin ordering, with fractional-index fixture parity between C# and TS.
- **Op queue:** coalescing rules, including create-then-delete cancellation.
- **Content:** LF normalization, plus checksum fixture parity between C# and TS.
- **Autosave:** with a fake clock, the 400 ms debounce and 1 s max-wait.
- **Search:** sanitizing, highlighting, and a 10k-note benchmark reporting the median against the 200 ms target.
- **External files:** an encoding/EOL matrix, atomic save, crash-recovery buffers.
- **View models:** tab, explorer flattening, move and session-restore behavior.
- **Core acceptance:** 500 notes across 6 nesting levels, then close and reopen the DB, then compare tree, content
  and session exactly.
- **Server:** RLS denies cross-user reads; concurrent opposite moves cannot create a cycle; event_seq has no
  gaps; MCP handshake; API health/readiness.

**Windows CI:**
- STA tests load every ResourceDictionary and window in both themes.
- FlaUI end-to-end, launched with `--data-dir <temp> --no-single-instance --no-hotkey`:
  1. Create a folder tree and notes, type, then drag a note into a subfolder.
  2. Search and find it.
  3. Close the app and relaunch: the tabs, text, tree and expansion all come back.
  4. Kill the process during editing, relaunch, and confirm the edits were recovered.
- Screenshots upload on failure. A portable zip is published as an artifact.

**Manual verification:** Not possible in this environment, and docs/testing.md will say so. The user (or a Windows
runner) launches the app with `dotnet run --project apps/windows/src/NimPad.App`. No feature is marked done in
implementation-status.md unless a test covers it.
