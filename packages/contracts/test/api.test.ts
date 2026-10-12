import { describe, expect, it } from 'vitest';
import {
  API_ERROR_CODES,
  API_ERROR_HTTP_STATUS,
  ApiError,
  ApiErrorCode,
  apiError,
  HealthResponse,
  ReadinessResponse,
} from '../src/api.ts';

describe('ApiError', () => {
  it('has the fixed list of codes', () => {
    expect(API_ERROR_CODES).toEqual([
      'bad_request',
      'unauthorized',
      'forbidden',
      'not_found',
      'conflict',
      'stale_revision',
      'payload_too_large',
      'rate_limited',
      'internal',
      'unavailable',
    ]);
    expect(ApiErrorCode.options).toEqual([...API_ERROR_CODES]);
  });

  it('maps every code to an HTTP error status', () => {
    expect(Object.keys(API_ERROR_HTTP_STATUS).sort()).toEqual([...API_ERROR_CODES].sort());
    for (const status of Object.values(API_ERROR_HTTP_STATUS)) {
      expect(status).toBeGreaterThanOrEqual(400);
      expect(status).toBeLessThan(600);
    }
    expect(API_ERROR_HTTP_STATUS.payload_too_large).toBe(413);
    expect(API_ERROR_HTTP_STATUS.unavailable).toBe(503);
  });

  it('builds envelopes without absent optional fields', () => {
    expect(apiError('not_found', 'Note not found')).toEqual({
      error: { code: 'not_found', message: 'Note not found' },
    });
    const full = apiError('bad_request', 'Invalid body', { details: [{ path: 'name' }], requestId: 'req-1' });
    expect(full).toEqual({
      error: {
        code: 'bad_request',
        message: 'Invalid body',
        details: [{ path: 'name' }],
        requestId: 'req-1',
      },
    });
    expect(ApiError.parse(full)).toEqual(full);
    expect(Object.keys(apiError('internal', 'x').error)).toEqual(['code', 'message']);
  });

  it.each([
    { error: { code: 'teapot', message: 'x' } },
    { error: { code: 'internal', message: '' } },
    { error: { code: 'internal' } },
    { code: 'internal', message: 'x' },
  ])('rejects %j', (value) => {
    expect(ApiError.safeParse(value).success).toBe(false);
  });
});

describe('health and readiness responses', () => {
  it('validates the documented shapes', () => {
    expect(HealthResponse.safeParse({ status: 'ok', version: '0.1.0' }).success).toBe(true);
    expect(HealthResponse.safeParse({ status: 'ok' }).success).toBe(false);
    expect(ReadinessResponse.safeParse({ status: 'ready' }).success).toBe(true);
    expect(ReadinessResponse.safeParse({ status: 'ok' }).success).toBe(false);
  });
});
