// Catalog-level guarantees (RLS flags, privileges, Supabase lock-out) and the integrity rules enforced by
// constraints and triggers.
import { readFile } from 'node:fs/promises';
import pg from 'pg';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  asUser,
  checksum,
  createDocument,
  createFolder,
  createTestDatabase,
  createUser,
  createWorkspace,
  databaseUnavailableReason,
  insertNode,
  newId,
  parentOf,
  pgError,
  SUPABASE_ROLES,
  suiteTitle,
  type TestDatabase,
} from '../src/harness.ts';

const unavailable = await databaseUnavailableReason();

/** The shared contract fixtures (packages/contracts/fixtures), read as plain JSON. */
async function contractFixture<T>(name: string): Promise<T> {
  const url = new URL(`../../../packages/contracts/fixtures/${name}`, import.meta.url);
  return JSON.parse(await readFile(url, 'utf8')) as T;
}

interface ChecksumFixture {
  cases: { name: string; input: string; canonical: string; checksum: string }[];
}

interface NodeNamesFixture {
  cases: { description: string; text?: string; utf16?: number[]; valid: boolean }[];
}

/** PostgreSQL text cannot hold U+0000, and pg would send an unpaired surrogate as U+FFFD. */
function isStorable(text: string): boolean {
  return !text.includes('\0') && !/\p{Cs}/u.test(text);
}

interface FractionalIndexFixture {
  between: { a: string | null; b: string | null; expected: string }[];
  nKeys: { a: string | null; b: string | null; expected: string[] }[];
  invalid: { a: string | null; b: string | null; message: string }[];
}

const EXPECTED_TABLES = [
  'activity_events',
  'audit_log',
  'device_sessions',
  'devices',
  'document_contents',
  'document_links',
  'document_revisions',
  'idempotency_records',
  'nodes',
  'sync_events',
  'users',
  'workspace_members',
  'workspaces',
];

describe.skipIf(unavailable !== undefined)(
  suiteTitle('schema, privileges and integrity', unavailable),
  () => {
    let db: TestDatabase;
    let user: string;
    let workspace: string;

    beforeAll(async () => {
      db = await createTestDatabase();
      user = await createUser(db, 'owner');
      workspace = await createWorkspace(db, user);
    });

    afterAll(async () => {
      await db?.drop();
    });

    describe('catalog', () => {
      it('enables and forces row-level security on every table in schema nimpad', async () => {
        const result = await db.owner.query<{
          relname: string;
          relrowsecurity: boolean;
          relforcerowsecurity: boolean;
        }>(
          `SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity
         FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = 'nimpad' AND c.relkind IN ('r', 'p') ORDER BY c.relname`,
        );
        expect(result.rows.map((row) => row.relname)).toEqual(EXPECTED_TABLES);
        for (const row of result.rows) {
          expect(row.relrowsecurity && row.relforcerowsecurity, row.relname).toBe(true);
        }
      });

      it('gives nimpad_app and its login members no way around row-level security', async () => {
        const result = await db.owner.query<{ rolname: string; rolsuper: boolean; rolbypassrls: boolean }>(
          'SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = ANY ($1)',
          [['nimpad_app', db.appRole]],
        );
        expect(result.rows).toHaveLength(2);
        for (const row of result.rows) expect(row.rolsuper || row.rolbypassrls, row.rolname).toBe(false);
        const owns = await db.owner.query(
          `SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = 'nimpad' AND pg_get_userbyid(c.relowner) IN ('nimpad_app', $1)`,
          [db.appRole],
        );
        expect(owns.rowCount).toBe(0);
      });

      it('lets nimpad_app write neither sync events nor any column the database maintains', async () => {
        const tables = await db.owner.query<{ privilege: string; granted: boolean }>(
          `SELECT p.privilege, has_table_privilege('nimpad_app', 'nimpad.sync_events', p.privilege) AS granted
           FROM (VALUES ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE')) AS p (privilege)`,
        );
        for (const row of tables.rows) expect(row.granted, `sync_events ${row.privilege}`).toBe(false);

        const maintained = [
          ['workspaces', 'event_seq'],
          ['workspaces', 'owner_id'],
          ['nodes', 'meta_rev'],
          ['nodes', 'workspace_id'],
          ['nodes', 'kind'],
          ['document_contents', 'content_rev'],
          ['document_contents', 'workspace_id'],
        ];
        for (const [table, column] of maintained) {
          const result = await db.owner.query<{ granted: boolean }>(
            "SELECT has_column_privilege('nimpad_app', $1, $2, 'UPDATE') AS granted",
            [`nimpad.${table}`, column],
          );
          expect(result.rows[0]!.granted, `${table}.${column}`).toBe(false);
        }
      });

      it('ties copied workspace ids to the node with two-column foreign keys', async () => {
        // RLS trusts workspace_id on these tables; only the composite key keeps it equal to the node's workspace.
        const result = await db.owner.query<{ conname: string; columns: string[]; referenced: string[] }>(
          `SELECT c.conname,
                  array(SELECT a.attname::text FROM unnest(c.conkey) WITH ORDINALITY k (attnum, i)
                        JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum ORDER BY k.i) AS columns,
                  array(SELECT a.attname::text FROM unnest(c.confkey) WITH ORDINALITY k (attnum, i)
                        JOIN pg_attribute a ON a.attrelid = c.confrelid AND a.attnum = k.attnum ORDER BY k.i) AS referenced
           FROM pg_constraint c
           WHERE c.contype = 'f' AND c.confrelid = 'nimpad.nodes'::regclass
           ORDER BY c.conname`,
        );
        expect(result.rows).toEqual([
          {
            conname: 'document_contents_node_fkey',
            columns: ['workspace_id', 'node_id'],
            referenced: ['workspace_id', 'id'],
          },
          {
            conname: 'document_links_source_fkey',
            columns: ['workspace_id', 'source_id'],
            referenced: ['workspace_id', 'id'],
          },
          {
            conname: 'document_revisions_node_fkey',
            columns: ['workspace_id', 'node_id'],
            referenced: ['workspace_id', 'id'],
          },
          {
            conname: 'nodes_parent_fkey',
            columns: ['workspace_id', 'parent_id'],
            referenced: ['workspace_id', 'id'],
          },
        ]);
      });

      it('grants nimpad_app no UPDATE/DELETE/TRUNCATE on append-only tables', async () => {
        const result = await db.owner.query<{ table_name: string; privilege: string; granted: boolean }>(
          `SELECT t.table_name, p.privilege, has_table_privilege('nimpad_app', 'nimpad.' || t.table_name, p.privilege) AS granted
         FROM (VALUES ('document_revisions'), ('audit_log'), ('sync_events')) AS t (table_name)
         CROSS JOIN (VALUES ('UPDATE'), ('DELETE'), ('TRUNCATE')) AS p (privilege)`,
        );
        for (const row of result.rows) expect(row.granted, `${row.table_name} ${row.privilege}`).toBe(false);
        const contents = await db.owner.query<{ granted: boolean }>(
          "SELECT has_table_privilege('nimpad_app', 'nimpad.document_contents', 'DELETE') AS granted",
        );
        expect(contents.rows[0]!.granted).toBe(false);
      });

      it('does not let PUBLIC execute any function in schema nimpad', async () => {
        const result = await db.owner.query<{ name: string }>(
          `SELECT p.oid::regprocedure::text AS name FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
         WHERE n.nspname = 'nimpad' AND has_function_privilege('public', p.oid, 'EXECUTE')`,
        );
        expect(result.rows).toEqual([]);
      });

      it.each(SUPABASE_ROLES)(
        'gives %s no privileges on schema nimpad or the migrations table',
        async (role) => {
          const schema = await db.owner.query<{ usage: boolean; create: boolean }>(
            `SELECT has_schema_privilege($1, 'nimpad', 'USAGE') AS usage, has_schema_privilege($1, 'nimpad', 'CREATE') AS create`,
            [role],
          );
          expect(schema.rows[0]).toEqual({ usage: false, create: false });

          const relations = await db.owner.query<{ name: string }>(
            `SELECT c.oid::regclass::text AS name FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE ((n.nspname = 'nimpad' AND c.relkind IN ('r', 'p', 'v', 'm', 'S'))
                OR c.oid = 'public.schema_migrations'::regclass)
           AND (CASE WHEN c.relkind = 'S'
                  THEN has_sequence_privilege($1, c.oid, 'USAGE,SELECT,UPDATE')
                  ELSE has_table_privilege($1, c.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                END)`,
            [role],
          );
          expect(relations.rows).toEqual([]);
          const migrationsTable = await db.owner.query<{ relrowsecurity: boolean }>(
            "SELECT relrowsecurity FROM pg_class WHERE oid = 'public.schema_migrations'::regclass",
          );
          expect(migrationsTable.rows[0]!.relrowsecurity).toBe(true);

          const functions = await db.owner.query<{ name: string }>(
            `SELECT p.oid::regprocedure::text AS name FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
         WHERE n.nspname = 'nimpad' AND has_function_privilege($1, p.oid, 'EXECUTE')`,
            [role],
          );
          expect(functions.rows).toEqual([]);

          // And in practice: the role cannot even reach the schema.
          const client = await db.owner.connect();
          try {
            await client.query(`SET ROLE ${pg.escapeIdentifier(role)}`);
            const error = await pgError(client.query('SELECT * FROM nimpad.nodes'));
            expect(error.message).toMatch(/permission denied for schema nimpad/);
            const migrations = await pgError(client.query('SELECT * FROM public.schema_migrations'));
            expect(migrations.message).toMatch(/permission denied for table schema_migrations/);
          } finally {
            await client.query('RESET ROLE').catch(() => {});
            client.release();
          }
        },
      );

      it('generates version-7 UUIDs in time order', async () => {
        const result = await db.owner.query<{ id: string }>(
          'SELECT nimpad.uuid_generate_v7()::text AS id FROM generate_series(1, 5)',
        );
        const ids = result.rows.map((row) => row.id);
        for (const id of ids)
          expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
        const millis = Number.parseInt(ids[0]!.replaceAll('-', '').slice(0, 12), 16);
        expect(Math.abs(millis - Date.now())).toBeLessThan(60_000);
        const prefixes = ids.map((id) => id.slice(0, 13));
        expect([...prefixes].sort()).toEqual(prefixes);
      });
    });

    describe('append-only tables, even for the owner', () => {
      it('blocks UPDATE of revisions but lets a purge remove them with their document', async () => {
        const doc = await createDocument(db, user, workspace, 'v1\n');
        const update = await pgError(
          db.owner.query("UPDATE nimpad.document_revisions SET change_summary = 'x'"),
        );
        expect(update.message).toBe('nimpad.document_revisions is append-only: UPDATE is not allowed');

        await asUser(db.app, user, (client) => client.query('DELETE FROM nimpad.nodes WHERE id = $1', [doc]));
        const left = await db.owner.query(
          'SELECT 1 FROM nimpad.document_revisions WHERE node_id = $1 UNION ALL SELECT 1 FROM nimpad.document_contents WHERE node_id = $1',
          [doc],
        );
        expect(left.rowCount).toBe(0);
      });

      it('blocks UPDATE, DELETE and TRUNCATE of the audit log', async () => {
        await db.owner.query(
          "INSERT INTO nimpad.audit_log (actor_kind, action, outcome) VALUES ('system', 'test.entry', 'allowed')",
        );
        for (const [sql, op] of [
          ["UPDATE nimpad.audit_log SET outcome = 'denied'", 'UPDATE'],
          ['DELETE FROM nimpad.audit_log', 'DELETE'],
          ['TRUNCATE nimpad.audit_log', 'TRUNCATE'],
        ]) {
          const error = await pgError(db.owner.query(sql!));
          expect(error.message).toBe(`nimpad.audit_log is append-only: ${op} is not allowed`);
        }
      });

      it('blocks UPDATE of sync events', async () => {
        await asUser(db.app, user, (client) =>
          client.query("SELECT nimpad.record_sync_event($1, 'node.created')", [workspace]),
        );
        const error = await pgError(db.owner.query("UPDATE nimpad.sync_events SET event_type = 'x'"));
        expect(error.message).toBe('nimpad.sync_events is append-only: UPDATE is not allowed');
      });
    });

    describe('node tree', () => {
      it('requires the parent to be a folder', async () => {
        const doc = await createDocument(db, user, workspace, 'parent?\n');
        const error = await pgError(
          asUser(db.app, user, (client) =>
            insertNode(client, { workspaceId: workspace, kind: 'document', parentId: doc }),
          ),
        );
        expect(error.constraint).toBe('nodes_parent_is_folder');

        const folder = await createFolder(db, user, workspace);
        const move = await pgError(
          asUser(db.app, user, (client) =>
            client.query('UPDATE nimpad.nodes SET parent_id = $1 WHERE id = $2', [doc, folder]),
          ),
        );
        expect(move.constraint).toBe('nodes_parent_is_folder');
      });

      it('rejects a parent in another workspace, even for a member of both', async () => {
        const otherWorkspace = await createWorkspace(db, user, 'Other');
        const foreignFolder = await createFolder(db, user, otherWorkspace);
        const error = await pgError(
          asUser(db.app, user, (client) =>
            insertNode(client, { workspaceId: workspace, kind: 'folder', parentId: foreignFolder }),
          ),
        );
        expect(error.code).toBe('23503');
        expect(error.constraint).toBe('nodes_parent_fkey');

        const local = await createFolder(db, user, workspace);
        const move = await pgError(
          asUser(db.app, user, (client) =>
            client.query('UPDATE nimpad.nodes SET parent_id = $1 WHERE id = $2', [foreignFolder, local]),
          ),
        );
        expect(move.constraint).toBe('nodes_parent_fkey');
        // Moving a node to another workspace is not possible at all.
        const transfer = await pgError(
          asUser(db.app, user, (client) =>
            client.query('UPDATE nimpad.nodes SET workspace_id = $1 WHERE id = $2', [otherWorkspace, local]),
          ),
        );
        expect(transfer.code).toBe('42501');
        // Not even the owner, whose column privileges would allow it.
        const ownerTransfer = await pgError(
          db.owner.query('UPDATE nimpad.nodes SET workspace_id = $1 WHERE id = $2', [otherWorkspace, local]),
        );
        expect(ownerTransfer.constraint).toBe('nodes_identity_immutable');
      });

      it('rejects cycles: self, direct and deep', async () => {
        const a = await createFolder(db, user, workspace);
        const b = await createFolder(db, user, workspace, a);
        const c = await createFolder(db, user, workspace, b);
        const move = (node: string, parent: string) =>
          pgError(
            asUser(db.app, user, (client) =>
              client.query('UPDATE nimpad.nodes SET parent_id = $1 WHERE id = $2', [parent, node]),
            ),
          );
        expect((await move(a, a)).constraint).toBe('nodes_no_cycle');
        expect((await move(a, b)).constraint).toBe('nodes_no_cycle');
        expect((await move(a, c)).constraint).toBe('nodes_no_cycle');
        expect(await parentOf(db, a)).toBeNull();

        // Moving down a level elsewhere is fine.
        const d = await createFolder(db, user, workspace);
        await asUser(db.app, user, (client) =>
          client.query('UPDATE nimpad.nodes SET parent_id = $1 WHERE id = $2', [c, d]),
        );
        expect(await parentOf(db, d)).toBe(c);
      });

      it('rejects a cycle created by one statement that swaps two parents', async () => {
        const a = await createFolder(db, user, workspace);
        const b = await createFolder(db, user, workspace);
        const error = await pgError(
          asUser(db.app, user, (client) =>
            client.query(
              'UPDATE nimpad.nodes SET parent_id = CASE id WHEN $1::uuid THEN $2::uuid ELSE $1::uuid END WHERE id IN ($1, $2)',
              [a, b],
            ),
          ),
        );
        expect(error.constraint).toBe('nodes_no_cycle');
        expect(await parentOf(db, a)).toBeNull();
        expect(await parentOf(db, b)).toBeNull();
      });

      it('keeps node kind immutable', async () => {
        const folder = await createFolder(db, user, workspace);
        const asApp = await pgError(
          asUser(db.app, user, (client) =>
            client.query("UPDATE nimpad.nodes SET kind = 'document' WHERE id = $1", [folder]),
          ),
        );
        expect(asApp.code).toBe('42501');
        const asOwner = await pgError(
          db.owner.query("UPDATE nimpad.nodes SET kind = 'document' WHERE id = $1", [folder]),
        );
        expect(asOwner.constraint).toBe('nodes_kind_immutable');
      });

      it('validates names, positions, pins and trash fields', async () => {
        const attempts: [Partial<Record<string, unknown>>, string][] = [
          [{ name: '' }, 'nodes_name_valid'],
          [{ name: '   ' }, 'nodes_name_valid'],
          [{ name: 'tab\there' }, 'nodes_name_valid'],
          [{ name: 'x'.repeat(256) }, 'nodes_name_valid'],
          [{ position: 'a 0' }, 'nodes_position_valid'],
          [{ position: 'a00' }, 'nodes_position_valid'],
          [{ is_pinned: true }, 'nodes_pin_position_iff_pinned'],
          [{ is_pinned: true, pin_position: 'b0' }, 'nodes_pin_position_valid'],
          [{ deleted_at: new Date() }, 'nodes_trash_fields_together'],
        ];
        for (const [overrides, constraint] of attempts) {
          const row = {
            name: 'ok',
            position: 'a0',
            is_pinned: false,
            pin_position: null,
            deleted_at: null,
            ...overrides,
          };
          const error = await pgError(
            asUser(db.app, user, (client) =>
              client.query(
                `INSERT INTO nimpad.nodes (id, workspace_id, kind, name, position, is_pinned, pin_position, deleted_at)
                 VALUES ($1, $2, 'folder', $3, $4, $5, $6, $7)`,
                [newId(), workspace, row.name, row.position, row.is_pinned, row.pin_position, row.deleted_at],
              ),
            ),
          );
          expect(error.constraint, JSON.stringify(overrides)).toBe(constraint);
        }
      });

      it('accepts exactly the position keys the shared fractional-index contract accepts', async () => {
        const fixture = await contractFixture<FractionalIndexFixture>('fractional-index.json');
        const valid = new Set<string>();
        for (const c of fixture.between) {
          valid.add(c.expected);
          for (const key of [c.a, c.b]) if (key !== null) valid.add(key);
        }
        for (const c of fixture.nKeys) for (const key of c.expected) valid.add(key);
        // The keys the reference rejects as malformed (rather than as out of order), plus a few more.
        const invalid = new Set([
          '',
          'a',
          'b0',
          'a00',
          'a0V0',
          '0',
          'A',
          `A${'0'.repeat(25)}`,
          'z'.repeat(26),
          'a0-',
        ]);
        for (const c of fixture.invalid) {
          const malformed = /^invalid order key(?: head)?: (.*)$/.exec(c.message);
          if (!malformed) continue;
          for (const key of [c.a, c.b]) {
            if (key !== null && (key === malformed[1] || (key[0] ?? 'undefined') === malformed[1]))
              invalid.add(key);
          }
        }
        expect(valid.size).toBeGreaterThan(200);
        expect(invalid).toContain(`A${'0'.repeat(26)}`);
        for (const key of valid) expect(invalid.has(key), key).toBe(false);

        const check = async (keys: Set<string>) => {
          const result = await db.owner.query<{ key: string; ok: boolean }>(
            'SELECT key, nimpad.is_valid_order_key(key) AS ok FROM unnest($1::text[]) AS key',
            [[...keys]],
          );
          return result.rows;
        };
        expect((await check(valid)).filter((row) => !row.ok)).toEqual([]);
        expect((await check(invalid)).filter((row) => row.ok)).toEqual([]);
      });

      it('accepts exactly the names the shared NodeName contract accepts', async () => {
        const fixture = await contractFixture<NodeNamesFixture>('node-names.json');
        const cases = fixture.cases.filter(
          (c): c is NodeNamesFixture['cases'][number] & { text: string } =>
            c.text !== undefined && isStorable(c.text),
        );
        expect(cases.length).toBeGreaterThan(25);
        const result = await db.owner.query<{ name: string; ok: boolean }>(
          'SELECT name, nimpad.is_valid_name(name) AS ok FROM unnest($1::text[]) AS name',
          [cases.map((c) => c.text)],
        );
        const verdicts = new Map(result.rows.map((row) => [row.name, row.ok]));
        for (const c of cases) expect(verdicts.get(c.text), c.description).toBe(c.valid);

        // Length is in UTF-16 code units, as in .NET and JavaScript: an emoji counts twice.
        const emoji = '\u{1F600}';
        const lengths = await db.owner.query<{ ok: boolean[] }>(
          'SELECT array_agg(nimpad.is_valid_name(name) ORDER BY i) AS ok FROM unnest($1::text[]) WITH ORDINALITY AS t (name, i)',
          [[`${emoji.repeat(127)}x`, emoji.repeat(128), emoji.repeat(200)]],
        );
        expect(lengths.rows[0]!.ok).toEqual([true, false, false]);

        const workspaceName = await pgError(
          asUser(db.app, user, (client) =>
            client.query('INSERT INTO nimpad.workspaces (owner_id, name) VALUES ($1, $2)', [user, ' padded']),
          ),
        );
        expect(workspaceName.constraint).toBe('workspaces_name_valid');
      });

      it('sorts positions ordinally (COLLATE "C"), as the shared ordering contract requires', async () => {
        const parent = await createFolder(db, user, workspace);
        await asUser(db.app, user, async (client) => {
          for (const position of ['a0', 'Zz', 'a0V', 'Yzz', 'b00', 'a0a', 'a0B']) {
            await insertNode(client, { workspaceId: workspace, kind: 'folder', parentId: parent, position });
          }
        });
        const result = await db.owner.query<{ position: string }>(
          'SELECT position FROM nimpad.nodes WHERE parent_id = $1 ORDER BY position, id',
          [parent],
        );
        // A linguistic collation would put 'a0a' before 'a0B' and 'a0V'.
        expect(result.rows.map((row) => row.position)).toEqual([
          'Yzz',
          'Zz',
          'a0',
          'a0B',
          'a0V',
          'a0a',
          'b00',
        ]);
        // The cluster default collation may be linguistic (en_US on Supabase); the columns must not depend on it.
        const collations = await db.owner.query<{ column_name: string; collation_name: string }>(
          `SELECT column_name, collation_name FROM information_schema.columns
         WHERE table_schema = 'nimpad' AND table_name = 'nodes' AND column_name IN ('position', 'pin_position')
         ORDER BY column_name`,
        );
        expect(collations.rows).toEqual([
          { column_name: 'pin_position', collation_name: 'C' },
          { column_name: 'position', collation_name: 'C' },
        ]);
      });
    });

    describe('document contents', () => {
      it('verifies the checksum against the text', async () => {
        const doc = await createDocument(db, user, workspace, 'hello\n');
        const error = await pgError(
          asUser(db.app, user, (client) =>
            client.query("UPDATE nimpad.document_contents SET text = 'changed' WHERE node_id = $1", [doc]),
          ),
        );
        expect(error.constraint).toBe('document_contents_checksum_matches');
        const unicode = 'naïve – 😀\n';
        await asUser(db.app, user, (client) =>
          client.query(
            'UPDATE nimpad.document_contents SET text = $2, checksum = $3, char_count = $4 WHERE node_id = $1',
            [doc, unicode, checksum(unicode), unicode.length],
          ),
        );
      });

      it('stores only canonical text, so its checksum is the shared contract checksum', async () => {
        const fixture = await contractFixture<ChecksumFixture>('checksum.json');
        const doc = await createDocument(db, user, workspace, '');
        const save = (text: string, sum: string) =>
          asUser(db.app, user, (client) =>
            client.query(
              'UPDATE nimpad.document_contents SET text = $2, checksum = $3, char_count = $4 WHERE node_id = $1',
              [doc, text, sum, text.length],
            ),
          );
        let rejected = 0;
        for (const c of fixture.cases.filter((c) => isStorable(c.input))) {
          // The canonical text with the contract checksum is always accepted...
          await save(c.canonical, c.checksum);
          // ...and text that still contains CR is refused, whichever checksum comes with it.
          if (c.input !== c.canonical) {
            expect((await pgError(save(c.input, checksum(c.input)))).constraint, c.name).toBe(
              'document_contents_text_canonical',
            );
            expect((await pgError(save(c.input, c.checksum))).code, c.name).toBe('23514');
            rejected += 1;
          }
        }
        expect(rejected).toBeGreaterThan(5);

        const revision = await pgError(
          db.owner.query(
            `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind)
             VALUES ($1, $2, 99, $3, 'plain', $4, 'user')`,
            [doc, workspace, 'a\r\nb', checksum('a\r\nb')],
          ),
        );
        expect(revision.constraint).toBe('document_revisions_text_canonical');
      });

      it('only exists for documents', async () => {
        const folder = await createFolder(db, user, workspace);
        const error = await pgError(
          asUser(db.app, user, (client) =>
            client.query(
              `INSERT INTO nimpad.document_contents (node_id, workspace_id, text, checksum, char_count, updated_by_kind)
             VALUES ($1, $2, '', $3, 0, 'user')`,
              [folder, workspace, checksum('')],
            ),
          ),
        );
        expect(error.constraint).toBe('document_contents_node_is_document');
        const revision = await pgError(
          asUser(db.app, user, (client) =>
            client.query(
              `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind)
               VALUES ($1, $2, 1, '', 'plain', $3, 'user')`,
              [folder, workspace, checksum('')],
            ),
          ),
        );
        expect(revision.constraint).toBe('document_revisions_node_is_document');
      });

      it('keeps revision numbers unique per document', async () => {
        const doc = await createDocument(db, user, workspace, 'r1\n');
        const error = await pgError(
          asUser(db.app, user, (client) =>
            client.query(
              `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind)
             VALUES ($1, $2, 1, 'again', 'plain', $3, 'agent')`,
              [doc, workspace, checksum('again')],
            ),
          ),
        );
        expect(error.code).toBe('23505');
      });

      it('verifies the checksum of a revision', async () => {
        const doc = await createDocument(db, user, workspace, 'r1\n');
        const error = await pgError(
          db.owner.query(
            `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind)
             VALUES ($1, $2, 2, 'r2', 'plain', $3, 'user')`,
            [doc, workspace, checksum('something else')],
          ),
        );
        expect(error.constraint).toBe('document_revisions_checksum_matches');
      });

      it('makes a revision a snapshot of the current content, numbered by its content_rev', async () => {
        const doc = await createDocument(db, user, workspace, 'v1\n');
        const insertRevision = (revision: number, text: string, format = 'plain') =>
          asUser(db.app, user, (client) =>
            client.query(
              `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind)
               VALUES ($1, $2, $3, $4, $5, $6, 'user')`,
              [doc, workspace, revision, text, format, checksum(text)],
            ),
          );
        // Revision 2 before the content reaches content_rev 2, or with other text or format: refused.
        expect((await pgError(insertRevision(2, 'v1\n'))).constraint).toBe(
          'document_revisions_match_content',
        );
        await asUser(db.app, user, (client) =>
          client.query(
            'UPDATE nimpad.document_contents SET text = $2, checksum = $3, char_count = 3 WHERE node_id = $1',
            [doc, 'v2\n', checksum('v2\n')],
          ),
        );
        expect((await pgError(insertRevision(2, 'v1\n'))).constraint).toBe(
          'document_revisions_match_content',
        );
        expect((await pgError(insertRevision(2, 'v2\n', 'markdown'))).constraint).toBe(
          'document_revisions_match_content',
        );
        expect((await pgError(insertRevision(3, 'v2\n'))).constraint).toBe(
          'document_revisions_match_content',
        );
        await insertRevision(2, 'v2\n');

        // A document without content has nothing to snapshot.
        const bare = await asUser(db.app, user, (client) =>
          insertNode(client, { workspaceId: workspace, kind: 'document' }),
        );
        const error = await pgError(
          asUser(db.app, user, (client) =>
            client.query(
              `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind)
               VALUES ($1, $2, 1, '', 'plain', $3, 'user')`,
              [bare, workspace, checksum('')],
            ),
          ),
        );
        expect(error.constraint).toBe('document_revisions_match_content');
      });
    });

    describe('server revisions', () => {
      async function revs(node: string): Promise<{ meta_rev: number; content_rev: number | null }> {
        const result = await db.owner.query<{ meta_rev: string; content_rev: string | null }>(
          `SELECT n.meta_rev, c.content_rev FROM nimpad.nodes n
           LEFT JOIN nimpad.document_contents c ON c.node_id = n.id WHERE n.id = $1`,
          [node],
        );
        const row = result.rows[0]!;
        return {
          meta_rev: Number(row.meta_rev),
          content_rev: row.content_rev === null ? null : Number(row.content_rev),
        };
      }
      const asApp = (sql: string, params: unknown[]) =>
        asUser(db.app, user, (client) => client.query(sql, params));

      it('moves meta_rev forward on every metadata change, and only then', async () => {
        const folder = await createFolder(db, user, workspace);
        const doc = await createDocument(db, user, workspace, 'x\n');
        expect(await revs(doc)).toEqual({ meta_rev: 1, content_rev: 1 });

        const changes: [string, unknown[]][] = [
          ["UPDATE nimpad.nodes SET name = 'renamed' WHERE id = $1", [doc]],
          ['UPDATE nimpad.nodes SET parent_id = $2 WHERE id = $1', [doc, folder]],
          ["UPDATE nimpad.nodes SET position = 'a1' WHERE id = $1", [doc]],
          ["UPDATE nimpad.nodes SET is_pinned = true, pin_position = 'a0' WHERE id = $1", [doc]],
          ['UPDATE nimpad.nodes SET deleted_at = now(), trash_batch_id = $2 WHERE id = $1', [doc, newId()]],
        ];
        for (const [index, [sql, params]] of changes.entries()) {
          await asApp(sql, params);
          expect((await revs(doc)).meta_rev, sql).toBe(index + 2);
        }
        // No change, or a content timestamp only: the metadata revision stays.
        await asApp("UPDATE nimpad.nodes SET name = 'renamed' WHERE id = $1", [doc]);
        await asApp('UPDATE nimpad.nodes SET content_updated_at = now() WHERE id = $1', [doc]);
        expect(await revs(doc)).toEqual({ meta_rev: 6, content_rev: 1 });

        // nimpad_app cannot set it, and a value set by the owner is overwritten.
        expect(
          (await pgError(asApp('UPDATE nimpad.nodes SET meta_rev = 100 WHERE id = $1', [doc]))).code,
        ).toBe('42501');
        await db.owner.query("UPDATE nimpad.nodes SET meta_rev = 2, name = 'again' WHERE id = $1", [doc]);
        expect((await revs(doc)).meta_rev).toBe(7);
      });

      it('moves content_rev forward when text or format changes, and only then', async () => {
        const doc = await createDocument(db, user, workspace, 'one\n');
        const save = (text: string, format = 'plain') =>
          asApp(
            'UPDATE nimpad.document_contents SET text = $2, checksum = $3, char_count = $4, format = $5 WHERE node_id = $1 RETURNING content_rev',
            [doc, text, checksum(text), text.length, format],
          );
        expect((await save('two\n')).rows).toEqual([{ content_rev: '2' }]);
        expect((await save('two\n')).rows).toEqual([{ content_rev: '2' }]);
        expect((await save('two\n', 'markdown')).rows).toEqual([{ content_rev: '3' }]);
        await asApp("UPDATE nimpad.document_contents SET updated_by_kind = 'import' WHERE node_id = $1", [
          doc,
        ]);
        expect(await revs(doc)).toEqual({ meta_rev: 1, content_rev: 3 });

        // Optimistic concurrency: a write against a stale revision matches no row.
        const stale = await asApp(
          'UPDATE nimpad.document_contents SET text = $2, checksum = $3, char_count = 6 WHERE node_id = $1 AND content_rev = 2',
          [doc, 'stale\n', checksum('stale\n')],
        );
        expect(stale.rowCount).toBe(0);

        expect(
          (
            await pgError(
              asApp('UPDATE nimpad.document_contents SET content_rev = 1 WHERE node_id = $1', [doc]),
            )
          ).code,
        ).toBe('42501');
        await db.owner.query('UPDATE nimpad.document_contents SET content_rev = 1 WHERE node_id = $1', [doc]);
        expect((await revs(doc)).content_rev).toBe(3);
      });
    });

    describe('idempotency records', () => {
      it('expire seven days after creation by default', async () => {
        const opId = newId();
        await asUser(db.app, user, (client) =>
          client.query(
            "INSERT INTO nimpad.idempotency_records (user_id, op_id, request_hash, status_code) VALUES ($1, $2, 'h', 200)",
            [user, opId],
          ),
        );
        const result = await db.owner.query<{ ttl: boolean }>(
          "SELECT expires_at - created_at = interval '7 days' AS ttl FROM nimpad.idempotency_records WHERE op_id = $1",
          [opId],
        );
        expect(result.rows).toEqual([{ ttl: true }]);
      });

      it('purges only expired records', async () => {
        const fresh = newId();
        await asUser(db.app, user, async (client) => {
          await client.query(
            "INSERT INTO nimpad.idempotency_records (user_id, op_id, request_hash, status_code) VALUES ($1, $2, 'h', 200)",
            [user, fresh],
          );
        });
        const expired = [newId(), newId(), newId()];
        for (const opId of expired) {
          await db.owner.query(
            `INSERT INTO nimpad.idempotency_records (user_id, op_id, request_hash, status_code, created_at, expires_at)
           VALUES ($1, $2, 'h', 200, now() - interval '9 days', now() - interval '2 days')`,
            [user, opId],
          );
        }
        const purged = await asUser(db.app, user, (client) =>
          client.query<{ n: number }>('SELECT nimpad.purge_expired_idempotency(2) AS n'),
        );
        expect(purged.rows[0]!.n).toBe(2);
        const again = await asUser(db.app, user, (client) =>
          client.query<{ n: number }>('SELECT nimpad.purge_expired_idempotency() AS n'),
        );
        expect(again.rows[0]!.n).toBe(1);
        const left = await db.owner.query<{ op_id: string }>(
          'SELECT op_id FROM nimpad.idempotency_records WHERE op_id = ANY ($1)',
          [[fresh, ...expired]],
        );
        expect(left.rows).toEqual([{ op_id: fresh }]);
      });
    });
  },
);
