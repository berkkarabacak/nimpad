// The local → server operation queue (architecture.md, "Pending operation queue").
//
// A PendingOperation is the part of a `pending_operations` row that is pushed to the server. The local bookkeeping
// columns (`seq`, `attempts`, `last_error`) are not part of the contract. All objects are strict: unknown keys are
// rejected so that a payload shape cannot drift silently between the C# writer and the server.
//
// Base revisions. `baseMetaRev`/`baseContentRev` are the server revisions the change was made against, or null when
// the node had not been acknowledged by the server yet (it was created on this device and its `create` is still
// queued or unacknowledged). Null never means "apply unconditionally": stale writes must be rejected, never
// silently applied (spec §4). Before a queued operation is pushed, its base revisions are therefore rebased: when
// the server acknowledges an operation, the sync engine rewrites the base revisions of the same node's later queued
// operations to the revisions the server returned, in the same local transaction. A pushed operation always
// satisfies {@link PushOperation}; the server answers `bad_request` to one that does not, and `stale_revision`
// when a base revision is behind its own.

import { z } from 'zod';
import { DocumentFormat } from './formats.ts';
import { isUuidV7, Uuid, UuidV7 } from './ids.ts';
import { FractionalKey, NodeName, Revision } from './nodes.ts';
import { ContentChecksum } from './text.ts';
import { IsoTimestamp } from './time.ts';

export const PENDING_OPERATION_KINDS = [
  'create',
  'update_content',
  'rename',
  'move',
  'pin',
  'delete',
  'restore',
  'purge',
] as const;
export const PendingOperationKind = z.enum(PENDING_OPERATION_KINDS);
export type PendingOperationKind = z.infer<typeof PendingOperationKind>;

/** Actors that can enqueue local operations. Agents never do: they act only through the server. */
export const LocalActorKind = z.enum(['user', 'import', 'system']);
export type LocalActorKind = z.infer<typeof LocalActorKind>;

// --- Payloads ---------------------------------------------------------------------------------------------

/**
 * `create`: the full initial state of the node; the operation's `nodeId` is the new node's UUIDv7. While the
 * create has not been attempted yet, later renames and content updates (text or format) of the node fold into this
 * payload. Moves and pins never fold: they are queued as their own operations (a move can target a folder created
 * later). A document's text is not carried: `checksum` identifies it and the push reads the current text.
 */
export const CreatePayload = z.discriminatedUnion('nodeKind', [
  z.strictObject({
    nodeKind: z.literal('folder'),
    parentId: Uuid.nullable(),
    name: NodeName,
    position: FractionalKey,
  }),
  z.strictObject({
    nodeKind: z.literal('document'),
    parentId: Uuid.nullable(),
    name: NodeName,
    position: FractionalKey,
    format: DocumentFormat,
    checksum: ContentChecksum,
  }),
]);
export type CreatePayload = z.infer<typeof CreatePayload>;

/**
 * `update_content`: the document's text and/or format changed. Carries the checksum of the current canonical
 * text, never the text itself; the push reads the text and checks it against this checksum. There is at most one
 * per node; replacing it keeps the original base revision.
 */
export const UpdateContentPayload = z.strictObject({
  checksum: ContentChecksum,
  format: DocumentFormat,
});
export type UpdateContentPayload = z.infer<typeof UpdateContentPayload>;

/** `rename`: the new name. A format change implied by a new extension is sent as `update_content`. */
export const RenamePayload = z.strictObject({
  name: NodeName,
});
export type RenamePayload = z.infer<typeof RenamePayload>;

/** `move`: a new parent and/or position (a reorder within the same parent is also a move). */
export const MovePayload = z.strictObject({
  parentId: Uuid.nullable(),
  position: FractionalKey,
});
export type MovePayload = z.infer<typeof MovePayload>;

/** `pin`: pin with a position in the Pinned section, or unpin. */
export const PinPayload = z.discriminatedUnion('pinned', [
  z.strictObject({ pinned: z.literal(true), pinPosition: FractionalKey }),
  z.strictObject({ pinned: z.literal(false), pinPosition: z.null() }),
]);
export type PinPayload = z.infer<typeof PinPayload>;

/** `delete`: move the node's subtree to the Trash as one batch, identified by a new UUIDv7. */
export const DeletePayload = z.strictObject({
  trashBatchId: UuidV7,
  deletedAt: IsoTimestamp,
});
export type DeletePayload = z.infer<typeof DeletePayload>;

/** `restore`: bring a trashed batch back. */
export const RestorePayload = z.strictObject({
  trashBatchId: Uuid,
});
export type RestorePayload = z.infer<typeof RestorePayload>;

/** `purge`: delete the node (and its trashed subtree) forever. No payload fields. */
export const PurgePayload = z.strictObject({});
export type PurgePayload = z.infer<typeof PurgePayload>;

/** Payload shape for each operation kind. */
export const PendingOperationPayload = {
  create: CreatePayload,
  update_content: UpdateContentPayload,
  rename: RenamePayload,
  move: MovePayload,
  pin: PinPayload,
  delete: DeletePayload,
  restore: RestorePayload,
  purge: PurgePayload,
} as const satisfies Record<PendingOperationKind, z.ZodType>;

// --- Operations -------------------------------------------------------------------------------------------

const operationFields = {
  /** UUIDv7 generated for this operation; the idempotency key sent to the server. */
  opId: UuidV7,
  workspaceId: Uuid,
  /** The target node. For a `create` it is the new node's ID, which must be a UUIDv7. */
  nodeId: Uuid,
  /**
   * Server revisions the change was made against; null while the node has not been acknowledged by the server
   * (see "Base revisions" at the top of this file).
   */
  baseMetaRev: Revision.nullable(),
  baseContentRev: Revision.nullable(),
  /** The installation that made the change: a UUIDv7 created on its first run. */
  deviceId: UuidV7,
  actorKind: LocalActorKind,
  createdAt: IsoTimestamp,
};

/** A queued local mutation, discriminated by `kind`, as stored in the local queue. */
export const PendingOperation = z
  .discriminatedUnion('kind', [
    z.strictObject({ ...operationFields, kind: z.literal('create'), payload: CreatePayload }),
    z.strictObject({ ...operationFields, kind: z.literal('update_content'), payload: UpdateContentPayload }),
    z.strictObject({ ...operationFields, kind: z.literal('rename'), payload: RenamePayload }),
    z.strictObject({ ...operationFields, kind: z.literal('move'), payload: MovePayload }),
    z.strictObject({ ...operationFields, kind: z.literal('pin'), payload: PinPayload }),
    z.strictObject({ ...operationFields, kind: z.literal('delete'), payload: DeletePayload }),
    z.strictObject({ ...operationFields, kind: z.literal('restore'), payload: RestorePayload }),
    z.strictObject({ ...operationFields, kind: z.literal('purge'), payload: PurgePayload }),
  ])
  .superRefine((op, ctx) => {
    if (op.kind === 'create' && !isUuidV7(op.nodeId)) {
      ctx.addIssue({ code: 'custom', path: ['nodeId'], message: 'A new node ID must be a version 7 UUID' });
    }
    if (op.kind === 'create' && (op.baseMetaRev !== null || op.baseContentRev !== null)) {
      ctx.addIssue({
        code: 'custom',
        path: ['baseMetaRev'],
        message: 'A create has no server base revisions',
      });
    }
    if ((op.kind === 'create' || op.kind === 'move') && op.payload.parentId === op.nodeId) {
      ctx.addIssue({
        code: 'custom',
        path: ['payload', 'parentId'],
        message: 'A node cannot be its own parent',
      });
    }
  });
export type PendingOperation = z.infer<typeof PendingOperation>;

/**
 * A {@link PendingOperation} as pushed to the server: its base revisions have been rebased (see "Base revisions"
 * above), so every operation except `create` names the metadata revision it was made against, and an
 * `update_content` also names the content revision. A `baseContentRev` may stay null on other kinds: folders have
 * no content.
 */
export const PushOperation = PendingOperation.superRefine((op, ctx) => {
  if (op.kind !== 'create' && op.baseMetaRev === null) {
    ctx.addIssue({
      code: 'custom',
      path: ['baseMetaRev'],
      message: 'Rebase before pushing: only a create may have a null base revision',
    });
  }
  if (op.kind === 'update_content' && op.baseContentRev === null) {
    ctx.addIssue({
      code: 'custom',
      path: ['baseContentRev'],
      message: 'Rebase before pushing: an update_content needs the content revision it was made against',
    });
  }
});
export type PushOperation = z.infer<typeof PushOperation>;
