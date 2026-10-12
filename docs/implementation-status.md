# Implementation status

What is built, what is tested, and what is deferred. "Done" means implemented **and** covered by automated tests
that run in CI. See [testing.md](testing.md) for where each suite runs.

Legend: ✅ done · 🟡 in progress · ⏳ planned (later phase) · ➖ deferred (decision recorded)

## Phase 0 — Repository and project foundation ✅

| Item | Status | Notes |
|---|---|---|
| Monorepo layout | ✅ | `apps/windows`, `services/{api,mcp,worker}`, `packages/contracts`, `database`, `tests`, `docs` |
| Windows desktop solution | ✅ | .NET 10, WPF + AvalonEdit, Core/Presentation/App, xunit.v3 on Microsoft.Testing.Platform |
| Backend and MCP projects | ✅ | Fastify API (health, readiness, error envelope, RLS-safe DB access), MCP server (STDIO, no tools yet), worker loop |
| Database migrations | ✅ | Runner with locking and checksums; `0001_core`, `0002_sync`; RLS forced; tested against PostgreSQL 16 |
| Shared API contracts | ✅ | `packages/contracts` schemas and fixtures, verified by both the TypeScript and C# suites |
| Automated tests and CI | ✅ | GitHub Actions: backend (Linux + PostgreSQL), desktop core (Linux), Windows app (build, tests, UI automation, portable zip) |
| Lint, format and build commands | ✅ | Biome, `tsc --noEmit`, `dotnet format --verify-no-changes`, warnings as errors |
| Local setup | ✅ | `scripts/setup.sh` / `setup.ps1`, `docker-compose.yml`, `.env.example`; backend flow verified (migrate, start API, `/readyz` 200 as the restricted role) |

## Phase 1 — Native editor and hierarchical workspace 🟡

| Item | Status | Notes |
|---|---|---|
| SQLite persistence | ✅ | WAL, `synchronous=FULL`, single background writer, versioned schema, FTS5 |
| Autosave | ✅ | 400 ms debounce, 1 s max wait, retries with back-off, ordered saves (Core); wired through the view models |
| Crash recovery | ✅ (Core, view models) | Committed data survives a crash; corrupt databases are restored from the newest good backup; unsaved external-file edits come back from recovery buffers |
| Nested folder tree | ✅ (Core, view models) | Unlimited nesting, stable UUIDv7 ids, fractional ordering, cycle checks, copy, pin, Trash with 30-day purge |
| Local search | ✅ (Core) | FTS5 with sanitized prefix queries, highlights and cancellation; under 200 ms on 10k notes |
| External files | ✅ (Core, view models) | Encoding and line-ending detection, atomic save, import and export; never uploaded |
| Session restore | ✅ (Core, view models) | Tabs, order, pins, caret, selection and scroll per tab; reopen-closed stack |
| Light/dark themes | ✅ | All design tokens, control kit, live switching, follow-Windows mode (verified on Windows CI) |
| Syntax modes | ✅ | Plain text plus 14 highlighted formats, themed per design tokens (verified on Windows CI) |
| View models | ✅ | Tabs, explorer, editor state, find & replace, status bar, settings, shortcuts, toasts, banners |
| Modern main window | 🟡 | Custom title bar, tab strip, status bar, responsive layouts |
| Tabs and keyboard shortcuts | 🟡 | View models done; WPF views in progress |
| Explorer sidebar with drag and drop | 🟡 | View model done; WPF view next |
| Editor (AvalonEdit), find & replace, links | 🟡 | Engine and state done; WPF view next |
| Settings window, single instance, global hotkey, file watcher | 🟡 | In progress |
| End-to-end UI tests | 🟡 | Written against the UI automation contract; merged as the UI lands |

### Deferred within Phase 1 (decided, not forgotten)

| Item | Reason |
|---|---|
| Right-hand context panel (Version History, Agent Activity, Tasks, Document Information) | Its tabs belong to Phases 3–5. Showing them earlier would mean fake content. It arrives with Version History in Phase 3. |
| Daily Notes, Agent Activity and Tasks sidebar sections | Phases 5–6. No placeholder sections are shown. |
| Narrow-layout tab chevrons, minimize to tray, split editor | Polish; the spec defers the split editor explicitly |
| Sign-in screen, Account and Synchronization settings pages | Phase 2. The status bar shows "Local only" and never claims to be synced. |

## Later phases

| Phase | Status | Prepared by Phases 0–1 |
|---|---|---|
| 2 — Google sign-in and cloud sync | ⏳ | Durable outgoing queue with idempotency keys and base revisions, `document_sync_base`, server sync tables and a gap-free event cursor, design in [synchronization.md](synchronization.md) |
| 3 — Version history | ⏳ | Server `document_revisions` with actor attribution and trigger-maintained revision numbers; append-only by design |
| 4 — MCP server and Claude integration | ⏳ | MCP package and STDIO adapter; design in [mcp-integration.md](mcp-integration.md) |
| 5 — Multi-agent task orchestration | ⏳ | Actor kinds and `activity_events`/`audit_log` in place |
| 6 — Daily notes and scheduled workflows | ⏳ | Worker loop with an injectable job source |
| 7 — Performance, reliability and release | ⏳ | Portable build produced by CI today; installer and updates later |

## Known follow-ups

- **Phase 2:** schedule `nimpad.purge_expired_idempotency()` once the API writes idempotency records.
- **Phase 2:** define the `sync_events.event_type` vocabulary and payload schemas before any event is written.
- **Before relying on Supabase's `postgres` role as the migration owner:** verify it has `BYPASSRLS`.
