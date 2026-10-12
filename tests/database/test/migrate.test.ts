import { execFile } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import {
  MIGRATION_LOCK_ID,
  MIGRATIONS_DIR,
  MigrationError,
  migrate,
  migrationStatus,
  readMigrations,
  setAppRolePassword,
} from '@nimpad/database';
import pg from 'pg';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  ADMIN_URL,
  createEmptyDatabase,
  databaseUnavailableReason,
  type EmptyDatabase,
  settle,
  suiteTitle,
  withUrl,
} from '../src/harness.ts';

const unavailable = await databaseUnavailableReason();
const run = promisify(execFile);
const CLI = path.join(MIGRATIONS_DIR, '..', 'src', 'cli.ts');

async function query<R extends pg.QueryResultRow>(
  url: string,
  sql: string,
  params: unknown[] = [],
): Promise<R[]> {
  const client = new pg.Client({ connectionString: url });
  await client.connect();
  try {
    return (await client.query<R>(sql, params)).rows;
  } finally {
    await client.end();
  }
}

describe.skipIf(unavailable !== undefined)(suiteTitle('migration runner', unavailable), () => {
  let db: EmptyDatabase;
  let dir: string;

  beforeEach(async () => {
    db = await createEmptyDatabase();
    dir = await mkdtemp(path.join(tmpdir(), 'nimpad-migrations-'));
  });

  afterEach(async () => {
    await db?.drop();
    await rm(dir, { recursive: true, force: true });
  });

  it('applies the shipped migrations once; a second run is a no-op', async () => {
    const first = await migrate({ connectionString: db.url });
    expect(first).toEqual({ applied: ['0001_core', '0002_sync'], skipped: [] });

    const second = await migrate({ connectionString: db.url });
    expect(second).toEqual({ applied: [], skipped: ['0001_core', '0002_sync'] });

    const files = await readMigrations();
    const rows = await query<{ version: string; checksum: string }>(
      db.url,
      'SELECT version, checksum FROM public.schema_migrations ORDER BY version',
    );
    expect(rows).toEqual(files.map((file) => ({ version: file.version, checksum: file.checksum })));

    const status = await migrationStatus({ connectionString: db.url });
    expect(status.pending).toEqual([]);
    expect(status.changed).toEqual([]);
    expect(status.applied.map((row) => row.version)).toEqual(['0001_core', '0002_sync']);
  });

  it('rejects an applied migration whose file changed, and applies nothing else', async () => {
    await writeFile(path.join(dir, '0001_first.sql'), 'CREATE TABLE public.first (id int);\n');
    await migrate({ connectionString: db.url, dir });

    await writeFile(path.join(dir, '0001_first.sql'), 'CREATE TABLE public.first (id int);\n-- edited\n');
    await writeFile(path.join(dir, '0002_second.sql'), 'CREATE TABLE public.second (id int);\n');

    const error = await migrate({ connectionString: db.url, dir }).then(
      () => undefined,
      (e: unknown) => e,
    );
    expect(error).toBeInstanceOf(MigrationError);
    expect((error as MigrationError).version).toBe('0001_first');
    expect((error as MigrationError).message).toMatch(/0001_first changed on disk \(checksum mismatch\)/);

    const tables = await query(
      db.url,
      "SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = 'second'",
    );
    expect(tables).toHaveLength(0);

    const status = await migrationStatus({ connectionString: db.url, dir });
    expect(status.changed).toEqual(['0001_first']);
    expect(status.pending).toEqual(['0002_second']);
  });

  it('rolls back a failing migration completely and does not record it', async () => {
    await writeFile(path.join(dir, '0001_ok.sql'), 'CREATE TABLE public.ok (id int);');
    await writeFile(path.join(dir, '0002_bad.sql'), 'CREATE TABLE public.bad (id int);\nSELECT 1 / 0;');

    const error = await migrate({ connectionString: db.url, dir }).then(
      () => undefined,
      (e: unknown) => e,
    );
    expect(error).toBeInstanceOf(MigrationError);
    expect((error as MigrationError).version).toBe('0002_bad');
    expect((error as MigrationError).message).toMatch(
      /0002_bad\.sql failed and was rolled back: division by zero/,
    );

    const versions = await query<{ version: string }>(db.url, 'SELECT version FROM public.schema_migrations');
    expect(versions.map((row) => row.version)).toEqual(['0001_ok']);
    const tables = await query<{ tablename: string }>(
      db.url,
      "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('ok', 'bad')",
    );
    expect(tables.map((row) => row.tablename)).toEqual(['ok']);
  });

  it('runs -- nimpad:no-transaction files statement by statement, outside a transaction', async () => {
    await writeFile(path.join(dir, '0001_table.sql'), 'CREATE TABLE public.t (a int, b text);');
    const concurrently = [
      '-- nimpad:no-transaction',
      'CREATE INDEX CONCURRENTLY IF NOT EXISTS t_a_idx ON public.t (a);',
      "COMMENT ON TABLE public.t IS 'has; semicolons';",
      'CREATE INDEX CONCURRENTLY IF NOT EXISTS t_b_idx ON public.t (b);',
    ].join('\n');
    await writeFile(path.join(dir, '0002_indexes.sql'), concurrently);

    const result = await migrate({ connectionString: db.url, dir });
    expect(result.applied).toEqual(['0001_table', '0002_indexes']);
    const indexes = await query<{ indexname: string }>(
      db.url,
      "SELECT indexname FROM pg_indexes WHERE tablename = 't' ORDER BY indexname",
    );
    expect(indexes.map((row) => row.indexname)).toEqual(['t_a_idx', 't_b_idx']);

    // The same statements without the marker run inside a transaction, which PostgreSQL refuses.
    await writeFile(
      path.join(dir, '0003_needs_marker.sql'),
      'CREATE INDEX CONCURRENTLY t_ab_idx ON public.t (a, b);',
    );
    await expect(migrate({ connectionString: db.url, dir })).rejects.toThrow(
      /cannot run inside a transaction block/,
    );
  });

  it('reports the file whose no-transaction run failed part-way, keeps earlier statements, and can re-run it', async () => {
    await writeFile(path.join(dir, '0001_table.sql'), 'CREATE TABLE public.t (a int);');
    const file = path.join(dir, '0002_indexes.sql');
    const statements = (target: string) =>
      [
        '-- nimpad:no-transaction',
        'CREATE INDEX CONCURRENTLY IF NOT EXISTS t_a_idx ON public.t (a);',
        `CREATE INDEX CONCURRENTLY IF NOT EXISTS other_idx ON ${target} (a);`,
        'CREATE TABLE IF NOT EXISTS public.after_indexes (a int);',
      ].join('\n');
    await writeFile(file, statements('public.missing'));

    const error = await migrate({ connectionString: db.url, dir }).then(
      () => undefined,
      (e: unknown) => e,
    );
    expect(error).toBeInstanceOf(MigrationError);
    expect((error as MigrationError).version).toBe('0002_indexes');
    expect((error as MigrationError).message).toMatch(
      /^0002_indexes\.sql: statement 2 of 3 failed: relation "public\.missing" does not exist.*earlier statements remain applied/,
    );
    const indexes = await query<{ indexname: string }>(
      db.url,
      "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 't' ORDER BY indexname",
    );
    expect(indexes.map((row) => row.indexname)).toEqual(['t_a_idx']);
    expect(await query(db.url, "SELECT 1 FROM pg_tables WHERE tablename = 'after_indexes'")).toHaveLength(0);
    const versions = await query<{ version: string }>(db.url, 'SELECT version FROM public.schema_migrations');
    expect(versions.map((row) => row.version)).toEqual(['0001_table']);

    // Fixed and re-run: the statements are written to be re-runnable, and the file is recorded this time.
    await writeFile(file, statements('public.t'));
    expect(await migrate({ connectionString: db.url, dir })).toEqual({
      applied: ['0002_indexes'],
      skipped: ['0001_table'],
    });
    const recorded = await query<{ checksum: string }>(
      db.url,
      "SELECT checksum FROM public.schema_migrations WHERE version = '0002_indexes'",
    );
    expect(recorded).toEqual([{ checksum: (await readMigrations(dir))[1]!.checksum }]);
  });

  it('names the file when a no-transaction migration cannot be split into statements', async () => {
    await writeFile(path.join(dir, '0001_broken.sql'), "-- nimpad:no-transaction\nSELECT 'unterminated;\n");
    const error = await migrate({ connectionString: db.url, dir }).then(
      () => undefined,
      (e: unknown) => e,
    );
    expect(error).toBeInstanceOf(MigrationError);
    expect((error as MigrationError).version).toBe('0001_broken');
    expect((error as MigrationError).message).toMatch(/^0001_broken\.sql: Unterminated string literal/);
  });

  it('runs SQL-standard function bodies (BEGIN ATOMIC) in no-transaction files', async () => {
    await writeFile(
      path.join(dir, '0001_atomic.sql'),
      [
        '-- nimpad:no-transaction',
        'CREATE TABLE public.atomic_t (a int);',
        'CREATE PROCEDURE public.insert_two() LANGUAGE sql',
        'BEGIN ATOMIC',
        '  INSERT INTO public.atomic_t VALUES (1);',
        '  INSERT INTO public.atomic_t VALUES (CASE WHEN true THEN 2 ELSE 3 END);',
        'END;',
        'CALL public.insert_two();',
      ].join('\n'),
    );
    await migrate({ connectionString: db.url, dir });
    const rows = await query<{ a: number }>(db.url, 'SELECT a FROM public.atomic_t ORDER BY a');
    expect(rows).toEqual([{ a: 1 }, { a: 2 }]);
  });

  it('ignores a byte-order mark at the start of a file, in both modes', async () => {
    // Windows editors often save one; the server would read it as part of the first token.
    await writeFile(path.join(dir, '0001_bom.sql'), '\ufeffCREATE TABLE public.bom (id int);\r\n');
    await writeFile(
      path.join(dir, '0002_bom_index.sql'),
      '\ufeff-- nimpad:no-transaction\r\nCREATE INDEX CONCURRENTLY bom_idx ON public.bom (id);\r\n',
    );
    const files = await readMigrations(dir);
    expect(files.map((file) => [file.transactional, file.sql.charCodeAt(0)])).toEqual([
      [true, 'C'.charCodeAt(0)],
      [false, '-'.charCodeAt(0)],
    ]);
    expect(await migrate({ connectionString: db.url, dir })).toEqual({
      applied: ['0001_bom', '0002_bom_index'],
      skipped: [],
    });
    expect(await query(db.url, "SELECT 1 FROM pg_indexes WHERE indexname = 'bom_idx'")).toHaveLength(1);
  });

  it('names the file, and raises no unhandled error, when the connection dies mid-migration', async () => {
    await writeFile(path.join(dir, '0001_ok.sql'), 'CREATE TABLE public.ok (id int);');
    const slow = path.join(dir, '0002_slow.sql');
    await writeFile(slow, 'CREATE TABLE public.slow (id int);\nSELECT pg_sleep(60);');
    const running = settle(migrate({ connectionString: db.url, dir }));

    const deadline = Date.now() + 10_000;
    for (;;) {
      const terminated = await query<{ pid: number }>(
        db.url,
        `SELECT pg_terminate_backend(pid) AS pid FROM pg_stat_activity
         WHERE datname = current_database() AND application_name = 'nimpad-migrate' AND query LIKE '%pg_sleep%'`,
      );
      if (terminated.length > 0) break;
      if (Date.now() > deadline) throw new Error('the migration never reached pg_sleep');
      await new Promise((resolve) => setTimeout(resolve, 20));
    }

    const outcome = await running;
    expect(outcome.ok).toBe(false);
    const error = (outcome as { error: unknown }).error;
    expect(error).toBeInstanceOf(MigrationError);
    expect((error as MigrationError).version).toBe('0002_slow');
    expect((error as MigrationError).message).toMatch(
      /^0002_slow\.sql failed and was rolled back: terminating connection/,
    );

    // Nothing of the file stayed, and the lock went away with the session: the next run applies the fixed file.
    expect(await query(db.url, "SELECT 1 FROM pg_tables WHERE tablename = 'slow'")).toHaveLength(0);
    await writeFile(slow, 'CREATE TABLE public.slow (id int);');
    expect((await migrate({ connectionString: db.url, dir })).applied).toEqual(['0002_slow']);
  });

  it('stops before a file when the migration lock is no longer held by its connection', async () => {
    // What a transaction-mode pooler does: the next transaction runs on a backend that does not hold the lock.
    // Here the first file releases the lock itself, which gives the second file the same situation.
    await writeFile(path.join(dir, '0001_release.sql'), `SELECT pg_advisory_unlock(${MIGRATION_LOCK_ID});`);
    await writeFile(path.join(dir, '0002_after.sql'), 'CREATE TABLE public.after_release (id int);');
    const logs: string[] = [];
    const error = await migrate({ connectionString: db.url, dir, log: (line) => logs.push(line) }).then(
      () => undefined,
      (e: unknown) => e,
    );
    expect(error).toBeInstanceOf(MigrationError);
    expect((error as MigrationError).version).toBe('0002_after');
    expect((error as MigrationError).message).toMatch(
      /migration lock .* not held .* session-mode connection/,
    );
    expect(await query(db.url, "SELECT 1 FROM pg_tables WHERE tablename = 'after_release'")).toHaveLength(0);
    const versions = await query<{ version: string }>(db.url, 'SELECT version FROM public.schema_migrations');
    expect(versions.map((row) => row.version)).toEqual(['0001_release']);
  });

  it('serializes concurrent runs with the advisory lock: exactly one applies the migrations', async () => {
    // Hold the runner's lock so all runs are provably waiting on it before any of them starts working.
    const holder = new pg.Client({ connectionString: db.url });
    await holder.connect();
    try {
      await holder.query('SELECT pg_advisory_lock($1::bigint)', [MIGRATION_LOCK_ID]);
      const logs: string[][] = [[], [], [], []];
      const runs = logs.map((lines) =>
        settle(migrate({ connectionString: db.url, log: (line) => lines.push(line) })),
      );

      const deadline = Date.now() + 10_000;
      for (;;) {
        const waiting = await holder.query<{ n: number }>(
          `SELECT count(*)::int AS n FROM pg_stat_activity
           WHERE datname = current_database() AND application_name = 'nimpad-migrate' AND wait_event = 'advisory'`,
        );
        if (waiting.rows[0]!.n === logs.length) break;
        if (Date.now() > deadline) throw new Error('migration runs did not all wait for the advisory lock');
        await new Promise((resolve) => setTimeout(resolve, 20));
      }
      await holder.query('SELECT pg_advisory_unlock($1::bigint)', [MIGRATION_LOCK_ID]);

      const outcomes = await Promise.all(runs);
      const results = outcomes.map((outcome) => {
        if (!outcome.ok) throw outcome.error;
        return outcome.value;
      });
      expect(results.filter((result) => result.applied.length === 2)).toHaveLength(1);
      expect(
        results.filter((result) => result.applied.length === 0 && result.skipped.length === 2),
      ).toHaveLength(3);
      for (const lines of logs) expect(lines[0]).toBe('Waiting for another migration run to finish...');
    } finally {
      await holder.end();
    }
  });

  it('refuses to run as a role that cannot bypass row-level security', async () => {
    const role = `${db.name}_weak`;
    const password = 'weak-role-password';
    await query(
      ADMIN_URL,
      `CREATE ROLE ${pg.escapeIdentifier(role)} LOGIN CREATEROLE PASSWORD ${pg.escapeLiteral(password)}`,
    );
    try {
      await query(
        db.url,
        `GRANT CREATE ON DATABASE ${pg.escapeIdentifier(db.name)} TO ${pg.escapeIdentifier(role)}`,
      );
      await query(db.url, `GRANT CREATE ON SCHEMA public TO ${pg.escapeIdentifier(role)}`);
      const url = withUrl(db.url, { user: role, password });
      await expect(migrate({ connectionString: url })).rejects.toThrow(
        /must run as a superuser or a role with BYPASSRLS/,
      );
      const schemas = await query(db.url, "SELECT 1 FROM pg_namespace WHERE nspname = 'nimpad'");
      expect(schemas).toHaveLength(0);
    } finally {
      await db.drop();
      await query(ADMIN_URL, `DROP ROLE IF EXISTS ${pg.escapeIdentifier(role)}`);
    }
  });

  it('sets a role password with quoting done by the server', async () => {
    await migrate({ connectionString: db.url });
    const role = `${db.name}_pw`;
    await query(ADMIN_URL, `CREATE ROLE ${pg.escapeIdentifier(role)} NOLOGIN`);
    try {
      // Quotes, backslashes and a statement terminator must all arrive verbatim.
      const password = String.raw`it's; DROP ROLE nimpad_app; -- \' "x"`;
      await setAppRolePassword(db.url, password, { role });
      const rows = await query<{ who: string }>(
        withUrl(db.url, { user: role, password }),
        'SELECT current_user AS who',
      );
      expect(rows).toEqual([{ who: role }]);
      const app = await query(ADMIN_URL, "SELECT 1 FROM pg_roles WHERE rolname = 'nimpad_app'");
      expect(app).toHaveLength(1);
    } finally {
      await query(ADMIN_URL, `DROP ROLE IF EXISTS ${pg.escapeIdentifier(role)}`);
    }
  });

  it('works from the command line, including --status', async () => {
    const env: NodeJS.ProcessEnv = { ...process.env, DATABASE_URL_MIGRATE: db.url };
    delete env.NIMPAD_APP_DB_PASSWORD;

    const before = await run(process.execPath, [CLI, '--status'], { env });
    expect(before.stdout).toBe('pending  0001_core\npending  0002_sync\n');

    const first = await run(process.execPath, [CLI], { env });
    expect(first.stdout).toContain('Applying 0001_core.sql');
    expect(first.stdout).toContain('applied  0002_sync');

    const second = await run(process.execPath, [CLI], { env });
    expect(second.stdout).toBe('Database is up to date (2 migration(s) applied)\n');

    const after = await run(process.execPath, [CLI, '--status'], { env });
    expect(after.stdout).toMatch(/^applied {2}0001_core {2}\S+Z\napplied {2}0002_sync {2}\S+Z\n$/);

    // An applied file that changed on disk: --status says so and exits 1.
    await writeFile(path.join(dir, '0001_first.sql'), 'CREATE TABLE public.first (id int);\n');
    await run(process.execPath, [CLI, '--dir', dir], { env });
    await writeFile(path.join(dir, '0001_first.sql'), 'CREATE TABLE public.first (id bigint);\n');
    const drift = await settle(run(process.execPath, [CLI, '--status', '--dir', dir], { env }));
    expect(drift.ok).toBe(false);
    expect((drift as { error: { code: number; stdout: string } }).error.code).toBe(1);
    expect((drift as { error: { stdout: string } }).error.stdout).toMatch(
      /^applied {2}0001_first {2}\S+Z {2}CHANGED ON DISK$/m,
    );

    const missing = await settle(run(process.execPath, [CLI], { env: { ...env, DATABASE_URL_MIGRATE: '' } }));
    expect(missing.ok).toBe(false);
    expect((missing as { error: { code: number; stderr: string } }).error.code).toBe(2);
    expect((missing as { error: { stderr: string } }).error.stderr).toContain(
      'DATABASE_URL_MIGRATE is not set',
    );
  });

  it('refuses to give nimpad_app a development password on a database that is not local', async () => {
    const env: NodeJS.ProcessEnv = {
      ...process.env,
      // Never contacted: the refusal comes before any connection.
      DATABASE_URL_MIGRATE: 'postgres://postgres:secret@db.project.supabase.invalid:5432/postgres',
      NIMPAD_APP_DB_PASSWORD: 'nimpad_app',
    };
    delete env.NIMPAD_ALLOW_REMOTE_DEV_PASSWORD;
    const refused = await settle(run(process.execPath, [CLI], { env }));
    expect(refused.ok).toBe(false);
    expect((refused as { error: { code: number } }).error.code).toBe(2);
    expect((refused as { error: { stderr: string } }).error.stderr).toContain(
      'NIMPAD_APP_DB_PASSWORD is only for a local database',
    );
    expect((refused as { error: { stderr: string } }).error.stderr).not.toContain('secret');

    // The library refuses as well, unless the caller explicitly allows a remote target.
    await expect(
      setAppRolePassword('postgres://postgres:secret@db.project.supabase.invalid:5432/postgres', 'x'),
    ).rejects.toThrow(/only for a local database/);
  });
});
