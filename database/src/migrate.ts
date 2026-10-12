import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import pg from 'pg';
import { splitSqlStatements } from './sql-split.ts';

/** The migrations shipped with this package. */
export const MIGRATIONS_DIR: string = fileURLToPath(new URL('../migrations', import.meta.url));

/**
 * Session advisory lock held for a whole migration run, so concurrent runners (two deploys, parallel CI jobs)
 * apply each file exactly once. The value is the ASCII bytes of "nimpadMG" read as a big-endian int64.
 */
export const MIGRATION_LOCK_ID = '7956010546164419911';

/** A migration whose first line is exactly this marker runs outside a transaction, one statement at a time. */
export const NO_TRANSACTION_MARKER = '-- nimpad:no-transaction';

const FILE_NAME = /^(\d{4})_[a-z0-9]+(?:_[a-z0-9]+)*\.sql$/;
const BYTE_ORDER_MARK = /^\uFEFF/;

/** A migration file read from disk. */
export interface MigrationFile {
  /** The file name without `.sql`, e.g. `0001_core`. Recorded in `schema_migrations.version`. */
  version: string;
  fileName: string;
  path: string;
  /** The file's text, without a leading byte-order mark (which the server would read as part of the first token). */
  sql: string;
  /** `sha256:` + lowercase hex SHA-256 of the file's UTF-8 bytes, with CRLF normalized to LF. */
  checksum: string;
  /** False when the file starts with {@link NO_TRANSACTION_MARKER}. */
  transactional: boolean;
}

export interface MigrateOptions {
  /**
   * Connection string for the schema owner: a superuser or a role with BYPASSRLS and CREATEROLE. It must be a
   * direct or session-mode connection, never a transaction-mode pooler (such as Supabase's Supavisor on port 6543):
   * the run holds a session-level advisory lock across many transactions. A run that finds itself on a server
   * connection that does not hold the lock stops with a {@link MigrationError} before applying the next file.
   */
  connectionString: string;
  /** Directory of `NNNN_name.sql` files. Defaults to {@link MIGRATIONS_DIR}. */
  dir?: string;
  /** Receives one line per notable step. Silent by default. */
  log?: (message: string) => void;
}

export interface MigrateResult {
  /** Versions applied by this run, in order. */
  applied: string[];
  /** Versions that were already applied (their checksums were verified), in order. */
  skipped: string[];
}

export interface AppliedMigration {
  version: string;
  checksum: string;
  appliedAt: Date;
}

export interface MigrationStatus {
  /** Rows of `schema_migrations`, in version order. */
  applied: AppliedMigration[];
  /** Files on disk that are not applied yet, in order. */
  pending: string[];
  /** Applied versions whose file on disk no longer matches the recorded checksum. */
  changed: string[];
  /** Applied versions with no file on disk (the database is newer than this checkout). */
  unknown: string[];
}

/** A migration failed or the migration set is inconsistent. `version` names the file involved, if any. */
export class MigrationError extends Error {
  readonly version: string | undefined;

  constructor(message: string, options: { version?: string; cause?: unknown } = {}) {
    super(message, { cause: options.cause });
    this.name = 'MigrationError';
    this.version = options.version;
  }
}

/** Computes a migration checksum. Line endings are normalized so a CRLF checkout does not look like drift. */
export function migrationChecksum(sql: string): string {
  const normalized = sql.replace(BYTE_ORDER_MARK, '').replace(/\r\n?/g, '\n');
  return `sha256:${createHash('sha256').update(normalized, 'utf8').digest('hex')}`;
}

/**
 * Reads every `*.sql` file in `dir`, sorted by file name. Every `.sql` file must be named `NNNN_name.sql`
 * (lowercase, digits and underscores) and numbers must be unique, so a misnamed file is an error rather than
 * silently ignored.
 */
export async function readMigrations(dir: string = MIGRATIONS_DIR): Promise<MigrationFile[]> {
  const names = (await readdir(dir)).filter((name) => name.endsWith('.sql')).sort();
  const seenNumbers = new Map<string, string>();
  const files: MigrationFile[] = [];
  for (const fileName of names) {
    const match = FILE_NAME.exec(fileName);
    if (!match) {
      throw new MigrationError(
        `Invalid migration file name "${fileName}" in ${dir}: expected NNNN_name.sql (lowercase, digits, underscores)`,
      );
    }
    const number = match[1]!;
    const previous = seenNumbers.get(number);
    if (previous) {
      throw new MigrationError(`Migrations "${previous}" and "${fileName}" share the number ${number}`);
    }
    seenNumbers.set(number, fileName);

    const filePath = path.join(dir, fileName);
    const sql = (await readFile(filePath, 'utf8')).replace(BYTE_ORDER_MARK, '');
    const firstLine = sql.split(/\r?\n/, 1)[0]!.trim();
    files.push({
      version: fileName.slice(0, -'.sql'.length),
      fileName,
      path: filePath,
      sql,
      checksum: migrationChecksum(sql),
      transactional: firstLine !== NO_TRANSACTION_MARKER,
    });
  }
  return files;
}

// Runs as one implicit transaction. RLS (with no policies) and the revokes keep the Supabase Data API, whose
// default privileges grant new public tables to anon/authenticated, from exposing the table.
const ENSURE_MIGRATIONS_TABLE = `
CREATE TABLE IF NOT EXISTS public.schema_migrations (
  version text PRIMARY KEY,
  checksum text NOT NULL,
  applied_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE public.schema_migrations ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.schema_migrations FROM PUBLIC;
DO $$
DECLARE
  r text;
BEGIN
  FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = r) THEN
      EXECUTE format('REVOKE ALL ON public.schema_migrations FROM %I', r);
    END IF;
  END LOOP;
END
$$;
`;

/**
 * Applies every pending migration in `dir` to the database.
 *
 * - Holds {@link MIGRATION_LOCK_ID} as a session advisory lock for the whole run, so concurrent calls serialize
 *   and the later ones find nothing to do.
 * - Verifies the checksum of every already-applied file first and aborts, applying nothing, if one changed.
 * - Runs each pending file in its own transaction together with its `schema_migrations` row, so a failing file
 *   leaves no trace. Files marked {@link NO_TRANSACTION_MARKER} run statement by statement without a
 *   transaction; if one fails part-way, the statements before it stay applied and the file is not recorded, so
 *   such files must be written to be re-runnable (`IF NOT EXISTS`). Transactional files must not contain their
 *   own `BEGIN`/`COMMIT`.
 */
export async function migrate(options: MigrateOptions): Promise<MigrateResult> {
  const log = options.log ?? (() => {});
  const files = await readMigrations(options.dir);
  const client = await connect(options.connectionString);
  let locked = false;
  try {
    const attempt = await client.query<{ locked: boolean }>(
      'SELECT pg_try_advisory_lock($1::bigint) AS locked',
      [MIGRATION_LOCK_ID],
    );
    if (!attempt.rows[0]?.locked) {
      log('Waiting for another migration run to finish...');
      await client.query('SELECT pg_advisory_lock($1::bigint)', [MIGRATION_LOCK_ID]);
    }
    locked = true;

    await client.query(ENSURE_MIGRATIONS_TABLE);
    const applied = await readApplied(client);
    const status = compare(files, applied);
    if (status.changed.length > 0) {
      throw new MigrationError(
        `Applied migration${status.changed.length > 1 ? 's' : ''} ${status.changed.join(', ')} changed on disk ` +
          '(checksum mismatch). Applied migrations are immutable: revert the edit and add a new migration instead.',
        { version: status.changed[0] },
      );
    }
    for (const version of status.unknown) {
      log(`Warning: ${version} is applied but has no file in ${options.dir ?? MIGRATIONS_DIR}`);
    }

    const result: MigrateResult = { applied: [], skipped: [] };
    for (const file of files) {
      if (applied.has(file.version)) {
        result.skipped.push(file.version);
        continue;
      }
      log(`Applying ${file.fileName}${file.transactional ? '' : ' (no transaction)'}`);
      await applyFile(client, file);
      result.applied.push(file.version);
    }
    log(
      result.applied.length > 0
        ? `Applied ${result.applied.length} migration(s); ${result.skipped.length} already applied`
        : `Database is up to date (${result.skipped.length} migration(s) applied)`,
    );
    return result;
  } finally {
    if (locked) await releaseLock(client, log);
    await client.end().catch(() => {});
  }
}

/**
 * Releases the run's lock. Never throws, so that it cannot replace the error that ended the run: if the connection
 * is gone, the server released the lock with the session.
 */
async function releaseLock(client: pg.Client, log: (message: string) => void): Promise<void> {
  try {
    const result = await client.query<{ released: boolean }>(
      'SELECT pg_advisory_unlock($1::bigint) AS released',
      [MIGRATION_LOCK_ID],
    );
    if (!result.rows[0]?.released) {
      log(
        'Warning: this connection no longer held the migration lock at the end of the run. DATABASE_URL_MIGRATE ' +
          'must be a direct or session-mode connection, not a transaction-mode pooler.',
      );
    }
  } catch {
    // The connection failed; the error that ended the run (if any) already says so.
  }
}

/** Compares the files in `dir` with `schema_migrations` without taking the lock or changing anything. */
export async function migrationStatus(options: Omit<MigrateOptions, 'log'>): Promise<MigrationStatus> {
  const files = await readMigrations(options.dir);
  const client = await connect(options.connectionString);
  try {
    const exists = await client.query<{ present: boolean }>(
      "SELECT to_regclass('public.schema_migrations') IS NOT NULL AS present",
    );
    const applied = exists.rows[0]?.present ? await readApplied(client) : new Map<string, AppliedMigration>();
    return compare(files, applied);
  } finally {
    await client.end().catch(() => {});
  }
}

export interface AppRolePasswordOptions {
  /** The role that gets LOGIN and the password. Defaults to `nimpad_app`. */
  role?: string;
  /**
   * Allows a target that {@link isLocalConnectionString} does not recognise as this machine (for example a
   * development database in another container). Never set it for a shared or production database.
   */
  allowRemote?: boolean;
}

/**
 * Lets `role` (by default `nimpad_app`) log in with `password`, for local development only: refuses a database that
 * is not on this machine unless `allowRemote` is set. nimpad_app may act as any user (it sets app.user_id itself),
 * so a known password on a reachable server would expose every user's data; production manages the credentials
 * of its runtime role outside the migrations.
 *
 * Passwords cannot be bound as statement parameters, so the server quotes the role with `format('%I')` and the
 * password with `format('%L')`, and the resulting statement is executed: neither value is ever concatenated into
 * SQL in JavaScript.
 */
export async function setAppRolePassword(
  connectionString: string,
  password: string,
  options: AppRolePasswordOptions = {},
): Promise<void> {
  const role = options.role ?? 'nimpad_app';
  if (password.length === 0) throw new Error(`The password for ${role} must not be empty`);
  if (!options.allowRemote && !isLocalConnectionString(connectionString)) {
    throw new Error(
      `A development password for ${role} is only for a local database, not ${describeHost(connectionString)}`,
    );
  }
  const client = await connect(connectionString);
  try {
    const statement = await client.query<{ sql: string }>(
      "SELECT format('ALTER ROLE %I LOGIN PASSWORD %L', $1::text, $2::text) AS sql",
      [role, password],
    );
    await client.query(statement.rows[0]!.sql);
  } finally {
    await client.end().catch(() => {});
  }
}

/**
 * True when `connectionString` is a `postgres://` URL for this machine: a loopback host (`localhost`, `127.x.x.x`,
 * `[::1]`) or a Unix socket directory given as `?host=/path` (which, as in pg, overrides the host part). Anything
 * else, including a URL that does not parse or names no host, counts as remote.
 */
export function isLocalConnectionString(connectionString: string): boolean {
  if (!/^postgres(?:ql)?:\/\//i.test(connectionString)) return false;
  let host: string;
  const hostParameter = /[?&]host=([^&#]*)/.exec(connectionString)?.[1];
  if (hostParameter !== undefined) {
    try {
      host = decodeURIComponent(hostParameter);
    } catch {
      return false;
    }
    if (host.startsWith('/')) return true;
  } else {
    try {
      host = new URL(connectionString).hostname;
    } catch {
      return false;
    }
  }
  host = host.toLowerCase();
  return host === 'localhost' || host === '[::1]' || host === '::1' || /^127(?:\.\d{1,3}){3}$/.test(host);
}

/** The host of a connection string, for messages (never the credentials). */
export function describeHost(connectionString: string): string {
  try {
    const url = new URL(connectionString);
    return url.searchParams.get('host') ?? (url.host || 'the default host');
  } catch {
    return 'an unparsable connection string';
  }
}

/** Opens a connection whose failures surface through the failing query instead of crashing the process. */
async function connect(connectionString: string): Promise<pg.Client> {
  const client = new pg.Client({ connectionString, application_name: 'nimpad-migrate' });
  // When the server or the network drops the connection, pg rejects the query in flight (or the next one) and also
  // emits 'error'. Without a listener that event is an uncaught exception that kills the embedding process.
  client.on('error', () => {});
  await client.connect();
  return client;
}

// A bigint advisory lock shows in pg_locks as its high half (classid), low half (objid) and objsubid 1.
const LOCK_HELD = `
SELECT EXISTS (
  SELECT 1 FROM pg_catalog.pg_locks
  WHERE locktype = 'advisory' AND pid = pg_catalog.pg_backend_pid() AND granted AND objsubid = 1
    AND classid = ($1::bigint >> 32)::oid AND objid = ($1::bigint & 4294967295)::oid
) AS held`;

/**
 * Throws unless the server connection about to run `file` holds the migration lock. Behind a transaction-mode pooler
 * consecutive transactions can run on different server connections, and then nothing would keep two runs apart.
 */
async function assertLockHeld(client: pg.Client, file: MigrationFile): Promise<void> {
  const result = await client.query<{ held: boolean }>(LOCK_HELD, [MIGRATION_LOCK_ID]);
  if (!result.rows[0]?.held) {
    throw new MigrationError(
      `${file.fileName} was not applied: the migration lock is not held by this server connection, so another run ` +
        'could apply migrations at the same time. DATABASE_URL_MIGRATE must be a direct or session-mode ' +
        'connection, not a transaction-mode pooler.',
      { version: file.version },
    );
  }
}

async function readApplied(client: pg.Client): Promise<Map<string, AppliedMigration>> {
  const result = await client.query<{ version: string; checksum: string; applied_at: Date }>(
    'SELECT version, checksum, applied_at FROM public.schema_migrations ORDER BY version',
  );
  return new Map(
    result.rows.map((row) => [
      row.version,
      { version: row.version, checksum: row.checksum, appliedAt: row.applied_at },
    ]),
  );
}

function compare(files: MigrationFile[], applied: Map<string, AppliedMigration>): MigrationStatus {
  const onDisk = new Map(files.map((file) => [file.version, file]));
  return {
    applied: [...applied.values()],
    pending: files.filter((file) => !applied.has(file.version)).map((file) => file.version),
    changed: [...applied.values()]
      .filter((row) => {
        const file = onDisk.get(row.version);
        return file !== undefined && file.checksum !== row.checksum;
      })
      .map((row) => row.version),
    unknown: [...applied.keys()].filter((version) => !onDisk.has(version)),
  };
}

async function applyFile(client: pg.Client, file: MigrationFile): Promise<void> {
  const record = 'INSERT INTO public.schema_migrations (version, checksum) VALUES ($1, $2)';
  if (!file.transactional) {
    await assertLockHeld(client, file);
    let statements: string[];
    try {
      statements = splitSqlStatements(file.sql);
    } catch (error) {
      throw new MigrationError(`${file.fileName}: ${(error as Error).message}`, {
        version: file.version,
        cause: error,
      });
    }
    for (const [index, statement] of statements.entries()) {
      try {
        await client.query(statement);
      } catch (error) {
        throw new MigrationError(
          `${file.fileName}: statement ${index + 1} of ${statements.length} failed: ${describe(error)}. ` +
            'This file runs without a transaction, so earlier statements remain applied.',
          { version: file.version, cause: error },
        );
      }
    }
    await client.query(record, [file.version, file.checksum]);
    return;
  }

  await client.query('BEGIN');
  try {
    // Inside the transaction, so that a pooler cannot put the check and the file on different server connections.
    await assertLockHeld(client, file);
    await client.query(file.sql);
    await client.query(record, [file.version, file.checksum]);
    await client.query('COMMIT');
  } catch (error) {
    await client.query('ROLLBACK').catch(() => {});
    if (error instanceof MigrationError) throw error;
    throw new MigrationError(`${file.fileName} failed and was rolled back: ${describe(error)}`, {
      version: file.version,
      cause: error,
    });
  }
}

function describe(error: unknown): string {
  if (error instanceof pg.DatabaseError) {
    const where = error.position ? ` (at character ${error.position})` : '';
    return `${error.message}${where}${error.code ? ` [SQLSTATE ${error.code}]` : ''}`;
  }
  return error instanceof Error ? error.message : String(error);
}
