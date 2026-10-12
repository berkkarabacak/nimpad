# NimPad

A fast, local-first text editor for Windows. Notes are organized in nested folders and kept in version history.
They are built to sync between your computers and to be shared with AI agents such as Claude through an API and
an MCP server.

> **Status:** Phase 0 (foundation) is complete and Phase 1 (offline editor) is in progress. See
> [docs/implementation-status.md](docs/implementation-status.md) for exactly what works today. Cloud sync,
> sign-in, version history UI and the MCP note tools are later phases ([product spec §12](docs/product-spec.md)).

## Repository layout

| Path | What |
|---|---|
| `apps/windows` | The Windows app: `NimPad.Core` (storage, services), `NimPad.Presentation` (view models), `NimPad.App` (WPF + AvalonEdit), and their tests |
| `services/api` | Cloud REST API (Fastify) |
| `services/mcp` | MCP server for AI agents ([docs/mcp-integration.md](docs/mcp-integration.md)) |
| `services/worker` | Background job runner and scheduler |
| `packages/contracts` | Shared schemas and the content and ordering rules, with fixtures the C# side also tests against |
| `database` | PostgreSQL migrations and the migration runner |
| `tests/database`, `tests/e2e-windows` | Database integration tests and Windows UI automation tests |
| `docs` | Product spec, architecture, design, plans and runbooks |

## Prerequisites

- **Node.js 22.18+** (`.nvmrc`) and **pnpm 10**. The services run TypeScript natively, so there is no build step.
- The **.NET 10 SDK**; `global.json` pins the version. The setup scripts install it if it's missing.
- **PostgreSQL 16**: `docker compose up -d db` works, or point `.env` at any local PostgreSQL.
- **Windows 10/11** to run the desktop app. On Linux and macOS it still compiles and its Core and view-model
  tests run.

## Setup

```sh
./scripts/setup.sh          # Linux/macOS
pwsh ./scripts/setup.ps1    # Windows
```

The script checks the prerequisites, installs dependencies, builds the desktop solution, creates `.env` from
[`.env.example`](.env.example), starts PostgreSQL (when Docker is available) and runs the migrations. Pass
`--no-db` / `-NoDb` to skip the database steps.

## Run

```sh
# Desktop app (Windows)
dotnet run --project apps/windows/src/NimPad.App
dotnet run --project apps/windows/src/NimPad.App -- --data-dir C:\temp\nimpad-try   # throwaway profile

# Local backend
pnpm db:migrate             # apply migrations (idempotent); `pnpm db:migrate --status` lists them
pnpm dev:api                # http://127.0.0.1:8787/healthz and /readyz
pnpm dev:worker
node services/mcp/src/main.ts   # MCP STDIO server; normally started by an MCP client
```

CI also publishes a self-contained portable build of the app (`NimPad-portable-win-x64`). Unzip it and run
`NimPad.exe`; it keeps its data next to the executable.

### Connect Claude (MCP)

```sh
claude mcp add nimpad -- node /absolute/path/to/nimpad/services/mcp/src/main.ts
```

The server connects today but exposes no tools yet; the note tools arrive in Phase 4. See
[docs/mcp-integration.md](docs/mcp-integration.md).

## Test and lint

```sh
pnpm lint && pnpm typecheck && pnpm test                       # TypeScript + database (needs PostgreSQL)
dotnet format apps/windows/NimPad.slnx --verify-no-changes
dotnet build apps/windows/NimPad.slnx
dotnet test --project apps/windows/tests/NimPad.Core.Tests
dotnet test --project apps/windows/tests/NimPad.Presentation.Tests
dotnet test --project apps/windows/tests/NimPad.App.Tests      # Windows only
dotnet test --project tests/e2e-windows/NimPad.E2E             # Windows only; drives the real app
```

[docs/testing.md](docs/testing.md) explains what each suite covers and where it runs. CI runs everything on every
pull request: TypeScript and PostgreSQL on Linux, the desktop core on Linux, and the app and its UI tests on Windows.

## Documentation

| Document | Contents |
|---|---|
| [product-spec.md](docs/product-spec.md) | The product specification (source of truth) |
| [architecture.md](docs/architecture.md) | Components, layering and cross-component contracts |
| [implementation-plan.md](docs/implementation-plan.md) | The engineering plan for the current phases |
| [implementation-status.md](docs/implementation-status.md) | What is built, tested and deferred |
| [design/](docs/design/README.md) | The approved design deck, page index and tokens |
| [synchronization.md](docs/synchronization.md) | Local-first storage and the sync design |
| [mcp-integration.md](docs/mcp-integration.md) | Connecting agents; the MCP design |
| [deployment.md](docs/deployment.md) | Running the services, database roles, Windows builds, recovery |
| [testing.md](docs/testing.md) | Test suites and how to run them |
