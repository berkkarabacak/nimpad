# Testing

Every significant feature has automated tests, and CI runs all of them on every pull request. A feature is only
marked done in [implementation-status.md](implementation-status.md) once a test covers it.

## Suites

| Suite | Location | Runs on | Covers |
|---|---|---|---|
| Contracts | `packages/contracts/test` | Linux, Windows dev machines | Schemas, canonical text, checksum, fractional-index ordering, names, formats, note links, pending-operation shapes; checks the committed fixtures match the generator |
| API | `services/api/test` | Linux + PostgreSQL | Config validation, error envelope, request ids, header redaction, body limit, readiness, role safety check, `withUserTx` against a real database (incl. a connection killed mid-transaction), graceful shutdown |
| MCP | `services/mcp/test` | Linux | Handshake in memory and over a real STDIO child process, no capabilities advertised |
| Worker | `services/worker/test` | Linux | Job loop, error isolation, graceful shutdown and signals, using fake timers and real processes |
| Database runner | `database/test` | Linux | Migration ordering, checksums, the advisory lock, BOM handling, the statement splitter |
| Database integration | `tests/database` | Linux + PostgreSQL | Real migrations in a throwaway database per file. See below. |
| Desktop Core | `apps/windows/tests/NimPad.Core.Tests` | Linux and Windows | See below. |
| View models | `apps/windows/tests/NimPad.Presentation.Tests` | Linux and Windows | See below. |
| App (WPF) | `apps/windows/tests/NimPad.App.Tests` | **Windows only** (skipped elsewhere) | Loads every theme dictionary and checks token values against the design. Lays out the design gallery in both themes, checks live theme switching, loads the syntax definitions in both themes with no default colors leaking, runs sample highlighting. Shell and view tests join as the views land. |
| End-to-end | `tests/e2e-windows/NimPad.E2E` | **Windows only** | Launches the real `NimPad.exe` with a temporary profile and drives it through UI Automation (FlaUI). See below. The suite is merged in stages as the UI it targets lands. |

Details for the larger suites:

- **Database integration** checks:
  - Idempotent migrate, and that checksum drift is rejected.
  - Row-level security isolation between users and roles.
  - Viewers cannot write; append-only tables stay append-only.
  - Cycle prevention, including concurrent opposite moves and stale SERIALIZABLE moves.
  - The sync sequence stays gap-free under concurrency and after rollbacks.
  - Revision counters, and that Supabase's `anon`/`authenticated` roles get no privileges.
- **Desktop Core** checks:
  - Storage pragmas and FTS5, startup recovery from corrupt files, the single writer's transaction semantics.
  - Every tree operation, and queue coalescing replayed against a model server.
  - Autosave timing (fake clock), search sanitizing and cancellation.
  - External-file encodings, line endings and atomic saves.
  - Every shared contract fixture.
  - The Phase 1 acceptance scenario.
- **View models** run against real Core services on a temporary data directory, with a test UI thread and a fake
  clock. They check:
  - Tab lifecycle, session restore and crash recovery.
  - Explorer flattening and live updates, including 10k nodes.
  - Find and replace, status texts, toasts and banners, settings.
  - Keyboard commands and shutdown.

Other checks run in CI: Biome (lint and format) and `tsc --noEmit` for TypeScript; `dotnet format --verify-no-changes`
and warnings-as-errors builds for .NET.

## Running

```sh
# TypeScript and database (PostgreSQL must be reachable; see .env.example)
pnpm lint && pnpm typecheck && pnpm test

# .NET (any OS)
dotnet test --project apps/windows/tests/NimPad.Core.Tests
dotnet test --project apps/windows/tests/NimPad.Presentation.Tests
dotnet test --project apps/windows/tests/NimPad.Core.Tests -- --filter-class NimPad.Core.Tests.Phase1AcceptanceTests

# Windows only
dotnet test --project apps/windows/tests/NimPad.App.Tests
dotnet build apps/windows/NimPad.slnx -c Release
dotnet test --project tests/e2e-windows/NimPad.E2E -c Release   # see tests/e2e-windows/README.md
```

### Database tests

- **Connections.** They use `DATABASE_URL_ADMIN`, an owner connection to the `postgres` database used to create
  and drop a throwaway database per test file, and `DATABASE_URL_TEST`.
- **When PostgreSQL is unreachable:**
  - Normally they **skip**, printing the reason with the password masked.
  - With `NIMPAD_REQUIRE_DB=1` (set in CI) they **fail** instead, so CI can never pass by skipping.
- **Cleanup.** They create their own login roles and drop them afterwards.

### End-to-end tests

The FlaUI suite needs an interactive Windows desktop.
- It launches `NIMPAD_EXE` (or the newest local build) with `--data-dir <temp> --no-single-instance --no-hotkey`,
  so it never touches your real notes.
- On failure it writes screenshots, a UI tree dump and the app's logs to `NIMPAD_E2E_ARTIFACTS`. CI uploads these
  as the `e2e-artifacts` artifact.
- The tests rely on the UI automation contract (AutomationIds and names) described in `tests/e2e-windows/README.md`.

## Acceptance coverage (Phase 1)

| Requirement | Covered by |
|---|---|
| Hundreds of notes in nested folders survive close and reopen without internet | `Phase1AcceptanceTests` (500 notes, 6 levels, compared against an independent record after reopen and after a simulated crash). Plus the end-to-end hierarchy and restart tests. |
| Scenario 1: instant local editing offline | End-to-end launch/type/restore tests. The app never touches the network in Phase 1. |
| Scenario 8: crash recovery | `Phase1AcceptanceTests` crash case, `ShutdownTests` (recovered external-file text), end-to-end kill-and-relaunch test |
| Autosave durable within 1 s | `AutosaveSchedulerTests` (400 ms debounce, 1 s max wait), WAL + `synchronous=FULL` checked in `LocalDatabaseTests` |
| Search under 200 ms over 10k notes | `SearchServiceTests` benchmark (median of 20 queries) |
| Tree navigation over large folders | 10k-node tree load (Core) and explorer flattening (view models) |

## Where things are verified

The development environment for this work is Linux:
- **Runs locally:** the TypeScript suites, the database suites (against PostgreSQL 16), the Core and view-model
  suites, and every build including the WPF app (it compiles via `EnableWindowsTargeting`).
- **Runs only on the CI `windows-latest` runner:** the WPF App tests and the end-to-end UI tests, because they need
  Windows.
- **Manual verification:** nothing has been checked by hand beyond what these tests cover. On a Windows machine,
  start with `dotnet run --project apps/windows/src/NimPad.App`.

## Policy

- No test is skipped, disabled or weakened to get a green build. Platform-gated tests skip only where the platform
  can't run them (WPF on Linux), and CI runs them on Windows.
- A bug fix comes with a test that fails without it. Reviews in this project re-ran each fix's test against the
  reverted code.
- Timing-sensitive tests use fake clocks (`FakeTimeProvider`, vitest fake timers). A wall-clock performance
  assertion takes the best of several runs.
