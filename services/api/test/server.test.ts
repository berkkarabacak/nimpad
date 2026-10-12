import { once } from 'node:events';
import { connect } from 'node:net';
import { ApiError, HealthResponse, isUuidV7, ReadinessResponse } from '@nimpad/contracts';
import type { FastifyInstance } from 'fastify';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { z } from 'zod';
import packageJson from '../package.json' with { type: 'json' };
import { type ApiDatabase, UnsafeDatabaseRoleError } from '../src/db.ts';
import { ApiHttpError, parseRequest } from '../src/errors.ts';
import { API_VERSION, type BuildServerOptions, buildServer } from '../src/server.ts';
import { captureLogs, testConfig } from './helpers.ts';

const healthyDb: ApiDatabase = { checkReady: async () => {} };

const servers: FastifyInstance[] = [];

function makeServer(overrides: Partial<BuildServerOptions> = {}) {
  const logs = captureLogs();
  const app = buildServer({ config: testConfig, db: healthyDb, logStream: logs.stream, ...overrides });
  servers.push(app);
  return { app, logs };
}

afterEach(async () => {
  await Promise.all(servers.splice(0).map((app) => app.close()));
});

describe('GET /healthz', () => {
  it('reports ok with the package version', async () => {
    const { app } = makeServer();
    const response = await app.inject({ method: 'GET', url: '/healthz' });
    expect(response.statusCode).toBe(200);
    expect(response.headers['content-type']).toMatch(/^application\/json/);
    expect(response.headers['cache-control']).toBe('no-store');
    expect(response.json()).toEqual({ status: 'ok', version: packageJson.version });
    expect(HealthResponse.parse(response.json()).version).toBe(API_VERSION);
  });

  it('does not touch the database', async () => {
    const { app } = makeServer({
      db: {
        checkReady: () => {
          throw new Error('must not be called');
        },
      },
    });
    expect((await app.inject({ method: 'GET', url: '/healthz' })).statusCode).toBe(200);
  });
});

describe('GET /readyz', () => {
  it('is ready when the database answers', async () => {
    let checks = 0;
    const { app } = makeServer({
      db: {
        checkReady: async () => {
          checks++;
        },
      },
    });
    const response = await app.inject({ method: 'GET', url: '/readyz' });
    expect(response.statusCode).toBe(200);
    expect(ReadinessResponse.parse(response.json())).toEqual({ status: 'ready' });
    expect(checks).toBe(1);
  });

  it('answers 503 unavailable when the check fails, without leaking the cause', async () => {
    const { app, logs } = makeServer({
      db: {
        checkReady: async () => Promise.reject(new Error('password authentication failed for user "s3cr3t"')),
      },
    });
    const response = await app.inject({ method: 'GET', url: '/readyz' });
    expect(response.statusCode).toBe(503);
    const body = ApiError.parse(response.json());
    expect(body.error.code).toBe('unavailable');
    expect(body.error.requestId).toBe(response.headers['x-request-id']);
    expect(response.body).not.toContain('s3cr3t');
    // The cause is logged for operators.
    const warning = logs.lines().find((line) => line.msg === 'readiness check failed');
    expect(warning?.level).toBe(40);
    expect(JSON.stringify(warning)).toContain('password authentication failed');
  });

  it('answers 503 and logs an error when the database role bypasses row-level security', async () => {
    const { app, logs } = makeServer({
      db: {
        checkReady: async () =>
          Promise.reject(
            new UnsafeDatabaseRoleError('nimpad', [
              'it is a superuser, so row-level security does not apply',
            ]),
          ),
      },
    });
    const response = await app.inject({ method: 'GET', url: '/readyz' });
    expect(response.statusCode).toBe(503);
    expect(ApiError.parse(response.json()).error).toMatchObject({
      code: 'unavailable',
      message: 'The database is not ready',
    });
    expect(response.body).not.toContain('superuser');
    const logged = logs.lines().find((line) => line.msg === 'database role bypasses row-level security');
    expect(logged).toMatchObject({ level: 50, role: 'nimpad' });
  });

  it('answers 503 when the check hangs past the timeout', async () => {
    const { app } = makeServer({ db: { checkReady: () => new Promise(() => {}) }, readinessTimeoutMs: 50 });
    const started = Date.now();
    const response = await app.inject({ method: 'GET', url: '/readyz' });
    expect(response.statusCode).toBe(503);
    expect(response.json().error.code).toBe('unavailable');
    expect(Date.now() - started).toBeLessThan(2_000);
  });
});

describe('not found', () => {
  it.each([
    ['GET', '/nope'],
    ['POST', '/healthz'],
    ['DELETE', '/'],
  ] as const)('%s %s returns the not_found envelope', async (method, url) => {
    const { app } = makeServer();
    const response = await app.inject({ method, url });
    expect(response.statusCode).toBe(404);
    const body = ApiError.parse(response.json());
    expect(body).toEqual({
      error: {
        code: 'not_found',
        message: `Route ${method} ${url} not found`,
        requestId: response.headers['x-request-id'],
      },
    });
  });
});

describe('request ids', () => {
  it('generates a UUIDv7 when none is sent, and echoes it', async () => {
    const { app } = makeServer();
    const response = await app.inject({ method: 'GET', url: '/healthz' });
    const id = response.headers['x-request-id'];
    expect(typeof id).toBe('string');
    expect(isUuidV7(id as string)).toBe(true);
  });

  it('honors a well-formed incoming x-request-id in the header, the envelope and the logs', async () => {
    const { app, logs } = makeServer();
    const response = await app.inject({
      method: 'GET',
      url: '/missing',
      headers: { 'x-request-id': 'edge-42.a:b_c' },
    });
    expect(response.headers['x-request-id']).toBe('edge-42.a:b_c');
    expect(response.json().error.requestId).toBe('edge-42.a:b_c');
    const requestLines = logs.lines().filter((line) => line.requestId !== undefined);
    expect(requestLines.length).toBeGreaterThan(0);
    expect(requestLines.every((line) => line.requestId === 'edge-42.a:b_c')).toBe(true);
  });

  it.each(['has spaces', 'x'.repeat(129), 'quote"', 'é'])('replaces a malformed id %j', async (incoming) => {
    const { app } = makeServer();
    const response = await app.inject({
      method: 'GET',
      url: '/healthz',
      headers: { 'x-request-id': incoming },
    });
    expect(response.headers['x-request-id']).not.toBe(incoming);
    expect(isUuidV7(response.headers['x-request-id'] as string)).toBe(true);
  });

  it('gives concurrent requests distinct ids', async () => {
    const { app } = makeServer();
    const responses = await Promise.all(
      Array.from({ length: 20 }, () => app.inject({ method: 'GET', url: '/healthz' })),
    );
    expect(new Set(responses.map((r) => r.headers['x-request-id'])).size).toBe(20);
  });
});

describe('logging', () => {
  it('logs request headers with credentials redacted', async () => {
    const { app, logs } = makeServer();
    await app.inject({
      method: 'GET',
      url: '/healthz',
      headers: {
        authorization: 'Bearer super-secret-token',
        cookie: 'session=secret-cookie-value',
        'proxy-authorization': 'Basic c2VjcmV0LXByb3h5',
        'x-api-key': 'secret-api-key',
        'user-agent': 'nimpad-test',
      },
    });
    const raw = logs.raw();
    for (const secret of [
      'super-secret-token',
      'secret-cookie-value',
      'c2VjcmV0LXByb3h5',
      'secret-api-key',
    ]) {
      expect(raw).not.toContain(secret);
    }
    const incoming = logs.lines().find((line) => line.msg === 'incoming request');
    const headers = (incoming?.req as { headers: Record<string, string> } | undefined)?.headers;
    expect(headers).toMatchObject({
      authorization: '[Redacted]',
      cookie: '[Redacted]',
      'proxy-authorization': '[Redacted]',
      'x-api-key': '[Redacted]',
      'user-agent': 'nimpad-test',
    });
  });

  it('respects the configured level', async () => {
    const { app, logs } = makeServer({ config: { ...testConfig, logLevel: 'warn' } });
    await app.inject({ method: 'GET', url: '/healthz' });
    expect(logs.lines().filter((line) => line.level < 40)).toEqual([]);
  });
});

describe('error envelope', () => {
  function withTestRoutes(overrides: Partial<BuildServerOptions> = {}) {
    const server = makeServer(overrides);
    const { app } = server;
    app.post('/test/echo', async (request) => ({ received: request.body }));
    app.post(
      '/test/schema',
      { schema: { body: { type: 'object', required: ['name'], properties: { name: { type: 'string' } } } } },
      async () => ({ ok: true }),
    );
    app.post('/test/parse', async (request) =>
      parseRequest(z.object({ name: z.string().min(1) }), request.body, 'body'),
    );
    app.get('/test/server-data', async () => z.object({ rev: z.int().min(1) }).parse({ rev: 0 }));
    app.get('/test/crash', async () => {
      throw new Error('cannot connect to postgres://app:hunter2@db.internal/nimpad');
    });
    app.get('/test/conflict', async () => {
      throw new ApiHttpError('stale_revision', 'Your copy is out of date', { details: { serverRev: 9 } });
    });
    return server;
  }

  it('rejects bodies over the configured limit with payload_too_large', async () => {
    const { app } = withTestRoutes({ config: { ...testConfig, bodyLimitBytes: 64 } });
    const small = await app.inject({ method: 'POST', url: '/test/echo', payload: { text: 'x'.repeat(10) } });
    expect(small.statusCode).toBe(200);
    expect(small.json()).toEqual({ received: { text: 'x'.repeat(10) } });

    const large = await app.inject({ method: 'POST', url: '/test/echo', payload: { text: 'x'.repeat(100) } });
    expect(large.statusCode).toBe(413);
    expect(ApiError.parse(large.json()).error).toEqual({
      code: 'payload_too_large',
      message: 'Request body is too large',
      requestId: large.headers['x-request-id'],
    });
  });

  it('applies the 1 MiB default body limit', async () => {
    const { app } = withTestRoutes();
    const justUnder = JSON.stringify({ text: 'x'.repeat(1024 * 1024 - 20) });
    expect(Buffer.byteLength(justUnder)).toBeLessThanOrEqual(1024 * 1024);
    const ok = await app.inject({
      method: 'POST',
      url: '/test/echo',
      payload: justUnder,
      headers: { 'content-type': 'application/json' },
    });
    expect(ok.statusCode).toBe(200);
    const tooLarge = await app.inject({
      method: 'POST',
      url: '/test/echo',
      payload: JSON.stringify({ text: 'x'.repeat(1024 * 1024) }),
      headers: { 'content-type': 'application/json' },
    });
    expect(tooLarge.statusCode).toBe(413);
    expect(tooLarge.json().error.code).toBe('payload_too_large');
  });

  it('maps schema validation errors to bad_request with details', async () => {
    const { app } = withTestRoutes();
    // An object cannot be coerced to a string (Fastify's Ajv would coerce a number).
    const wrongType = await app.inject({
      method: 'POST',
      url: '/test/schema',
      payload: { name: { first: 'x' } },
    });
    expect(wrongType.statusCode).toBe(400);
    const body = ApiError.parse(wrongType.json());
    expect(body.error.code).toBe('bad_request');
    expect(body.error.details).toEqual([{ path: 'body.name', message: 'must be string' }]);

    const missing = await app.inject({ method: 'POST', url: '/test/schema', payload: {} });
    expect(missing.statusCode).toBe(400);
    expect(missing.json().error.details).toEqual([
      { path: 'body', message: "must have required property 'name'" },
    ]);
  });

  it('maps request validation through parseRequest to bad_request with details', async () => {
    const { app } = withTestRoutes();
    const response = await app.inject({ method: 'POST', url: '/test/parse', payload: { name: '' } });
    expect(response.statusCode).toBe(400);
    expect(response.json().error).toMatchObject({
      code: 'bad_request',
      details: [{ path: 'body.name', message: expect.any(String) }],
    });
  });

  it('reports server data that fails zod validation as an internal error, logged as one', async () => {
    const { app, logs } = withTestRoutes();
    const response = await app.inject({ method: 'GET', url: '/test/server-data' });
    expect(response.statusCode).toBe(500);
    expect(response.json().error).toEqual({
      code: 'internal',
      message: 'Internal server error',
      requestId: response.headers['x-request-id'],
    });
    expect(response.body).not.toContain('rev');
    expect(logs.lines().find((line) => line.level === 50)?.requestId).toBe(response.headers['x-request-id']);
  });

  it('maps malformed JSON to bad_request', async () => {
    const { app } = withTestRoutes();
    const response = await app.inject({
      method: 'POST',
      url: '/test/echo',
      payload: '{"text": ',
      headers: { 'content-type': 'application/json' },
    });
    expect(response.statusCode).toBe(400);
    expect(ApiError.parse(response.json()).error.code).toBe('bad_request');
  });

  it('maps an unsupported media type to bad_request, keeping status 415', async () => {
    const { app } = withTestRoutes();
    const response = await app.inject({
      method: 'POST',
      url: '/test/echo',
      payload: 'x',
      headers: { 'content-type': 'application/x-nimpad' },
    });
    expect(response.statusCode).toBe(415);
    expect(ApiError.parse(response.json()).error.code).toBe('bad_request');
  });

  it('passes intentional ApiHttpErrors through', async () => {
    const { app } = withTestRoutes();
    const response = await app.inject({ method: 'GET', url: '/test/conflict' });
    expect(response.statusCode).toBe(409);
    expect(response.json().error).toEqual({
      code: 'stale_revision',
      message: 'Your copy is out of date',
      details: { serverRev: 9 },
      requestId: response.headers['x-request-id'],
    });
  });

  it('never leaks messages or stack traces of unexpected errors, but logs them', async () => {
    const { app, logs } = withTestRoutes();
    const response = await app.inject({ method: 'GET', url: '/test/crash' });
    expect(response.statusCode).toBe(500);
    expect(response.json()).toEqual({
      error: {
        code: 'internal',
        message: 'Internal server error',
        requestId: response.headers['x-request-id'],
      },
    });
    expect(response.body).not.toContain('hunter2');
    expect(response.body).not.toContain('at ');
    const logged = logs.lines().find((line) => line.level === 50);
    expect(logged?.requestId).toBe(response.headers['x-request-id']);
    expect((logged?.err as { stack?: string } | undefined)?.stack).toContain('cannot connect');
  });

  it('answers malformed URLs with the envelope', async () => {
    const { app } = makeServer();
    const response = await app.inject({ method: 'GET', url: '/%zz' });
    expect(response.statusCode).toBe(400);
    expect(ApiError.parse(response.json()).error.code).toBe('bad_request');
    expect(response.headers['x-request-id']).toBeDefined();
  });
});

describe('over a real socket', () => {
  it('answers malformed HTTP with the envelope', async () => {
    const { app } = makeServer();
    await app.listen({ host: '127.0.0.1', port: 0 });
    const { port } = app.server.address() as { port: number };
    const socket = connect(port, '127.0.0.1');
    await once(socket, 'connect');
    socket.write('THIS IS NOT HTTP\r\n\r\n');
    const chunks: Buffer[] = [];
    socket.on('data', (chunk: Buffer) => chunks.push(chunk));
    await once(socket, 'close');
    const raw = Buffer.concat(chunks).toString('utf8');
    expect(raw).toMatch(/^HTTP\/1\.1 400 /);
    const body = raw.slice(raw.indexOf('\r\n\r\n') + 4);
    expect(ApiError.parse(JSON.parse(body)).error.code).toBe('bad_request');
  });

  /** Registers GET /test/slow, which waits until `release()` is called, and starts listening. */
  async function slowServer() {
    const { app, logs } = makeServer();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let entered: () => void = () => {};
    const handlerEntered = new Promise<void>((resolve) => {
      entered = resolve;
    });
    let lateHandlerRuns = 0;
    app.get('/test/slow', async () => {
      entered();
      await gate;
      return { done: true };
    });
    app.get('/test/late', async () => {
      lateHandlerRuns++;
      return { late: true };
    });
    await app.listen({ host: '127.0.0.1', port: 0 });
    const { port } = app.server.address() as { port: number };
    const socket = connect(port, '127.0.0.1');
    await once(socket, 'connect');
    const chunks: Buffer[] = [];
    socket.on('data', (chunk: Buffer) => chunks.push(chunk));
    let socketClosed = false;
    socket.on('close', () => {
      socketClosed = true;
    });
    socket.write('GET /test/slow HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n');
    await handlerEntered;
    return {
      app,
      logs,
      socket,
      release,
      raw: () => Buffer.concat(chunks).toString('utf8'),
      socketClosed: () => socketClosed,
      lateHandlerRuns: () => lateHandlerRuns,
    };
  }

  it('closes a keep-alive connection whose request was in flight, so close() does not wait for the client', async () => {
    // Without the onSend hook the 200 says "Connection: keep-alive" and close() waits until the client hangs up
    // (Fastify's keep-alive timeout is 72 s): Node closes idle connections only once, when close() starts.
    const { app, socket, release, raw, socketClosed } = await slowServer();
    try {
      let closedAt = 0;
      const started = Date.now();
      const closed = app.close().then(() => {
        closedAt = Date.now();
      });
      // Finish the request only once shutdown has really begun (preClose ran and the listener stopped); a response
      // sent earlier would be idle when close() starts, and Node would close its connection anyway.
      await vi.waitFor(() => expect(app.server.listening).toBe(false));
      release();
      await Promise.race([closed, new Promise((resolve) => setTimeout(resolve, 3_000))]);
      expect(closedAt, 'close() resolved').not.toBe(0);
      expect(closedAt - started).toBeLessThan(2_000);
      await vi.waitFor(() => expect(socketClosed()).toBe(true));

      // The response's own head (no other response shares the connection here) carries Connection: close.
      const text = raw();
      expect(text).toMatch(/^HTTP\/1\.1 200 /);
      const head = text.slice(0, text.indexOf('\r\n\r\n'));
      expect(head).toMatch(/^connection: close$/im);
      expect(head).not.toMatch(/keep-alive/i);
      expect(text).toContain('{"done":true}');
    } finally {
      socket.destroy();
    }
  });

  it('refuses a request that arrives on an open connection during shutdown, before its handler runs', async () => {
    const { app, logs, socket, release, raw, socketClosed, lateHandlerRuns } = await slowServer();
    try {
      const closed = app.close();
      await vi.waitFor(() => expect(app.server.listening).toBe(false));
      socket.write('GET /test/late HTTP/1.1\r\nHost: localhost\r\n\r\n');
      await vi.waitFor(() => {
        expect(logs.lines().some((line) => line.msg === 'The server is shutting down')).toBe(true);
      });
      release();
      await closed;
      await vi.waitFor(() => expect(socketClosed()).toBe(true));
      expect(raw()).toContain('{"done":true}');
      expect(lateHandlerRuns()).toBe(0);
      // The refusal is queued behind the in-flight response, which says "Connection: close"; the connection then
      // closes, so (as HTTP/1.1 prescribes for pipelined requests) the client retries the late request elsewhere.
      expect(raw().match(/^HTTP\/1\.1 /gm)).toEqual(['HTTP/1.1 ']);
      const refusal = logs.lines().find((line) => line.msg === 'The server is shutting down');
      expect(refusal).toMatchObject({ level: 40, code: 'unavailable', statusCode: 503 });
    } finally {
      socket.destroy();
    }
  });
});
