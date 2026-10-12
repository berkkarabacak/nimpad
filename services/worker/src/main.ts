// Entry point: `node src/main.ts`. Runs the job loop until SIGINT/SIGTERM, then waits for the job in flight
// (up to WORKER_SHUTDOWN_TIMEOUT_MS) and exits. A second signal exits immediately with code 1 (see process.ts).
//
// There is no job source yet: Phase 6 adds the database-backed queue and the scheduler, and registers the first
// handlers. Until then the worker idles and says so in its startup log line.

import { ConfigError, loadWorkerConfig } from './config.ts';
import { createJsonLogger } from './logger.ts';
import { runWorkerProcess } from './process.ts';
import { EmptyJobSource } from './sources.ts';
import { Worker } from './worker.ts';

let config: ReturnType<typeof loadWorkerConfig>;
try {
  config = loadWorkerConfig();
} catch (error) {
  if (error instanceof ConfigError) {
    console.error(error.message);
    process.exit(1);
  }
  throw error;
}

const log = createJsonLogger(config.logLevel, process.stdout, { service: 'nimpad-worker' });
const worker = new Worker({
  source: new EmptyJobSource(),
  pollIntervalMs: config.pollIntervalMs,
  logger: log,
});
log.info(
  { pollIntervalMs: config.pollIntervalMs, handlers: worker.handlerNames, source: 'none' },
  'worker started; no job source is configured until Phase 6, so it idles',
);
await runWorkerProcess({
  worker,
  shutdownTimeoutMs: config.shutdownTimeoutMs,
  log,
  exit: (code) => process.exit(code),
  signals: process,
});
log.info({}, 'worker stopped');
