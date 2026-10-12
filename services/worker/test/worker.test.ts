import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkerLogger } from '../src/logger.ts';
import { EmptyJobSource } from '../src/sources.ts';
import { DEFAULT_POLL_INTERVAL_MS, type Job, type JobSource, Worker } from '../src/worker.ts';

class FakeJobSource implements JobSource {
  readonly queue: Job[] = [];
  readonly completed: Job[] = [];
  readonly failed: Array<{ job: Job; error: Error }> = [];
  readonly claimSignals: AbortSignal[] = [];
  claims = 0;
  claimImpl: ((signal: AbortSignal) => Promise<Job | null>) | undefined;
  completeError: Error | undefined;
  failError: Error | undefined;

  async claim(signal: AbortSignal): Promise<Job | null> {
    this.claims++;
    this.claimSignals.push(signal);
    if (this.claimImpl !== undefined) {
      return this.claimImpl(signal);
    }
    return this.queue.shift() ?? null;
  }

  async complete(job: Job): Promise<void> {
    if (this.completeError !== undefined) {
      throw this.completeError;
    }
    this.completed.push(job);
  }

  async fail(job: Job, error: Error): Promise<void> {
    this.failed.push({ job, error });
    if (this.failError !== undefined) {
      throw this.failError;
    }
  }
}

interface LogEntry {
  level: 'error' | 'warn' | 'info' | 'debug';
  fields: Record<string, unknown>;
  message: string;
}

function recordingLogger(): WorkerLogger & { entries: LogEntry[] } {
  const entries: LogEntry[] = [];
  const at =
    (level: LogEntry['level']) =>
    (fields: Record<string, unknown>, message: string): void => {
      entries.push({ level, fields, message });
    };
  return { entries, error: at('error'), warn: at('warn'), info: at('info'), debug: at('debug') };
}

function job(id: string, name = 'echo'): Job {
  return { id, name, payload: { id }, attempt: 1 };
}

function deferred(): { promise: Promise<void>; resolve: () => void } {
  let resolve: () => void = () => {};
  const promise = new Promise<void>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

/** Lets pending promise callbacks run without moving the fake clock. */
const flush = () => vi.advanceTimersByTimeAsync(0);

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('register', () => {
  it('registers handlers by unique, non-empty name', () => {
    const worker = new Worker({ source: new FakeJobSource() });
    const handler = async () => {};
    expect(worker.register('a', handler).register('b', handler).handlerNames).toEqual(['a', 'b']);
    expect(() => worker.register('a', handler)).toThrow('A handler for job "a" is already registered');
    expect(() => worker.register('', handler)).toThrow(TypeError);
  });

  it('validates the poll interval', () => {
    expect(() => new Worker({ source: new FakeJobSource(), pollIntervalMs: -1 })).toThrow(RangeError);
    expect(() => new Worker({ source: new FakeJobSource(), pollIntervalMs: Number.NaN })).toThrow(RangeError);
    expect(DEFAULT_POLL_INTERVAL_MS).toBe(1_000);
  });
});

describe('polling', () => {
  it('polls immediately, then once per interval while idle', async () => {
    const source = new FakeJobSource();
    const worker = new Worker({ source, pollIntervalMs: 1_000 });
    const running = worker.start();
    await flush();
    expect(source.claims).toBe(1);
    await vi.advanceTimersByTimeAsync(999);
    expect(source.claims).toBe(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(source.claims).toBe(2);
    await vi.advanceTimersByTimeAsync(3_000);
    expect(source.claims).toBe(5);
    await worker.stop();
    await running;
  });

  it('runs available jobs back to back, in order, without waiting', async () => {
    const source = new FakeJobSource();
    source.queue.push(job('1'), job('2'), job('3'));
    const ran: string[] = [];
    const worker = new Worker({ source, pollIntervalMs: 1_000 }).register('echo', async (j) => {
      ran.push(j.id);
    });
    const running = worker.start();
    await flush();
    expect(ran).toEqual(['1', '2', '3']);
    expect(source.completed.map((j) => j.id)).toEqual(['1', '2', '3']);
    // Three successful claims plus the empty one that started the idle wait.
    expect(source.claims).toBe(4);
    source.queue.push(job('4'));
    await vi.advanceTimersByTimeAsync(1_000);
    expect(ran).toEqual(['1', '2', '3', '4']);
    await worker.stop();
    await running;
  });

  it('passes the job, an abort signal and the logger to the handler', async () => {
    const source = new FakeJobSource();
    source.queue.push({ id: 'j', name: 'echo', payload: { x: 1 }, attempt: 3 });
    const log = recordingLogger();
    const seen: unknown[] = [];
    const worker = new Worker({ source, logger: log }).register('echo', async (j, context) => {
      seen.push(j, context.signal instanceof AbortSignal, context.log === log);
    });
    const running = worker.start();
    await flush();
    expect(seen).toEqual([{ id: 'j', name: 'echo', payload: { x: 1 }, attempt: 3 }, true, true]);
    expect(log.entries).toContainEqual({
      level: 'info',
      fields: { jobId: 'j', jobName: 'echo', attempt: 3, durationMs: 0 },
      message: 'job completed',
    });
    await worker.stop();
    await running;
  });
});

describe('error isolation', () => {
  it('records a failing handler with the source and keeps going', async () => {
    const source = new FakeJobSource();
    source.queue.push(job('1', 'boom'), job('2', 'echo'));
    const log = recordingLogger();
    const failure = new Error('handler exploded');
    const worker = new Worker({ source, logger: log })
      .register('boom', async () => {
        throw failure;
      })
      .register('echo', async () => {});
    const running = worker.start();
    await flush();
    expect(source.failed).toEqual([{ job: job('1', 'boom'), error: failure }]);
    expect(source.completed.map((j) => j.id)).toEqual(['2']);
    expect(log.entries.find((e) => e.message === 'job failed')).toMatchObject({
      level: 'warn',
      fields: { jobId: '1', jobName: 'boom', err: failure },
    });
    expect(worker.isRunning).toBe(true);
    await worker.stop();
    await running;
  });

  it('wraps non-Error throws and synchronous throws', async () => {
    const source = new FakeJobSource();
    source.queue.push(job('1', 'string'), job('2', 'sync'));
    const worker = new Worker({ source })
      .register('string', async () => {
        throw 'just a string';
      })
      .register('sync', (() => {
        throw new Error('thrown before any await');
      }) as unknown as () => Promise<void>);
    const running = worker.start();
    await flush();
    expect(source.failed.map((f) => f.error.message)).toEqual([
      'Non-error thrown: just a string',
      'thrown before any await',
    ]);
    await worker.stop();
    await running;
  });

  it('fails jobs that have no handler', async () => {
    const source = new FakeJobSource();
    source.queue.push(job('1', 'unknown'));
    const log = recordingLogger();
    const worker = new Worker({ source, logger: log });
    const running = worker.start();
    await flush();
    expect(source.failed).toHaveLength(1);
    expect(source.failed[0]?.error.message).toBe('No handler registered for job "unknown"');
    expect(log.entries.some((e) => e.level === 'error' && e.message === 'job has no handler')).toBe(true);
    await worker.stop();
    await running;
  });

  it('survives a failing claim and retries after the interval', async () => {
    const source = new FakeJobSource();
    const log = recordingLogger();
    let calls = 0;
    source.claimImpl = async () => {
      calls++;
      if (calls === 1) {
        throw new Error('database unavailable');
      }
      return null;
    };
    const worker = new Worker({ source, logger: log, pollIntervalMs: 500 });
    const running = worker.start();
    await flush();
    expect(calls).toBe(1);
    expect(log.entries.find((e) => e.message === 'claiming a job failed')?.level).toBe('error');
    await vi.advanceTimersByTimeAsync(500);
    expect(calls).toBe(2);
    expect(worker.isRunning).toBe(true);
    await worker.stop();
    await running;
  });

  it('survives failures to record completion or failure', async () => {
    const source = new FakeJobSource();
    source.completeError = new Error('complete failed');
    source.failError = new Error('fail failed');
    source.queue.push(job('1', 'echo'), job('2', 'boom'), job('3', 'echo'));
    const log = recordingLogger();
    let echoes = 0;
    const worker = new Worker({ source, logger: log })
      .register('echo', async () => {
        echoes++;
      })
      .register('boom', async () => {
        throw new Error('handler failed');
      });
    const running = worker.start();
    await flush();
    expect(echoes).toBe(2);
    expect(source.failed.map((f) => f.job.id)).toEqual(['2']);
    const recordErrors = log.entries
      .filter((e) => e.message.startsWith('recording job'))
      .map((e) => e.message);
    expect(recordErrors).toEqual([
      'recording job completion failed',
      'recording job failure failed',
      'recording job completion failed',
    ]);
    expect(worker.isRunning).toBe(true);
    await worker.stop();
    await running;
  });
});

describe('start and stop', () => {
  it('stop() resolves only after the job in flight finishes, and nothing is claimed afterwards', async () => {
    const source = new FakeJobSource();
    source.queue.push(job('slow'), job('next'));
    const gate = deferred();
    let handlerSignal: AbortSignal | undefined;
    const worker = new Worker({ source }).register('echo', async (j, context) => {
      if (j.id === 'slow') {
        handlerSignal = context.signal;
        await gate.promise;
      }
    });
    const running = worker.start();
    await flush();
    expect(source.claims).toBe(1);

    let stopped = false;
    const stopping = worker.stop().then(() => {
      stopped = true;
    });
    await vi.advanceTimersByTimeAsync(60_000);
    expect(stopped).toBe(false);
    expect(worker.isRunning).toBe(true);
    // The handler can see that a stop was requested.
    expect(handlerSignal?.aborted).toBe(true);

    gate.resolve();
    await stopping;
    await running;
    expect(stopped).toBe(true);
    expect(worker.isRunning).toBe(false);
    expect(source.completed.map((j) => j.id)).toEqual(['slow']);
    expect(source.claims).toBe(1);
    expect(source.queue.map((j) => j.id)).toEqual(['next']);
  });

  it('stops promptly while idle, without waiting for the poll interval', async () => {
    const source = new FakeJobSource();
    const worker = new Worker({ source, pollIntervalMs: 60_000 });
    const running = worker.start();
    await flush();
    await worker.stop();
    await running;
    expect(worker.isRunning).toBe(false);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('stops when the signal passed to start() aborts', async () => {
    const source = new FakeJobSource();
    const controller = new AbortController();
    const worker = new Worker({ source });
    let done = false;
    const running = worker.start(controller.signal).then(() => {
      done = true;
    });
    await flush();
    expect(done).toBe(false);
    controller.abort();
    await running;
    expect(done).toBe(true);
    expect(source.claimSignals[0]?.aborted).toBe(true);
  });

  it('does not poll at all when started with an aborted signal', async () => {
    const source = new FakeJobSource();
    await new Worker({ source }).start(AbortSignal.abort());
    expect(source.claims).toBe(0);
  });

  it('runs a job that was claimed while the stop was requested', async () => {
    const source = new FakeJobSource();
    const claimed = deferred();
    source.claimImpl = async () => {
      await claimed.promise;
      return job('late');
    };
    const ran: string[] = [];
    const worker = new Worker({ source }).register('echo', async (j) => {
      ran.push(j.id);
    });
    const running = worker.start();
    await flush();
    const stopping = worker.stop();
    claimed.resolve();
    await stopping;
    await running;
    expect(ran).toEqual(['late']);
    expect(source.completed.map((j) => j.id)).toEqual(['late']);
  });

  it('rejects a second start while running, and can be restarted after stopping', async () => {
    const source = new FakeJobSource();
    const worker = new Worker({ source });
    const running = worker.start();
    await expect(worker.start()).rejects.toThrow('Worker is already running');
    await worker.stop();
    await running;
    const again = worker.start();
    await flush();
    expect(worker.isRunning).toBe(true);
    await worker.stop();
    await again;
  });

  it('stop() on a worker that is not running resolves immediately', async () => {
    await expect(new Worker({ source: new FakeJobSource() }).stop()).resolves.toBeUndefined();
  });
});

describe('EmptyJobSource', () => {
  it('never has a job, so the worker idles', async () => {
    const source = new EmptyJobSource();
    await expect(source.claim()).resolves.toBeNull();
    await expect(source.complete(job('x'))).rejects.toThrow();
    await expect(source.fail(job('x'))).rejects.toThrow();
  });
});
