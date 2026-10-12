// A minimal structured logger (one JSON object per line), so the worker needs no logging dependency.

export const LOG_LEVELS = ['fatal', 'error', 'warn', 'info', 'debug', 'trace', 'silent'] as const;
export type LogLevel = (typeof LOG_LEVELS)[number];

/** The logging surface the worker uses. Compatible with pino's `(object, message)` call style. */
export interface WorkerLogger {
  error(fields: Record<string, unknown>, message: string): void;
  warn(fields: Record<string, unknown>, message: string): void;
  info(fields: Record<string, unknown>, message: string): void;
  debug(fields: Record<string, unknown>, message: string): void;
}

// pino's numeric levels, so log tooling can treat the API's and the worker's lines alike.
const LEVEL_VALUES: Record<Exclude<LogLevel, 'silent'>, number> = {
  fatal: 60,
  error: 50,
  warn: 40,
  info: 30,
  debug: 20,
  trace: 10,
};

function serializeError(error: Error): Record<string, unknown> {
  return { type: error.name, message: error.message, stack: error.stack };
}

/**
 * Creates a logger writing JSON lines (`{"level":30,"time":…,"msg":…,…fields}`) to `stream`.
 * An `err` field holding an Error is serialized with its type, message and stack.
 */
export function createJsonLogger(
  level: LogLevel,
  stream: { write(line: string): unknown } = process.stdout,
  base: Record<string, unknown> = {},
): WorkerLogger {
  const threshold = level === 'silent' ? Number.POSITIVE_INFINITY : LEVEL_VALUES[level];
  const write = (value: number, fields: Record<string, unknown>, message: string): void => {
    if (value < threshold) {
      return;
    }
    const entry: Record<string, unknown> = {
      level: value,
      time: Date.now(),
      ...base,
      ...fields,
      msg: message,
    };
    if (entry.err instanceof Error) {
      entry.err = serializeError(entry.err);
    }
    stream.write(`${JSON.stringify(entry)}\n`);
  };
  return {
    error: (fields, message) => write(LEVEL_VALUES.error, fields, message),
    warn: (fields, message) => write(LEVEL_VALUES.warn, fields, message),
    info: (fields, message) => write(LEVEL_VALUES.info, fields, message),
    debug: (fields, message) => write(LEVEL_VALUES.debug, fields, message),
  };
}
