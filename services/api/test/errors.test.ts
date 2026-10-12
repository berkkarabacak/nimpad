import { ApiError } from '@nimpad/contracts';
import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import { ApiHttpError, parseRequest, toErrorResponse } from '../src/errors.ts';

describe('toErrorResponse', () => {
  it('passes ApiHttpError through with its status', () => {
    const response = toErrorResponse(
      new ApiHttpError('stale_revision', 'Pull first', { details: { rev: 3 } }),
      'r1',
    );
    expect(response).toEqual({
      statusCode: 409,
      body: {
        error: { code: 'stale_revision', message: 'Pull first', details: { rev: 3 }, requestId: 'r1' },
      },
    });
  });

  it('treats a bare ZodError as a server bug (500), never as a bad request', () => {
    // Request input is validated with parseRequest; a ZodError escaping a handler means server data (a database
    // row, a stored payload, a response) failed validation, which must not be blamed on the client.
    const result = z.object({ name: z.string(), tags: z.array(z.string()) }).safeParse({ tags: ['a', 1] });
    expect(result.success).toBe(false);
    expect(toErrorResponse(result.error, 'r2')).toEqual({
      statusCode: 500,
      body: { error: { code: 'internal', message: 'Internal server error', requestId: 'r2' } },
    });
  });

  it('maps Fastify validation errors with their context', () => {
    const error = Object.assign(new Error('body/name must be string'), {
      statusCode: 400,
      validationContext: 'body',
      validation: [{ instancePath: '/name', message: 'must be string' }],
    });
    expect(toErrorResponse(error).body.error.details).toEqual([
      { path: 'body.name', message: 'must be string' },
    ]);
  });

  it.each([
    [400, 'bad_request'],
    [401, 'unauthorized'],
    [403, 'forbidden'],
    [404, 'not_found'],
    [409, 'conflict'],
    [413, 'payload_too_large'],
    [415, 'bad_request'],
    [429, 'rate_limited'],
    [503, 'unavailable'],
  ] as const)('maps status %i to %s', (statusCode, code) => {
    const response = toErrorResponse(Object.assign(new Error('x'), { statusCode }));
    expect(response.statusCode).toBe(statusCode);
    expect(response.body.error.code).toBe(code);
    expect(ApiError.safeParse(response.body).success).toBe(true);
  });

  it('hides the message of unexpected errors', () => {
    for (const thrown of [
      new Error('connect ECONNREFUSED postgres://app:hunter2@db'),
      Object.assign(new Error('boom'), { statusCode: 502 }),
      'a string',
      null,
    ]) {
      const response = toErrorResponse(thrown);
      expect(response).toEqual({
        statusCode: 500,
        body: { error: { code: 'internal', message: 'Internal server error' } },
      });
    }
  });
});

describe('parseRequest', () => {
  const Body = z.object({ name: z.string().min(1), tags: z.array(z.string()) });

  it('returns the parsed value', () => {
    expect(parseRequest(Body, { name: 'a', tags: [] }, 'body')).toEqual({ name: 'a', tags: [] });
  });

  it('throws bad_request with each issue located in the request', () => {
    let thrown: unknown;
    try {
      parseRequest(Body, { tags: ['a', 1] }, 'body');
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(ApiHttpError);
    const response = toErrorResponse(thrown, 'r3');
    expect(response.statusCode).toBe(400);
    expect(response.body.error).toEqual({
      code: 'bad_request',
      message: 'Request validation failed',
      details: [
        { path: 'body.name', message: expect.any(String) },
        { path: 'body.tags.1', message: expect.any(String) },
      ],
      requestId: 'r3',
    });
  });

  it('prefixes the request part even for a top-level issue', () => {
    expect(() => parseRequest(z.string(), 42, 'querystring')).toThrow(ApiHttpError);
    try {
      parseRequest(z.string(), 42, 'querystring');
    } catch (error) {
      expect((error as ApiHttpError).details).toEqual([{ path: 'querystring', message: expect.any(String) }]);
    }
  });
});
