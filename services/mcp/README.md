# @nimpad/mcp

NimPad's [Model Context Protocol](https://modelcontextprotocol.io) server, built on
`@modelcontextprotocol/sdk`.

## Current state (Phase 0)

- `createNimpadMcpServer()` (`src/server.ts`) returns an `McpServer` that reports `serverInfo`
  `{ name: "nimpad", version: <package version> }`.
- `src/main.ts` is the local **STDIO** adapter: an MCP client starts it as a child process
  (`node services/mcp/src/main.ts`). It exits when the client closes stdin, or on SIGINT/SIGTERM. Diagnostics go
  to stderr, since stdout carries the protocol.
- **No tools, resources or prompts are registered yet**, so the server advertises no capabilities. It completes
  the initialize handshake and answers `ping`; anything else returns "method not found". Nothing is stubbed or
  simulated.

## Planned (Phase 4)

- The note and task tools from the product spec (§6.3), registered here and calling the same service layer as
  the REST API, with the same validation, revision checks, attribution and audit (spec §10).
- Agent identities and per-agent permissions (§6.4–6.5).
- The remote HTTPS transport (`MCP_HOST`/`MCP_PORT` in `.env.example`) next to the STDIO adapter.

## Connecting an MCP client (STDIO)

Configure the client to start the server with Node directly, using the absolute path of your checkout:

```json
{
  "mcpServers": {
    "nimpad": { "command": "node", "args": ["/absolute/path/to/nimpad/services/mcp/src/main.ts"] }
  }
}
```

stdout carries the JSON-RPC protocol and nothing else. Do not use `pnpm --filter @nimpad/mcp start` as the client
command: pnpm prints its `> @nimpad/mcp@0.1.0 start` banner to stdout, which breaks the handshake. If a client
must go through pnpm, use `pnpm --silent --filter @nimpad/mcp start`.

## Commands

```sh
pnpm --filter @nimpad/mcp start      # run the STDIO server by hand (pnpm's banner goes to stdout; see above)
pnpm --filter @nimpad/mcp test       # handshake tests (in-memory and over a real STDIO child process)
pnpm --filter @nimpad/mcp typecheck
```
