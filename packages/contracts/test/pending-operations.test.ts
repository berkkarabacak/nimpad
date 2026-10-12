import { describe, expect, it } from 'vitest';
import {
  CreatePayload,
  DeletePayload,
  LocalActorKind,
  PENDING_OPERATION_KINDS,
  PendingOperation,
  PendingOperationKind,
  PendingOperationPayload,
  PushOperation,
  RestorePayload,
} from '../src/pending-operations.ts';
import { pendingOperationsFixture } from './fixtures.ts';

const { valid, invalid, needsRebase } = pendingOperationsFixture;
const V4 = '3f0c2a4e-9b1d-4c6e-8f7a-2b5d9e1c0a34';
const NIL = '00000000-0000-0000-0000-000000000000';

function example(kind: string): Record<string, unknown> {
  const found = valid.find((e) => e.operation.kind === kind)?.operation;
  if (found === undefined) {
    throw new Error(`no valid ${kind} example`);
  }
  return found;
}

function issuePaths(schema: typeof PendingOperation, operation: unknown): string[] {
  return schema.safeParse(operation).error?.issues.map((issue) => issue.path.join('.')) ?? [];
}

describe('operation kinds', () => {
  it('matches the architecture list, with a payload schema for each', () => {
    expect(PENDING_OPERATION_KINDS).toEqual([
      'create',
      'update_content',
      'rename',
      'move',
      'pin',
      'delete',
      'restore',
      'purge',
    ]);
    expect(PendingOperationKind.options).toEqual([...PENDING_OPERATION_KINDS]);
    expect(Object.keys(PendingOperationPayload).sort()).toEqual([...PENDING_OPERATION_KINDS].sort());
  });

  it('allows only local actors', () => {
    expect(LocalActorKind.options).toEqual(['user', 'import', 'system']);
  });
});

describe('fixture parity (fixtures/pending-operations.json)', () => {
  it('has valid and invalid examples for every kind', () => {
    for (const kind of PENDING_OPERATION_KINDS) {
      expect(valid.some((e) => e.operation.kind === kind)).toBe(true);
      expect(invalid.some((e) => e.operation.kind === kind)).toBe(true);
    }
  });

  it.each(valid)('accepts and round-trips: $description', ({ operation }) => {
    const parsed = PendingOperation.parse(operation);
    expect(parsed).toEqual(operation);
    expect(JSON.parse(JSON.stringify(parsed))).toEqual(operation);
    // Every valid example is also pushable as it is.
    expect(PushOperation.parse(operation)).toEqual(operation);
    // The payload also validates on its own against the schema for its kind.
    const payloadSchema = PendingOperationPayload[parsed.kind];
    expect(payloadSchema.parse(parsed.payload)).toEqual(operation.payload);
  });

  it.each(invalid)('rejects: $description', ({ operation }) => {
    expect(PendingOperation.safeParse(operation).success).toBe(false);
    expect(PushOperation.safeParse(operation).success).toBe(false);
  });

  it.each(needsRebase)('queues but does not push before a rebase: $description', ({ operation }) => {
    expect(PendingOperation.parse(operation)).toEqual(operation);
    expect(PushOperation.safeParse(operation).success).toBe(false);
  });
});

describe('IDs', () => {
  it('requires UUIDv7 for the IDs NimPad generates: opId, deviceId, a new node, a new trash batch', () => {
    const rename = example('rename');
    expect(issuePaths(PendingOperation, { ...rename, opId: V4 })).toEqual(['opId']);
    expect(issuePaths(PendingOperation, { ...rename, opId: NIL })).toEqual(['opId']);
    expect(issuePaths(PendingOperation, { ...rename, deviceId: V4 })).toEqual(['deviceId']);
    expect(issuePaths(PendingOperation, { ...example('create'), nodeId: V4 })).toEqual(['nodeId']);
    expect(DeletePayload.safeParse({ trashBatchId: V4, deletedAt: '2026-10-12T14:01:00.000Z' }).success).toBe(
      false,
    );
  });

  it('accepts any UUID version where an ID refers to something that already exists', () => {
    const rename = example('rename');
    expect(PendingOperation.safeParse({ ...rename, nodeId: V4, workspaceId: V4 }).success).toBe(true);
    const move = example('move');
    expect(
      PendingOperation.safeParse({ ...move, payload: { ...(move.payload as object), parentId: V4 } }).success,
    ).toBe(true);
    expect(RestorePayload.safeParse({ trashBatchId: V4 }).success).toBe(true);
  });
});

describe('PushOperation (base revisions are rebased before a push)', () => {
  it('needs a base meta revision on every kind but create', () => {
    for (const kind of ['rename', 'move', 'pin', 'delete', 'restore', 'purge', 'update_content']) {
      const queued = { ...example(kind), baseMetaRev: null };
      expect(PendingOperation.safeParse(queued).success).toBe(true);
      expect(issuePaths(PushOperation, queued)).toContain('baseMetaRev');
    }
  });

  it('needs a base content revision on update_content only (folders have none)', () => {
    const update = { ...example('update_content'), baseContentRev: null };
    expect(issuePaths(PushOperation, update)).toEqual(['baseContentRev']);
    for (const kind of ['rename', 'move', 'pin', 'delete', 'restore', 'purge']) {
      expect(PushOperation.safeParse({ ...example(kind), baseContentRev: null }).success).toBe(true);
    }
  });

  it('pushes a create with null base revisions, and only so', () => {
    const create = example('create');
    expect(PushOperation.parse(create)).toEqual(create);
    expect(PushOperation.safeParse({ ...create, baseMetaRev: 1 }).success).toBe(false);
  });

  it('explains how to fix an unrebased operation', () => {
    const message = PushOperation.safeParse({ ...example('rename'), baseMetaRev: null }).error?.issues[0]
      ?.message;
    expect(message).toMatch(/^Rebase before pushing/);
  });
});

describe('PendingOperation', () => {
  const base = valid.find((e) => e.operation.kind === 'update_content')?.operation;

  it('rejects a zero base revision (server revisions start at 1)', () => {
    expect(PendingOperation.safeParse({ ...base, baseMetaRev: 0 }).success).toBe(false);
    expect(PendingOperation.safeParse({ ...base, baseContentRev: 0 }).success).toBe(false);
  });

  it('never accepts text in update_content (it carries the checksum only)', () => {
    expect(base).toBeDefined();
    const withText = { ...base, payload: { ...(base?.payload as object), text: 'hello' } };
    const result = PendingOperation.safeParse(withText);
    expect(result.success).toBe(false);
  });

  it('narrows the payload type by kind', () => {
    const op = PendingOperation.parse(base);
    if (op.kind !== 'update_content') {
      throw new Error('expected update_content');
    }
    expect(op.payload.checksum).toMatch(/^sha256:/);
  });

  it('distinguishes folder and document creates', () => {
    expect(
      CreatePayload.safeParse({ nodeKind: 'folder', parentId: null, name: 'A', position: 'a0' }).success,
    ).toBe(true);
    expect(
      CreatePayload.safeParse({ nodeKind: 'document', parentId: null, name: 'A', position: 'a0' }).success,
    ).toBe(false);
  });
});
