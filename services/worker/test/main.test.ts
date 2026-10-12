// Runs src/main.ts as a real process to check startup and graceful shutdown.

import { type ChildProcess, spawn } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it } from 'vitest';

const serviceDir = fileURLToPath(new URL('..', import.meta.url));
const children: ChildProcess[] = [];

function run(env: Record<string, string>): {
  child: ChildProcess;
  output: () => string;
  closed: Promise<number | null>;
} {
  const child = spawn(process.execPath, ['src/main.ts'], {
    cwd: serviceDir,
    env: { PATH: process.env.PATH ?? '', ...env },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  children.push(child);
  let output = '';
  const append = (chunk: Buffer): void => {
    output += chunk.toString('utf8');
  };
  child.stdout?.on('data', append);
  child.stderr?.on('data', append);
  // 'close' (unlike 'exit') fires only after the stdio streams have ended, so the output is complete.
  const closed = once(child, 'close').then(([code]) => code as number | null);
  return { child, output: () => output, closed };
}

async function waitFor(predicate: () => boolean, what: string, output: () => string): Promise<void> {
  const deadline = Date.now() + 10_000;
  while (!predicate()) {
    if (Date.now() > deadline) {
      throw new Error(`Timed out waiting for ${what}:\n${output()}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
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
    'idles without a job source and exits 0 on %s',
    async (signal) => {
      const { child, output, closed } = run({ WORKER_POLL_INTERVAL_MS: '20', LOG_LEVEL: 'debug' });
      await waitFor(() => output().includes('"msg":"worker loop started"'), 'startup', output);
      expect(output()).toContain('no job source is configured until Phase 6');
      child.kill(signal);
      expect(await closed).toBe(0);
      expect(output()).toContain('"msg":"shutting down after the job in flight"');
      expect(output()).toContain('"msg":"worker stopped"');
    },
    20_000,
  );

  it('exits 1 with a readable message when the configuration is invalid', async () => {
    const { output, closed } = run({ WORKER_POLL_INTERVAL_MS: 'often' });
    expect(await closed).toBe(1);
    expect(output()).toContain('Invalid worker configuration:');
    expect(output()).toContain('WORKER_POLL_INTERVAL_MS: must be an integer number of milliseconds >= 10');
  }, 20_000);
});
