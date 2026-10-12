import { z } from 'zod';
import { DocumentFormat } from './formats.ts';
import { isValidOrderKey } from './fractional-index.ts';
import { Uuid } from './ids.ts';
import {
  ContentChecksum,
  contentChecksum,
  isCanonicalText,
  isWellFormedText,
  trimWhiteSpace,
} from './text.ts';
import { IsoTimestamp } from './time.ts';

/** Only folders have children. */
export const NodeKind = z.enum(['folder', 'document']);
export type NodeKind = z.infer<typeof NodeKind>;

/** Who made a change. Agents act only through the server; `import` marks content copied in from a file. */
export const ActorKind = z.enum(['user', 'agent', 'system', 'import']);
export type ActorKind = z.infer<typeof ActorKind>;

/** A fractional-index sibling position key (see fractional-index.ts). */
export const FractionalKey = z.string().refine(isValidOrderKey, 'Expected a fractional-index position key');
export type FractionalKey = z.infer<typeof FractionalKey>;

/**
 * A server revision number (meta or content). The server starts every revision at 1 and only increments it, so a
 * revision is a positive safe integer; "never synced" is `null`, never 0.
 */
export const Revision = z.int().min(1);
export type Revision = z.infer<typeof Revision>;

/** Longest allowed node name, in UTF-16 code units (the same unit as .NET `string.Length`). */
export const MAX_NODE_NAME_LENGTH = 255;

// C0 control characters and DEL.
// biome-ignore lint/suspicious/noControlCharactersInRegex: matching control characters is the point.
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f]/;

/**
 * A node name as stored and sent over the wire:
 *
 * - 1–255 UTF-16 code units;
 * - no C0 control character or DEL (so no line breaks or tabs; U+0085 inside a name is allowed);
 * - well-formed UTF-16 (no unpaired surrogate, which UTF-8 cannot carry);
 * - not blank, and already trimmed: it neither starts nor ends with a white-space character, where white space is
 *   exactly `WHITE_SPACE_CHARACTERS` (Unicode White_Space; U+FEFF and U+200B are not white space).
 *
 * Input typed by a person or an agent goes through {@link normalizeNodeName} first.
 */
export const NodeName = z
  .string()
  // Explicit `length` checks: zod's own .min()/.max() count Unicode code points, not UTF-16 code units.
  .refine((name) => name.length >= 1, 'Name must not be empty')
  .refine(
    (name) => name.length <= MAX_NODE_NAME_LENGTH,
    `Name must be at most ${MAX_NODE_NAME_LENGTH} UTF-16 code units long`,
  )
  .refine((name) => !CONTROL_CHARACTERS.test(name), 'Name must not contain control characters')
  .refine(isWellFormedText, 'Name must not contain unpaired surrogates')
  .refine((name) => trimWhiteSpace(name) !== '', 'Name must not be blank')
  .refine((name) => {
    const trimmed = trimWhiteSpace(name);
    return trimmed === '' || trimmed === name;
  }, 'Name must not start or end with white space');
export type NodeName = z.infer<typeof NodeName>;

/**
 * Turns a user- or agent-supplied name into its stored form: trims white space from both ends, then validates it
 * as a {@link NodeName}. Returns null when the trimmed name is still not acceptable. The desktop app's
 * `NodeNames.Normalize` follows the same rules (fixtures/node-names.json).
 */
export function normalizeNodeName(name: string): NodeName | null {
  const trimmed = trimWhiteSpace(name);
  return NodeName.safeParse(trimmed).success ? trimmed : null;
}

/**
 * The fields of a {@link Node} without the cross-field invariants, for deriving request, response and tool schemas
 * (`pick`, `omit` and `partial` are not available on a refined object). Re-apply the invariants with
 * `.superRefine(refineNodeInvariants)`.
 */
export const NodeFields = z.object({
  id: Uuid,
  workspaceId: Uuid,
  /** Null for nodes at the workspace root. */
  parentId: Uuid.nullable(),
  kind: NodeKind,
  name: NodeName,
  position: FractionalKey,
  isPinned: z.boolean(),
  /** Order within the Pinned section; set exactly when `isPinned`. */
  pinPosition: FractionalKey.nullable(),
  createdAt: IsoTimestamp,
  updatedAt: IsoTimestamp,
  /** Last content change of a document; always null for folders. */
  contentUpdatedAt: IsoTimestamp.nullable(),
  /** Set when the node is in the Trash, together with `trashBatchId`. */
  deletedAt: IsoTimestamp.nullable(),
  /** Groups a subtree trashed by one action so it is restored as a unit. */
  trashBatchId: Uuid.nullable(),
  /** Server metadata revision; null while the node has never been synced. */
  metaRev: Revision.nullable(),
});
export type NodeFields = z.infer<typeof NodeFields>;

/**
 * The cross-field rules of a {@link Node}. Each rule is checked only when all of its fields are present, so the
 * function also suits schemas derived with `pick` or `partial`.
 */
export function refineNodeInvariants(node: Partial<NodeFields>, ctx: z.RefinementCtx): void {
  if (node.id !== undefined && node.parentId !== undefined && node.parentId === node.id) {
    ctx.addIssue({ code: 'custom', path: ['parentId'], message: 'A node cannot be its own parent' });
  }
  if (
    node.isPinned !== undefined &&
    node.pinPosition !== undefined &&
    node.isPinned !== (node.pinPosition !== null)
  ) {
    ctx.addIssue({
      code: 'custom',
      path: ['pinPosition'],
      message: 'pinPosition is set exactly when isPinned',
    });
  }
  if (
    node.deletedAt !== undefined &&
    node.trashBatchId !== undefined &&
    (node.deletedAt === null) !== (node.trashBatchId === null)
  ) {
    ctx.addIssue({
      code: 'custom',
      path: ['trashBatchId'],
      message: 'deletedAt and trashBatchId are set together',
    });
  }
  if (node.kind === 'folder' && node.contentUpdatedAt !== undefined && node.contentUpdatedAt !== null) {
    ctx.addIssue({ code: 'custom', path: ['contentUpdatedAt'], message: 'Folders have no content' });
  }
}

/** A folder or document in a workspace tree (metadata only; text lives in {@link DocumentContent}). */
export const Node = NodeFields.superRefine(refineNodeInvariants);
export type Node = z.infer<typeof Node>;

/**
 * The fields of a {@link DocumentContent} without its invariants (see {@link NodeFields} for why). Re-apply them
 * with `.superRefine(refineDocumentContentInvariants)`.
 */
export const DocumentContentFields = z.object({
  nodeId: Uuid,
  format: DocumentFormat,
  text: z.string(),
  checksum: ContentChecksum,
  /** Server content revision; null while the document has never been synced. */
  contentRev: Revision.nullable(),
  updatedAt: IsoTimestamp,
});
export type DocumentContentFields = z.infer<typeof DocumentContentFields>;

/**
 * The rules of {@link DocumentContent}: the text is canonical (LF only) and storable (no U+0000, no unpaired
 * surrogate; see `isStorableText`), and `checksum` matches it. Checked only for the fields that are present.
 */
export function refineDocumentContentInvariants(
  content: Partial<DocumentContentFields>,
  ctx: z.RefinementCtx,
): void {
  if (content.text === undefined) {
    return;
  }
  if (!isCanonicalText(content.text)) {
    ctx.addIssue({ code: 'custom', path: ['text'], message: 'Managed note text must use LF line endings' });
  } else if (content.text.includes('\0')) {
    ctx.addIssue({ code: 'custom', path: ['text'], message: 'Managed note text must not contain U+0000' });
  } else if (!isWellFormedText(content.text)) {
    ctx.addIssue({
      code: 'custom',
      path: ['text'],
      message: 'Managed note text must not contain unpaired surrogates',
    });
  } else if (content.checksum !== undefined && content.checksum !== contentChecksum(content.text)) {
    ctx.addIssue({ code: 'custom', path: ['checksum'], message: 'Checksum does not match text' });
  }
}

/** The text of a managed document. The text is canonical and storable, and `checksum` must match it. */
export const DocumentContent = DocumentContentFields.superRefine(refineDocumentContentInvariants);
export type DocumentContent = z.infer<typeof DocumentContent>;
