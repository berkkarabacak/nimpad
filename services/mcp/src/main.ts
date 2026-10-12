// Local STDIO adapter: `node src/main.ts`, launched by an MCP client (for example Claude Desktop) as a child
// process. stdout carries the protocol, so diagnostics go to stderr only. The process exits when the client
// closes stdin or on SIGINT/SIGTERM.

import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { createNimpadMcpServer, MCP_SERVER_NAME, MCP_SERVER_VERSION } from './server.ts';

function log(message: string): void {
  process.stderr.write(`[${MCP_SERVER_NAME}-mcp] ${message}\n`);
}

const server = createNimpadMcpServer();
const transport = new StdioServerTransport();

let closing = false;
async function shutdown(reason: string): Promise<void> {
  if (closing) {
    return;
  }
  closing = true;
  log(`shutting down (${reason})`);
  try {
    await server.close();
  } catch (error) {
    log(`error while closing: ${error instanceof Error ? error.message : String(error)}`);
    process.exitCode = 1;
  }
  // stdin may still hold the event loop open after a signal; nothing else is pending once the server is closed.
  process.stdin.destroy();
}

process.on('SIGINT', () => void shutdown('SIGINT'));
process.on('SIGTERM', () => void shutdown('SIGTERM'));
process.stdin.on('end', () => void shutdown('stdin closed'));

await server.connect(transport);
log(`version ${MCP_SERVER_VERSION} ready on stdio (no tools until Phase 4)`);
