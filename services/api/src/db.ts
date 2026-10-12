// PostgreSQL access. Every query that touches user data runs inside withUserTx, which scopes the row-level
// security identity (`app.user_id`, read by nimpad.current_user_id()) to one transaction so that it can never
// leak to the next user of a pooled connection (docs/architecture.md, "Server data model").
//
// Row-level security only protects anything when the API's role is subject to it, so the role is verified
// (checkDatabaseRole) before the API reports ready, at startup, and before the first transaction of a pool.
//
// Every wait is bounded. A connection whose server never answers (a half-open TCP connection after a failover, a
// proxy that stops forwarding) fails its query after the client-side query timeout and is destroyed, never handed
// to the next caller; the server cancels statements and ends idle transactions on its own as well, so a stalled
// request cannot keep row locks.

import { Uuid } from '@nimpad/contracts';
import { Pool, type PoolClient, type PoolConfig, type QueryConfig } from 'pg';

/** The role migration 0001 creates for the API: not the owner, no BYPASSRLS, subject to every policy. */
export const APP_ROLE = 'nimpad_app';

/** Server-side limit for one statement inside {@link withUserTx} (`statement_timeout`). */
export const DEFAULT_STATEMENT_TIMEOUT_MS = 15_000;

/** Server-side limit for a transaction left idle by its client (`idle_in_transaction_session_timeout`). */
export const DEFAULT_IDLE_IN_TRANSACTION_TIMEOUT_MS = 15_000;

/** How long the readiness and role check waits for its query before the connection is given up. */
export const DEFAULT_READY_CHECK_TIMEOUT_MS = 2_000;

/** Pool settings used by the API unless overridden. */
export const DEFAULT_POOL_OPTIONS = {
  max: 10,
  idleTimeoutMillis: 30_000,
  // Fail fast instead of hanging requests (and readiness checks) when the database is unreachable.
  connectionTimeoutMillis: 5_000,
  // TCP keepalive lets the operating system notice a server that vanished while a connection sat idle.
  keepAlive: true,
  keepAliveInitialDelayMillis: 10_000,
  // Client-side bound on every query, longer than the server-side statement timeout so that the server normally
  // cancels first (which leaves the connection usable). When it fires the server never answered at all; the
  // connection is then destroyed (see withUserTx and ping).
  query_timeout: DEFAULT_STATEMENT_TIMEOUT_MS + 5_000,
  application_name: 'nimpad-api',
} as const satisfies PoolConfig;

// pg's message for a query whose `query_timeout` expired. pg does not cancel such a query or close its
// connection, so the connection must not be used again.
const QUERY_TIMEOUT_MESSAGE = 'Query read timeout';

/** A query with its own client-side timeout. pg reads `query_timeout` per query; @types/pg does not declare it. */
interface TimedQueryConfig extends QueryConfig {
  query_timeout: number;
}

function isQueryTimeout(error: unknown): boolean {
  return error instanceof Error && error.message === QUERY_TIMEOUT_MESSAGE;
}

function toError(value: unknown): Error {
  return value instanceof Error ? value : new Error(String(value));
}

/**
 * Creates the connection pool. The URL must name a role that is subject to row-level security: `nimpad_app` or a
 * LOGIN role that is a member of it, never the owner or a superuser (see {@link checkDatabaseRole}).
 *
 * @param onIdleClientError Called when an idle pooled connection fails (for example when the server restarts).
 *   The pool discards that connection by itself; without a listener the error would crash the process.
 */
export function createPool(
  databaseUrl: string,
  onIdleClientError: (error: Error) => void,
  options: PoolConfig = {},
): Pool {
  const pool = new Pool({ ...DEFAULT_POOL_OPTIONS, ...options, connectionString: databaseUrl });
  pool.on('error', onIdleClientError);
  return pool;
}

/**
 * Resolves when the database answers a trivial query; rejects otherwise. The query gives up after `timeoutMs`,
 * and a connection that timed out is destroyed rather than returned to the pool.
 */
export async function ping(pool: Pool, timeoutMs: number = DEFAULT_READY_CHECK_TIMEOUT_MS): Promise<void> {
  // pool.query releases the connection with the query's error, which destroys it.
  const query: TimedQueryConfig = { text: 'SELECT 1', query_timeout: timeoutMs };
  await pool.query(query);
}

/** Thrown when the database role would bypass row-level security, or lacks the app role's privileges. */
export class UnsafeDatabaseRoleError extends Error {
  /** The connected role. */
  readonly role: string;
  /** Every problem found, in words. */
  readonly problems: readonly string[];

  constructor(role: string, problems: readonly string[]) {
    super(`The database role "${role}" must not be used by the API: ${problems.join('; ')}`);
    this.name = 'UnsafeDatabaseRoleError';
    this.role = role;
    this.problems = problems;
  }
}

/** Options of {@link checkDatabaseRole}. */
export interface RoleCheckOptions {
  /** The role whose privileges the API's role must hold. Default {@link APP_ROLE}. */
  appRole?: string;
  /** How long to wait for the query. Default {@link DEFAULT_READY_CHECK_TIMEOUT_MS}. */
  timeoutMs?: number;
}

/**
 * Verifies, in one query, that the database answers and that the connected role is subject to row-level
 * security: not a superuser, no BYPASSRLS (either one skips every policy, even with FORCE ROW LEVEL SECURITY), and
 * holding the app role's privileges (a member of `nimpad_app` with INHERIT, or `nimpad_app` itself).
 *
 * @throws UnsafeDatabaseRoleError when the role is not safe to serve requests with.
 * @throws Error when the database cannot be reached or does not answer within the timeout.
 */
export async function checkDatabaseRole(pool: Pool, options: RoleCheckOptions = {}): Promise<void> {
  const appRole = options.appRole ?? APP_ROLE;
  const query: TimedQueryConfig = {
    text: `SELECT r.rolname AS role, r.rolsuper AS superuser, r.rolbypassrls AS bypass_rls,
             (SELECT pg_catalog.pg_has_role(r.oid, a.oid, 'USAGE')
                FROM pg_catalog.pg_roles a WHERE a.rolname = $1) AS has_app_privileges
           FROM pg_catalog.pg_roles r WHERE r.rolname = current_user`,
    values: [appRole],
    query_timeout: options.timeoutMs ?? DEFAULT_READY_CHECK_TIMEOUT_MS,
  };
  // pool.query releases the connection with the query's error, which destroys a connection that timed out.
  const { rows } = await pool.query<{
    role: string;
    superuser: boolean;
    bypass_rls: boolean;
    has_app_privileges: boolean | null;
  }>(query);
  const row = rows[0];
  if (row === undefined) {
    throw new Error('The current database role is not listed in pg_roles');
  }
  const problems: string[] = [];
  if (row.superuser) {
    problems.push('it is a superuser, so row-level security does not apply');
  }
  if (row.bypass_rls) {
    problems.push('it has BYPASSRLS, so row-level security does not apply');
  }
  if (row.has_app_privileges === null) {
    problems.push(`role "${appRole}" does not exist (run the database migrations)`);
  } else if (!row.has_app_privileges && !row.superuser) {
    problems.push(`it does not have the privileges of "${appRole}" (GRANT ${appRole} TO ${row.role})`);
  }
  if (problems.length > 0) {
    throw new UnsafeDatabaseRoleError(row.role, problems);
  }
}

/** Server-side timeouts applied to each {@link withUserTx} transaction. */
export interface TransactionTimeouts {
  /** `statement_timeout` for the transaction, in ms. Default {@link DEFAULT_STATEMENT_TIMEOUT_MS}. */
  statementTimeoutMs?: number;
  /**
   * `idle_in_transaction_session_timeout` for the transaction, in ms: the server ends the session when `fn` keeps
   * the transaction open without sending a statement for this long. Default
   * {@link DEFAULT_IDLE_IN_TRANSACTION_TIMEOUT_MS}.
   */
  idleInTransactionTimeoutMs?: number;
}

function timeoutSetting(name: string, value: number): string {
  if (!Number.isInteger(value) || value < 1) {
    throw new RangeError(`${name} must be a positive integer number of milliseconds: ${value}`);
  }
  return String(value);
}

/**
 * Runs `fn` in a transaction on behalf of `userId`: `BEGIN`, then one statement that sets `app.user_id`,
 * `statement_timeout` and `idle_in_transaction_session_timeout` with `set_config(…, true)`, then `fn(client)`, then
 * `COMMIT`. All three settings are transaction-local, so they disappear at commit or rollback.
 *
 * If `fn` (or the commit) throws, the transaction is rolled back and the original error is rethrown. The client
 * goes back to the pool unless its state is unknown, in which case it is destroyed: when the connection failed,
 * when a query timed out on the client side (the server never answered, so a rollback would hang too), or when the
 * rollback itself failed.
 *
 * @throws TypeError when `userId` is not a canonical UUID (nothing is sent to the database).
 */
export async function withUserTx<T>(
  pool: Pool,
  userId: string,
  fn: (client: PoolClient) => Promise<T>,
  timeouts: TransactionTimeouts = {},
): Promise<T> {
  if (!Uuid.safeParse(userId).success) {
    throw new TypeError('withUserTx: userId must be a lowercase hyphenated UUID');
  }
  const statementTimeout = timeoutSetting(
    'statementTimeoutMs',
    timeouts.statementTimeoutMs ?? DEFAULT_STATEMENT_TIMEOUT_MS,
  );
  const idleTimeout = timeoutSetting(
    'idleInTransactionTimeoutMs',
    timeouts.idleInTransactionTimeoutMs ?? DEFAULT_IDLE_IN_TRANSACTION_TIMEOUT_MS,
  );
  const client = await pool.connect();
  // While a client is checked out the pool does not listen for its errors; an unhandled 'error' event (for
  // example the connection being terminated mid-transaction) would crash the process. The failing query rejects
  // on its own, so the listener only records that the connection is unusable.
  let discard: Error | undefined;
  const onClientError = (error: Error): void => {
    discard ??= error;
  };
  client.on('error', onClientError);
  try {
    await client.query('BEGIN');
    await client.query(
      `SELECT set_config('app.user_id', $1, true), set_config('statement_timeout', $2, true),
              set_config('idle_in_transaction_session_timeout', $3, true)`,
      [userId, statementTimeout, idleTimeout],
    );
    const result = await fn(client);
    await client.query('COMMIT');
    return result;
  } catch (error) {
    if (isQueryTimeout(error)) {
      discard ??= toError(error);
    }
    if (discard === undefined) {
      try {
        await client.query('ROLLBACK');
      } catch (rollbackError) {
        discard ??= toError(rollbackError);
      }
    }
    throw error;
  } finally {
    client.off('error', onClientError);
    // Releasing with an error destroys the connection instead of returning it to the pool.
    client.release(discard);
  }
}

/** What the HTTP layer needs from the database. */
export interface ApiDatabase {
  /**
   * Resolves when the database answers and the API's role is subject to row-level security; used by
   * `GET /readyz`. Rejects with {@link UnsafeDatabaseRoleError} for an unsafe role, or another error when the
   * database cannot be reached in time.
   */
  checkReady(): Promise<void>;
}

/** The production {@link ApiDatabase}: a pg pool plus its lifecycle. */
export interface PostgresDatabase extends ApiDatabase {
  readonly pool: Pool;
  /**
   * {@link withUserTx} on this pool. The first call (and any call after a failed check) verifies the role with
   * {@link checkDatabaseRole}, so no user data is ever read or written with a role that bypasses row-level
   * security.
   */
  withUser<T>(userId: string, fn: (client: PoolClient) => Promise<T>): Promise<T>;
  /** Closes every pooled connection. Call once, after the HTTP server has stopped. */
  close(): Promise<void>;
}

/** Options of {@link createDatabase}. */
export interface DatabaseOptions extends TransactionTimeouts {
  /** See {@link RoleCheckOptions.appRole}. */
  appRole?: string;
  /** How long the readiness and role check waits. Default {@link DEFAULT_READY_CHECK_TIMEOUT_MS}. */
  readyCheckTimeoutMs?: number;
}

/**
 * Wraps `pool` as the API's database: readiness and role checks with {@link checkDatabaseRole}, user transactions
 * with {@link withUserTx} behind a one-time role verification, and `close()`.
 */
export function createDatabase(pool: Pool, options: DatabaseOptions = {}): PostgresDatabase {
  const roleCheck: RoleCheckOptions = {};
  if (options.appRole !== undefined) {
    roleCheck.appRole = options.appRole;
  }
  if (options.readyCheckTimeoutMs !== undefined) {
    roleCheck.timeoutMs = options.readyCheckTimeoutMs;
  }
  const timeouts: TransactionTimeouts = {};
  if (options.statementTimeoutMs !== undefined) {
    timeouts.statementTimeoutMs = options.statementTimeoutMs;
  }
  if (options.idleInTransactionTimeoutMs !== undefined) {
    timeouts.idleInTransactionTimeoutMs = options.idleInTransactionTimeoutMs;
  }
  // Shared by concurrent first calls; forgotten when it fails so that the next call checks again.
  let verified: Promise<void> | undefined;
  const verifyRole = (): Promise<void> => {
    verified ??= checkDatabaseRole(pool, roleCheck).catch((error: unknown) => {
      verified = undefined;
      throw error;
    });
    return verified;
  };
  return {
    pool,
    checkReady: () => checkDatabaseRole(pool, roleCheck),
    async withUser(userId, fn) {
      await verifyRole();
      return withUserTx(pool, userId, fn, timeouts);
    },
    close: () => pool.end(),
  };
}
