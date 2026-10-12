# NimPad architecture

NimPad is a local-first Windows editor backed by a cloud service and an AI-agent integration layer. This document
records the structure and the cross-component contracts. The product requirements are in
[product-spec.md](product-spec.md); the current state of each feature is in
[implementation-status.md](implementation-status.md).

## Components

```
┌──────────────── Windows app (apps/windows) ─────────────────┐
│ NimPad.App (WPF, AvalonEdit)       views, themes, input     │
│        │ binds to                                           │
│ NimPad.Presentation (net10.0)      view models, no WPF refs │
│        │ calls                                              │
│ NimPad.Core (net10.0)              domain, services,        │
│                                    SQLite storage, op queue │
└────────┬────────────────────────────────────────────────────┘
         │ HTTPS (Phase 2: sync engine drains pending_operations)
┌────────▼──────────── NimPad Cloud ──────────────────────────┐
│ services/api (Fastify)   REST, revision checks, authz       │
│ services/mcp             MCP server (remote HTTPS + STDIO)  │
│ services/worker          DB-backed jobs, scheduler          │
│ packages/contracts       zod schemas shared by all of them  │
│ database/migrations      PostgreSQL (Supabase-compatible)   │
└─────────────────────────────────────────────────────────────┘
```

Dependency rules:
- `NimPad.Core` references no UI framework. `NimPad.Presentation` references Core and CommunityToolkit.Mvvm only,
  never WPF. Platform services (dispatcher, clipboard, file dialogs, editor buffer, clock) are interfaces in
  Presentation that the App implements. This keeps Core and Presentation unit-testable on any OS.
- `NimPad.App` contains only views, WPF-specific services and composition (`App.xaml.cs` wires everything).
- TypeScript services depend on `@nimpad/contracts`; they never import each other.
- Every mutation, whether from a person or an agent, goes through the same service layer, with the same validation,
  revision checks, attribution and audit. Nothing writes tables directly (spec §10).

## Local-first data flow (desktop)

1. The user types. AvalonEdit updates its `TextDocument` immediately; the UI never waits on I/O.
2. `AutosaveScheduler` debounces (400 ms after the last change, at most 1000 ms after the first unsaved change).
   It then takes a snapshot on the UI thread and hands it to `DocumentService`.
3. `DocumentService` submits a job to the **single background writer**: one SQLite connection on a dedicated
   thread, fed by a channel.
4. The writer runs one `BEGIN IMMEDIATE` transaction that updates `document_contents`, the FTS index, links, and
   `pending_operations`, then commits. The database uses WAL and `synchronous=FULL`, so a commit is durable.
5. Reads (tree, search, previews) use separate pooled read connections, off the UI thread.
6. Phase 2 adds a sync engine that drains `pending_operations` to the API with idempotency keys and expected
   revisions, and pulls server changes via a `sync_events` cursor.

Phase 1 never drains the queue. The status bar says **"Local only"**: no sync is simulated.

## Identity, time and content contracts

These rules are shared by C#, TypeScript and SQL. The fixtures in `packages/contracts/fixtures/` are the
executable form of this section; both the C# and TS test suites must pass against them.

- **IDs:**
  - UUID version 7, serialized as lowercase hyphenated text (`0192f0c4-…`).
  - Clients generate IDs for new nodes; the server accepts client-generated IDs.
  - SQLite stores IDs as `TEXT`. In C#, always bind them as strings (`id.ToString("D")`), never as `Guid`
    parameters.
- **Timestamps:**
  - UTC throughout.
  - Locally: ISO-8601 text with milliseconds and a `Z` suffix (`2026-10-12T13:59:43.123Z`). On the server:
    `timestamptz`.
- **Canonical note text:**
  - Managed notes are stored with `\n` line endings. `\r\n` and lone `\r` are normalized on save; external files
    keep their own endings.
  - Text is a .NET/JS string; when serialized it is UTF-8, never with a BOM.
- **Content checksum:** `sha256:` + the lowercase hex SHA-256 of the canonical text's UTF-8 bytes.
- **Sibling ordering:** every node has a `position` key, and siblings sort by `(position, id)` using ordinal
  string comparison.
  - Keys are produced by the fractional-indexing algorithm from rocicorp's `fractional-indexing`
    (`generateKeyBetween(a, b)`), with the base-62 digit set
    `0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz`.
  - Inserting between two siblings never renumbers the others, which keeps sync operations small.
  - C# and TS implementations must produce identical keys for the fixture cases.
- **Node kinds:** `folder` and `document`. Only folders have children.
- **Formats:**
  - Supported values: `plain`, `markdown`, `json`, `xml`, `yaml`, `python`, `groovy`, `javascript`, `typescript`,
    `csharp`, `sql`, `powershell`, `ini`, `css`, `html`.
  - The format is detected from the note name's extension (`config.json` → `json`), else `plain`.
  - It can be overridden from the status bar.

## Pending operation queue (local → server)

Each local mutation appends to `pending_operations` in the same transaction:

| column | meaning |
|---|---|
| `seq` | `INTEGER PRIMARY KEY AUTOINCREMENT`; the push order |
| `op_id` | UUIDv7; the idempotency key sent to the server |
| `workspace_id`, `node_id` | target |
| `kind` | `create`, `update_content`, `rename`, `move`, `pin`, `delete`, `restore`, `purge` |
| `payload` | JSON (shapes defined by `PendingOperationPayload` in contracts) |
| `base_meta_rev`, `base_content_rev` | server revisions the change was made against (NULL when never synced) |
| `device_id` | this installation |
| `actor_kind` | `user`, `import` or `system` (agents act only through the server) |
| `created_at`, `attempts`, `last_error` | bookkeeping |

Coalescing keeps the queue bounded:
- There is at most one `update_content` per node. Replacing it keeps the original base revision, and the payload
  carries only the checksum; the push reads the current text.
- An update or rename folds into a still-pending `create` of the same node.
- If a node that never reached the server is deleted and then purged, its pending operations are dropped.

Phase 2 three-way merge uses `document_sync_base`, the last text acknowledged by the server; it is empty until
sync exists.

## Server data model

All tables live in the PostgreSQL schema `nimpad`.

- Migrations are plain SQL in `database/migrations`, applied by `database/src/migrate.ts`. The runner takes an
  advisory lock, runs each file in its own transaction, and records checksums.
- Migrations are added per phase. 0001 holds the core document model; 0002 holds sync and devices. Agents, tasks,
  approvals, webhooks and agent runs arrive with Phases 4–6.
- Row-level security:
  - Every table has `ENABLE` and `FORCE ROW LEVEL SECURITY`.
  - Policies call `nimpad.current_user_id()`, which reads `current_setting('app.user_id', true)`.
  - The API sets that value with `set_config('app.user_id', $1, true)` inside each transaction (helper:
    `withUserTx`), so it never leaks across pooled connections.
  - The same SQL runs on plain PostgreSQL and on Supabase. It deliberately avoids `auth.uid()`.
- The API connects as `nimpad_app`, a non-owner role without `BYPASSRLS`. Migrations run as the owner.
- `anon` and `authenticated` (Supabase roles) get no privileges on the `nimpad` schema, so the Supabase Data API
  cannot expose it.
- Sync cursor: each workspace has an `event_seq` counter, incremented inside the same transaction that writes a
  `sync_events` row. That makes `(workspace_id, seq)` gap-free in commit order, which is what clients page by.
- Folder moves take `pg_advisory_xact_lock` on the workspace and then run a cycle check, so concurrent opposite
  moves cannot create a cycle.

## Desktop threading model

- **UI thread:**
  - WPF.
  - All view-model state changes happen here, and AvalonEdit documents belong to it.
  - `TextDocument.CreateSnapshot()` is taken here; the snapshot is immutable and safe to read elsewhere.
- **Writer thread:** owns the only write connection. Callers `await` the job; failures surface as exceptions,
  keeping the note dirty and raising an error banner.
- **Readers:** pooled connections used from the thread pool.
- Large file loads build the `TextDocument` off-thread and transfer ownership with `SetOwnerThread`.

## Storage locations

| What | Where |
|---|---|
| Database | `%LOCALAPPDATA%\NimPad\profiles\local\nimpad.db` (Phase 2 adds `profiles\{userId}`) |
| Backups | `%LOCALAPPDATA%\NimPad\profiles\local\backups\nimpad-YYYYMMDD.db` (7 kept) |
| Logs | `%LOCALAPPDATA%\NimPad\logs\nimpad-YYYYMMDD.log` |
| Portable mode | a `portable.txt` file next to `NimPad.exe` puts all of the above under `.\data\` |
| Override | `--data-dir <path>`, used by the tests |

## Security notes (Phase 1 scope)

- External files are never uploaded. Importing one copies it into a managed note through an explicit command.
- The single-instance pipe name includes the user SID, and its ACL is restricted to the current user.
- No secrets ship in the desktop app. Server configuration comes from the environment (`.env.example` lists every
  variable).
