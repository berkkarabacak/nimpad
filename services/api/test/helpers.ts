import type { ApiConfig } from '../src/config.ts';

export const testConfig: ApiConfig = {
  host: '127.0.0.1',
  port: 0,
  logLevel: 'info',
  databaseUrl: 'postgres://nimpad:nimpad@127.0.0.1:5432/nimpad',
  bodyLimitBytes: 1024 * 1024,
};

export interface LogLine {
  level: number;
  msg?: string;
  requestId?: string;
  [key: string]: unknown;
}

/** A log destination that keeps every line, for asserting on what the server logged. */
export function captureLogs(): {
  stream: { write(line: string): void };
  raw: () => string;
  lines: () => LogLine[];
} {
  const chunks: string[] = [];
  return {
    stream: { write: (line: string) => chunks.push(line) },
    raw: () => chunks.join(''),
    lines: () =>
      chunks
        .join('')
        .split('\n')
        .filter((line) => line.trim() !== '')
        .map((line) => JSON.parse(line) as LogLine),
  };
}

/** The test database URL (CI sets DATABASE_URL_TEST; locally the default matches docker-compose and setup.sh). */
export const TEST_DATABASE_URL =
  process.env.DATABASE_URL_TEST ?? 'postgres://nimpad:nimpad@127.0.0.1:5432/nimpad';

/** The URL with its password masked, safe for test names and messages. */
export function maskUrl(url: string): string {
  try {
    const parsed = new URL(url);
    if (parsed.password !== '') {
      parsed.password = '***';
    }
    return parsed.toString();
  } catch {
    return '<invalid url>';
  }
}
