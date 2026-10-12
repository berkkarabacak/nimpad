# MCP integration

NimPad exposes its workspace to AI agents such as Claude through a [Model Context Protocol](https://modelcontextprotocol.io)
server, `services/mcp`, built on the official TypeScript SDK (`@modelcontextprotocol/sdk`). Agents never depend
on the Windows UI. They talk to the same service layer as the REST API, with the same validation, revision
checks, attribution and audit (spec §6, §10).

**Status:**
- **Phase 0 (done):** the server package and its local STDIO adapter. It completes the MCP handshake and answers
  `ping`.
- **Phase 4 (not started):** the note tools, the remote HTTPS endpoint and agent authorization.

**No tools are registered yet, and none are faked.** A connected client sees a server named `nimpad` with no
tools.

## Connecting Claude to the local STDIO server

Prerequisites: Node 22.18 or newer, and `pnpm install` run in the repository.

### Claude Code

```sh
claude mcp add nimpad -- node /absolute/path/to/nimpad/services/mcp/src/main.ts
```

### Claude Desktop and other clients that use a JSON config

```json
{
  "mcpServers": {
    "nimpad": {
      "command": "node",
      "args": ["/absolute/path/to/nimpad/services/mcp/src/main.ts"]
    }
  }
}
```

Start the server with `node`, as shown. Running it through `pnpm ... start` makes pnpm print a banner on stdout,
which corrupts the JSON-RPC stream; if you must use pnpm, add `--silent`. The server writes diagnostics to stderr
only, and exits when the client closes stdin.

To verify the connection: `/mcp` in Claude Code lists `nimpad` as connected, with no tools. The handshake is also
covered by `pnpm --filter @nimpad/mcp test`, both in memory and over a real STDIO child process.

## Phase 4 design

### Transports

- **Remote HTTPS** (Streamable HTTP). The primary endpoint, hosted with the API. It works when no desktop app is
  running (spec §6.1). `MCP_HOST` and `MCP_PORT` in `.env.example` are reserved for it.
- **Local STDIO**, the existing adapter, for development and for Claude Code. It authenticates with an agent
  credential taken from the environment.

### Authorization and agent identity

- The remote endpoint uses OAuth-compatible delegated authorization, as the MCP spec requires for HTTP transports.
  A user authorizes a specific agent identity; tokens expire and can be revoked (spec §15).
- Each agent has its own identity and credentials: id, display name, integration type, last heartbeat, allowed
  workspaces and folders, permission level, revocation status (spec §6.4). The `agents` and `agent_credentials`
  tables arrive in the Phase 4 migration. Credentials are stored hashed.
- Permission levels are read-only, read and write, task management, and workspace administration. They can be
  limited to particular workspaces or folders (spec §6.5) and are enforced in the service layer, backed by
  PostgreSQL row-level security. Every denied attempt is written to `audit_log` (Scenario 9).
- High-impact operations (bulk delete, mass restructuring, destructive restore) can require a human approval
  (`approvals`, Phase 5).

### Tools

The tools are exposed progressively, in groups, so clients aren't overwhelmed (spec §6.3).

| Group | Tools |
|---|---|
| Workspace | `list_workspaces`, `get_workspace_tree`, `list_folder_contents` |
| Notes | `search_notes`, `get_note`, `create_note`, `update_note`, `append_to_note`, `move_note`, `get_note_history`, `restore_note_version` |
| Daily notes | `get_or_create_daily_note` (Phase 6) |
| Orchestration | `list_tasks`, `create_task`, `claim_task`, `update_task`, `add_task_comment`, `complete_task`, `get_recent_activity` (Phase 5) |

Write tools follow these rules:
- They require a stable note id, an `expectedRevision` and an `idempotencyKey`, mirroring the desktop's queued
  operations.
- A stale write returns a structured `stale_revision` error that includes the current revision, so the agent
  can re-read and retry, or propose the change for review (Scenario 7).
- Results are structured, with ids, revision numbers, timestamps and errors from the shared `ApiError` codes in
  `packages/contracts`.
- Every committed write creates an identifiable revision attributed to the agent (`actor_kind = 'agent'`,
  `actor_id`), so the desktop's version history shows who changed what (Scenario 4 and Scenario 5).

### Untrusted content

Note text is data, never instructions. Tool results return note content as plain data. Server behavior and
permissions never depend on what a note says, and an agent can never gain privileges because a document tells it
to (spec §15).

### What MCP does not do

MCP gives agents tools. It does not run agents, schedule prompts, or keep them alive. Autonomous and scheduled
work, such as "every morning, prepare my daily brief", needs the Phase 6 runner. That runner has an explicitly
configured model provider, permissions and an execution policy, and tracks cost and failures (spec §7.4, §8).
