// Row-level security as the API sees it: a LOGIN role that is a member of nimpad_app, with app.user_id set per
// transaction. Every table in schema nimpad is covered.
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  addMember,
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
  pgError,
  setUser,
  suiteTitle,
  type TestDatabase,
} from '../src/harness.ts';

const unavailable = await databaseUnavailableReason();

const RLS_VIOLATION = '42501';

/** Every table that holds workspace data, with the column that ties a row to its workspace. */
const WORKSPACE_TABLES = [
  'nodes',
  'document_contents',
  'document_revisions',
  'document_links',
  'activity_events',
  'sync_events',
] as const;

const ALL_TABLES = [
  'users',
  'workspaces',
  'workspace_members',
  ...WORKSPACE_TABLES,
  'audit_log',
  'devices',
  'device_sessions',
  'idempotency_records',
] as const;

describe.skipIf(unavailable !== undefined)(suiteTitle('row-level security', unavailable), () => {
  let db: TestDatabase;
  // alice owns workspace A, bob owns workspace B; vera is a viewer and eddie an editor of A; nina is in nothing.
  let alice: string;
  let bob: string;
  let vera: string;
  let eddie: string;
  let nina: string;
  let workspaceA: string;
  let workspaceB: string;
  let folderA: string;
  let docA: string;
  let docB: string;
  let deviceB: string;

  async function count(userId: string | null, table: string): Promise<number> {
    return asUser(db.app, userId, async (client) => {
      const result = await client.query<{ n: number }>(`SELECT count(*)::int AS n FROM nimpad.${table}`);
      return result.rows[0]!.n;
    });
  }

  async function ownerCount(sql: string, params: unknown[]): Promise<number> {
    const result = await db.owner.query<{ n: number }>(`SELECT count(*)::int AS n FROM ${sql}`, params);
    return result.rows[0]!.n;
  }

  async function createDevice(userId: string): Promise<string> {
    return asUser(db.app, userId, async (client) => {
      const result = await client.query<{ id: string }>(
        `INSERT INTO nimpad.devices (user_id, name, platform, app_version) VALUES ($1, 'PC', 'windows', '0.1.0')
         RETURNING id`,
        [userId],
      );
      return result.rows[0]!.id;
    });
  }

  beforeAll(async () => {
    db = await createTestDatabase();
    [alice, bob, vera, eddie, nina] = (await Promise.all(
      ['alice', 'bob', 'vera', 'eddie', 'nina'].map((name) => createUser(db, name)),
    )) as [string, string, string, string, string];
    workspaceA = await createWorkspace(db, alice, 'Alice');
    workspaceB = await createWorkspace(db, bob, 'Bob');
    await addMember(db, alice, workspaceA, vera, 'viewer');
    await addMember(db, alice, workspaceA, eddie, 'editor');

    folderA = await createFolder(db, alice, workspaceA);
    docA = await createDocument(db, alice, workspaceA, 'alice text\n', folderA);
    docB = await createDocument(db, bob, workspaceB, 'bob text\n');

    for (const [user, workspace, doc] of [
      [alice, workspaceA, docA],
      [bob, workspaceB, docB],
    ] as const) {
      await asUser(db.app, user, async (client) => {
        await client.query(
          'INSERT INTO nimpad.document_links (workspace_id, source_id, target_id) VALUES ($1, $2, $3)',
          [workspace, doc, newId()],
        );
        await client.query(
          `INSERT INTO nimpad.activity_events (workspace_id, actor_kind, actor_id, verb, subject_type, subject_id)
           VALUES ($1, 'user', $2, 'document.created', 'node', $3)`,
          [workspace, user, doc],
        );
        await client.query("SELECT nimpad.record_sync_event($1, 'node.created', $2)", [workspace, doc]);
        await client.query(
          `INSERT INTO nimpad.audit_log (actor_kind, actor_id, workspace_id, action, target_type, target_id, outcome)
           VALUES ('user', $1, $2, 'document.create', 'node', $3, 'allowed')`,
          [user, workspace, doc],
        );
        await client.query(
          `INSERT INTO nimpad.idempotency_records (user_id, op_id, request_hash, status_code, response)
           VALUES ($1, $2, 'hash', 201, '{}')`,
          [user, newId()],
        );
      });
    }
    deviceB = await asUser(db.app, bob, async (client) => {
      const result = await client.query<{ id: string }>(
        `INSERT INTO nimpad.devices (user_id, name, platform, app_version) VALUES ($1, 'Bob PC', 'windows', '0.1.0')
         RETURNING id`,
        [bob],
      );
      const id = result.rows[0]!.id;
      await client.query(
        'INSERT INTO nimpad.device_sessions (device_id, user_id, workspace_id) VALUES ($1, $2, $3)',
        [id, bob, workspaceB],
      );
      return id;
    });
  });

  afterAll(async () => {
    await db?.drop();
  });

  it('seeds every table, as seen by the owner', async () => {
    for (const table of ALL_TABLES) {
      expect(await ownerCount(`nimpad.${table}`, []), table).toBeGreaterThan(0);
    }
  });

  it('shows nothing when app.user_id is not set', async () => {
    for (const table of ALL_TABLES) {
      expect(await count(null, table), table).toBe(0);
    }
  });

  it('shows nothing once a previous transaction on the same connection has ended', async () => {
    const client = await db.appClient();
    await client.query('BEGIN');
    await setUser(client, alice);
    expect((await client.query('SELECT 1 FROM nimpad.nodes')).rowCount).toBeGreaterThan(0);
    await client.query('COMMIT');
    // The transaction-local setting is gone (it reads as '' now, which counts as unset).
    for (const table of ALL_TABLES) {
      const result = await client.query<{ n: number }>(`SELECT count(*)::int AS n FROM nimpad.${table}`);
      expect(result.rows[0]!.n, table).toBe(0);
    }
  });

  it('shows each member (owner, editor, viewer) exactly their workspace, its rows and its members', async () => {
    const members = await ownerCount('nimpad.workspace_members WHERE workspace_id = $1', [workspaceA]);
    expect(members).toBe(3);
    for (const [label, userId] of [
      ['owner', alice],
      ['editor', eddie],
      ['viewer', vera],
    ] as const) {
      await asUser(db.app, userId, async (client) => {
        const workspaces = await client.query<{ id: string }>('SELECT id FROM nimpad.workspaces');
        expect(
          workspaces.rows.map((row) => row.id),
          label,
        ).toEqual([workspaceA]);
        for (const table of WORKSPACE_TABLES) {
          const result = await client.query<{ workspace_id: string }>(
            `SELECT DISTINCT workspace_id FROM nimpad.${table}`,
          );
          expect(
            result.rows.map((row) => row.workspace_id),
            `${label}: ${table}`,
          ).toEqual([workspaceA]);
        }
        const memberRows = await client.query<{ workspace_id: string }>(
          'SELECT workspace_id FROM nimpad.workspace_members',
        );
        expect(memberRows.rows, label).toHaveLength(members);
        expect(new Set(memberRows.rows.map((row) => row.workspace_id)), label).toEqual(new Set([workspaceA]));
        const byId = await client.query('SELECT 1 FROM nimpad.nodes WHERE id = $1', [docB]);
        expect(byId.rowCount, label).toBe(0);
      });
    }
  });

  it("shows members their co-members' profiles, and nobody else's", async () => {
    const visible = async (userId: string) =>
      asUser(db.app, userId, async (client) => {
        const result = await client.query<{ id: string; display_name: string | null }>(
          'SELECT id, display_name FROM nimpad.users ORDER BY id',
        );
        return result.rows;
      });
    const coMembersOfA = [alice, eddie, vera].sort();
    for (const userId of [alice, eddie, vera]) {
      expect((await visible(userId)).map((row) => row.id)).toEqual(coMembersOfA);
    }
    // The owner can show who wrote a revision or who is a member: display names resolve.
    expect((await visible(alice)).find((row) => row.id === vera)?.display_name).toBe('vera');
    expect((await visible(bob)).map((row) => row.id)).toEqual([bob]);
    expect((await visible(nina)).map((row) => row.id)).toEqual([nina]);
  });

  it('shows a user who is in no workspace nothing but their own profile', async () => {
    for (const table of ALL_TABLES) {
      expect(await count(nina, table), table).toBe(table === 'users' ? 1 : 0);
    }
  });

  it("does not let a user update or delete another workspace's rows", async () => {
    await asUser(db.app, alice, async (client) => {
      const rename = await client.query("UPDATE nimpad.nodes SET name = 'stolen' WHERE id = $1", [docB]);
      expect(rename.rowCount).toBe(0);
      const content = await client.query(
        "UPDATE nimpad.document_contents SET text = '', checksum = $2, char_count = 0 WHERE node_id = $1",
        [docB, checksum('')],
      );
      expect(content.rowCount).toBe(0);
      expect(
        (await client.query('DELETE FROM nimpad.nodes WHERE workspace_id = $1', [workspaceB])).rowCount,
      ).toBe(0);
      expect(
        (await client.query('DELETE FROM nimpad.document_links WHERE workspace_id = $1', [workspaceB]))
          .rowCount,
      ).toBe(0);
      const workspace = await client.query("UPDATE nimpad.workspaces SET name = 'stolen' WHERE id = $1", [
        workspaceB,
      ]);
      expect(workspace.rowCount).toBe(0);
      const member = await client.query(
        "UPDATE nimpad.workspace_members SET role = 'viewer' WHERE workspace_id = $1",
        [workspaceB],
      );
      expect(member.rowCount).toBe(0);
      expect(
        (await client.query('DELETE FROM nimpad.workspace_members WHERE workspace_id = $1', [workspaceB]))
          .rowCount,
      ).toBe(0);
      expect(
        (await client.query("UPDATE nimpad.users SET display_name = 'x' WHERE id = $1", [bob])).rowCount,
      ).toBe(0);
      expect(
        (await client.query("UPDATE nimpad.devices SET name = 'x' WHERE id = $1", [deviceB])).rowCount,
      ).toBe(0);
      expect(
        (await client.query('DELETE FROM nimpad.device_sessions WHERE device_id = $1', [deviceB])).rowCount,
      ).toBe(0);
    });
    expect(await ownerCount("nimpad.nodes WHERE id = $1 AND name <> 'stolen'", [docB])).toBe(1);
    expect(
      await ownerCount("nimpad.document_contents WHERE node_id = $1 AND text = 'bob text\n'", [docB]),
    ).toBe(1);
    expect(await ownerCount('nimpad.workspace_members WHERE workspace_id = $1', [workspaceB])).toBe(1);
    expect(await ownerCount('nimpad.device_sessions WHERE device_id = $1', [deviceB])).toBe(1);
  });

  it('does not let a user insert into another workspace, or join it', async () => {
    const attempts: [string, string, unknown[]][] = [
      [
        'nodes',
        `INSERT INTO nimpad.nodes (id, workspace_id, kind, name, position) VALUES ($1, $2, 'folder', 'x', 'a0')`,
        [newId(), workspaceB],
      ],
      [
        'document_contents',
        `INSERT INTO nimpad.document_contents (node_id, workspace_id, text, checksum, char_count, updated_by_kind)
         VALUES ($1, $2, '', $3, 0, 'user')`,
        [docB, workspaceB, checksum('')],
      ],
      [
        'document_revisions',
        `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind)
         VALUES ($1, $2, 99, '', 'plain', $3, 'user')`,
        [docB, workspaceB, checksum('')],
      ],
      [
        'document_links',
        'INSERT INTO nimpad.document_links (workspace_id, source_id, target_id) VALUES ($1, $2, $3)',
        [workspaceB, docB, newId()],
      ],
      [
        'activity_events',
        `INSERT INTO nimpad.activity_events (workspace_id, actor_kind, verb, subject_type) VALUES ($1, 'user', 'x', 'node')`,
        [workspaceB],
      ],
      [
        'workspace_members',
        "INSERT INTO nimpad.workspace_members (workspace_id, user_id, role) VALUES ($1, $2, 'owner')",
        [workspaceB, alice],
      ],
      ['record_sync_event', "SELECT nimpad.record_sync_event($1, 'node.created')", [workspaceB]],
      [
        'workspaces (owned by someone else)',
        "INSERT INTO nimpad.workspaces (owner_id, name) VALUES ($1, 'Fake')",
        [bob],
      ],
      [
        'users (someone else)',
        "INSERT INTO nimpad.users (id, email) VALUES ($1, 'x@example.test')",
        [newId()],
      ],
      [
        'devices (someone else)',
        "INSERT INTO nimpad.devices (user_id, name, platform, app_version) VALUES ($1, 'x', 'windows', '1')",
        [bob],
      ],
      [
        'idempotency_records (someone else)',
        "INSERT INTO nimpad.idempotency_records (user_id, op_id, request_hash, status_code) VALUES ($1, $2, 'h', 200)",
        [bob, newId()],
      ],
    ];
    for (const [label, sql, params] of attempts) {
      const error = await pgError(asUser(db.app, alice, (client) => client.query(sql, params)));
      expect(error.code, label).toBe(RLS_VIOLATION);
    }
    expect(await ownerCount('nimpad.workspace_members WHERE workspace_id = $1', [workspaceB])).toBe(1);
  });

  it("does not let a row claim the caller's workspace while pointing at a node of another one", async () => {
    // Row-level security trusts the copied workspace_id; the composite foreign keys keep it honest.
    const bareB = await asUser(db.app, bob, (client) =>
      insertNode(client, { workspaceId: workspaceB, kind: 'document' }),
    );
    const forgeries: [string, string, unknown[]][] = [
      [
        'document_revisions_node_fkey',
        // An unused number: without the composite key this would take bob's next revision number.
        `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind)
         VALUES ($1, $2, 2, 'bob text\n', 'plain', $3, 'user')`,
        [docB, workspaceA, checksum('bob text\n')],
      ],
      [
        'document_contents_node_fkey',
        `INSERT INTO nimpad.document_contents (node_id, workspace_id, text, checksum, char_count, updated_by_kind)
         VALUES ($1, $2, '', $3, 0, 'user')`,
        [bareB, workspaceA, checksum('')],
      ],
      [
        'document_links_source_fkey',
        'INSERT INTO nimpad.document_links (workspace_id, source_id, target_id) VALUES ($1, $2, $3)',
        [workspaceA, docB, newId()],
      ],
      [
        'nodes_parent_fkey',
        `INSERT INTO nimpad.nodes (id, workspace_id, parent_id, kind, name, position)
         VALUES ($1, $2, $3, 'folder', 'x', 'a0')`,
        [newId(), workspaceA, bareB],
      ],
    ];
    for (const [constraint, sql, params] of forgeries) {
      const error = await pgError(asUser(db.app, alice, (client) => client.query(sql, params)));
      expect(error.code, constraint).toBe('23503');
      expect(error.constraint).toBe(constraint);
    }
    expect(await ownerCount('nimpad.document_revisions WHERE node_id = $1', [docB])).toBe(1);
  });

  it('lets nobody write sync events or the event counter except through record_sync_event', async () => {
    const attempts: [string, unknown[]][] = [
      ['UPDATE nimpad.workspaces SET event_seq = 0 WHERE id = $1', [workspaceA]],
      [
        `INSERT INTO nimpad.sync_events (workspace_id, seq, event_type, actor_kind) VALUES ($1, 1000, 'node.created', 'user')`,
        [workspaceA],
      ],
      ['DELETE FROM nimpad.sync_events WHERE workspace_id = $1', [workspaceA]],
    ];
    for (const [sql, params] of attempts) {
      expect((await pgError(asUser(db.app, alice, (client) => client.query(sql, params)))).code, sql).toBe(
        '42501',
      );
    }
    expect(await ownerCount('nimpad.workspaces WHERE id = $1 AND event_seq = 1', [workspaceA])).toBe(1);
  });

  it('lets a viewer read but not write', async () => {
    expect(await count(vera, 'nodes')).toBe(
      await ownerCount('nimpad.nodes WHERE workspace_id = $1', [workspaceA]),
    );
    expect(await count(vera, 'document_contents')).toBe(1);

    // Every insert path the app role has on workspace tables: refused by the insert policies.
    const inserts: [string, string, unknown[]][] = [
      [
        'nodes',
        `INSERT INTO nimpad.nodes (id, workspace_id, kind, name, position) VALUES ($1, $2, 'document', 'x', 'a1')`,
        [newId(), workspaceA],
      ],
      [
        'document_contents',
        `INSERT INTO nimpad.document_contents (node_id, workspace_id, text, checksum, char_count, updated_by_kind)
         VALUES ($1, $2, '', $3, 0, 'user')`,
        [docA, workspaceA, checksum('')],
      ],
      [
        'document_revisions',
        `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind)
         VALUES ($1, $2, 1, 'alice text\n', 'plain', $3, 'user')`,
        [docA, workspaceA, checksum('alice text\n')],
      ],
      [
        'document_links',
        'INSERT INTO nimpad.document_links (workspace_id, source_id, target_id) VALUES ($1, $2, $3)',
        [workspaceA, docA, newId()],
      ],
      [
        'activity_events',
        `INSERT INTO nimpad.activity_events (workspace_id, actor_kind, actor_id, verb, subject_type)
         VALUES ($1, 'user', $2, 'document.viewed', 'node')`,
        [workspaceA, vera],
      ],
      [
        'workspace_members',
        "INSERT INTO nimpad.workspace_members (workspace_id, user_id, role) VALUES ($1, $2, 'viewer')",
        [workspaceA, nina],
      ],
      ['record_sync_event', "SELECT nimpad.record_sync_event($1, 'node.updated')", [workspaceA]],
    ];
    for (const [label, sql, params] of inserts) {
      expect((await pgError(asUser(db.app, vera, (client) => client.query(sql, params)))).code, label).toBe(
        RLS_VIOLATION,
      );
    }

    // Every update and delete path: the policies hide the rows, so nothing changes.
    const changes: [string, string, unknown[]][] = [
      ['rename a node', "UPDATE nimpad.nodes SET name = 'x' WHERE id = $1", [docA]],
      ['move a node', 'UPDATE nimpad.nodes SET parent_id = NULL WHERE id = $1', [docA]],
      ['delete a node', 'DELETE FROM nimpad.nodes WHERE id = $1', [docA]],
      [
        'save content',
        "UPDATE nimpad.document_contents SET text = '', checksum = $2, char_count = 0 WHERE node_id = $1",
        [docA, checksum('')],
      ],
      ['delete a link', 'DELETE FROM nimpad.document_links WHERE source_id = $1', [docA]],
      ['rename the workspace', "UPDATE nimpad.workspaces SET name = 'x' WHERE id = $1", [workspaceA]],
      ['trash the workspace', 'UPDATE nimpad.workspaces SET deleted_at = now() WHERE id = $1', [workspaceA]],
      ['promote themselves', "UPDATE nimpad.workspace_members SET role = 'owner' WHERE user_id = $1", [vera]],
      [
        'demote the editor',
        "UPDATE nimpad.workspace_members SET role = 'viewer' WHERE user_id = $1",
        [eddie],
      ],
      ['remove the editor', 'DELETE FROM nimpad.workspace_members WHERE user_id = $1', [eddie]],
    ];
    for (const [label, sql, params] of changes) {
      const result = await asUser(db.app, vera, (client) => client.query(sql, params));
      expect(result.rowCount, label).toBe(0);
    }

    // Tables the app role may only append to.
    for (const sql of [
      "UPDATE nimpad.activity_events SET verb = 'x'",
      'DELETE FROM nimpad.activity_events',
      "UPDATE nimpad.document_revisions SET change_summary = 'x'",
      'DELETE FROM nimpad.document_contents',
    ]) {
      expect((await pgError(asUser(db.app, vera, (client) => client.query(sql)))).code, sql).toBe('42501');
    }

    expect(await ownerCount("nimpad.workspace_members WHERE user_id = $1 AND role = 'viewer'", [vera])).toBe(
      1,
    );
    expect(await ownerCount("nimpad.workspace_members WHERE user_id = $1 AND role = 'editor'", [eddie])).toBe(
      1,
    );
    expect(await ownerCount('nimpad.nodes WHERE id = $1 AND parent_id = $2', [docA, folderA])).toBe(1);
    expect(await ownerCount('nimpad.document_links WHERE source_id = $1', [docA])).toBe(1);
    expect(await ownerCount('nimpad.workspaces WHERE id = $1 AND deleted_at IS NULL', [workspaceA])).toBe(1);
  });

  it('lets an editor write content but not rename the workspace or manage members', async () => {
    // Node, content and revision 1, all as the editor.
    const doc = await createDocument(db, eddie, workspaceA, 'eddie v1\n', folderA);
    const folder = await createFolder(db, eddie, workspaceA, folderA);
    await asUser(db.app, eddie, async (client) => {
      const save = await client.query(
        `UPDATE nimpad.document_contents SET text = $2, checksum = $3, char_count = 9, updated_by_kind = 'user',
           updated_by_id = $4
         WHERE node_id = $1 AND content_rev = 1 RETURNING content_rev`,
        [doc, 'eddie v2\n', checksum('eddie v2\n'), eddie],
      );
      expect(save.rows).toEqual([{ content_rev: '2' }]);
      await client.query(
        `INSERT INTO nimpad.document_revisions (node_id, workspace_id, revision, text, format, checksum, actor_kind, actor_id)
         VALUES ($1, $2, 2, $3, 'plain', $4, 'user', $5)`,
        [doc, workspaceA, 'eddie v2\n', checksum('eddie v2\n'), eddie],
      );
      const target = newId();
      await client.query(
        'INSERT INTO nimpad.document_links (workspace_id, source_id, target_id) VALUES ($1, $2, $3)',
        [workspaceA, doc, target],
      );
      expect(
        (
          await client.query('DELETE FROM nimpad.document_links WHERE source_id = $1 AND target_id = $2', [
            doc,
            target,
          ])
        ).rowCount,
      ).toBe(1);
      await client.query(
        `INSERT INTO nimpad.activity_events (workspace_id, actor_kind, actor_id, verb, subject_type, subject_id)
         VALUES ($1, 'user', $2, 'document.updated', 'node', $3)`,
        [workspaceA, eddie, doc],
      );
      expect(
        (await client.query("UPDATE nimpad.nodes SET name = 'E' WHERE id = $1", [folder])).rowCount,
      ).toBe(1);
      await client.query("SELECT nimpad.record_sync_event($1, 'node.renamed', $2)", [workspaceA, folder]);
      expect((await client.query('DELETE FROM nimpad.nodes WHERE id = $1', [folder])).rowCount).toBe(1);
    });
    expect(await ownerCount('nimpad.document_revisions WHERE node_id = $1', [doc])).toBe(2);
    expect(
      await ownerCount("nimpad.activity_events WHERE actor_id = $1 AND verb = 'document.updated'", [eddie]),
    ).toBe(1);
    expect(await ownerCount('nimpad.nodes WHERE id = $1', [folder])).toBe(0);

    await asUser(db.app, eddie, async (client) => {
      expect(
        (await client.query("UPDATE nimpad.workspaces SET name = 'x' WHERE id = $1", [workspaceA])).rowCount,
      ).toBe(0);
      expect(
        (
          await client.query(
            "UPDATE nimpad.workspace_members SET role = 'viewer' WHERE workspace_id = $1 AND user_id = $2",
            [workspaceA, vera],
          )
        ).rowCount,
      ).toBe(0);
      expect(
        (
          await client.query(
            'DELETE FROM nimpad.workspace_members WHERE workspace_id = $1 AND user_id = $2',
            [workspaceA, vera],
          )
        ).rowCount,
      ).toBe(0);
    });
    const join = await pgError(
      asUser(db.app, eddie, (client) =>
        client.query(
          "INSERT INTO nimpad.workspace_members (workspace_id, user_id, role) VALUES ($1, $2, 'viewer')",
          [workspaceA, nina],
        ),
      ),
    );
    expect(join.code).toBe(RLS_VIOLATION);
    expect(await ownerCount('nimpad.workspace_members WHERE workspace_id = $1', [workspaceA])).toBe(3);
  });

  it('lets the owner manage members, but never orphan the workspace', async () => {
    await asUser(db.app, alice, async (client) => {
      await client.query(
        "INSERT INTO nimpad.workspace_members (workspace_id, user_id, role) VALUES ($1, $2, 'viewer')",
        [workspaceA, nina],
      );
      const promote = await client.query(
        "UPDATE nimpad.workspace_members SET role = 'editor' WHERE workspace_id = $1 AND user_id = $2",
        [workspaceA, nina],
      );
      expect(promote.rowCount).toBe(1);
    });
    expect(await count(nina, 'nodes')).toBeGreaterThan(0);

    // A member may leave on their own.
    await asUser(db.app, nina, (client) =>
      client.query('DELETE FROM nimpad.workspace_members WHERE workspace_id = $1 AND user_id = $2', [
        workspaceA,
        nina,
      ]),
    );
    expect(await count(nina, 'nodes')).toBe(0);

    const demote = await pgError(
      asUser(db.app, alice, (client) =>
        client.query(
          "UPDATE nimpad.workspace_members SET role = 'editor' WHERE workspace_id = $1 AND user_id = $2",
          [workspaceA, alice],
        ),
      ),
    );
    expect(demote.constraint).toBe('workspace_members_owner_kept');
    const leave = await pgError(
      asUser(db.app, alice, (client) =>
        client.query('DELETE FROM nimpad.workspace_members WHERE workspace_id = $1 AND user_id = $2', [
          workspaceA,
          alice,
        ]),
      ),
    );
    expect(leave.constraint).toBe('workspace_members_owner_kept');
  });

  it('lets users edit only their own profile', async () => {
    await asUser(db.app, alice, async (client) => {
      expect(
        (await client.query("UPDATE nimpad.users SET display_name = 'Alice A.' WHERE id = $1", [alice]))
          .rowCount,
      ).toBe(1);
      // Visible as a co-member, but not editable.
      expect(
        (await client.query("UPDATE nimpad.users SET display_name = 'x' WHERE id = $1", [vera])).rowCount,
      ).toBe(0);
    });
    expect(await ownerCount("nimpad.users WHERE id = $1 AND display_name = 'vera'", [vera])).toBe(1);
    const takeover = await pgError(
      asUser(db.app, alice, (client) =>
        client.query('UPDATE nimpad.users SET id = $2 WHERE id = $1', [alice, bob]),
      ),
    );
    expect(takeover.code).toBe('42501');
  });

  it('keeps the audit log insert-only and shows users only their own actions', async () => {
    await asUser(db.app, null, (client) =>
      client.query(
        `INSERT INTO nimpad.audit_log (actor_kind, action, outcome, details, request_id)
         VALUES ('anonymous', 'auth.sign_in', 'denied', '{"reason": "bad token"}', 'req-1')`,
      ),
    );
    await asUser(db.app, alice, async (client) => {
      const rows = await client.query<{ actor_id: string }>('SELECT actor_id FROM nimpad.audit_log');
      expect(rows.rows.length).toBeGreaterThan(0);
      expect(rows.rows.every((row) => row.actor_id === alice)).toBe(true);
    });
    expect(await count(vera, 'audit_log')).toBe(0);
    for (const sql of ["UPDATE nimpad.audit_log SET outcome = 'allowed'", 'DELETE FROM nimpad.audit_log']) {
      expect((await pgError(asUser(db.app, alice, (client) => client.query(sql)))).code, sql).toBe('42501');
    }
  });

  it('does not let nimpad_app change or delete revisions', async () => {
    for (const sql of [
      "UPDATE nimpad.document_revisions SET text = 'rewritten'",
      'DELETE FROM nimpad.document_revisions',
    ]) {
      const error = await pgError(asUser(db.app, alice, (client) => client.query(sql)));
      expect(error.code, sql).toBe('42501');
      expect(error.message, sql).toMatch(/permission denied for table document_revisions/);
    }
  });

  it('does not let a device session point at a workspace the user cannot read', async () => {
    const error = await pgError(
      asUser(db.app, bob, (client) =>
        client.query('UPDATE nimpad.device_sessions SET workspace_id = $2 WHERE device_id = $1', [
          deviceB,
          workspaceA,
        ]),
      ),
    );
    expect(error.code).toBe(RLS_VIOLATION);
    const newDevice = await createDevice(bob);
    const insert = await pgError(
      asUser(db.app, bob, (client) =>
        client.query(
          'INSERT INTO nimpad.device_sessions (device_id, user_id, workspace_id) VALUES ($1, $2, $3)',
          [newDevice, bob, workspaceA],
        ),
      ),
    );
    expect(insert.code).toBe(RLS_VIOLATION);
  });

  it("does not let a user touch another user's devices, sessions or idempotency records", async () => {
    // A session row for bob's device under alice's name: the composite key ties a session to its device's user.
    const bobsDevice = await createDevice(bob);
    const session = await pgError(
      asUser(db.app, alice, (client) =>
        client.query('INSERT INTO nimpad.device_sessions (device_id, user_id) VALUES ($1, $2)', [
          bobsDevice,
          alice,
        ]),
      ),
    );
    expect(session.code).toBe('23503');
    expect(session.constraint).toBe('device_sessions_device_fkey');

    const overwrite = await asUser(db.app, alice, (client) =>
      client.query(
        'UPDATE nimpad.idempotency_records SET status_code = 500, response = \'{"stolen": true}\' WHERE user_id = $1',
        [bob],
      ),
    );
    expect(overwrite.rowCount).toBe(0);
    // A blind UPDATE (no WHERE, no RETURNING) needs no SELECT rights, so only the UPDATE policies stand between
    // alice and every other user's rows here.
    const own = await ownerCount('nimpad.idempotency_records WHERE user_id = $1', [alice]);
    const blind = await asUser(db.app, alice, (client) =>
      client.query('UPDATE nimpad.idempotency_records SET status_code = 500'),
    );
    expect(blind.rowCount).toBe(own);
    const blindDevices = await asUser(db.app, alice, (client) =>
      client.query("UPDATE nimpad.devices SET name = 'renamed by alice'"),
    );
    expect(blindDevices.rowCount).toBe(0);
    expect(
      await ownerCount(
        "nimpad.idempotency_records WHERE user_id = $1 AND status_code = 201 AND response = '{}'",
        [bob],
      ),
    ).toBe(1);
    expect(await ownerCount("nimpad.devices WHERE name = 'renamed by alice'", [])).toBe(0);
    expect(
      (
        await asUser(db.app, alice, (client) =>
          client.query('UPDATE nimpad.devices SET revoked_at = now() WHERE id = $1', [bobsDevice]),
        )
      ).rowCount,
    ).toBe(0);
  });

  it('releases the device sessions of a member who leaves or is removed', async () => {
    const sam = await createUser(db, 'sam');
    const samsDevice = await createDevice(sam);
    const pointAtA = () =>
      asUser(db.app, sam, (client) =>
        client.query(
          `INSERT INTO nimpad.device_sessions (device_id, user_id, workspace_id) VALUES ($1, $2, $3)
           ON CONFLICT (device_id) DO UPDATE SET workspace_id = EXCLUDED.workspace_id`,
          [samsDevice, sam, workspaceA],
        ),
      );
    const sessionWorkspace = async () => {
      const result = await db.owner.query<{ workspace_id: string | null }>(
        'SELECT workspace_id FROM nimpad.device_sessions WHERE device_id = $1',
        [samsDevice],
      );
      return result.rows[0]!.workspace_id;
    };
    const saveState = () =>
      asUser(db.app, sam, (client) =>
        client.query(`UPDATE nimpad.device_sessions SET state = '{"tabs": []}' WHERE device_id = $1`, [
          samsDevice,
        ]),
      );

    // Removed by the owner.
    await addMember(db, alice, workspaceA, sam, 'viewer');
    await pointAtA();
    expect(await sessionWorkspace()).toBe(workspaceA);
    await asUser(db.app, alice, (client) =>
      client.query('DELETE FROM nimpad.workspace_members WHERE workspace_id = $1 AND user_id = $2', [
        workspaceA,
        sam,
      ]),
    );
    expect(await sessionWorkspace()).toBeNull();
    expect((await saveState()).rowCount).toBe(1);

    // Left on their own.
    await addMember(db, alice, workspaceA, sam, 'editor');
    await pointAtA();
    await asUser(db.app, sam, (client) =>
      client.query('DELETE FROM nimpad.workspace_members WHERE workspace_id = $1 AND user_id = $2', [
        workspaceA,
        sam,
      ]),
    );
    expect(await sessionWorkspace()).toBeNull();
    expect((await saveState()).rowCount).toBe(1);

    // A hard-deleted workspace (operator action) takes its memberships with it and releases sessions too.
    const samsWorkspace = await createWorkspace(db, sam, 'Sam');
    await asUser(db.app, sam, (client) =>
      client.query('UPDATE nimpad.device_sessions SET workspace_id = $2 WHERE device_id = $1', [
        samsDevice,
        samsWorkspace,
      ]),
    );
    await db.owner.query('DELETE FROM nimpad.workspaces WHERE id = $1', [samsWorkspace]);
    expect(await sessionWorkspace()).toBeNull();
    expect((await saveState()).rowCount).toBe(1);
  });

  it('keeps revoked devices revoked', async () => {
    await asUser(db.app, bob, (client) =>
      client.query('UPDATE nimpad.devices SET revoked_at = now() WHERE id = $1', [deviceB]),
    );
    const error = await pgError(
      asUser(db.app, bob, (client) =>
        client.query('UPDATE nimpad.devices SET revoked_at = NULL WHERE id = $1', [deviceB]),
      ),
    );
    expect(error.constraint).toBe('devices_revocation_final');
  });
});
