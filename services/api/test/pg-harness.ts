// Real-PostgreSQL helpers for the API tests (DATABASE_URL_TEST, a superuser URL). When the database is
// unreachable, tests that need it are skipped with the reason, unless NIMPAD_REQUIRE_DB=1 (CI): then the test file
// fails at import.

import { randomBytes } from 'node:crypto';
import { createServer, type Socket, connect as tcpConnect } from 'node:net';
import pg from 'pg';
import { maskUrl, TEST_DATABASE_URL } from './helpers.ts';

/** Resolves to null when `url` answers a query within 3 s, else to the reason it does not. */
export async function probeDatabase(url: string): Promise<string | null> {
  const client = new pg.Client({ connectionString: url, connectionTimeoutMillis: 3_000 });
  client.on('error', () => {});
  try {
    await client.connect();
    await client.query('SELECT 1');
    return null;
  } catch (error) {
    return error instanceof Error ? error.message : String(error);
  } finally {
    await client.end().catch(() => {});
  }
}

/**
 * Probes DATABASE_URL_TEST once per test file. Throws when NIMPAD_REQUIRE_DB=1 and it is unreachable; otherwise
 * returns the skip reason (null when the database is available).
 */
export async function databaseGate(): Promise<{ unreachable: string | null; skipReason: string }> {
  const unreachable = await probeDatabase(TEST_DATABASE_URL);
  if (unreachable !== null && process.env.NIMPAD_REQUIRE_DB === '1') {
    throw new Error(
      `NIMPAD_REQUIRE_DB=1 but PostgreSQL at ${maskUrl(TEST_DATABASE_URL)} is unreachable: ${unreachable}`,
    );
  }
  return {
    unreachable,
    skipReason: `PostgreSQL at ${maskUrl(TEST_DATABASE_URL)} is unreachable (${unreachable}); set NIMPAD_REQUIRE_DB=1 to fail instead`,
  };
}

/** `url` with another user, password and/or database. */
export function withUrl(
  url: string,
  change: { user?: string; password?: string; database?: string },
): string {
  const parsed = new URL(url);
  if (change.user !== undefined) {
    parsed.username = encodeURIComponent(change.user);
  }
  if (change.password !== undefined) {
    parsed.password = encodeURIComponent(change.password);
  }
  if (change.database !== undefined) {
    parsed.pathname = `/${encodeURIComponent(change.database)}`;
  }
  return parsed.toString();
}

/** `url` pointing at 127.0.0.1:`port` instead of its own host and port. */
export function viaPort(url: string, port: number): string {
  const parsed = new URL(url);
  parsed.hostname = '127.0.0.1';
  parsed.port = String(port);
  return parsed.toString();
}

async function admin<T>(fn: (client: pg.Client) => Promise<T>, url = TEST_DATABASE_URL): Promise<T> {
  const client = new pg.Client({ connectionString: url });
  await client.connect();
  try {
    return await fn(client);
  } finally {
    await client.end();
  }
}

/** A table every test database has: rows owned by users, visible only to their owner through row-level security. */
export const NOTES_TABLE = 'public.notes';

/** A throwaway database and the LOGIN roles that connect to it. Dropped (roles included) by `drop()`. */
export interface TestDatabase {
  readonly name: string;
  /** The superuser from DATABASE_URL_TEST, on this database: bypasses row-level security. */
  readonly ownerUrl: string;
  /** A LOGIN role that is a member of nimpad_app (and nothing else): how the API is meant to connect. */
  readonly appUrl: string;
  readonly appLogin: string;
  /** Creates another LOGIN role on this database, as a superuser would configure it. */
  createLogin(options: { memberOfApp: boolean; bypassRls?: boolean }): Promise<string>;
  drop(): Promise<void>;
}

/**
 * Creates a database with {@link NOTES_TABLE} (ENABLE + FORCE row-level security, a policy for nimpad_app on
 * `owner = app.user_id`, SELECT and INSERT granted to nimpad_app) and a LOGIN role that is a member of nimpad_app.
 * nimpad_app is created exactly as migration 0001 does when it does not exist yet (roles are cluster-wide; it is
 * left in place, as the migrations leave it).
 */
export async function createTestDatabase(
  rows: ReadonlyArray<{ owner: string; body: string }>,
): Promise<TestDatabase> {
  const name = `nimpad_api_test_${randomBytes(6).toString('hex')}`;
  const logins: string[] = [];
  const createLogin = async (options: { memberOfApp: boolean; bypassRls?: boolean }): Promise<string> => {
    const login = `${name}_${logins.length}`;
    const password = randomBytes(18).toString('hex');
    await admin(async (client) => {
      await client.query(
        `CREATE ROLE ${pg.escapeIdentifier(login)} LOGIN NOSUPERUSER INHERIT ${options.bypassRls ? 'BYPASSRLS' : 'NOBYPASSRLS'} PASSWORD ${pg.escapeLiteral(password)}`,
      );
      logins.push(login);
      if (options.memberOfApp) {
        await client.query(`GRANT nimpad_app TO ${pg.escapeIdentifier(login)}`);
      }
    });
    return withUrl(TEST_DATABASE_URL, { user: login, password, database: name });
  };
  const drop = async (): Promise<void> => {
    await admin(async (client) => {
      await client.query(`DROP DATABASE IF EXISTS ${pg.escapeIdentifier(name)} WITH (FORCE)`);
      for (const login of logins) {
        await client.query(`DROP ROLE IF EXISTS ${pg.escapeIdentifier(login)}`);
      }
    });
  };

  await admin(async (client) => {
    await client.query(`DO $$
      BEGIN
        IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'nimpad_app') THEN
          BEGIN
            CREATE ROLE nimpad_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
          EXCEPTION WHEN duplicate_object OR unique_violation THEN
            NULL;
          END;
        END IF;
      END
      $$`);
    await client.query(`CREATE DATABASE ${pg.escapeIdentifier(name)}`);
  });
  const ownerUrl = withUrl(TEST_DATABASE_URL, { database: name });
  try {
    await admin(async (client) => {
      await client.query(`CREATE TABLE ${NOTES_TABLE} (owner uuid NOT NULL, body text NOT NULL)`);
      await client.query(`ALTER TABLE ${NOTES_TABLE} ENABLE ROW LEVEL SECURITY`);
      await client.query(`ALTER TABLE ${NOTES_TABLE} FORCE ROW LEVEL SECURITY`);
      await client.query(
        `CREATE POLICY notes_owner ON ${NOTES_TABLE} TO nimpad_app
           USING (owner = NULLIF(current_setting('app.user_id', true), '')::uuid)
           WITH CHECK (owner = NULLIF(current_setting('app.user_id', true), '')::uuid)`,
      );
      await client.query(`GRANT SELECT, INSERT ON ${NOTES_TABLE} TO nimpad_app`);
      for (const row of rows) {
        await client.query(`INSERT INTO ${NOTES_TABLE} (owner, body) VALUES ($1, $2)`, [row.owner, row.body]);
      }
    }, ownerUrl);
    const appUrl = await createLogin({ memberOfApp: true });
    return { name, ownerUrl, appUrl, appLogin: logins[0] as string, createLogin, drop };
  } catch (error) {
    await drop();
    throw error;
  }
}

/** A TCP proxy in front of PostgreSQL that can silently stop forwarding, like a half-open connection. */
export interface StallingProxy {
  readonly port: number;
  /** From now on, bytes in both directions are swallowed. Connections stay open. */
  stall(): void;
  /** Forwards again (bytes swallowed meanwhile are lost, so stalled connections are corrupt). */
  resume(): void;
  close(): Promise<void>;
}

export async function startStallingProxy(targetUrl: string): Promise<StallingProxy> {
  const target = new URL(targetUrl);
  const targetPort = Number(target.port || 5432);
  const sockets = new Set<Socket>();
  let stalled = false;
  const server = createServer((client) => {
    const upstream = tcpConnect(targetPort, target.hostname);
    sockets.add(client).add(upstream);
    const destroyBoth = (): void => {
      client.destroy();
      upstream.destroy();
      sockets.delete(client);
      sockets.delete(upstream);
    };
    client.on('data', (chunk) => {
      if (!stalled) {
        upstream.write(chunk);
      }
    });
    upstream.on('data', (chunk) => {
      if (!stalled) {
        client.write(chunk);
      }
    });
    for (const socket of [client, upstream]) {
      socket.on('error', destroyBoth);
      socket.on('close', destroyBoth);
    }
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address() as { port: number };
  return {
    port,
    stall: () => {
      stalled = true;
    },
    resume: () => {
      stalled = false;
    },
    close: async () => {
      for (const socket of sockets) {
        socket.destroy();
      }
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}
