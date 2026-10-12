import { z } from 'zod';

/** Every error code the API can return. Clients branch on `code`, never on `message`. */
export const API_ERROR_CODES = [
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
] as const;
export const ApiErrorCode = z.enum(API_ERROR_CODES);
export type ApiErrorCode = z.infer<typeof ApiErrorCode>;

/** The HTTP status that accompanies each error code. */
export const API_ERROR_HTTP_STATUS: Readonly<Record<ApiErrorCode, number>> = {
  bad_request: 400,
  unauthorized: 401,
  forbidden: 403,
  not_found: 404,
  conflict: 409,
  // The client's base revision is behind the server's; it must pull and merge before retrying.
  stale_revision: 409,
  payload_too_large: 413,
  rate_limited: 429,
  internal: 500,
  unavailable: 503,
};

/** The body of every non-2xx API response. */
export const ApiError = z.object({
  error: z.object({
    code: ApiErrorCode,
    /** Human-readable; safe to show, never contains stack traces or secrets. */
    message: z.string().min(1),
    /** Structured, code-specific data (for example validation issues). */
    details: z.unknown().optional(),
    /** Echo of the request's `x-request-id`, for correlating with server logs. */
    requestId: z.string().min(1).optional(),
  }),
});
export type ApiError = z.infer<typeof ApiError>;

/** Builds an {@link ApiError} envelope, omitting absent optional fields. */
export function apiError(
  code: ApiErrorCode,
  message: string,
  options: { details?: unknown; requestId?: string } = {},
): ApiError {
  const error: ApiError['error'] = { code, message };
  if (options.details !== undefined) {
    error.details = options.details;
  }
  if (options.requestId !== undefined) {
    error.requestId = options.requestId;
  }
  return { error };
}

/** `GET /healthz`: the process is up. */
export const HealthResponse = z.object({
  status: z.literal('ok'),
  version: z.string().min(1),
});
export type HealthResponse = z.infer<typeof HealthResponse>;

/** `GET /readyz` (200): dependencies are reachable. A 503 carries an {@link ApiError} with code `unavailable`. */
export const ReadinessResponse = z.object({
  status: z.literal('ready'),
});
export type ReadinessResponse = z.infer<typeof ReadinessResponse>;
