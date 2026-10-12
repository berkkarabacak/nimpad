import { describe, expect, it } from 'vitest';
import { fromFixtureString } from '../scripts/fixture-builders.ts';
import { DOCUMENT_FORMATS, DocumentFormat } from '../src/formats.ts';
import {
  ActorKind,
  DocumentContent,
  DocumentContentFields,
  FractionalKey,
  MAX_NODE_NAME_LENGTH,
  Node,
  NodeFields,
  NodeKind,
  NodeName,
  normalizeNodeName,
  Revision,
  refineDocumentContentInvariants,
  refineNodeInvariants,
} from '../src/nodes.ts';
import { contentChecksum } from '../src/text.ts';
import { nodeNamesFixture } from './fixtures.ts';

const folder: Node = {
  id: '01a13069-88e3-7296-af4f-6b32291fd002',
  workspaceId: '01a13069-8113-7d4b-b45c-a607c0eb8f23',
  parentId: null,
  kind: 'folder',
  name: 'Projects',
  position: 'a0',
  isPinned: false,
  pinPosition: null,
  createdAt: '2026-10-12T13:59:43.123Z',
  updatedAt: '2026-10-12T13:59:43.123Z',
  contentUpdatedAt: null,
  deletedAt: null,
  trashBatchId: null,
  metaRev: null,
};

const document: Node = {
  ...folder,
  id: '01a13069-8ccb-704d-b1f6-079e37aa44d2',
  parentId: folder.id,
  kind: 'document',
  name: 'groceries.md',
  position: 'a0V',
  isPinned: true,
  pinPosition: 'a0',
  contentUpdatedAt: '2026-10-12T14:00:01.000Z',
  metaRev: 3,
};

describe('enumerations', () => {
  it('lists exactly the formats in docs/architecture.md, in order', () => {
    expect(DOCUMENT_FORMATS).toEqual([
      'plain',
      'markdown',
      'json',
      'xml',
      'yaml',
      'python',
      'groovy',
      'javascript',
      'typescript',
      'csharp',
      'sql',
      'powershell',
      'ini',
      'css',
      'html',
    ]);
    expect(DocumentFormat.options).toEqual([...DOCUMENT_FORMATS]);
    expect(DocumentFormat.safeParse('Markdown').success).toBe(false);
    expect(DocumentFormat.safeParse('md').success).toBe(false);
  });

  it('defines node and actor kinds', () => {
    expect(NodeKind.options).toEqual(['folder', 'document']);
    expect(ActorKind.options).toEqual(['user', 'agent', 'system', 'import']);
  });
});

describe('Node', () => {
  it('accepts a folder and a pinned document', () => {
    expect(Node.parse(folder)).toEqual(folder);
    expect(Node.parse(document)).toEqual(document);
  });

  it('accepts a trashed node', () => {
    const trashed = { ...document, deletedAt: '2026-10-13T08:00:00.000Z', trashBatchId: folder.workspaceId };
    expect(Node.safeParse(trashed).success).toBe(true);
  });

  it.each<[string, Partial<Record<keyof Node, unknown>>]>([
    ['uppercase id', { id: folder.id.toUpperCase() }],
    ['unknown kind', { kind: 'file' }],
    ['invalid position', { position: 'a00' }],
    ['own parent', { parentId: folder.id }],
    ['pinned without pinPosition', { isPinned: true }],
    ['pinPosition without pin', { pinPosition: 'a0' }],
    ['deletedAt without trashBatchId', { deletedAt: '2026-10-13T08:00:00.000Z' }],
    ['trashBatchId without deletedAt', { trashBatchId: folder.workspaceId }],
    ['folder with contentUpdatedAt', { contentUpdatedAt: '2026-10-12T14:00:01.000Z' }],
    ['timestamp without milliseconds', { createdAt: '2026-10-12T13:59:43Z' }],
    ['negative metaRev', { metaRev: -1 }],
    ['zero metaRev (server revisions start at 1)', { metaRev: 0 }],
    ['fractional metaRev', { metaRev: 1.5 }],
    ['blank name', { name: '  ' }],
  ])('rejects %s', (_label, change) => {
    expect(Node.safeParse({ ...folder, ...change }).success).toBe(false);
  });
});

describe('Revision', () => {
  it('starts at 1 and is a safe integer', () => {
    expect(Revision.safeParse(1).success).toBe(true);
    expect(Revision.safeParse(Number.MAX_SAFE_INTEGER).success).toBe(true);
    for (const value of [0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1, '1', null]) {
      expect(Revision.safeParse(value).success).toBe(false);
    }
  });
});

describe('NodeName', () => {
  it('accepts trimmed names up to 255 UTF-16 code units, including non-ASCII', () => {
    expect(NodeName.safeParse('x'.repeat(MAX_NODE_NAME_LENGTH)).success).toBe(true);
    expect(NodeName.safeParse('Notes — 日本 😀').success).toBe(true);
    expect(NodeName.safeParse(`${'x'.repeat(MAX_NODE_NAME_LENGTH - 2)}😀`).success).toBe(true);
  });

  it('counts UTF-16 code units like .NET string.Length, not code points', () => {
    // 254 + 2 = 256 code units, but only 255 code points: zod's own .max() would accept it.
    const name = `${'x'.repeat(MAX_NODE_NAME_LENGTH - 1)}😀`;
    expect(name.length).toBe(MAX_NODE_NAME_LENGTH + 1);
    expect(NodeName.safeParse(name).success).toBe(false);
  });

  it.each([
    ['empty', ''],
    ['too long', 'x'.repeat(MAX_NODE_NAME_LENGTH + 1)],
    ['whitespace only (incl. U+3000)', ` ${String.fromCodePoint(0x3000)} `],
    ['U+0085 only (white space in .NET; JavaScript trim() keeps it)', String.fromCodePoint(0x85)],
    ['leading space', ' name'],
    ['trailing space', 'name '],
    ['trailing no-break space', `name${String.fromCodePoint(0xa0)}`],
    ['line feed', 'a\nb'],
    ['tab', 'a\tb'],
    ['NUL', `a${String.fromCodePoint(0)}b`],
    ['DEL', `a${String.fromCodePoint(0x7f)}b`],
    ['unpaired high surrogate', 'a\ud800'],
    ['unpaired low surrogate', '\udc00a'],
  ])('rejects %s', (_label, name) => {
    expect(NodeName.safeParse(name).success).toBe(false);
  });

  it.each([
    ['U+FEFF alone (not white space in .NET; JavaScript trim() strips it)', String.fromCodePoint(0xfeff)],
    ['U+200B alone', String.fromCodePoint(0x200b)],
    ['U+0085 inside', `a${String.fromCodePoint(0x85)}b`],
    ['U+0080 inside (only C0 controls and DEL are rejected)', `a${String.fromCodePoint(0x80)}b`],
  ])('accepts %s', (_label, name) => {
    expect(NodeName.safeParse(name).success).toBe(true);
  });

  it('explains why a name is rejected', () => {
    const messages = (name: string) => NodeName.safeParse(name).error?.issues.map((issue) => issue.message);
    expect(messages('   ')).toEqual(['Name must not be blank']);
    expect(messages(' a')).toEqual(['Name must not start or end with white space']);
    expect(messages('a\nb')).toEqual(['Name must not contain control characters']);
    expect(messages('a\ud800')).toEqual(['Name must not contain unpaired surrogates']);
  });
});

describe('normalizeNodeName', () => {
  it('trims white space, then validates', () => {
    expect(normalizeNodeName('  Projects\t')).toBe('Projects');
    expect(normalizeNodeName(`${String.fromCodePoint(0x3000)}全角${String.fromCodePoint(0x85)}`)).toBe(
      '全角',
    );
    expect(normalizeNodeName(` ${'x'.repeat(MAX_NODE_NAME_LENGTH)} `)).toBe('x'.repeat(MAX_NODE_NAME_LENGTH));
    expect(normalizeNodeName(String.fromCodePoint(0xfeff))).toBe(String.fromCodePoint(0xfeff));
  });

  it('returns null when the trimmed name is still invalid', () => {
    for (const name of ['', '   ', 'a\nb', ' a\ud800 ', 'x'.repeat(MAX_NODE_NAME_LENGTH + 1)]) {
      expect(normalizeNodeName(name)).toBeNull();
    }
  });

  it.each(nodeNamesFixture.cases)('matches node-names.json: $description', (testCase) => {
    const name = fromFixtureString(testCase);
    expect(normalizeNodeName(name)).toBe(testCase.normalized);
    expect(NodeName.safeParse(name).success).toBe(testCase.valid);
    expect(testCase.valid).toBe(testCase.normalized === name);
  });
});

describe('FractionalKey', () => {
  it('accepts generated keys and rejects malformed ones', () => {
    expect(FractionalKey.safeParse('a0V').success).toBe(true);
    expect(FractionalKey.safeParse('a0!').success).toBe(false);
    expect(FractionalKey.safeParse('').success).toBe(false);
  });
});

describe('DocumentContent', () => {
  const text = '# Groceries\n\n- milk\n';
  const content: DocumentContent = {
    nodeId: document.id,
    format: 'markdown',
    text,
    checksum: contentChecksum(text),
    contentRev: 7,
    updatedAt: '2026-10-12T14:00:01.000Z',
  };

  it('accepts canonical text with a matching checksum', () => {
    expect(DocumentContent.parse(content)).toEqual(content);
    expect(DocumentContent.safeParse({ ...content, text: '', checksum: contentChecksum('') }).success).toBe(
      true,
    );
  });

  it('rejects non-canonical text even when the checksum matches its canonical form', () => {
    const crlf = '# Groceries\r\n\r\n- milk\r\n';
    expect(contentChecksum(crlf)).toBe(content.checksum);
    expect(DocumentContent.safeParse({ ...content, text: crlf }).success).toBe(false);
  });

  it('rejects a checksum that does not match the text', () => {
    expect(DocumentContent.safeParse({ ...content, text: `${text}more` }).success).toBe(false);
  });

  it('rejects an unknown format', () => {
    expect(DocumentContent.safeParse({ ...content, format: 'rust' }).success).toBe(false);
  });

  it.each([
    ['U+0000', `a${String.fromCodePoint(0)}b`, 'Managed note text must not contain U+0000'],
    ['an unpaired surrogate', 'x\ud800y', 'Managed note text must not contain unpaired surrogates'],
  ])('rejects text containing %s even with its own checksum', (_label, bad, message) => {
    const result = DocumentContent.safeParse({ ...content, text: bad, checksum: contentChecksum(bad) });
    expect(result.error?.issues.map((issue) => issue.message)).toEqual([message]);
  });

  it('rejects a zero content revision', () => {
    expect(DocumentContent.safeParse({ ...content, contentRev: 0 }).success).toBe(false);
  });
});

describe('field shapes for derived schemas', () => {
  it('Node and DocumentContent themselves cannot be picked (zod forbids it on refined objects)', () => {
    expect(() => Node.pick({ id: true })).toThrow(/refinements/);
    expect(() => DocumentContent.partial()).toThrow(/refinements/);
  });

  it('NodeFields can be picked or made partial, and the invariants re-applied', () => {
    const PinState = NodeFields.pick({ isPinned: true, pinPosition: true }).superRefine(refineNodeInvariants);
    expect(PinState.safeParse({ isPinned: true, pinPosition: 'a0' }).success).toBe(true);
    expect(PinState.safeParse({ isPinned: true, pinPosition: null }).success).toBe(false);

    const Patch = NodeFields.partial().superRefine(refineNodeInvariants);
    expect(Patch.safeParse({}).success).toBe(true);
    expect(Patch.safeParse({ name: 'Renamed' }).success).toBe(true);
    // Rules whose fields are absent are not checked; present pairs still are.
    expect(Patch.safeParse({ deletedAt: '2026-10-13T08:00:00.000Z' }).success).toBe(true);
    expect(Patch.safeParse({ deletedAt: '2026-10-13T08:00:00.000Z', trashBatchId: null }).success).toBe(
      false,
    );
    expect(Patch.safeParse({ id: folder.id, parentId: folder.id }).success).toBe(false);
    expect(Patch.safeParse({ kind: 'folder', contentUpdatedAt: '2026-10-12T14:00:01.000Z' }).success).toBe(
      false,
    );
    expect(Patch.safeParse({ name: ' untrimmed' }).success).toBe(false);
  });

  it('DocumentContentFields can be picked, and the invariants re-applied', () => {
    const TextAndChecksum = DocumentContentFields.pick({ text: true, checksum: true }).superRefine(
      refineDocumentContentInvariants,
    );
    expect(TextAndChecksum.safeParse({ text: 'a\n', checksum: contentChecksum('a\n') }).success).toBe(true);
    expect(TextAndChecksum.safeParse({ text: 'a\n', checksum: contentChecksum('b') }).success).toBe(false);
    expect(TextAndChecksum.safeParse({ text: 'a\r\n', checksum: contentChecksum('a\n') }).success).toBe(
      false,
    );

    const TextOnly = DocumentContentFields.pick({ text: true }).superRefine(refineDocumentContentInvariants);
    expect(TextOnly.safeParse({ text: 'any canonical text' }).success).toBe(true);
    expect(TextOnly.safeParse({ text: 'x\ud800' }).success).toBe(false);
  });
});
