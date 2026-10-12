import { type ChildProcess, spawn } from 'node:child_process';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { LATEST_PROTOCOL_VERSION } from '@modelcontextprotocol/sdk/types.js';
import { afterEach, describe, expect, it } from 'vitest';
import packageJson from '../package.json' with { type: 'json' };
import { createNimpadMcpServer, MCP_SERVER_NAME, MCP_SERVER_VERSION } from '../src/server.ts';

const serviceDir = fileURLToPath(new URL('..', import.meta.url));
const children: ChildProcess[] = [];

// A failed assertion must not leave a server running (its stdin pipe would keep it alive until vitest exits).
afterEach(() => {
  for (const child of children.splice(0)) {
    if (child.exitCode === null && child.signalCode === null) {
      child.kill('SIGKILL');
    }
  }
});

describe('createNimpadMcpServer (in-memory transport)', () => {
  async function connect() {
    const server = createNimpadMcpServer();
    const client = new Client({ name: 'nimpad-test', version: '0.0.0' });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);
    return { server, client };
  }

  it('completes initialize and reports serverInfo', async () => {
    const { server, client } = await connect();
    try {
      expect(MCP_SERVER_NAME).toBe('nimpad');
      expect(MCP_SERVER_VERSION).toBe(packageJson.version);
      expect(client.getServerVersion()).toMatchObject({ name: 'nimpad', version: packageJson.version });
      await expect(client.ping()).resolves.toEqual({});
    } finally {
      await client.close();
      await server.close();
    }
  });

  it('registers no tools, resources or prompts yet (they arrive in Phase 4)', async () => {
    const { server, client } = await connect();
    try {
      const capabilities = client.getServerCapabilities() ?? {};
      expect(capabilities.tools).toBeUndefined();
      expect(capabilities.resources).toBeUndefined();
      expect(capabilities.prompts).toBeUndefined();
      // Not advertised and not handled: the server answers "method not found" rather than a fake empty list.
      await expect(client.listTools()).rejects.toThrow(/Method not found/i);
    } finally {
      await client.close();
      await server.close();
    }
  });
});

describe('src/main.ts (STDIO adapter)', () => {
  it('completes the handshake with the SDK client over a real child process', async () => {
    const transport = new StdioClientTransport({
      command: process.execPath,
      args: ['src/main.ts'],
      cwd: serviceDir,
      stderr: 'pipe',
    });
    const client = new Client({ name: 'nimpad-test', version: '0.0.0' });
    try {
      await client.connect(transport);
      expect(client.getServerVersion()).toMatchObject({ name: 'nimpad', version: packageJson.version });
      await expect(client.ping()).resolves.toEqual({});
    } finally {
      // Closing the transport also ends the child process, even when connect() failed.
      await client.close();
      await transport.close();
    }
  }, 20_000);

  it.each(['stdin closed', 'SIGTERM', 'SIGINT'] as const)(
    'answers initialize on stdout only and exits 0 when %s',
    async (ending) => {
      const child = spawn(process.execPath, ['src/main.ts'], {
        cwd: serviceDir,
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      children.push(child);
      // 'close' (unlike 'exit') fires only after stdout and stderr have ended, so stderr below is complete.
      const closed = once(child, 'close');
      let stderr = '';
      child.stderr.on('data', (chunk: Buffer) => {
        stderr += chunk.toString('utf8');
      });
      const lines = createInterface({ input: child.stdout })[Symbol.asyncIterator]();
      child.stdin.write(
        `${JSON.stringify({
          jsonrpc: '2.0',
          id: 1,
          method: 'initialize',
          params: {
            protocolVersion: LATEST_PROTOCOL_VERSION,
            capabilities: {},
            clientInfo: { name: 'raw-test', version: '0.0.0' },
          },
        })}\n`,
      );
      const first = await lines.next();
      const response = JSON.parse(first.value as string);
      expect(response).toMatchObject({
        jsonrpc: '2.0',
        id: 1,
        result: { serverInfo: { name: 'nimpad', version: packageJson.version } },
      });

      if (ending === 'stdin closed') {
        child.stdin.end();
      } else {
        child.kill(ending);
      }
      const [code, signal] = (await closed) as [number | null, NodeJS.Signals | null];
      expect({ code, signal }).toEqual({ code: 0, signal: null });
      // Nothing but protocol messages on stdout.
      const rest: string[] = [];
      for (let next = await lines.next(); !next.done; next = await lines.next()) {
        rest.push(next.value);
      }
      expect(rest).toEqual([]);
      expect(stderr).toContain('ready on stdio');
      expect(stderr).toContain(`shutting down (${ending})`);
    },
    20_000,
  );
});
