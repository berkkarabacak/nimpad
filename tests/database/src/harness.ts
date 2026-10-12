// Test harness for the database integration tests: per-file throwaway databases, a LOGIN role that is a member of
// nimpad_app, transactions "as" a user, and helpers for lock-interleaving tests.
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { setTimeout as sleep } from 'node:timers/promises';
import { migrate } from '@nimpad/database';
import pg from 'pg';

/** Superuser connection to the maintenance database; tests create and drop their own databases through it. */
export const ADMIN_URL = process.env.DATABASE_URL_ADMIN ?? 'postgres://nimpad:nimpad@127.0.0.1:5432/postgres';

/** With NIMPAD_REQUIRE_DB=1 (CI), an unreachable server fails the run instead of skipping the tests. */
export const REQUIRE_DB = process.env.NIMPAD_REQUIRE_DB === '1';

/** The Supabase Data API roles the migrations must lock out. Created by the global setup when missing. */
export const SUPABASE_ROLES = ['anon', 'authenticated'] as const;

/** Connection URL with the database and/or credentials replaced. */
export function withUrl(
  base: string,
  changes: { database?: string; user?: string; password?: string },
): string {
  const url = new URL(base);
  if (changes.database !== undefined) url.pathname = `/${encodeURIComponent(changes.database)}`;
  if (changes.user !== undefined) url.username = encodeURIComponent(changes.user);
  if (changes.password !== undefined) url.password = encodeURIComponent(changes.password);
  return url.toString();
}

function redact(connectionString: string): string {
  const url = new URL(connectionString);
  if (url.password) url.password = '***';
  return url.toString();
}

/**
 * Returns why PostgreSQL cannot be used, or undefined when it can. When NIMPAD_REQUIRE_DB=1 an unreachable server
 * is an error: this throws, which fails the test file (or the whole run, from the global setup).
 */
export async function databaseUnavailableReason(): Promise<string | undefined> {
  const client = new pg.Client({ connectionString: ADMIN_URL, connectionTimeoutMillis: 5000 });
  try {
    await client.connect();
    await client.query('SELECT 1');
    return undefined;
  } catch (error) {
    const reason = `PostgreSQL is not reachable at ${redact(ADMIN_URL)} (${(error as Error).message})`;
    if (REQUIRE_DB)
      throw new Error(`${reason}; NIMPAD_REQUIRE_DB=1, so the database tests fail instead of skipping`);
    return reason;
  } finally {
    await client.end().catch(() => {});
  }
}

/** Describe-block title that carries the skip reason, so a skipped run says why. */
export function suiteTitle(title: string, unavailable: string | undefined): string {
  return unavailable === undefined
    ? title
    : `${title} [skipped: ${unavailable}; set NIMPAD_REQUIRE_DB=1 to fail]`;
}

async function admin<T>(fn: (client: pg.Client) => Promise<T>, url: string = ADMIN_URL): Promise<T> {
  const client = new pg.Client({ connectionString: url });
  await client.connect();
  try {
    return await fn(client);
  } finally {
    await client.end();
  }
}

function uniqueName(prefix: string): string {
  return `${prefix}_${process.pid}_${randomBytes(5).toString('hex')}`;
}

/** An empty database with Supabase-like default privileges (anything new is granted to anon/authenticated). */
export interface EmptyDatabase {
  readonly name: string;
  /** Superuser (migration owner) connection string for this database. */
  readonly url: string;
  drop(): Promise<void>;
}

export async function createEmptyDatabase(): Promise<EmptyDatabase> {
  const name = uniqueName('nimpad_test');
  await admin((client) => client.query(`CREATE DATABASE ${pg.escapeIdentifier(name)}`));
  const url = withUrl(ADMIN_URL, { database: name });
  try {
    // Supabase grants everything the migration role creates to its Data API roles by default. Reproducing that
    // proves the migrations revoke it again rather than relying on a clean default.
    await admin(async (client) => {
      await client.query(`
        ALTER DEFAULT PRIVILEGES GRANT USAGE ON SCHEMAS TO anon, authenticated;
        ALTER DEFAULT PRIVILEGES GRANT ALL ON TABLES TO anon, authenticated;
        ALTER DEFAULT PRIVILEGES GRANT ALL ON SEQUENCES TO anon, authenticated;
        ALTER DEFAULT PRIVILEGES GRANT ALL ON FUNCTIONS TO anon, authenticated;
        GRANT USAGE ON SCHEMA public TO anon, authenticated;
      `);
    }, url);
  } catch (error) {
    await dropDatabase(name);
    throw error;
  }
  return { name, url, drop: () => dropDatabase(name) };
}

async function dropDatabase(name: string): Promise<void> {
  await admin((client) => client.query(`DROP DATABASE IF EXISTS ${pg.escapeIdentifier(name)} WITH (FORCE)`));
}

/** A migrated database plus a LOGIN role that is a member of nimpad_app, as the API would connect. */
export interface TestDatabase {
  readonly name: string;
  /** Migration owner (superuser): bypasses row-level security. Use for setup and for checking results. */
  readonly owner: pg.Pool;
  /** The app role: subject to row-level security and to nimpad_app's grants. */
  readonly app: pg.Pool;
  readonly ownerUrl: string;
  readonly appUrl: string;
  readonly appRole: string;
  /** A dedicated app-role connection, for interleaving transactions by hand. Closed by `drop()`. */
  appClient(): Promise<pg.Client>;
  /**
   * Closes every connection opened with `appClient()` so far. Call it after each test, so a test that fails with a
   * transaction still open does not leave its locks behind for the next one.
   */
  closeAppClients(): Promise<void>;
  drop(): Promise<void>;
}

export async function createTestDatabase(): Promise<TestDatabase> {
  const database = await createEmptyDatabase();
  const appRole = `${database.name}_app`;
  const appPassword = randomBytes(18).toString('hex');
  const clients: pg.Client[] = [];
  let owner: pg.Pool | undefined;
  let app: pg.Pool | undefined;
  let roleCreated = false;

  const closeAppClients = async (): Promise<void> => {
    await Promise.allSettled(clients.splice(0).map((client) => client.end()));
  };
  const drop = async (): Promise<void> => {
    await closeAppClients();
    await Promise.allSettled([owner?.end(), app?.end()]);
    await database.drop();
    if (roleCreated)
      await admin((client) => client.query(`DROP ROLE IF EXISTS ${pg.escapeIdentifier(appRole)}`));
  };

  try {
    await migrate({ connectionString: database.url });
    await admin(async (client) => {
      await client.query(
        `CREATE ROLE ${pg.escapeIdentifier(appRole)} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD ${pg.escapeLiteral(appPassword)}`,
      );
      roleCreated = true;
      await client.query(`GRANT nimpad_app TO ${pg.escapeIdentifier(appRole)}`);
    });
    const appUrl = withUrl(database.url, { user: appRole, password: appPassword });
    owner = new pg.Pool({ connectionString: database.url, max: 3 });
    app = new pg.Pool({ connectionString: appUrl, max: 3 });
    const appPool = app;
    const ownerPool = owner;
    return {
      name: database.name,
      owner: ownerPool,
      app: appPool,
      ownerUrl: database.url,
      appUrl,
      appRole,
      async appClient() {
        const client = new pg.Client({ connectionString: appUrl });
        await client.connect();
        clients.push(client);
        return client;
      },
      closeAppClients,
      drop,
    };
  } catch (error) {
    await drop();
    throw error;
  }
}

/** pg.Pool and pg.Client both satisfy this. */
export type Queryable = Pick<pg.ClientBase, 'query'>;

/**
 * Runs `fn` in a transaction with app.user_id set the way the API's withUserTx does it (transaction-local).
 * `userId` null leaves it unset. Rolls back on error and rethrows.
 */
export async function asUser<T>(
  pool: pg.Pool,
  userId: string | null,
  fn: (client: pg.PoolClient) => Promise<T>,
): Promise<T> {
  const client = await pool.connect();
  try {
    await client.query('BEGIN');
    if (userId !== null) await setUser(client, userId);
    const result = await fn(client);
    await client.query('COMMIT');
    return result;
  } catch (error) {
    await client.query('ROLLBACK').catch(() => {});
    throw error;
  } finally {
    client.release();
  }
}

export async function setUser(client: Queryable, userId: string): Promise<void> {
  await client.query("SELECT set_config('app.user_id', $1, true)", [userId]);
}

/** Awaits `promise`, which must reject with a PostgreSQL error, and returns that error. */
export async function pgError(promise: Promise<unknown>): Promise<pg.DatabaseError> {
  try {
    await promise;
  } catch (error) {
    if (error instanceof pg.DatabaseError) return error;
    throw error;
  }
  throw new Error('Expected the statement to fail, but it succeeded');
}

export async function backendPid(client: Queryable): Promise<number> {
  const result = await client.query<{ pid: number }>('SELECT pg_backend_pid() AS pid');
  return result.rows[0]!.pid;
}

/**
 * Polls until backend `waiterPid` is blocked by backend `holderPid`'s locks. Read both pids before starting the
 * statement expected to block: a pg client queues further queries behind a blocked one.
 */
export async function waitUntilBlockedBy(
  observer: Queryable,
  waiterPid: number,
  holderPid: number,
  timeoutMs = 10_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const result = await observer.query<{ blocked: boolean }>(
      'SELECT $2::int = ANY (pg_blocking_pids($1::int)) AS blocked',
      [waiterPid, holderPid],
    );
    if (result.rows[0]?.blocked) return;
    await sleep(10);
  }
  throw new Error(`Backend ${waiterPid} was not blocked by backend ${holderPid} within ${timeoutMs} ms`);
}

/** Settles a promise into a value, so a test can start a statement, do other work, and inspect the outcome later. */
export function settle<T>(
  promise: Promise<T>,
): Promise<{ ok: true; value: T } | { ok: false; error: unknown }> {
  return promise.then(
    (value) => ({ ok: true as const, value }),
    (error: unknown) => ({ ok: false as const, error }),
  );
}

export function checksum(text: string): string {
  return `sha256:${createHash('sha256').update(text, 'utf8').digest('hex')}`;
}

export const newId = (): string => randomUUID();

// ---------------------------------------------------------------------------------------------------------
// Seeding through the app role, so the setup itself exercises the insert policies.
// ---------------------------------------------------------------------------------------------------------

export async function createUser(db: TestDatabase, label: string): Promise<string> {
  const id = newId();
  await asUser(db.app, id, (client) =>
    client.query('INSERT INTO nimpad.users (id, email, display_name) VALUES ($1, $2, $3)', [
      id,
      `${label}-${id.slice(0, 8)}@example.test`,
      label,
    ]),
  );
  return id;
}

export async function createWorkspace(
  db: TestDatabase,
  ownerId: string,
  name = 'My Workspace',
): Promise<string> {
  const id = newId();
  await asUser(db.app, ownerId, (client) =>
    client.query('INSERT INTO nimpad.workspaces (id, owner_id, name) VALUES ($1, $2, $3)', [
      id,
      ownerId,
      name,
    ]),
  );
  return id;
}

export async function addMember(
  db: TestDatabase,
  actingOwnerId: string,
  workspaceId: string,
  userId: string,
  role: 'owner' | 'editor' | 'viewer',
): Promise<void> {
  await asUser(db.app, actingOwnerId, (client) =>
    client.query('INSERT INTO nimpad.workspace_members (workspace_id, user_id, role) VALUES ($1, $2, $3)', [
      workspaceId,
      userId,
      role,
    ]),
  );
}

export interface NodeInput {
  workspaceId: string;
  kind: 'folder' | 'document';
  name?: string;
  parentId?: string | null;
  position?: string;
  id?: string;
}

export async function insertNode(client: Queryable, input: NodeInput): Promise<string> {
  const id = input.id ?? newId();
  await client.query(
    `INSERT INTO nimpad.nodes (id, workspace_id, parent_id, kind, name, position, content_updated_at)
     VALUES ($1, $2, $3, $4, $5, $6, CASE WHEN $4 = 'document' THEN now() END)`,
    [
      id,
      input.workspaceId,
      input.parentId ?? null,
      input.kind,
      input.name ?? `${input.kind} ${id.slice(0, 8)}`,
      input.position ?? 'a0',
    ],
  );
  return id;
}

/** Inserts a document node with its content and revision 1, as `userId`. */
export async function createDocument(
  db: TestDatabase,
  userId: string,
  workspaceId: string,
  text: string,
  parentId: string | null = null,
): Promise<string> {
  return asUser(db.app, userId, async (client) => {
    const id = await insertNode(client, { workspaceId, kind: 'document', parentId });
    await client.query(
      `INSERT INTO nimpad.document_contents
         (node_id, workspace_id, text, format, content_rev, checksum, char_count, updated_by_kind, updated_by_id)
       VALUES ($1, $2, $3, 'plain', 1, $4, $5, 'user', $6)`,
      [id, workspaceId, text, checksum(text), text.length, userId],
    );
    await client.query(
      `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind, actor_id)
       VALUES ($1, $2, 1, $3, 'plain', $4, 'user', $5)`,
      [id, workspaceId, text, checksum(text), userId],
    );
    return id;
  });
}

export async function createFolder(
  db: TestDatabase,
  userId: string,
  workspaceId: string,
  parentId: string | null = null,
): Promise<string> {
  return asUser(db.app, userId, (client) => insertNode(client, { workspaceId, kind: 'folder', parentId }));
}

export async function parentOf(db: TestDatabase, nodeId: string): Promise<string | null> {
  const result = await db.owner.query<{ parent_id: string | null }>(
    'SELECT parent_id FROM nimpad.nodes WHERE id = $1',
    [nodeId],
  );
  return result.rows[0]!.parent_id;
}
