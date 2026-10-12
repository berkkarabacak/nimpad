import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import packageJson from '../package.json' with { type: 'json' };

/** The name NimPad's MCP server reports in `serverInfo` during initialization. */
export const MCP_SERVER_NAME = 'nimpad';

/** The server version reported in `serverInfo`: the package version. */
export const MCP_SERVER_VERSION: string = packageJson.version;

/**
 * Creates NimPad's MCP server, not yet connected to a transport.
 *
 * It registers no tools, resources or prompts yet: the note and task tools arrive in Phase 4 together with
 * agent identities and permissions, and they will call the same service layer as the REST API (spec §10).
 * Until then the server completes the MCP handshake and answers `ping`, and advertises no capabilities.
 */
export function createNimpadMcpServer(): McpServer {
  return new McpServer({ name: MCP_SERVER_NAME, title: 'NimPad', version: MCP_SERVER_VERSION });
}
