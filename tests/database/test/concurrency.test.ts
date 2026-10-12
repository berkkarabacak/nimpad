// Concurrency guarantees: opposite folder moves cannot create a cycle, and sync event sequence numbers are
// gap-free in commit order. Transactions are interleaved by hand on separate connections and the tests wait until
// PostgreSQL reports the second backend as blocked before letting the first one finish.
import { setTimeout as sleep } from 'node:timers/promises';
import type pg from 'pg';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import {
  addMember,
  asUser,
  backendPid,
  createFolder,
  createTestDatabase,
  createUser,
  createWorkspace,
  databaseUnavailableReason,
  insertNode,
  parentOf,
  pgError,
  settle,
  setUser,
  suiteTitle,
  type TestDatabase,
  waitUntilBlockedBy,
} from '../src/harness.ts';

const unavailable = await databaseUnavailableReason();

const MOVE = 'UPDATE nimpad.nodes SET parent_id = $1 WHERE id = $2';

describe.skipIf(unavailable !== undefined)(suiteTitle('concurrency', unavailable), () => {
  let db: TestDatabase;
  let owner: string;
  let editor: string;
  let workspace: string;

  // Backend pids, read when a transaction starts: a client cannot answer queries while a statement is blocked.
  const pids = new WeakMap<pg.Client, number>();

  async function begin(userId: string, isolation = 'READ COMMITTED'): Promise<pg.Client> {
    const client = await db.appClient();
    pids.set(client, await backendPid(client));
    await client.query(`BEGIN ISOLATION LEVEL ${isolation}`);
    await setUser(client, userId);
    return client;
  }

  /** Waits until `waiter`'s statement is blocked by a lock that `holder` holds. */
  function blockedBy(waiter: pg.Client, holder: pg.Client): Promise<void> {
    return waitUntilBlockedBy(db.owner, pids.get(waiter)!, pids.get(holder)!);
  }

  async function assertAcyclic(workspaceId: string): Promise<void> {
    const result = await db.owner.query<{ id: string }>(
      `WITH RECURSIVE walk (start, id, depth) AS (
         SELECT n.id, n.parent_id, 1 FROM nimpad.nodes n WHERE n.workspace_id = $1 AND n.parent_id IS NOT NULL
         UNION ALL
         SELECT w.start, n.parent_id, w.depth + 1 FROM walk w JOIN nimpad.nodes n ON n.id = w.id
         WHERE n.parent_id IS NOT NULL AND w.depth < 1000
       )
       SELECT DISTINCT start AS id FROM walk WHERE id = start OR depth >= 1000`,
      [workspaceId],
    );
    expect(result.rows, 'nodes that are their own ancestor').toEqual([]);
  }

  beforeAll(async () => {
    db = await createTestDatabase();
    owner = await createUser(db, 'owner');
    editor = await createUser(db, 'editor');
    workspace = await createWorkspace(db, owner);
    await addMember(db, owner, workspace, editor, 'editor');
  });

  afterEach(async () => {
    await db?.closeAppClients();
  });

  afterAll(async () => {
    await db?.drop();
  });

  describe('opposite folder moves', () => {
    it('READ COMMITTED: the second move waits for the first and is rejected once it commits', async () => {
      const a = await createFolder(db, owner, workspace);
      const b = await createFolder(db, owner, workspace);
      const first = await begin(owner);
      const second = await begin(editor);
      // Both transactions are open and have read the tree before either move.
      await first.query('SELECT count(*) FROM nimpad.nodes');
      await second.query('SELECT count(*) FROM nimpad.nodes');

      await first.query(MOVE, [b, a]); // A under B
      const secondMove = settle(second.query(MOVE, [a, b])); // B under A
      await blockedBy(second, first);
      await first.query('COMMIT');

      const outcome = await secondMove;
      expect(outcome.ok).toBe(false);
      expect((outcome as { error: pg.DatabaseError }).error.constraint).toBe('nodes_no_cycle');
      await second.query('ROLLBACK');

      expect(await parentOf(db, a)).toBe(b);
      expect(await parentOf(db, b)).toBeNull();
      await assertAcyclic(workspace);
    });

    it('READ COMMITTED: if the first move rolls back, the waiting move succeeds', async () => {
      const a = await createFolder(db, owner, workspace);
      const b = await createFolder(db, owner, workspace);
      const first = await begin(owner);
      const second = await begin(editor);

      await first.query(MOVE, [b, a]);
      const secondMove = settle(second.query(MOVE, [a, b]));
      await blockedBy(second, first);
      await first.query('ROLLBACK');

      expect((await secondMove).ok).toBe(true);
      await second.query('COMMIT');
      expect(await parentOf(db, a)).toBeNull();
      expect(await parentOf(db, b)).toBe(a);
      await assertAcyclic(workspace);
    });

    it('READ COMMITTED: a move deeper in the chain is also caught (A under C while C goes under B under A)', async () => {
      const a = await createFolder(db, owner, workspace);
      const b = await createFolder(db, owner, workspace, a);
      const c = await createFolder(db, owner, workspace);
      const first = await begin(owner);
      const second = await begin(editor);

      await first.query(MOVE, [c, a]); // A under C: A has no ancestors that are A, fine
      const secondMove = settle(second.query(MOVE, [b, c])); // C under B (whose parent is A, now under C)
      await blockedBy(second, first);
      await first.query('COMMIT');

      const outcome = await secondMove;
      expect(outcome.ok).toBe(false);
      expect((outcome as { error: pg.DatabaseError }).error.constraint).toBe('nodes_no_cycle');
      await second.query('ROLLBACK');
      await assertAcyclic(workspace);
    });

    // A snapshot taken before another transaction's move committed must not be used to approve the opposite move.
    // SSI alone does not catch this when the other side runs READ COMMITTED (the API's level), so every move
    // updates the workspace row and a stale REPEATABLE READ or SERIALIZABLE snapshot fails with 40001.
    it.each(['REPEATABLE READ', 'SERIALIZABLE'])(
      '%s: a move whose snapshot predates a committed READ COMMITTED move fails with 40001',
      async (isolation) => {
        const a = await createFolder(db, owner, workspace);
        const b = await createFolder(db, owner, workspace);
        const stale = await begin(editor, isolation);
        await stale.query('SELECT count(*) FROM nimpad.nodes'); // takes the snapshot: A and B are both roots

        const first = await begin(owner);
        await first.query(MOVE, [b, a]); // A under B
        await first.query('COMMIT');

        const error = await pgError(stale.query(MOVE, [a, b])); // B under A, judged on the old snapshot
        expect(error.code).toBe('40001');
        await stale.query('ROLLBACK');

        expect(await parentOf(db, a)).toBe(b);
        expect(await parentOf(db, b)).toBeNull();
        await assertAcyclic(workspace);
      },
    );

    it.each(['REPEATABLE READ', 'SERIALIZABLE'])(
      '%s: a move that waits for a concurrent READ COMMITTED move fails with 40001 once that one commits',
      async (isolation) => {
        const a = await createFolder(db, owner, workspace);
        const b = await createFolder(db, owner, workspace);
        const first = await begin(owner);
        const second = await begin(editor, isolation);
        await second.query('SELECT count(*) FROM nimpad.nodes');

        await first.query(MOVE, [b, a]);
        const secondMove = settle(second.query(MOVE, [a, b]));
        await blockedBy(second, first);
        await first.query('COMMIT');

        const outcome = await secondMove;
        expect(outcome.ok).toBe(false);
        expect((outcome as { error: pg.DatabaseError }).error.code).toBe('40001');
        await second.query('ROLLBACK');
        await assertAcyclic(workspace);
      },
    );

    it('REPEATABLE READ: a move with an up-to-date snapshot is allowed', async () => {
      const a = await createFolder(db, owner, workspace);
      const b = await createFolder(db, owner, workspace);
      const client = await begin(editor, 'REPEATABLE READ');
      await client.query(MOVE, [b, a]);
      await client.query('COMMIT');
      expect(await parentOf(db, a)).toBe(b);
    });

    it('SERIALIZABLE: exactly one of the two moves commits', async () => {
      const a = await createFolder(db, owner, workspace);
      const b = await createFolder(db, owner, workspace);
      const first = await begin(owner, 'SERIALIZABLE');
      const second = await begin(editor, 'SERIALIZABLE');
      await first.query('SELECT count(*) FROM nimpad.nodes');
      await second.query('SELECT count(*) FROM nimpad.nodes');

      await first.query(MOVE, [b, a]);
      const secondMove = settle(second.query(MOVE, [a, b]));
      await blockedBy(second, first);
      await first.query('COMMIT');

      const moved = await secondMove;
      const committed = moved.ok ? await settle(second.query('COMMIT')) : moved;
      if (!committed.ok) {
        expect((committed.error as pg.DatabaseError).code).toBe('40001');
        await second.query('ROLLBACK');
      }
      expect(committed.ok).toBe(false);
      expect(await parentOf(db, a)).toBe(b);
      expect(await parentOf(db, b)).toBeNull();
      await assertAcyclic(workspace);
    });

    it('stays acyclic under repeated unsynchronized opposite moves', async () => {
      for (let round = 0; round < 15; round += 1) {
        const a = await createFolder(db, owner, workspace);
        const b = await createFolder(db, owner, workspace);
        const attempt = async (userId: string, node: string, parent: string) => {
          const client = await begin(userId);
          await sleep(Math.random() * 5);
          const result = await settle(client.query(MOVE, [parent, node]));
          await sleep(Math.random() * 5);
          await client.query(result.ok ? 'COMMIT' : 'ROLLBACK');
          await client.end();
          return result;
        };
        const outcomes = await Promise.all([attempt(owner, a, b), attempt(editor, b, a)]);
        expect(
          outcomes.filter((outcome) => outcome.ok),
          `round ${round}`,
        ).toHaveLength(1);
        for (const outcome of outcomes) {
          if (!outcome.ok) expect((outcome.error as pg.DatabaseError).constraint).toBe('nodes_no_cycle');
        }
      }
      await assertAcyclic(workspace);
    });

    it('creating nodes in different folders does not serialize the workspace', async () => {
      const folderA = await createFolder(db, owner, workspace);
      const folderB = await createFolder(db, owner, workspace);
      const first = await begin(owner);
      const second = await begin(editor);
      await second.query("SET LOCAL lock_timeout = '2s'");
      await insertNode(first, { workspaceId: workspace, kind: 'document', parentId: folderA });
      // Would wait for `first` (and hit the lock timeout, 55P03) if inserts took a workspace-wide lock.
      await insertNode(second, { workspaceId: workspace, kind: 'document', parentId: folderB });
      await second.query('COMMIT');
      await first.query('COMMIT');
    });
  });

  describe('workspace lock ordering', () => {
    // A sync push applies several queued operations in one transaction and records an event for each, so events
    // and moves interleave in any order. Moves and record_sync_event must take one and the same workspace lock.
    it('lets "event, then move" and "move, then event" transactions both commit without a deadlock', async () => {
      const x = await createFolder(db, owner, workspace);
      const y = await createFolder(db, owner, workspace);
      const z = await createFolder(db, owner, workspace);
      const target = await createFolder(db, owner, workspace);

      const first = await begin(owner);
      const second = await begin(editor);
      await first.query("UPDATE nimpad.nodes SET name = 'renamed' WHERE id = $1", [x]);
      await first.query("SELECT nimpad.record_sync_event($1, 'node.renamed', $2)", [workspace, x]);

      const secondWork = settle(
        (async () => {
          await second.query(MOVE, [target, z]);
          await second.query("SELECT nimpad.record_sync_event($1, 'node.moved', $2)", [workspace, z]);
        })(),
      );
      // With one lock, `second` already waits at its move; with two, it would get the move's lock and wait at
      // the event, and `first`'s move below would then deadlock against it (40P01).
      await blockedBy(second, first);

      await first.query(MOVE, [target, y]);
      await first.query("SELECT nimpad.record_sync_event($1, 'node.moved', $2)", [workspace, y]);
      await first.query('COMMIT');

      expect(await secondWork).toEqual({ ok: true, value: undefined });
      await second.query('COMMIT');
      expect(await parentOf(db, y)).toBe(target);
      expect(await parentOf(db, z)).toBe(target);
    });

    it('makes a move wait for a transaction that recorded an event, and vice versa', async () => {
      const a = await createFolder(db, owner, workspace);
      const b = await createFolder(db, owner, workspace);

      const recorder = await begin(owner);
      await recorder.query("SELECT nimpad.record_sync_event($1, 'node.updated')", [workspace]);
      const mover = await begin(editor);
      const move = settle(mover.query(MOVE, [b, a]));
      await blockedBy(mover, recorder);
      await recorder.query('COMMIT');
      expect((await move).ok).toBe(true);

      const recorder2 = await begin(owner);
      const record = settle(
        recorder2.query("SELECT nimpad.record_sync_event($1, 'node.updated')", [workspace]),
      );
      await blockedBy(recorder2, mover);
      await mover.query('COMMIT');
      expect((await record).ok).toBe(true);
      await recorder2.query('COMMIT');
    });
  });

  describe('sync event sequence', () => {
    let eventsWorkspace: string;

    async function seqs(): Promise<number[]> {
      const result = await db.owner.query<{ seq: string }>(
        'SELECT seq FROM nimpad.sync_events WHERE workspace_id = $1 ORDER BY seq',
        [eventsWorkspace],
      );
      return result.rows.map((row) => Number(row.seq));
    }

    async function eventSeq(): Promise<number> {
      const result = await db.owner.query<{ event_seq: string }>(
        'SELECT event_seq FROM nimpad.workspaces WHERE id = $1',
        [eventsWorkspace],
      );
      return Number(result.rows[0]!.event_seq);
    }

    async function eventRow(seq: number): Promise<Record<string, unknown>> {
      const result = await db.owner.query(
        `SELECT node_id, op_id, device_id, actor_kind, actor_id, payload FROM nimpad.sync_events
         WHERE workspace_id = $1 AND seq = $2`,
        [eventsWorkspace, seq],
      );
      return result.rows[0]!;
    }

    const range = (from: number, to: number): number[] =>
      Array.from({ length: to - from + 1 }, (_, i) => from + i);

    async function record(client: pg.ClientBase, type = 'node.updated'): Promise<number> {
      const result = await client.query<{ seq: string }>('SELECT nimpad.record_sync_event($1, $2) AS seq', [
        eventsWorkspace,
        type,
      ]);
      return Number(result.rows[0]!.seq);
    }

    beforeAll(async () => {
      eventsWorkspace = await createWorkspace(db, owner, 'Events');
      await addMember(db, owner, eventsWorkspace, editor, 'editor');
    });

    it('hands out the next 10 numbers to 10 concurrent transactions, and readers never see a gap', async () => {
      const start = await eventSeq();
      const observations: { count: number; max: number }[] = [];
      let writing = true;
      const reader = (async () => {
        while (writing) {
          const result = await asUser(db.app, owner, (c) =>
            c.query<{ count: number; max: number }>(
              'SELECT count(*)::int AS count, coalesce(max(seq), 0)::int AS max FROM nimpad.sync_events WHERE workspace_id = $1',
              [eventsWorkspace],
            ),
          );
          observations.push(result.rows[0]!);
          await sleep(1);
        }
      })();

      const writers = Array.from({ length: 10 }, async (_, i) => {
        const client = await begin(i % 2 === 0 ? owner : editor);
        await sleep(Math.random() * 10);
        const seq = await record(client, 'node.created');
        await sleep(Math.random() * 10); // hold the workspace row lock for a while
        await client.query('COMMIT');
        await client.end();
        return seq;
      });
      const returned = await Promise.all(writers);
      writing = false;
      await reader;

      expect([...returned].sort((x, y) => x - y)).toEqual(range(start + 1, start + 10));
      expect(await seqs()).toEqual(range(1, start + 10));
      expect(await eventSeq()).toBe(start + 10);
      expect(observations.length).toBeGreaterThan(0);
      for (const observation of observations) expect(observation.count).toBe(observation.max);
    });

    it('leaves no gap when a transaction rolls back', async () => {
      const start = await eventSeq();
      const rolledBack = await begin(owner);
      expect(await record(rolledBack)).toBe(start + 1);
      await rolledBack.query('ROLLBACK');

      const committed = await begin(owner);
      expect(await record(committed)).toBe(start + 1);
      await committed.query('COMMIT');
      expect(await seqs()).toEqual(range(1, start + 1));
    });

    it('makes the next writer wait for the holder, so numbers follow commit order', async () => {
      const start = await eventSeq();
      const holder = await begin(owner);
      expect(await record(holder)).toBe(start + 1);
      const waiter = await begin(editor);
      const waiting = settle(record(waiter));
      await blockedBy(waiter, holder);

      // While start + 1 is uncommitted, nothing past start is visible and start + 2 has not been handed out.
      const visible = await asUser(db.app, owner, (c) =>
        c.query<{ max: number }>(
          'SELECT coalesce(max(seq), 0)::int AS max FROM nimpad.sync_events WHERE workspace_id = $1',
          [eventsWorkspace],
        ),
      );
      expect(visible.rows[0]!.max).toBe(start);

      await holder.query('COMMIT');
      const outcome = await waiting;
      expect(outcome).toEqual({ ok: true, value: start + 2 });
      await waiter.query('COMMIT');
      expect(await seqs()).toEqual(range(1, start + 2));
    });

    it('stays gap-free when concurrent transactions mix commits and rollbacks', async () => {
      const start = await eventSeq();
      const writers = Array.from({ length: 12 }, async (_, i) => {
        const client = await begin(owner);
        await sleep(Math.random() * 5);
        await record(client);
        await sleep(Math.random() * 5);
        const commit = i % 3 !== 0;
        await client.query(commit ? 'COMMIT' : 'ROLLBACK');
        await client.end();
        return commit;
      });
      const committed = (await Promise.all(writers)).filter(Boolean).length;
      expect(committed).toBe(8);
      expect(await seqs()).toEqual(range(1, start + committed));
      expect(await eventSeq()).toBe(start + committed);
    });

    it('records the optional fields, and refuses viewers', async () => {
      const viewer = await createUser(db, 'viewer');
      await addMember(db, owner, eventsWorkspace, viewer, 'viewer');
      const error = await pgError(asUser(db.app, viewer, (client) => record(client)));
      expect(error.code).toBe('42501');

      const opId = '0192f0c4-0000-7000-8000-000000000001';
      const nodeId = '0192f0c4-0000-7000-8000-000000000002';
      const device = await asUser(db.app, editor, async (client) => {
        const result = await client.query<{ id: string }>(
          `INSERT INTO nimpad.devices (user_id, name, platform, app_version) VALUES ($1, 'PC', 'windows', '0.1.0')
           RETURNING id`,
          [editor],
        );
        return result.rows[0]!.id;
      });
      const seq = await asUser(db.app, editor, async (client) => {
        const result = await client.query<{ seq: string }>(
          `SELECT nimpad.record_sync_event(workspace_id => $1, event_type => 'node.renamed', node_id => $2,
             payload => '{"name": "x"}', op_id => $3, device_id => $4) AS seq`,
          [eventsWorkspace, nodeId, opId, device],
        );
        return Number(result.rows[0]!.seq);
      });
      expect(await eventRow(seq)).toEqual({
        node_id: nodeId,
        op_id: opId,
        device_id: device,
        actor_kind: 'user',
        actor_id: editor,
        payload: { name: 'x' },
      });
    });

    it('attributes events: users and imports to the caller, agents to the agent named, system to nobody', async () => {
      const agentId = '0192f0c4-0000-7000-8000-0000000000a1';
      const call = (args: string, params: unknown[] = []) =>
        asUser(db.app, editor, async (client) => {
          const result = await client.query<{ seq: string }>(
            `SELECT nimpad.record_sync_event(workspace_id => $1, event_type => 'node.updated'${args}) AS seq`,
            [eventsWorkspace, ...params],
          );
          return Number(result.rows[0]!.seq);
        });
      const actor = async (seq: number) => {
        const row = await eventRow(seq);
        return { actor_kind: row.actor_kind, actor_id: row.actor_id };
      };

      expect(await actor(await call(''))).toEqual({ actor_kind: 'user', actor_id: editor });
      expect(await actor(await call(", actor_kind => 'user', actor_id => $2", [editor]))).toEqual({
        actor_kind: 'user',
        actor_id: editor,
      });
      expect(await actor(await call(", actor_kind => 'import'"))).toEqual({
        actor_kind: 'import',
        actor_id: editor,
      });
      expect(await actor(await call(", actor_kind => 'agent', actor_id => $2", [agentId]))).toEqual({
        actor_kind: 'agent',
        actor_id: agentId,
      });
      expect(await actor(await call(", actor_kind => 'system'"))).toEqual({
        actor_kind: 'system',
        actor_id: null,
      });

      // A user cannot record an event in someone else's name, and an agent event must name the agent.
      const impersonation = await pgError(call(", actor_kind => 'user', actor_id => $2", [owner]));
      expect(impersonation.code).toBe('42501');
      const importAs = await pgError(call(", actor_kind => 'import', actor_id => $2", [owner]));
      expect(importAs.code).toBe('42501');
      const anonymousAgent = await pgError(call(", actor_kind => 'agent'"));
      expect(anonymousAgent.code).toBe('22023');
      const unknownKind = await pgError(call(", actor_kind => 'robot'"));
      expect(unknownKind.code).toBe('23514');
    });

    it("refuses a device that is not the caller's, or is revoked", async () => {
      const ownersDevice = await asUser(db.app, owner, async (client) => {
        const result = await client.query<{ id: string }>(
          `INSERT INTO nimpad.devices (user_id, name, platform, app_version) VALUES ($1, 'PC', 'windows', '0.1.0')
           RETURNING id`,
          [owner],
        );
        return result.rows[0]!.id;
      });
      const withDevice = (userId: string, device: string) =>
        asUser(db.app, userId, (client) =>
          client.query("SELECT nimpad.record_sync_event($1, 'node.updated', device_id => $2)", [
            eventsWorkspace,
            device,
          ]),
        );
      // Recording under another user's device would make that device skip the event as its own echo.
      expect((await pgError(withDevice(editor, ownersDevice))).code).toBe('42501');
      await withDevice(owner, ownersDevice);
      await asUser(db.app, owner, (client) =>
        client.query('UPDATE nimpad.devices SET revoked_at = now() WHERE id = $1', [ownersDevice]),
      );
      expect((await pgError(withDevice(owner, ownersDevice))).code).toBe('42501');
    });
  });
});
