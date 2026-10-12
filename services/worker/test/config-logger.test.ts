import { describe, expect, it } from 'vitest';
import { ConfigError, DEFAULT_SHUTDOWN_TIMEOUT_MS, loadWorkerConfig } from '../src/config.ts';
import { createJsonLogger } from '../src/logger.ts';

describe('loadWorkerConfig', () => {
  it('applies defaults, treating empty variables as unset', () => {
    const expected = {
      logLevel: 'info',
      pollIntervalMs: 1_000,
      shutdownTimeoutMs: DEFAULT_SHUTDOWN_TIMEOUT_MS,
    };
    expect(loadWorkerConfig({})).toEqual(expected);
    expect(
      loadWorkerConfig({ LOG_LEVEL: '', WORKER_POLL_INTERVAL_MS: '', WORKER_SHUTDOWN_TIMEOUT_MS: '' }),
    ).toEqual(expected);
  });

  it('reads every variable', () => {
    expect(
      loadWorkerConfig({
        LOG_LEVEL: 'debug',
        WORKER_POLL_INTERVAL_MS: '250',
        WORKER_SHUTDOWN_TIMEOUT_MS: '0',
      }),
    ).toEqual({ logLevel: 'debug', pollIntervalMs: 250, shutdownTimeoutMs: 0 });
  });

  it('lists every invalid variable', () => {
    let error: unknown;
    try {
      loadWorkerConfig({
        LOG_LEVEL: 'chatty',
        WORKER_POLL_INTERVAL_MS: '5',
        WORKER_SHUTDOWN_TIMEOUT_MS: 'soon',
      });
    } catch (caught) {
      error = caught;
    }
    expect(error).toBeInstanceOf(ConfigError);
    expect((error as ConfigError).issues).toEqual([
      'LOG_LEVEL: must be one of fatal, error, warn, info, debug, trace, silent',
      'WORKER_POLL_INTERVAL_MS: must be an integer number of milliseconds >= 10',
      'WORKER_SHUTDOWN_TIMEOUT_MS: must be an integer number of milliseconds >= 0',
    ]);
  });
});

describe('createJsonLogger', () => {
  function capture() {
    const lines: string[] = [];
    return { lines, stream: { write: (line: string) => lines.push(line) } };
  }

  it('writes one JSON object per line with pino-compatible levels', () => {
    const { lines, stream } = capture();
    const log = createJsonLogger('info', stream, { service: 'test' });
    log.info({ jobId: '1' }, 'job completed');
    log.warn({}, 'careful');
    expect(lines).toHaveLength(2);
    expect(lines.every((line) => line.endsWith('\n'))).toBe(true);
    expect(JSON.parse(lines[0] as string)).toMatchObject({
      level: 30,
      service: 'test',
      jobId: '1',
      msg: 'job completed',
    });
    expect(JSON.parse(lines[1] as string).level).toBe(40);
  });

  it('filters by level', () => {
    const { lines, stream } = capture();
    const log = createJsonLogger('warn', stream);
    log.debug({}, 'd');
    log.info({}, 'i');
    log.warn({}, 'w');
    log.error({}, 'e');
    expect(lines.map((line) => JSON.parse(line).msg)).toEqual(['w', 'e']);

    const silent = capture();
    createJsonLogger('silent', silent.stream).error({}, 'nothing');
    expect(silent.lines).toEqual([]);
  });

  it('serializes errors with their stack', () => {
    const { lines, stream } = capture();
    createJsonLogger('info', stream).error({ err: new TypeError('bad') }, 'failed');
    const entry = JSON.parse(lines[0] as string);
    expect(entry.err).toMatchObject({ type: 'TypeError', message: 'bad' });
    expect(entry.err.stack).toContain('TypeError: bad');
  });
});
