// API configuration, read from the environment (see .env.example) and validated once at startup.

import { z } from 'zod';

export const LOG_LEVELS = ['fatal', 'error', 'warn', 'info', 'debug', 'trace', 'silent'] as const;
export type LogLevel = (typeof LOG_LEVELS)[number];

/** Default request body limit: 1 MiB. */
export const DEFAULT_BODY_LIMIT_BYTES = 1024 * 1024;

/** Validated API settings. */
export interface ApiConfig {
  /** Interface to listen on. */
  host: string;
  /** TCP port; 0 picks a free port. */
  port: number;
  logLevel: LogLevel;
  /** PostgreSQL connection string for the non-owner runtime role (`nimpad_app`). */
  databaseUrl: string;
  /** Largest accepted request body, in bytes. Larger bodies get a `payload_too_large` error. */
  bodyLimitBytes: number;
}

/** Thrown by {@link loadConfig}; `issues` lists every missing or invalid variable. Values are never included. */
export class ConfigError extends Error {
  readonly issues: readonly string[];

  constructor(issues: readonly string[]) {
    super(`Invalid API configuration:\n${issues.map((issue) => `  - ${issue}`).join('\n')}`);
    this.name = 'ConfigError';
    this.issues = issues;
  }
}

// An empty variable (`API_PORT=` in a .env file) counts as unset, so defaults still apply.
const unsetIfEmpty = (value: unknown): unknown => (value === '' ? undefined : value);

const required = (message: string) => ({
  error: (issue: { input: unknown }) => (issue.input === undefined ? 'is required' : message),
});

const integerInRange = (min: number, max: number, fallback: number, message: string) =>
  z.preprocess(
    (value) => unsetIfEmpty(value) ?? String(fallback),
    z
      .string()
      .regex(/^\d+$/, message)
      .transform(Number)
      .pipe(z.number().int().min(min, message).max(max, message)),
  );

function isPostgresUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return (url.protocol === 'postgres:' || url.protocol === 'postgresql:') && url.hostname !== '';
  } catch {
    return false;
  }
}

const EnvSchema = z.object({
  API_HOST: z.preprocess(unsetIfEmpty, z.string().default('127.0.0.1')),
  API_PORT: integerInRange(0, 65_535, 8787, 'must be an integer from 0 to 65535'),
  LOG_LEVEL: z.preprocess(
    unsetIfEmpty,
    z.enum(LOG_LEVELS, { error: `must be one of ${LOG_LEVELS.join(', ')}` }).default('info'),
  ),
  DATABASE_URL: z.preprocess(
    unsetIfEmpty,
    z
      .string(required('must be a string'))
      .refine(isPostgresUrl, 'must be a postgres:// or postgresql:// URL with a host'),
  ),
  BODY_LIMIT_BYTES: integerInRange(
    1,
    2 ** 31 - 1,
    DEFAULT_BODY_LIMIT_BYTES,
    'must be a positive integer number of bytes',
  ),
});

/**
 * Reads and validates the API configuration.
 *
 * @param env Variables to read; defaults to `process.env`.
 * @throws ConfigError listing every missing or invalid variable.
 */
export function loadConfig(env: Record<string, string | undefined> = process.env): ApiConfig {
  const result = EnvSchema.safeParse(env);
  if (!result.success) {
    const issues = result.error.issues.map((issue) => `${issue.path.join('.')}: ${issue.message}`);
    throw new ConfigError(issues);
  }
  const vars = result.data;
  return {
    host: vars.API_HOST,
    port: vars.API_PORT,
    logLevel: vars.LOG_LEVEL,
    databaseUrl: vars.DATABASE_URL,
    bodyLimitBytes: vars.BODY_LIMIT_BYTES,
  };
}
