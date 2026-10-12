import { z } from 'zod';
import { LOG_LEVELS, type LogLevel } from './logger.ts';
import { DEFAULT_POLL_INTERVAL_MS } from './worker.ts';

/** Grace period for the job in flight after SIGINT/SIGTERM before the process exits with code 1. */
export const DEFAULT_SHUTDOWN_TIMEOUT_MS = 30_000;

export interface WorkerConfig {
  logLevel: LogLevel;
  /** Wait between polls when no job is due. */
  pollIntervalMs: number;
  /** How long a shutdown waits for the job in flight. */
  shutdownTimeoutMs: number;
}

/** Thrown by {@link loadWorkerConfig}; `issues` lists every invalid variable. */
export class ConfigError extends Error {
  readonly issues: readonly string[];

  constructor(issues: readonly string[]) {
    super(`Invalid worker configuration:\n${issues.map((issue) => `  - ${issue}`).join('\n')}`);
    this.name = 'ConfigError';
    this.issues = issues;
  }
}

const milliseconds = (fallback: number, min: number) =>
  z.preprocess(
    (value) => (value === '' || value === undefined ? String(fallback) : value),
    z
      .string()
      .regex(/^\d+$/, `must be an integer number of milliseconds >= ${min}`)
      .transform(Number)
      .pipe(
        z
          .number()
          .int()
          .min(min, `must be an integer number of milliseconds >= ${min}`)
          .max(2 ** 31 - 1, 'is too large'),
      ),
  );

const EnvSchema = z.object({
  LOG_LEVEL: z.preprocess(
    (value) => (value === '' ? undefined : value),
    z.enum(LOG_LEVELS, { error: `must be one of ${LOG_LEVELS.join(', ')}` }).default('info'),
  ),
  WORKER_POLL_INTERVAL_MS: milliseconds(DEFAULT_POLL_INTERVAL_MS, 10),
  WORKER_SHUTDOWN_TIMEOUT_MS: milliseconds(DEFAULT_SHUTDOWN_TIMEOUT_MS, 0),
});

/**
 * Reads and validates the worker configuration. Empty variables count as unset.
 *
 * @throws ConfigError listing every invalid variable.
 */
export function loadWorkerConfig(env: Record<string, string | undefined> = process.env): WorkerConfig {
  const result = EnvSchema.safeParse(env);
  if (!result.success) {
    throw new ConfigError(result.error.issues.map((issue) => `${issue.path.join('.')}: ${issue.message}`));
  }
  return {
    logLevel: result.data.LOG_LEVEL,
    pollIntervalMs: result.data.WORKER_POLL_INTERVAL_MS,
    shutdownTimeoutMs: result.data.WORKER_SHUTDOWN_TIMEOUT_MS,
  };
}
