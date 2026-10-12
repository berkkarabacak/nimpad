// Runs src/main.ts as a real process: startup, serving, and graceful shutdown on signals. Most tests need no
// database (the pool connects lazily and an unreachable database is not fatal at startup); the ones that do are
// gated like db.test.ts.

import { type ChildProcess, spawn } from 'node:child_process';
import { once } from 'node:events';
import { connect } from 'node:net';
import { fileURLToPath } from 'node:url';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { createTestDatabase, databaseGate, type TestDatabase } from './pg-harness.ts';

const serviceDir = fileURLToPath(new URL('..', import.meta.url));
const children: ChildProcess[] = [];
const UNREACHABLE_DATABASE_URL = 'postgres://nimpad_app:nimpad_app@127.0.0.1:1/nimpad';

const { unreachable, skipReason } = await databaseGate();

interface Running {
  child: ChildProcess;
  output: () => string;
  /** Resolves with the exit code once the process has exited and its stdout and stderr are drained. */
  closed: Promise<number | null>;
}

interface Started extends Running {
  baseUrl: string;
}

function run(env: Record<string, string>): Running {
  const child = spawn(process.execPath, ['src/main.ts'], {
    cwd: serviceDir,
    env: { PATH: process.env.PATH ?? '', ...env },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  children.push(child);
  let output = '';
  child.stdout?.on('data', (chunk: Buffer) => {
    output += chunk.toString('utf8');
  });
  child.stderr?.on('data', (chunk: Buffer) => {
    output += chunk.toString('utf8');
  });
  // 'close' (unlike 'exit') fires only after the stdio streams have ended, so the output is complete.
  const closed = once(child, 'close').then(([code]) => code as number | null);
  return { child, output: () => output, closed };
}

async function waitForOutput(running: Running, pattern: RegExp, what: string): Promise<RegExpExecArray> {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    const match = pattern.exec(running.output());
    if (match !== null) {
      return match;
    }
    if (running.child.exitCode !== null) {
      break;
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error(`Timed out waiting for ${what}:\n${running.output()}`);
}

async function start(databaseUrl = UNREACHABLE_DATABASE_URL): Promise<Started> {
  const running = run({ API_HOST: '127.0.0.1', API_PORT: '0', LOG_LEVEL: 'info', DATABASE_URL: databaseUrl });
  const match = await waitForOutput(running, /Server listening at (http:\/\/127\.0\.0\.1:\d+)/, 'startup');
  return { ...running, baseUrl: match[1] as string };
}

afterEach(() => {
  for (const child of children.splice(0)) {
    if (child.exitCode === null && child.signalCode === null) {
      child.kill('SIGKILL');
    }
  }
});

describe('main.ts', () => {
  it.each(['SIGTERM', 'SIGINT'] as const)(
    'serves requests and exits 0 after %s',
    async (signal) => {
      const { child, baseUrl, output, closed } = await start();
      const response = await fetch(`${baseUrl}/healthz`);
      expect(response.status).toBe(200);
      expect(await response.json()).toMatchObject({ status: 'ok' });

      child.kill(signal);
      expect(await closed).toBe(0);
      expect(output()).toContain('database not reachable at startup');
      expect(output()).toContain('"msg":"shutting down"');
      expect(output()).toContain('"msg":"shutdown complete"');
    },
    20_000,
  );

  it('exits 1 at once on a second signal while a request holds the shutdown open', async () => {
    const started = await start();
    const { port } = new URL(started.baseUrl);
    // A request whose headers never finish keeps its connection busy, so close() waits for it.
    const socket = connect(Number(port), '127.0.0.1');
    await once(socket, 'connect');
    socket.write('GET /healthz HTTP/1.1\r\nHost: localhost\r\n');
    try {
      started.child.kill('SIGTERM');
      await waitForOutput(started, /"msg":"shutting down"/, 'the first signal');
      const signalled = Date.now();
      started.child.kill('SIGINT');
      expect(await started.closed).toBe(1);
      // At once: not via the 10 s forced exit, which would also exit 1.
      expect(Date.now() - signalled).toBeLessThan(3_000);
      expect(started.output()).toContain('second signal received; exiting immediately');
      expect(started.output()).not.toContain('graceful shutdown timed out');
      expect(started.output()).not.toContain('"msg":"shutdown complete"');
    } finally {
      socket.destroy();
    }
  }, 20_000);

  it('exits 1 with a readable message when the configuration is invalid', async () => {
    const { output, closed } = run({ API_PORT: 'eighty' });
    expect(await closed).toBe(1);
    expect(output()).toContain('Invalid API configuration:');
    expect(output()).toContain('API_PORT: must be an integer from 0 to 65535');
    expect(output()).toContain('DATABASE_URL: is required');
  }, 20_000);
});

describe('main.ts with PostgreSQL', () => {
  let database: TestDatabase | undefined;

  beforeAll(async () => {
    if (unreachable === null) {
      database = await createTestDatabase([]);
    }
  }, 30_000);

  afterAll(async () => {
    await database?.drop();
  }, 30_000);

  function db(): TestDatabase {
    if (database === undefined) {
      throw new Error('no test database');
    }
    return database;
  }

  it('refuses to start when the database role bypasses row-level security', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const { output, closed } = run({ API_PORT: '0', DATABASE_URL: db().ownerUrl });
    expect(await closed).toBe(1);
    expect(output()).toContain('refusing to start: the database role bypasses row-level security');
    expect(output()).toContain('superuser');
    expect(output()).not.toContain('Server listening');
  }, 20_000);

  it('is ready under the app role, and exits promptly after SIGTERM with a pooled connection open', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const { child, baseUrl, output, closed } = await start(db().appUrl);
    expect(output()).not.toContain('database not reachable at startup');
    const response = await fetch(`${baseUrl}/readyz`);
    expect(response.status).toBe(200);
    // The pool now holds an idle connection, which would keep the process alive for idleTimeoutMillis (30 s)
    // unless shutdown closes the pool.
    const signalled = Date.now();
    child.kill('SIGTERM');
    expect(await closed).toBe(0);
    expect(Date.now() - signalled).toBeLessThan(5_000);
    expect(output()).toContain('"msg":"shutdown complete"');
  }, 20_000);
});
