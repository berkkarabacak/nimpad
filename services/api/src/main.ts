// Entry point: `node src/main.ts`. Reads the environment, checks the database role, starts listening, and shuts
// down gracefully on SIGINT/SIGTERM: stop accepting connections and finish in-flight requests, then close the
// database pool (see lifecycle.ts).

import type { FastifyInstance } from 'fastify';
import { ConfigError, loadConfig } from './config.ts';
import { createDatabase, createPool, UnsafeDatabaseRoleError } from './db.ts';
import { createShutdownHandler } from './lifecycle.ts';
import { buildServer } from './server.ts';

/** After this long, a shutdown that is still waiting on requests or connections exits with code 1. */
const SHUTDOWN_TIMEOUT_MS = 10_000;

async function main(): Promise<void> {
  let config: ReturnType<typeof loadConfig>;
  try {
    config = loadConfig();
  } catch (error) {
    if (error instanceof ConfigError) {
      console.error(error.message);
      process.exitCode = 1;
      return;
    }
    throw error;
  }

  let server: FastifyInstance | undefined;
  const pool = createPool(config.databaseUrl, (error) => {
    server?.log.error({ err: error }, 'idle database connection failed');
  });
  const db = createDatabase(pool);
  server = buildServer({ config, db });
  const app = server;

  // A role that bypasses row-level security would serve every user's rows to everyone: refuse to start. A database
  // that is merely unreachable is not fatal; GET /readyz answers 503 until it is back (and checks the role again).
  try {
    await db.checkReady();
  } catch (error) {
    if (error instanceof UnsafeDatabaseRoleError) {
      app.log.fatal(
        { err: error, role: error.role },
        'refusing to start: the database role bypasses row-level security',
      );
      process.exitCode = 1;
      await db.close();
      return;
    }
    app.log.warn({ err: error }, 'database not reachable at startup; GET /readyz reports 503 until it is');
  }

  const shutdown = createShutdownHandler({
    stop: async () => {
      await app.close();
      await db.close();
    },
    timeoutMs: SHUTDOWN_TIMEOUT_MS,
    log: app.log,
    exit: (code) => process.exit(code),
  });
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);

  try {
    await app.listen({ host: config.host, port: config.port });
  } catch (error) {
    app.log.fatal({ err: error }, 'failed to start');
    process.exitCode = 1;
    await db.close();
    process.off('SIGINT', shutdown);
    process.off('SIGTERM', shutdown);
  }
}

await main();
