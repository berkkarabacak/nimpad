import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createShutdownHandler } from '../src/lifecycle.ts';

interface Logged {
  level: 'info' | 'warn' | 'error';
  fields: Record<string, unknown>;
  message: string;
}

function setup(stop: () => Promise<void>, timeoutMs = 10_000) {
  const logged: Logged[] = [];
  const exits: number[] = [];
  const log = {
    info: (fields: Record<string, unknown>, message: string) =>
      logged.push({ level: 'info', fields, message }),
    warn: (fields: Record<string, unknown>, message: string) =>
      logged.push({ level: 'warn', fields, message }),
    error: (fields: Record<string, unknown>, message: string) =>
      logged.push({ level: 'error', fields, message }),
  };
  const shutdown = createShutdownHandler({ stop, timeoutMs, log, exit: (code) => exits.push(code) });
  return { shutdown, logged, exits, messages: () => logged.map((entry) => entry.message) };
}

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('createShutdownHandler', () => {
  it('stops once and lets the process end on its own when stop() completes', async () => {
    const stop = vi.fn(async () => {});
    const { shutdown, exits, messages } = setup(stop);
    shutdown('SIGTERM');
    await vi.advanceTimersByTimeAsync(0);
    expect(stop).toHaveBeenCalledTimes(1);
    expect(messages()).toEqual(['shutting down', 'shutdown complete']);
    // The forced exit was disarmed.
    await vi.advanceTimersByTimeAsync(60_000);
    expect(exits).toEqual([]);
  });

  it('exits 1 when stop() is still waiting after the timeout', async () => {
    const { shutdown, exits, logged } = setup(() => new Promise(() => {}), 10_000);
    shutdown('SIGINT');
    await vi.advanceTimersByTimeAsync(9_999);
    expect(exits).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(exits).toEqual([1]);
    expect(logged.at(-1)).toEqual({
      level: 'error',
      fields: { timeoutMs: 10_000 },
      message: 'graceful shutdown timed out',
    });
  });

  it('exits 1 at once on a second signal, without stopping twice', async () => {
    const stop = vi.fn(() => new Promise<void>(() => {}));
    const { shutdown, exits, logged } = setup(stop);
    shutdown('SIGTERM');
    await vi.advanceTimersByTimeAsync(100);
    shutdown('SIGINT');
    expect(exits).toEqual([1]);
    expect(stop).toHaveBeenCalledTimes(1);
    expect(logged.at(-1)).toMatchObject({ level: 'warn', fields: { signal: 'SIGINT' } });
    expect(logged.at(-1)?.message).toBe('second signal received; exiting immediately');
  });

  it('exits 1 when stop() fails', async () => {
    const failure = new Error('pool end failed');
    const { shutdown, exits, logged } = setup(async () => Promise.reject(failure));
    shutdown('SIGTERM');
    await vi.advanceTimersByTimeAsync(0);
    expect(exits).toEqual([1]);
    expect(logged.at(-1)).toEqual({ level: 'error', fields: { err: failure }, message: 'shutdown failed' });
    // Not twice: the forced exit was disarmed.
    await vi.advanceTimersByTimeAsync(60_000);
    expect(exits).toEqual([1]);
  });

  it('does not let the forced-exit timer alone keep the process alive', () => {
    const unref = vi.fn();
    const realSetTimeout = globalThis.setTimeout;
    const spy = vi
      .spyOn(globalThis, 'setTimeout')
      .mockImplementation(((handler: () => void, ms?: number) =>
        Object.assign(realSetTimeout(handler, ms), { unref })) as unknown as typeof setTimeout);
    try {
      setup(() => new Promise(() => {})).shutdown('SIGTERM');
      expect(unref).toHaveBeenCalledTimes(1);
    } finally {
      spy.mockRestore();
    }
  });
});
