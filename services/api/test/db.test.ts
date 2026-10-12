// withUserTx, the role check and readiness against a real PostgreSQL (DATABASE_URL_TEST). When the database is
// unreachable these tests are skipped with the reason, unless NIMPAD_REQUIRE_DB=1 (CI), in which case the file fails.

import { ApiError } from '@nimpad/contracts';
import type { Pool, PoolClient } from 'pg';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import {
  checkDatabaseRole,
  createDatabase,
  createPool,
  DEFAULT_POOL_OPTIONS,
  DEFAULT_STATEMENT_TIMEOUT_MS,
  ping,
  UnsafeDatabaseRoleError,
  withUserTx,
} from '../src/db.ts';
import { buildServer } from '../src/server.ts';
import { captureLogs, maskUrl, TEST_DATABASE_URL, testConfig } from './helpers.ts';
import {
  createTestDatabase,
  databaseGate,
  NOTES_TABLE,
  type StallingProxy,
  startStallingProxy,
  type TestDatabase,
  viaPort,
} from './pg-harness.ts';

const USER_A = '01a13069-8113-7d4b-b45c-a607c0eb8f23';
const USER_B = '3f0c2a4e-9b1d-4c6e-8f7a-2b5d9e1c0a34';

const { unreachable, skipReason } = await databaseGate();

async function settingAndPid(client: Pool | PoolClient): Promise<{ userId: string | null; pid: number }> {
  const { rows } = await client.query<{ user_id: string | null; pid: number }>(
    "SELECT current_setting('app.user_id', true) AS user_id, pg_backend_pid() AS pid",
  );
  const row = rows[0];
  if (row === undefined) {
    throw new Error('no row');
  }
  return { userId: row.user_id, pid: row.pid };
}

async function show(client: Pool | PoolClient, setting: string): Promise<string> {
  const { rows } = await client.query<Record<string, string>>(`SHOW ${setting}`);
  return Object.values(rows[0] ?? {})[0] ?? '';
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe('withUserTx without a database', () => {
  it('rejects a malformed user id before connecting', async () => {
    const pool = createPool('postgres://nobody:nothing@127.0.0.1:1/none', () => {});
    let called = false;
    for (const userId of ['', 'not-a-uuid', USER_A.toUpperCase(), `${USER_A}'; DROP TABLE x; --`]) {
      await expect(
        withUserTx(pool, userId, async () => {
          called = true;
        }),
      ).rejects.toThrow(TypeError);
    }
    expect(called).toBe(false);
    expect(pool.totalCount).toBe(0);
    await pool.end();
  });

  it('rejects invalid timeouts before connecting', async () => {
    const pool = createPool('postgres://nobody:nothing@127.0.0.1:1/none', () => {});
    for (const timeouts of [{ statementTimeoutMs: 0 }, { idleInTransactionTimeoutMs: 1.5 }]) {
      await expect(withUserTx(pool, USER_A, async () => {}, timeouts)).rejects.toThrow(RangeError);
    }
    expect(pool.totalCount).toBe(0);
    await pool.end();
  });

  it('bounds every query on the client side and keeps TCP alive by default', async () => {
    // The client-side timeout outlasts the server-side statement timeout, so the server normally cancels first.
    expect(DEFAULT_POOL_OPTIONS.query_timeout).toBeGreaterThan(DEFAULT_STATEMENT_TIMEOUT_MS);
    const pool = createPool('postgres://nobody:nothing@127.0.0.1:1/none', () => {});
    expect(pool.options).toMatchObject({
      keepAlive: true,
      query_timeout: DEFAULT_POOL_OPTIONS.query_timeout,
      connectionTimeoutMillis: DEFAULT_POOL_OPTIONS.connectionTimeoutMillis,
    });
    await pool.end();
  });

  it('ping rejects when the database is unreachable', async () => {
    const pool = createPool('postgres://nobody:nothing@127.0.0.1:1/none', () => {});
    await expect(ping(pool)).rejects.toThrow();
    await pool.end();
  });
});

describe(`withUserTx against PostgreSQL (${maskUrl(TEST_DATABASE_URL)})`, () => {
  // One connection, so "the same pooled connection" is guaranteed and checked via pg_backend_pid().
  let pool: Pool;
  const idleErrors: Error[] = [];

  beforeAll(() => {
    pool = createPool(TEST_DATABASE_URL, (error) => idleErrors.push(error), { max: 1 });
  });

  afterEach(async () => {
    if (unreachable === null) {
      await pool.query('DROP TABLE IF EXISTS pg_temp.with_user_tx_probe');
      expect(pool.waitingCount).toBe(0);
      expect(idleErrors).toEqual([]);
    }
  });

  afterAll(async () => {
    await pool?.end();
  });

  it('makes app.user_id visible inside the transaction only', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const before = await settingAndPid(pool);
    expect(before.userId === null || before.userId === '').toBe(true);

    // set_config(..., true) is transaction-local: seeing it in a later statement proves fn runs inside the
    // explicit transaction (in autocommit mode it would vanish when its own statement ended).
    const inside = await withUserTx(pool, USER_A, (client) => settingAndPid(client));
    expect(inside.userId).toBe(USER_A);
    expect(inside.pid).toBe(before.pid);

    const after = await settingAndPid(pool);
    expect(after.pid).toBe(before.pid);
    expect(after.userId === null || after.userId === '').toBe(true);
    expect(after.userId).not.toBe(USER_A);
  });

  it('does not carry one user into the next transaction on the same connection', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const first = await withUserTx(pool, USER_A, (client) => settingAndPid(client));
    const second = await withUserTx(pool, USER_B, (client) => settingAndPid(client));
    expect(second.pid).toBe(first.pid);
    expect(first.userId).toBe(USER_A);
    expect(second.userId).toBe(USER_B);
  });

  it('sets server-side statement and idle-in-transaction timeouts for the transaction only', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const before = {
      statement: await show(pool, 'statement_timeout'),
      idle: await show(pool, 'idle_in_transaction_session_timeout'),
    };
    const defaults = await withUserTx(pool, USER_A, async (client) => ({
      statement: await show(client, 'statement_timeout'),
      idle: await show(client, 'idle_in_transaction_session_timeout'),
    }));
    expect(defaults).toEqual({ statement: '15s', idle: '15s' });
    const custom = await withUserTx(
      pool,
      USER_A,
      async (client) => ({
        statement: await show(client, 'statement_timeout'),
        idle: await show(client, 'idle_in_transaction_session_timeout'),
      }),
      { statementTimeoutMs: 1_234, idleInTransactionTimeoutMs: 2_500 },
    );
    expect(custom).toEqual({ statement: '1234ms', idle: '2500ms' });
    expect(await show(pool, 'statement_timeout')).toBe(before.statement);
    expect(await show(pool, 'idle_in_transaction_session_timeout')).toBe(before.idle);
  });

  it('commits the work of fn and returns its result', async (context) => {
    context.skip(unreachable !== null, skipReason);
    await pool.query('CREATE TEMP TABLE with_user_tx_probe (v text NOT NULL)');
    const result = await withUserTx(pool, USER_A, async (client) => {
      await client.query("INSERT INTO with_user_tx_probe (v) VALUES (current_setting('app.user_id'))");
      return 'done';
    });
    expect(result).toBe('done');
    const { rows } = await pool.query<{ v: string }>('SELECT v FROM with_user_tx_probe');
    expect(rows).toEqual([{ v: USER_A }]);
  });

  it('rolls back and rethrows the original error when fn throws', async (context) => {
    context.skip(unreachable !== null, skipReason);
    await pool.query('CREATE TEMP TABLE with_user_tx_probe (v text NOT NULL)');
    const failure = new Error('fn failed');
    const { pid } = await settingAndPid(pool);
    await expect(
      withUserTx(pool, USER_A, async (client) => {
        await client.query("INSERT INTO with_user_tx_probe (v) VALUES ('should vanish')");
        throw failure;
      }),
    ).rejects.toBe(failure);

    const { rows } = await pool.query('SELECT v FROM with_user_tx_probe');
    expect(rows).toEqual([]);
    // The connection went back to the pool clean: same backend, no open transaction, no user.
    const after = await settingAndPid(pool);
    expect(after.pid).toBe(pid);
    expect(after.userId === null || after.userId === '').toBe(true);
    expect(pool.totalCount).toBe(1);
    expect(pool.idleCount).toBe(1);
  });

  it('rolls back when a statement inside fn fails', async (context) => {
    context.skip(unreachable !== null, skipReason);
    await pool.query('CREATE TEMP TABLE with_user_tx_probe (v text NOT NULL)');
    await expect(
      withUserTx(pool, USER_A, async (client) => {
        await client.query("INSERT INTO with_user_tx_probe (v) VALUES ('first')");
        await client.query('INSERT INTO with_user_tx_probe (v) VALUES (NULL)');
      }),
    ).rejects.toThrow(/null value/);
    expect((await pool.query('SELECT v FROM with_user_tx_probe')).rows).toEqual([]);
  });

  it('rethrows a COMMIT failure, keeps nothing, and returns the connection clean', async (context) => {
    context.skip(unreachable !== null, skipReason);
    // A deferred constraint is checked only at COMMIT, so fn succeeds and COMMIT fails.
    await pool.query(
      'CREATE TEMP TABLE with_user_tx_probe (v text NOT NULL UNIQUE DEFERRABLE INITIALLY DEFERRED)',
    );
    const { pid } = await settingAndPid(pool);
    let fnFinished = false;
    const failure = await withUserTx(pool, USER_A, async (client) => {
      await client.query("INSERT INTO with_user_tx_probe (v) VALUES ('same'), ('same')");
      fnFinished = true;
    }).then(
      () => null,
      (error: unknown) => error,
    );
    expect(fnFinished).toBe(true);
    expect(failure).toMatchObject({ code: '23505' });
    expect((await pool.query('SELECT v FROM with_user_tx_probe')).rows).toEqual([]);
    const after = await settingAndPid(pool);
    expect(after.pid).toBe(pid);
    expect(after.userId === null || after.userId === '').toBe(true);
    expect(
      (await pool.query<{ open: boolean }>('SELECT now() <> statement_timestamp() AS open')).rows,
    ).toEqual([{ open: false }]);
  });

  it('lets the server cancel a statement that runs too long, keeping the connection', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const { pid } = await settingAndPid(pool);
    const started = Date.now();
    await expect(
      withUserTx(pool, USER_A, (client) => client.query('SELECT pg_sleep(5)'), { statementTimeoutMs: 200 }),
    ).rejects.toMatchObject({ code: '57014' });
    expect(Date.now() - started).toBeLessThan(3_000);
    expect((await settingAndPid(pool)).pid).toBe(pid);
  });

  it('lets the server end a transaction that fn leaves idle, and discards that connection', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const { pid } = await settingAndPid(pool);
    await expect(
      withUserTx(
        pool,
        USER_A,
        async (client) => {
          await sleep(800);
          await client.query('SELECT 1');
        },
        { idleInTransactionTimeoutMs: 200 },
      ),
    ).rejects.toThrow();
    expect(pool.totalCount).toBe(0);
    const next = await withUserTx(pool, USER_B, (client) => settingAndPid(client));
    expect(next.pid).not.toBe(pid);
    expect(next.userId).toBe(USER_B);
  });

  it('discards a connection that dies mid-transaction instead of crashing or reusing it', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const { pid } = await settingAndPid(pool);
    await expect(
      withUserTx(pool, USER_A, async (client) => {
        await client.query('SELECT pg_terminate_backend(pg_backend_pid())');
      }),
    ).rejects.toThrow();
    expect(pool.totalCount).toBe(0);
    const next = await withUserTx(pool, USER_B, (client) => settingAndPid(client));
    expect(next.pid).not.toBe(pid);
    expect(next.userId).toBe(USER_B);
  });

  it('isolates concurrent transactions on different connections', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const wide = createPool(TEST_DATABASE_URL, () => {}, { max: 2 });
    try {
      const both = await Promise.all(
        [USER_A, USER_B].map((userId) =>
          withUserTx(wide, userId, async (client) => {
            await client.query('SELECT pg_sleep(0.05)');
            return settingAndPid(client);
          }),
        ),
      );
      expect(both.map((s) => s.userId)).toEqual([USER_A, USER_B]);
      expect(both[0]?.pid).not.toBe(both[1]?.pid);
    } finally {
      await wide.end();
    }
  });
});

describe('a connection whose server stops answering (half-open, e.g. after a failover)', () => {
  let proxy: StallingProxy | undefined;

  afterEach(async () => {
    await proxy?.close();
    proxy = undefined;
  });

  it('withUserTx fails after the query timeout, destroys the connection without a rollback, and recovers', async (context) => {
    context.skip(unreachable !== null, skipReason);
    proxy = await startStallingProxy(TEST_DATABASE_URL);
    const queryTimeoutMs = 500;
    const pool = createPool(viaPort(TEST_DATABASE_URL, proxy.port), () => {}, {
      max: 1,
      query_timeout: queryTimeoutMs,
    });
    try {
      const before = await withUserTx(pool, USER_A, (client) => settingAndPid(client));
      proxy.stall();
      const started = Date.now();
      await expect(withUserTx(pool, USER_A, (client) => client.query('SELECT 1'))).rejects.toThrow(
        'Query read timeout',
      );
      // One timeout, not two: no ROLLBACK is queued behind the hung query (it would time out as well).
      expect(Date.now() - started).toBeLessThan(2 * queryTimeoutMs - 50);
      expect(pool.totalCount).toBe(0);

      proxy.resume();
      const after = await withUserTx(pool, USER_B, (client) => settingAndPid(client));
      expect(after.userId).toBe(USER_B);
      expect(after.pid).not.toBe(before.pid);
    } finally {
      await pool.end();
    }
  });
});

describe(`database role and row-level security (${maskUrl(TEST_DATABASE_URL)})`, () => {
  let database: TestDatabase | undefined;
  const pools: Pool[] = [];
  const pool = (url: string, options = {}): Pool => {
    const created = createPool(url, () => {}, { max: 2, ...options });
    pools.push(created);
    return created;
  };

  beforeAll(async () => {
    if (unreachable === null) {
      database = await createTestDatabase([
        { owner: USER_A, body: 'a1' },
        { owner: USER_A, body: 'a2' },
        { owner: USER_B, body: 'b1' },
      ]);
    }
  }, 30_000);

  afterAll(async () => {
    await Promise.all(pools.splice(0).map((p) => p.end()));
    await database?.drop();
  }, 30_000);

  function db(): TestDatabase {
    if (database === undefined) {
      throw new Error('no test database');
    }
    return database;
  }

  it('accepts a LOGIN role that is a member of nimpad_app', async (context) => {
    context.skip(unreachable !== null, skipReason);
    await expect(checkDatabaseRole(pool(db().appUrl))).resolves.toBeUndefined();
  });

  it('rejects a superuser (the owner URL), naming the problem', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const owner = pool(db().ownerUrl);
    // The official postgres image creates its bootstrap superuser with BYPASSRLS as well, while a role created
    // with plain SUPERUSER lacks it. Every problem that applies is reported, so expect exactly the role's own.
    const { rows } = await owner.query<{ bypassRls: boolean }>(
      'SELECT rolbypassrls AS "bypassRls" FROM pg_catalog.pg_roles WHERE rolname = current_user',
    );
    const error = await checkDatabaseRole(owner).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(UnsafeDatabaseRoleError);
    expect((error as UnsafeDatabaseRoleError).problems).toEqual([
      'it is a superuser, so row-level security does not apply',
      ...(rows[0]?.bypassRls ? ['it has BYPASSRLS, so row-level security does not apply'] : []),
    ]);
  });

  it('rejects a role with BYPASSRLS even when it is a member of nimpad_app', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const url = await db().createLogin({ memberOfApp: true, bypassRls: true });
    const error = await checkDatabaseRole(pool(url)).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(UnsafeDatabaseRoleError);
    expect((error as UnsafeDatabaseRoleError).problems).toEqual([
      'it has BYPASSRLS, so row-level security does not apply',
    ]);
  });

  it('rejects a role without the privileges of nimpad_app, or when nimpad_app is missing', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const url = await db().createLogin({ memberOfApp: false });
    const error = await checkDatabaseRole(pool(url)).catch((e: unknown) => e);
    expect((error as UnsafeDatabaseRoleError).problems).toEqual([
      expect.stringContaining('does not have the privileges of "nimpad_app"'),
    ]);
    const missing = await checkDatabaseRole(pool(db().appUrl), { appRole: 'nimpad_no_such_role' }).catch(
      (e: unknown) => e,
    );
    expect((missing as UnsafeDatabaseRoleError).problems).toEqual([
      'role "nimpad_no_such_role" does not exist (run the database migrations)',
    ]);
  });

  it('withUser under the app role sees and writes only the current user’s rows', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const database = createDatabase(pool(db().appUrl));
    const bodies = (userId: string) =>
      database.withUser(userId, async (client) =>
        (await client.query<{ body: string }>(`SELECT body FROM ${NOTES_TABLE} ORDER BY body`)).rows.map(
          (row) => row.body,
        ),
      );
    expect(await bodies(USER_A)).toEqual(['a1', 'a2']);
    expect(await bodies(USER_B)).toEqual(['b1']);
    // Outside withUser no user is set, so no row is visible at all.
    expect((await database.pool.query(`SELECT body FROM ${NOTES_TABLE}`)).rows).toEqual([]);
    // Writing a row for someone else violates the policy's WITH CHECK.
    await expect(
      database.withUser(USER_A, (client) =>
        client.query(`INSERT INTO ${NOTES_TABLE} (owner, body) VALUES ($1, 'forged')`, [USER_B]),
      ),
    ).rejects.toMatchObject({ code: '42501' });
    expect(await bodies(USER_B)).toEqual(['b1']);
  });

  it('withUser refuses to run anything under a role that bypasses row-level security', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const database = createDatabase(pool(db().ownerUrl));
    let ran = false;
    await expect(
      database.withUser(USER_A, async () => {
        ran = true;
      }),
    ).rejects.toBeInstanceOf(UnsafeDatabaseRoleError);
    // Checked again on the next call, not cached as a failure.
    await expect(database.withUser(USER_A, async () => {})).rejects.toBeInstanceOf(UnsafeDatabaseRoleError);
    expect(ran).toBe(false);
  });

  it('GET /readyz is ready under the app role and 503 under the superuser', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const logs = captureLogs();
    const safe = buildServer({
      config: testConfig,
      db: createDatabase(pool(db().appUrl)),
      logStream: logs.stream,
    });
    const unsafe = buildServer({
      config: testConfig,
      db: createDatabase(pool(db().ownerUrl)),
      logStream: logs.stream,
    });
    try {
      const ready = await safe.inject({ method: 'GET', url: '/readyz' });
      expect(ready.statusCode).toBe(200);
      expect(ready.json()).toEqual({ status: 'ready' });

      const refused = await unsafe.inject({ method: 'GET', url: '/readyz' });
      expect(refused.statusCode).toBe(503);
      expect(ApiError.parse(refused.json()).error.code).toBe('unavailable');
      expect(
        logs.lines().find((line) => line.msg === 'database role bypasses row-level security'),
      ).toMatchObject({
        level: 50,
      });
    } finally {
      await Promise.all([safe.close(), unsafe.close()]);
    }
  });

  it('GET /readyz answers 503 while the database stops answering, and recovers once it answers again', async (context) => {
    context.skip(unreachable !== null, skipReason);
    const proxy = await startStallingProxy(db().appUrl);
    // One connection: if a timed-out check kept it checked out, every later check would wait for it and fail too.
    // New connection attempts through the stalled proxy hang as well, until connectionTimeoutMillis.
    const stalled = pool(viaPort(db().appUrl, proxy.port), { max: 1, connectionTimeoutMillis: 500 });
    const app = buildServer({
      config: testConfig,
      db: createDatabase(stalled, { readyCheckTimeoutMs: 300 }),
      logStream: captureLogs().stream,
      readinessTimeoutMs: 1_000,
    });
    const status = async () => (await app.inject({ method: 'GET', url: '/readyz' })).statusCode;
    try {
      expect(await status()).toBe(200);
      proxy.stall();
      for (let probe = 0; probe < 3; probe++) {
        expect(await status()).toBe(503);
      }
      proxy.resume();
      // Bounded by the check's query timeout plus the connect timeout, not by TCP (minutes) or never.
      await vi.waitFor(async () => expect(await status()).toBe(200), { timeout: 5_000, interval: 100 });
      expect(stalled.totalCount).toBe(1);
    } finally {
      await app.close();
      await proxy.close();
    }
  });
});

describe('GET /readyz with an unreachable database', () => {
  it('answers 503 unavailable', async () => {
    const db = createDatabase(createPool('postgres://nobody:nothing@127.0.0.1:1/none', () => {}));
    const app = buildServer({ config: testConfig, db, logStream: captureLogs().stream });
    try {
      const response = await app.inject({ method: 'GET', url: '/readyz' });
      expect(response.statusCode).toBe(503);
      expect(ApiError.parse(response.json()).error.code).toBe('unavailable');
    } finally {
      await app.close();
      await db.close();
    }
  });
});
