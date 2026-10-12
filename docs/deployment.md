# Deployment

This document covers running the cloud services and distributing the Windows app. For local development, see the
[README](../README.md).

**Status:** the services are deployable skeletons today.
- The API serves health and readiness checks.
- The MCP server runs as a STDIO adapter.
- The worker idles until the Phase 6 job queue exists.

Production hosting, Google OAuth and Supabase configuration arrive with Phase 2. The placeholders are in
`.env.example`.

## Cloud services

### Requirements

- Node.js 22.18 or newer. The services run their TypeScript directly with Node's built-in type stripping, so
  there is no build step.
- PostgreSQL 16, either Supabase or plain PostgreSQL. The SQL avoids Supabase-only functions.
- pnpm 10: `pnpm install --frozen-lockfile --prod` installs the runtime dependencies.

### Environment variables

| Variable | Used by | Default | Notes |
|---|---|---|---|
| `DATABASE_URL` | api | required | Runtime connection. Must be a **non-owner** role that is a member of `nimpad_app` (see below). |
| `API_HOST` / `API_PORT` | api | `127.0.0.1` / `8787` | Bind address. Put TLS in front (reverse proxy or platform router). |
| `LOG_LEVEL` | api, worker | `info` | `fatal`, `error`, `warn`, `info`, `debug`, `trace` or `silent`. Logs are JSON on stdout; authorization, cookie and API-key headers are redacted. |
| `BODY_LIMIT_BYTES` | api | `1048576` | Largest accepted request body. |
| `WORKER_POLL_INTERVAL_MS` / `WORKER_SHUTDOWN_TIMEOUT_MS` | worker | `1000` / `30000` | |
| `DATABASE_URL_MIGRATE` | migrations | required | Owner connection used only by `pnpm db:migrate`. |
| `SUPABASE_URL`, `SUPABASE_ANON_KEY`, `SUPABASE_JWKS_URL`, `GOOGLE_CLIENT_ID` | api (Phase 2) | — | Placeholders; unused today. |

Never commit real values. Use the platform's secret store.

### Database setup

1. **Create the database with UTF8 encoding** (Supabase databases already are).
2. **Run the migrations as the schema owner:**

   ```sh
   DATABASE_URL_MIGRATE=postgres://<owner>@<host>:5432/<db> pnpm db:migrate
   pnpm db:migrate -- --status    # shows applied and pending files
   ```

   - The owner must be a superuser or have `BYPASSRLS`, because the migrations create policies and roles.
   - Use a **direct or session-mode** connection, never a transaction-mode pooler. The runner holds a
     session-level advisory lock so that concurrent deploys serialize.
   - Applied files are checksummed. Editing a migration that has already been applied makes the runner refuse to
     continue; add a new migration instead.
3. **Create the API's login role.** The migrations create `nimpad_app` as a group role without `LOGIN` and
   without `BYPASSRLS`. Give the API its own login role:

   ```sql
   CREATE ROLE nimpad_api LOGIN PASSWORD '<generated secret>';
   GRANT nimpad_app TO nimpad_api;
   ```

   - At startup the API refuses to run as a superuser, as a `BYPASSRLS` role, or as a role without
     `nimpad_app`'s privileges, so a misconfiguration cannot silently disable row-level security.
   - `NIMPAD_APP_DB_PASSWORD` is for local development only: it gives `nimpad_app` itself a login. It refuses any
     database that isn't on the local machine.
4. **Supabase specifics:**
   - Run the migrations over the direct connection, port 5432.
   - The API can use Supavisor in transaction mode. `withUserTx` scopes the user id with
     `set_config(..., true)`, so it never leaks between pooled clients.
   - The migrations revoke every privilege on the `nimpad` schema from the `anon` and `authenticated` roles, so the
     Supabase Data API cannot reach the tables.
   - Not yet verified: whether Supabase's `postgres` role has `BYPASSRLS`. Check this before relying on it as the
     migration owner.

### Running the services

```sh
pnpm --filter @nimpad/api start       # node services/api/src/main.ts
pnpm --filter @nimpad/worker start
node services/mcp/src/main.ts         # STDIO adapter, started by an MCP client (see mcp-integration.md)
```

- **Health and readiness:**
  - `GET /healthz` is a liveness check that never touches the database.
  - `GET /readyz` returns 200 when the database answers within 2 s, and 503 `unavailable` otherwise. Point the
    load balancer at `/readyz`.
- **Shutdown:**
  - On SIGTERM, the API stops accepting connections, finishes in-flight requests, closes the pool and exits. It
    forces an exit after 10 s, and a second signal exits immediately.
  - The worker finishes its current job first, up to `WORKER_SHUTDOWN_TIMEOUT_MS`.
- **Errors:** every response uses the `ApiError` envelope from `packages/contracts`, with no stack traces.
  `x-request-id` is honored or generated, and echoed in responses and logs.

### Backups and recovery (server)

- Use the provider's point-in-time recovery (Supabase PITR), or scheduled `pg_dump` with off-site retention for
  plain PostgreSQL.
- `document_revisions` and `audit_log` are append-only by design, and revision history is the user-facing
  recovery path. Database backups protect against losing the whole database.

## Windows app

### Builds produced by CI

Every CI run publishes a self-contained portable build for x64, called `NimPad-portable-win-x64`. It is built
ReadyToRun and needs no installed .NET runtime.
- To use it, unzip it anywhere and run `NimPad.exe`.
- The zip contains a `portable.txt` marker, so all data stays in a `data\` folder next to the executable.

An installer (MSIX or MSI), code signing and in-app updates are Phase 7 work.

### Where the desktop keeps its data

| What | Installed | Portable |
|---|---|---|
| Database | `%LOCALAPPDATA%\NimPad\profiles\local\nimpad.db` | `.\data\profiles\local\nimpad.db` |
| Backups (daily, 7 kept) | `…\profiles\local\backups\` | `.\data\profiles\local\backups\` |
| Logs (no note contents) | `%LOCALAPPDATA%\NimPad\logs\` | `.\data\logs\` |

`--data-dir <path>` overrides the location. The UI tests use it.

### Desktop recovery

- **Crash.** Every committed save survives: the database runs in WAL mode with `synchronous=FULL`. Unsaved edits
  to external files are restored from recovery buffers on the next start.
- **Corrupt database.** At startup a `quick_check` failure, or a "not a database" error, does three things:
  - moves the file aside as `nimpad.corrupt-<timestamp>.db`;
  - restores the newest backup that passes its own check;
  - explains what happened in a banner.
- **Locked database.** A database locked by another process fails loudly and is never moved aside.
- **Manual restore.** Close NimPad, then copy a file from `backups\` over `nimpad.db` after deleting
  `nimpad.db-wal` and `nimpad.db-shm`.
