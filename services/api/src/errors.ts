// Maps anything thrown while handling a request to the ApiError envelope from @nimpad/contracts.
// Clients branch on `error.code`; messages of 5xx errors are generic so that internals never leak.

import { API_ERROR_HTTP_STATUS, type ApiError, type ApiErrorCode, apiError } from '@nimpad/contracts';
import type { z } from 'zod';

/** An error a route handler throws on purpose; it reaches the client as-is (code, message, details). */
export class ApiHttpError extends Error {
  readonly code: ApiErrorCode;
  readonly statusCode: number;
  readonly details: unknown;

  constructor(code: ApiErrorCode, message: string, options: { details?: unknown; cause?: unknown } = {}) {
    super(message, { cause: options.cause });
    this.name = 'ApiHttpError';
    this.code = code;
    this.statusCode = API_ERROR_HTTP_STATUS[code];
    this.details = options.details;
  }
}

/** A field-level validation problem, as listed in `details` of a `bad_request` error. */
export interface ValidationIssue {
  /** Where the problem is, e.g. `body.name` or `querystring.limit`. */
  path: string;
  message: string;
}

export interface ErrorResponse {
  statusCode: number;
  body: ApiError;
}

const CODE_BY_STATUS: Readonly<Record<number, ApiErrorCode>> = {
  400: 'bad_request',
  401: 'unauthorized',
  403: 'forbidden',
  404: 'not_found',
  409: 'conflict',
  413: 'payload_too_large',
  429: 'rate_limited',
  503: 'unavailable',
};

interface FastifyValidationError {
  validation: ReadonlyArray<{ instancePath?: string; message?: string }>;
  validationContext?: string;
}

function isFastifyValidationError(error: unknown): error is FastifyValidationError {
  return (
    typeof error === 'object' &&
    error !== null &&
    Array.isArray((error as { validation?: unknown }).validation)
  );
}

function statusCodeOf(error: unknown): number | undefined {
  if (typeof error !== 'object' || error === null) {
    return undefined;
  }
  const status = (error as { statusCode?: unknown }).statusCode;
  return typeof status === 'number' && Number.isInteger(status) ? status : undefined;
}

function joinPath(context: string | undefined, path: ReadonlyArray<PropertyKey>): string {
  return [context, ...path.map(String)].filter((part) => part !== undefined && part !== '').join('.');
}

/** The part of a request that {@link parseRequest} validates; it prefixes each issue path. */
export type RequestPart = 'body' | 'querystring' | 'params' | 'headers';

/**
 * Validates one part of a request with a zod schema and returns the parsed value. Invalid input becomes a
 * `bad_request` {@link ApiHttpError} whose `details` list each issue (`body.name: …`).
 *
 * Route handlers validate client input with this function, never with a bare `schema.parse`: a `ZodError` that
 * escapes a handler means server data failed validation (a bug), which {@link toErrorResponse} reports as a 500.
 */
export function parseRequest<Schema extends z.ZodType>(
  schema: Schema,
  value: unknown,
  part: RequestPart,
): z.output<Schema> {
  const result = schema.safeParse(value);
  if (result.success) {
    return result.data;
  }
  const details: ValidationIssue[] = result.error.issues.map((issue) => ({
    path: joinPath(part, issue.path),
    message: issue.message,
  }));
  throw new ApiHttpError('bad_request', 'Request validation failed', { details, cause: result.error });
}

/**
 * Converts any thrown value into a status code and an ApiError body.
 *
 * - {@link ApiHttpError}: its own code, message and details (request validation through {@link parseRequest} is one).
 * - Fastify schema validation errors (request validation by route schemas): `bad_request` with `details` listing
 *   each issue.
 * - Other errors carrying a 4xx `statusCode` (Fastify's own, such as a malformed JSON body): the matching code,
 *   or `bad_request` for 4xx statuses without a dedicated code (the HTTP status is kept, e.g. 415).
 * - A 503 `statusCode`: `unavailable`.
 * - Anything else, including a bare `ZodError` (server-side data that failed validation is a bug, not a bad
 *   request): 500 `internal` with a generic message.
 */
export function toErrorResponse(error: unknown, requestId?: string): ErrorResponse {
  const build = (
    statusCode: number,
    code: ApiErrorCode,
    message: string,
    details?: unknown,
  ): ErrorResponse => {
    const options: { details?: unknown; requestId?: string } = {};
    if (details !== undefined) {
      options.details = details;
    }
    if (requestId !== undefined) {
      options.requestId = requestId;
    }
    return { statusCode, body: apiError(code, message, options) };
  };

  if (error instanceof ApiHttpError) {
    return build(error.statusCode, error.code, error.message, error.details);
  }
  if (isFastifyValidationError(error)) {
    const details: ValidationIssue[] = error.validation.map((issue) => ({
      path: joinPath(error.validationContext, (issue.instancePath ?? '').split('/').slice(1)),
      message: issue.message ?? 'is invalid',
    }));
    return build(400, 'bad_request', 'Request validation failed', details);
  }

  const status = statusCodeOf(error);
  if (status === 413) {
    return build(413, 'payload_too_large', 'Request body is too large');
  }
  if (status !== undefined && status >= 400 && status < 500) {
    const message = error instanceof Error && error.message !== '' ? error.message : 'Bad request';
    return build(status, CODE_BY_STATUS[status] ?? 'bad_request', message);
  }
  if (status === 503) {
    return build(503, 'unavailable', 'Service unavailable');
  }
  return build(500, 'internal', 'Internal server error');
}
