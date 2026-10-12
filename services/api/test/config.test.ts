import { describe, expect, it } from 'vitest';
import { ConfigError, DEFAULT_BODY_LIMIT_BYTES, loadConfig } from '../src/config.ts';

const DATABASE_URL = 'postgres://nimpad_app:nimpad_app@127.0.0.1:5432/nimpad';

function configError(env: Record<string, string | undefined>): ConfigError {
  try {
    loadConfig(env);
  } catch (error) {
    if (error instanceof ConfigError) {
      return error;
    }
    throw error;
  }
  throw new Error('expected loadConfig to throw');
}

describe('loadConfig', () => {
  it('applies defaults', () => {
    expect(loadConfig({ DATABASE_URL })).toEqual({
      host: '127.0.0.1',
      port: 8787,
      logLevel: 'info',
      databaseUrl: DATABASE_URL,
      bodyLimitBytes: DEFAULT_BODY_LIMIT_BYTES,
    });
    expect(DEFAULT_BODY_LIMIT_BYTES).toBe(1_048_576);
  });

  it('reads every variable', () => {
    expect(
      loadConfig({
        API_HOST: '0.0.0.0',
        API_PORT: '9000',
        LOG_LEVEL: 'debug',
        DATABASE_URL: 'postgresql://u:p@db.example.com:6543/postgres?sslmode=require',
        BODY_LIMIT_BYTES: '2048',
      }),
    ).toEqual({
      host: '0.0.0.0',
      port: 9000,
      logLevel: 'debug',
      databaseUrl: 'postgresql://u:p@db.example.com:6543/postgres?sslmode=require',
      bodyLimitBytes: 2048,
    });
  });

  it('treats empty variables as unset', () => {
    const config = loadConfig({
      DATABASE_URL,
      API_HOST: '',
      API_PORT: '',
      LOG_LEVEL: '',
      BODY_LIMIT_BYTES: '',
    });
    expect(config).toMatchObject({
      host: '127.0.0.1',
      port: 8787,
      logLevel: 'info',
      bodyLimitBytes: 1_048_576,
    });
  });

  it('accepts port 0 (pick a free port)', () => {
    expect(loadConfig({ DATABASE_URL, API_PORT: '0' }).port).toBe(0);
  });

  it('reports a missing DATABASE_URL', () => {
    const error = configError({});
    expect(error.issues).toEqual(['DATABASE_URL: is required']);
    expect(error.message).toBe('Invalid API configuration:\n  - DATABASE_URL: is required');
    expect(configError({ DATABASE_URL: '' }).issues).toEqual(['DATABASE_URL: is required']);
  });

  it('lists every invalid variable at once', () => {
    const error = configError({
      API_PORT: '80a',
      LOG_LEVEL: 'loud',
      DATABASE_URL: 'mysql://root:hunter2@db/x',
      BODY_LIMIT_BYTES: '0',
    });
    expect(error.issues).toEqual([
      'API_PORT: must be an integer from 0 to 65535',
      'LOG_LEVEL: must be one of fatal, error, warn, info, debug, trace, silent',
      'DATABASE_URL: must be a postgres:// or postgresql:// URL with a host',
      'BODY_LIMIT_BYTES: must be a positive integer number of bytes',
    ]);
  });

  it.each(['65536', '-1', '1.5', ' 80', '1e3'])('rejects API_PORT=%j', (value) => {
    expect(configError({ DATABASE_URL, API_PORT: value }).issues).toEqual([
      'API_PORT: must be an integer from 0 to 65535',
    ]);
  });

  it.each(['not a url', 'postgres://', 'http://db/x'])('rejects DATABASE_URL=%j', (value) => {
    expect(configError({ DATABASE_URL: value }).issues).toEqual([
      'DATABASE_URL: must be a postgres:// or postgresql:// URL with a host',
    ]);
  });

  it('never echoes values (the database URL contains a password)', () => {
    const error = configError({ DATABASE_URL: 'mysql://root:hunter2@db/x' });
    expect(error.message).not.toContain('hunter2');
  });
});
