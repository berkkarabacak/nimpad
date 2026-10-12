// Process lifecycle for main.ts, kept free of `process` so it can be tested with fake timers.

/** The logging calls the lifecycle makes (pino's `(fields, message)` style). */
export interface LifecycleLogger {
  info(fields: Record<string, unknown>, message: string): void;
  warn(fields: Record<string, unknown>, message: string): void;
  error(fields: Record<string, unknown>, message: string): void;
}

/** Options of {@link createShutdownHandler}. */
export interface ShutdownOptions {
  /** Stops the service: the HTTP server first (finishing requests in flight), then the database pool. */
  stop: () => Promise<void>;
  /** After this long, a shutdown still waiting on `stop` exits with code 1. */
  timeoutMs: number;
  log: LifecycleLogger;
  /** Ends the process (`process.exit` in production). */
  exit: (code: number) => void;
}

/**
 * Returns the SIGINT/SIGTERM handler. The first signal starts `stop()` and arms a forced `exit(1)` after
 * `timeoutMs`; when `stop()` resolves, the timer is cleared and the process ends on its own once nothing is left
 * to do (exit code 0). A `stop()` that rejects exits with code 1. A second signal exits with code 1 at once.
 */
export function createShutdownHandler(options: ShutdownOptions): (signal: NodeJS.Signals) => void {
  const { stop, timeoutMs, log, exit } = options;
  let stopping = false;
  return (signal) => {
    if (stopping) {
      log.warn({ signal }, 'second signal received; exiting immediately');
      exit(1);
      return;
    }
    stopping = true;
    log.info({ signal }, 'shutting down');
    const forceExit = setTimeout(() => {
      log.error({ timeoutMs }, 'graceful shutdown timed out');
      exit(1);
    }, timeoutMs);
    // The timer alone must not keep the process alive once everything else has closed.
    forceExit.unref();
    stop().then(
      () => {
        clearTimeout(forceExit);
        log.info({}, 'shutdown complete');
      },
      (error: unknown) => {
        clearTimeout(forceExit);
        log.error({ err: error }, 'shutdown failed');
        exit(1);
      },
    );
  };
}
