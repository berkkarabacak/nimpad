// Builds the Fastify application. Nothing here listens or connects: main.ts does that, and tests use inject().

import { type IncomingHttpHeaders, STATUS_CODES } from 'node:http';
import { generateUuidV7, type HealthResponse, type ReadinessResponse } from '@nimpad/contracts';
import Fastify, {
  type FastifyInstance,
  type FastifyReply,
  type FastifyRequest,
  LogController,
} from 'fastify';
import packageJson from '../package.json' with { type: 'json' };
import type { ApiConfig } from './config.ts';
import { type ApiDatabase, DEFAULT_READY_CHECK_TIMEOUT_MS, UnsafeDatabaseRoleError } from './db.ts';
import { ApiHttpError, toErrorResponse } from './errors.ts';

/** The API version reported by `GET /healthz`. */
export const API_VERSION: string = packageJson.version;

export const REQUEST_ID_HEADER = 'x-request-id';

/** Default time `GET /readyz` waits for the database before answering 503. */
export const DEFAULT_READINESS_TIMEOUT_MS = DEFAULT_READY_CHECK_TIMEOUT_MS;

/**
 * Log paths whose values are replaced by `[Redacted]`. Request headers are logged (they help debugging), so the
 * credentials among them must never reach the log.
 */
export const REDACTED_LOG_PATHS: readonly string[] = [
  'req.headers.authorization',
  'req.headers.cookie',
  'req.headers["proxy-authorization"]',
  'req.headers["x-api-key"]',
  'res.headers["set-cookie"]',
  'headers.authorization',
  'headers.cookie',
];

// Incoming request ids are echoed into logs and headers, so only a conservative character set is honored.
const VALID_REQUEST_ID = /^[A-Za-z0-9._:-]{1,128}$/;

export interface BuildServerOptions {
  config: Pick<ApiConfig, 'logLevel' | 'bodyLimitBytes'>;
  db: ApiDatabase;
  /** Where log lines go (one JSON object per line). Defaults to stdout; tests pass a capturing stream. */
  logStream?: { write(line: string): void };
  /** How long `GET /readyz` waits for the database check. */
  readinessTimeoutMs?: number;
}

function requestIdFrom(headers: IncomingHttpHeaders): string {
  const incoming = headers[REQUEST_ID_HEADER];
  return typeof incoming === 'string' && VALID_REQUEST_ID.test(incoming) ? incoming : generateUuidV7();
}

/** Rejects with a timeout error if `promise` has not settled within `ms`. */
async function withTimeout<T>(promise: Promise<T>, ms: number, what: string): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(`${what} timed out after ${ms} ms`)), ms);
  });
  try {
    return await Promise.race([promise, timeout]);
  } finally {
    clearTimeout(timer);
  }
}

function sendError(error: unknown, request: FastifyRequest, reply: FastifyReply): FastifyReply {
  const { statusCode, body } = toErrorResponse(error, request.id);
  const summary = { code: body.error.code, statusCode };
  if (statusCode >= 500 && !(error instanceof ApiHttpError)) {
    // Unexpected: full detail (including the stack) goes to the log only; the client gets the generic envelope.
    request.log.error({ err: error, ...summary }, body.error.message);
  } else if (statusCode >= 500) {
    request.log.warn(summary, body.error.message);
  } else {
    request.log.info(summary, body.error.message);
  }
  return reply.code(statusCode).header(REQUEST_ID_HEADER, request.id).send(body);
}

/** Creates the API application with its error envelope, request ids, logging and health routes. */
export function buildServer(options: BuildServerOptions): FastifyInstance {
  const { config, db } = options;
  const readinessTimeoutMs = options.readinessTimeoutMs ?? DEFAULT_READINESS_TIMEOUT_MS;
  let closing = false;

  const app = Fastify({
    logger: {
      level: config.logLevel,
      redact: { paths: [...REDACTED_LOG_PATHS], censor: '[Redacted]' },
      serializers: {
        req: (req: { method?: string; url?: string; headers?: IncomingHttpHeaders; ip?: string }) => ({
          method: req.method,
          url: req.url,
          headers: req.headers,
          remoteAddress: req.ip,
        }),
      },
      ...(options.logStream ? { stream: options.logStream } : {}),
    },
    bodyLimit: config.bodyLimitBytes,
    genReqId: (req) => requestIdFrom(req.headers),
    logController: new LogController({ requestIdLogLabel: 'requestId' }),
    // Fastify's built-in 503 while closing uses its own body; the onRequest hook below answers with the envelope.
    return503OnClosing: false,
    frameworkErrors: (error, request, reply) => {
      sendError(error, request, reply);
    },
    clientErrorHandler: (error, socket) => {
      // Malformed HTTP never reaches Fastify's routing; answer with the envelope and drop the connection.
      const code = (error as NodeJS.ErrnoException).code;
      if (code === 'ECONNRESET' || socket.destroyed) {
        return;
      }
      if (socket.writable) {
        const status = code === 'ERR_HTTP_REQUEST_TIMEOUT' ? 408 : code === 'HPE_HEADER_OVERFLOW' ? 431 : 400;
        const body = JSON.stringify(toErrorResponse({ statusCode: status }).body);
        socket.write(
          `HTTP/1.1 ${status} ${STATUS_CODES[status]}\r\nConnection: close\r\nContent-Type: application/json; charset=utf-8\r\n` +
            `Content-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`,
        );
      }
      socket.destroy(error);
    },
  });

  app.addHook('preClose', async () => {
    closing = true;
  });

  app.addHook('onRequest', async (request, reply) => {
    reply.header(REQUEST_ID_HEADER, request.id);
    if (closing) {
      throw new ApiHttpError('unavailable', 'The server is shutting down');
    }
  });

  // Responses finished during shutdown close their connection, so a keep-alive client cannot hold close() open.
  app.addHook('onSend', async (_request, reply) => {
    if (closing) {
      reply.header('connection', 'close');
    }
  });

  app.setErrorHandler((error, request, reply) => sendError(error, request, reply));

  app.setNotFoundHandler((request, reply) =>
    sendError(
      new ApiHttpError('not_found', `Route ${request.method} ${request.url} not found`),
      request,
      reply,
    ),
  );

  app.get('/healthz', async (_request, reply): Promise<HealthResponse> => {
    reply.header('cache-control', 'no-store');
    return { status: 'ok', version: API_VERSION };
  });

  app.get('/readyz', async (request, reply): Promise<ReadinessResponse> => {
    reply.header('cache-control', 'no-store');
    try {
      await withTimeout(db.checkReady(), readinessTimeoutMs, 'Database readiness check');
    } catch (error) {
      if (error instanceof UnsafeDatabaseRoleError) {
        // A configuration error, not an outage: serving with this role would expose every user's rows.
        request.log.error({ err: error, role: error.role }, 'database role bypasses row-level security');
      } else {
        request.log.warn({ err: error }, 'readiness check failed');
      }
      throw new ApiHttpError('unavailable', 'The database is not ready');
    }
    return { status: 'ready' };
  });

  return app;
}
