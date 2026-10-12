import { EventEmitter } from 'node:events';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkerLogger } from '../src/logger.ts';
import { runWorkerProcess } from '../src/process.ts';
import { type Job, type JobSource, Worker } from '../src/worker.ts';

/** Hands out one job, then none. */
class OneJobSource implements JobSource {
  #job: Job | null = { id: 'job-1', name: 'block', payload: null, attempt: 1 };
  readonly completed: Job[] = [];

  async claim(): Promise<Job | null> {
    const job = this.#job;
    this.#job = null;
    return job;
  }

  async complete(job: Job): Promise<void> {
    this.completed.push(job);
  }

  async fail(): Promise<void> {}
}

interface LogEntry {
  level: string;
  fields: Record<string, unknown>;
  message: string;
}

function setup(shutdownTimeoutMs = 1_000) {
  const entries: LogEntry[] = [];
  const at =
    (level: string) =>
    (fields: Record<string, unknown>, message: string): void => {
      entries.push({ level, fields, message });
    };
  const log: WorkerLogger = { error: at('error'), warn: at('warn'), info: at('info'), debug: at('debug') };
  const source = new OneJobSource();
  let release: () => void = () => {};
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let entered = false;
  let handlerSignal: AbortSignal | undefined;
  const worker = new Worker({ source, pollIntervalMs: 100, logger: log }).register(
    'block',
    async (_job, context) => {
      entered = true;
      handlerSignal = context.signal;
      // Ignores the abort signal on purpose: a job that does not stop early.
      await gate;
    },
  );
  const signals = new EventEmitter();
  const exits: number[] = [];
  let finished = false;
  const run = runWorkerProcess({
    worker,
    shutdownTimeoutMs,
    log,
    exit: (code) => exits.push(code),
    signals,
  }).then(() => {
    finished = true;
  });
  return {
    signals,
    exits,
    entries,
    source,
    release,
    run,
    entered: () => entered,
    handlerSignal: () => handlerSignal,
    finished: () => finished,
    messages: () => entries.map((entry) => entry.message),
  };
}

const flush = () => vi.advanceTimersByTimeAsync(0);

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('runWorkerProcess', () => {
  it('waits for the job in flight after a signal, then resolves without exiting', async () => {
    const process = setup(1_000);
    await flush();
    expect(process.entered()).toBe(true);

    process.signals.emit('SIGTERM', 'SIGTERM');
    expect(process.handlerSignal()?.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(500);
    expect(process.finished()).toBe(false);

    process.release();
    await flush();
    expect(process.finished()).toBe(true);
    expect(process.source.completed.map((job) => job.id)).toEqual(['job-1']);
    expect(process.messages()).toContain('shutting down after the job in flight');
    // The forced exit was disarmed, and the signal listeners removed.
    await vi.advanceTimersByTimeAsync(10_000);
    expect(process.exits).toEqual([]);
    expect(process.signals.listenerCount('SIGTERM')).toBe(0);
    expect(process.signals.listenerCount('SIGINT')).toBe(0);
  });

  it('exits 1 when the job in flight outlasts the shutdown timeout', async () => {
    const process = setup(1_000);
    await flush();
    process.signals.emit('SIGINT', 'SIGINT');
    await vi.advanceTimersByTimeAsync(999);
    expect(process.exits).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(process.exits).toEqual([1]);
    expect(process.entries.at(-1)).toEqual({
      level: 'error',
      fields: { timeoutMs: 1_000 },
      message: 'graceful shutdown timed out',
    });
    process.release();
    await process.run;
  });

  it('exits 1 at once on a second signal', async () => {
    const process = setup(60_000);
    await flush();
    process.signals.emit('SIGTERM', 'SIGTERM');
    await vi.advanceTimersByTimeAsync(10);
    process.signals.emit('SIGINT', 'SIGINT');
    expect(process.exits).toEqual([1]);
    expect(process.entries.at(-1)).toMatchObject({
      level: 'warn',
      fields: { signal: 'SIGINT' },
      message: 'second signal received; exiting immediately',
    });
    process.release();
    await process.run;
  });

  it('stops promptly when idle', async () => {
    const process = setup(1_000);
    await flush();
    process.release();
    // The one job finished; the loop is now waiting for the next poll.
    await flush();
    process.signals.emit('SIGTERM', 'SIGTERM');
    await flush();
    expect(process.finished()).toBe(true);
    expect(process.exits).toEqual([]);
  });
});
