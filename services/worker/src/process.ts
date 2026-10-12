// Process lifecycle for main.ts: run the worker until SIGINT/SIGTERM. Kept free of `process` so that the shutdown
// timeout and the second-signal exit can be tested with fake timers.

import type { WorkerLogger } from './logger.ts';
import type { Worker } from './worker.ts';

/** The shutdown signals the worker reacts to. */
export const SHUTDOWN_SIGNALS = ['SIGINT', 'SIGTERM'] as const;

/** Where shutdown signals come from (`process` in production, an EventEmitter in tests). */
export interface SignalSource {
  on(signal: NodeJS.Signals, listener: (signal: NodeJS.Signals) => void): unknown;
  off(signal: NodeJS.Signals, listener: (signal: NodeJS.Signals) => void): unknown;
}

/** Options of {@link runWorkerProcess}. */
export interface WorkerProcessOptions {
  worker: Worker;
  /** How long the first signal waits for the job in flight before `exit(1)`. */
  shutdownTimeoutMs: number;
  log: WorkerLogger;
  /** Ends the process (`process.exit` in production). */
  exit: (code: number) => void;
  signals: SignalSource;
}

/**
 * Runs `worker` until the first SIGINT or SIGTERM, then lets the job in flight finish and resolves once the loop
 * has stopped (the caller then lets the process end with code 0).
 *
 * - If the job in flight is still running `shutdownTimeoutMs` after that signal, it calls `exit(1)`.
 * - A second signal calls `exit(1)` at once.
 */
export async function runWorkerProcess(options: WorkerProcessOptions): Promise<void> {
  const { worker, shutdownTimeoutMs, log, exit, signals } = options;
  const controller = new AbortController();
  let received = 0;
  let forceExit: NodeJS.Timeout | undefined;
  const onSignal = (signal: NodeJS.Signals): void => {
    received++;
    if (received > 1) {
      log.warn({ signal }, 'second signal received; exiting immediately');
      exit(1);
      return;
    }
    log.info({ signal }, 'shutting down after the job in flight');
    forceExit = setTimeout(() => {
      log.error({ timeoutMs: shutdownTimeoutMs }, 'graceful shutdown timed out');
      exit(1);
    }, shutdownTimeoutMs);
    // The timer alone must not keep the process alive once the loop has stopped.
    forceExit.unref();
    controller.abort();
  };
  for (const signal of SHUTDOWN_SIGNALS) {
    signals.on(signal, onSignal);
  }
  try {
    await worker.start(controller.signal);
  } finally {
    clearTimeout(forceExit);
    for (const signal of SHUTDOWN_SIGNALS) {
      signals.off(signal, onSignal);
    }
  }
}
