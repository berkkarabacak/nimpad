// Builds the computed fixtures (fractional-index.json, checksum.json, formats.json, node-names.json,
// note-text.json, note-links.json) from the inputs listed here and the implementation in src/. `generate-fixtures.ts` writes them to disk; test/fixtures.test.ts rebuilds them and
// fails if the committed files differ, so the fixtures cannot drift from the implementation unnoticed.
//
// The implementation itself is checked independently against the reference library's published examples in
// test/fractional-index.test.ts, against known SHA-256 vectors in test/text.test.ts, and against hand-written
// expectations in the other test files.

import {
  BASE_62_DIGITS,
  canonicalizeText,
  contentChecksum,
  DOCUMENT_FORMATS,
  type DocumentFormat,
  detectFormat,
  FORMAT_BY_EXTENSION,
  findNoteLinks,
  generateKeyBetween,
  generateNKeysBetween,
  isStorableText,
  isWellFormedText,
  NOTE_LINK_PREFIX,
  NodeName,
  type NoteLink,
  normalizeNodeName,
  toStorableText,
  WHITE_SPACE_CHARACTERS,
} from '../src/index.ts';

type Key = string | null;

export interface BetweenCase {
  a: Key;
  b: Key;
  expected: string;
}

export interface NKeysCase {
  a: Key;
  b: Key;
  n: number;
  expected: string[];
}

export interface InvalidCase {
  a: Key;
  b: Key;
  error: true;
  message: string;
}

export interface FractionalIndexFixture {
  description: string;
  digits: string;
  between: BetweenCase[];
  nKeys: NKeysCase[];
  invalid: InvalidCase[];
}

export interface ChecksumCase {
  name: string;
  input: string;
  canonical: string;
  checksum: string;
}

export interface ChecksumFixture {
  description: string;
  cases: ChecksumCase[];
}

const LARGEST_INTEGER = 'z'.repeat(27);
const SMALLEST_INTEGER = `A${'0'.repeat(26)}`;

/** A small deterministic pseudo-random generator (LCG, Numerical Recipes constants). */
function lcg(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    return state / 2 ** 32;
  };
}

export function buildFractionalIndexFixture(): FractionalIndexFixture {
  const between: BetweenCase[] = [];
  const seen = new Set<string>();
  const add = (a: Key, b: Key): string => {
    const expected = generateKeyBetween(a, b);
    const id = JSON.stringify([a, b]);
    if (!seen.has(id)) {
      seen.add(id);
      between.push({ a, b, expected });
    }
    return expected;
  };

  // Both ends open.
  add(null, null);

  // Append chain from the first key, crossing the 'az' -> 'b00' integer-length boundary.
  let key = 'a0';
  for (let i = 0; i < 66; i++) {
    key = add(key, null);
  }

  // Prepend chain from the first key, crossing the 'Z0' -> 'Yzz' boundary.
  key = 'a0';
  for (let i = 0; i < 66; i++) {
    key = add(null, key);
  }

  // Midpoints between adjacent integer keys, including across integer-part boundaries.
  for (const [a, b] of [
    ['a0', 'a1'],
    ['a1', 'a2'],
    ['a9', 'aA'],
    ['aZ', 'aa'],
    ['ay', 'az'],
    ['Zy', 'Zz'],
    ['Zz', 'a0'],
    ['az', 'b00'],
    ['b00', 'b01'],
    ['bzz', 'c000'],
    ['Yzz', 'Z0'],
    ['Z0', 'Z1'],
    ['Xzzz', 'Y00'],
  ] as const) {
    add(a, b);
  }

  // Integer-part boundaries and the reference library's published examples.
  for (const [a, b] of [
    ['Zz', 'a1'],
    ['Zz', 'a01'],
    ['az', 'b01'],
    ['a0', 'b00'],
    ['Zz', 'b00'],
    ['Yzz', null],
    [null, 'b00'],
    [null, 'Y00'],
    ['bzz', null],
    ['a0V', 'a1'],
    ['a0', 'a0V'],
    ['a0', 'a0G'],
    ['b125', 'b129'],
    ['a0', 'a1V'],
    [null, 'a0V'],
    [null, 'b999'],
    ['a5', 'a7'],
    ['a1V', 'a2'],
    [`${LARGEST_INTEGER.slice(0, -1)}y`, null],
    [LARGEST_INTEGER, null],
    [`${LARGEST_INTEGER}V`, null],
    [null, `${SMALLEST_INTEGER}1`],
    [null, `${SMALLEST_INTEGER}0V`],
  ] as const) {
    add(a, b);
  }

  // Rounding of the middle digit is "half up" (JavaScript Math.round), not banker's rounding:
  // ('a0', 'a05') averages digits 0 and 5 to 2.5 and must pick '3'; ('a02', 'a07') averages 2 and 7 and must
  // pick '5'.
  for (const [a, b] of [
    ['a0', 'a05'],
    ['a02', 'a07'],
    ['a01', 'a06'],
    ['a0', 'a03'],
    ['a0x', 'a0z'],
  ] as const) {
    add(a, b);
  }

  // Long fractional parts: repeated bisection toward the lower bound, toward the upper bound, and zig-zag.
  let lo = 'a0';
  let hi = 'a1';
  for (let i = 0; i < 14; i++) {
    hi = add(lo, hi);
  }
  lo = 'a0';
  hi = 'a1';
  for (let i = 0; i < 14; i++) {
    lo = add(lo, hi);
  }
  lo = 'a0';
  hi = 'a1';
  for (let i = 0; i < 20; i++) {
    const mid = add(lo, hi);
    if (i % 2 === 0) {
      lo = mid;
    } else {
      hi = mid;
    }
  }
  for (const [a, b] of [
    ['a0zzzzzzzzzzzzzzzzzz', 'a1'],
    ['a0', 'a00000000000001'],
    ['a0V', 'a0V1'],
    ['a0VVVVVVVVVVVVV', 'a0VVVVVVVVVVVVW'],
    ['a0zzzzzzzzzzzzzzzzzz', null],
    [null, 'a00000000000001'],
  ] as const) {
    add(a, b);
  }

  // A realistic edit session: 60 inserts at pseudo-random positions in one sibling list.
  const random = lcg(20261012);
  const list: string[] = [];
  for (let i = 0; i < 60; i++) {
    const index = Math.floor(random() * (list.length + 1));
    const inserted = add(list[index - 1] ?? null, list[index] ?? null);
    list.splice(index, 0, inserted);
  }

  const nKeys: NKeysCase[] = (
    [
      [null, null, 0],
      [null, null, 1],
      [null, null, 5],
      ['a4', null, 10],
      [null, 'a0', 5],
      ['a0', 'a2', 20],
      ['a0', 'a1', 1],
      ['a0', 'a1', 3],
      ['a0', 'a1', 10],
      ['Zz', 'a0', 7],
      ['ax', null, 4],
      [null, 'b01', 4],
      ['a0V', 'a0l', 5],
      ['a0', 'a0V', 31],
      ['b125', 'b129', 6],
    ] as const
  ).map(([a, b, n]) => ({ a, b, n, expected: generateNKeysBetween(a, b, n) }));

  const invalid: InvalidCase[] = (
    [
      ['a1', 'a0'],
      ['a0', 'a0'],
      ['a0V', 'a0'],
      ['a1', 'a0V'],
      ['b00', 'a0'],
      ['a00', null],
      [null, 'a00'],
      ['a0', 'a1V0'],
      ['a00', 'a1'],
      ['0', '1'],
      ['!0', null],
      ['', null],
      [null, ''],
      ['b0', null],
      [null, 'B'],
      [null, SMALLEST_INTEGER],
      [SMALLEST_INTEGER, 'a0'],
    ] as const
  ).map(([a, b]) => {
    try {
      generateKeyBetween(a, b);
    } catch (error) {
      return { a, b, error: true, message: (error as Error).message };
    }
    throw new Error(`Expected generateKeyBetween(${a}, ${b}) to throw`);
  });

  return {
    description:
      'Fractional-index position keys (rocicorp/fractional-indexing, base 62). See fixtures/README.md. ' +
      'Generated by scripts/generate-fixtures.ts; do not edit by hand.',
    digits: BASE_62_DIGITS,
    between,
    nKeys,
    invalid,
  };
}

const CHECKSUM_INPUTS: ReadonlyArray<readonly [name: string, input: string]> = [
  ['empty string', ''],
  ['ascii without line break', 'abc'],
  ['single LF', '\n'],
  ['single CRLF', '\r\n'],
  ['single lone CR', '\r'],
  ['trailing LF', 'hello\n'],
  ['trailing CRLF', 'hello\r\n'],
  ['trailing lone CR', 'hello\r'],
  ['CRLF lines', 'line 1\r\nline 2\r\nline 3'],
  ['lone CR lines (classic Mac)', 'line 1\rline 2\rline 3'],
  ['mixed CRLF, CR and LF', 'a\r\nb\rc\nd\r\n'],
  ['LF then CR is two breaks', 'a\n\rb'],
  ['CR CR LF is two breaks', 'a\r\r\nb'],
  ['blank CRLF lines', '\r\n\r\n\r\n'],
  ['tabs and spaces are kept', '\tindented  \r\n  trailing spaces  '],
  ['precomposed e-acute', 'café\r\n'],
  ['decomposed e-acute is not normalized', 'cafe\u0301\r\n'],
  ['emoji (surrogate pair)', 'note 😀\r\n🚀'],
  ['CJK', '日本語のメモ\r\n中文笔记\r한국어'],
  ['U+2028 and U+0085 are not line endings here', 'a\u2028b\u0085c'],
  ['leading U+FEFF is content, not stripped', '\uFEFFtext\r\n'],
  ['U+0000 is hashed as the byte 0x00 (such text is not storable; see note-text.json)', 'a\u0000b\r\n'],
  ['markdown sample', '# Title\r\n\r\n- item one\r\n- item two\r\n\r\n```js\r\nconst x = 1;\r\n```\r\n'],
];

export function buildChecksumFixture(): ChecksumFixture {
  return {
    description:
      'Canonical note text (CRLF and lone CR become LF) and its content checksum ' +
      "('sha256:' + lowercase hex SHA-256 of the canonical text's UTF-8 bytes). See fixtures/README.md. " +
      'Generated by scripts/generate-fixtures.ts; do not edit by hand.',
    cases: CHECKSUM_INPUTS.map(([name, input]) => ({
      name,
      input,
      canonical: canonicalizeText(input),
      checksum: contentChecksum(input),
    })),
  };
}

// --- Strings that are not well-formed UTF-16 ------------------------------------------------------------------

/**
 * A string in a fixture: `text` when it is well-formed UTF-16, else `utf16`, the list of its UTF-16 code units.
 * (Strict JSON readers such as System.Text.Json refuse to decode an escaped unpaired surrogate.)
 */
export type FixtureString = { text: string; utf16?: never } | { utf16: number[]; text?: never };

export function toFixtureString(value: string): FixtureString {
  return isWellFormedText(value)
    ? { text: value }
    : { utf16: Array.from({ length: value.length }, (_, i) => value.charCodeAt(i)) };
}

export function fromFixtureString(value: FixtureString): string {
  return value.utf16 === undefined ? value.text : String.fromCharCode(...value.utf16);
}

// --- formats.json ---------------------------------------------------------------------------------------------

export interface FormatCase {
  name: string;
  format: DocumentFormat;
}

export interface FormatsFixture {
  description: string;
  formats: readonly DocumentFormat[];
  cases: FormatCase[];
}

const FORMAT_SPECIAL_NAMES: readonly string[] = [
  '',
  '   ',
  'README',
  'notes.txt',
  'file.tar.gz',
  'name.',
  'notes.md.',
  '.md',
  '.editorconfig',
  '.gitignore',
  'archive.tar.json',
  'notes.md.txt',
  'my.notes.md',
  'folder/inside.md',
  'CONFIG.JSON',
  'Readme.Md',
  'Script.PS1',
  ' spaced.md ',
  'nbsp.md\u00a0',
  'next line.md\u0085',
  '\u3000ideographic.yaml\u3000',
  'zero width.md\u200b',
  'bom.md\ufeff',
  'Jenkinsfile',
  'JENKINSFILE',
  'jenkinsfile',
  ' Jenkinsfile ',
  'Jenkinsfile.groovy',
  'Jenkinsfile.bak',
  'my Jenkinsfile',
  'long s.ſql',
  'dotless i.ıni',
  'fullwidth.ｍｄ',
  'kelvin.Kml',
];

export function buildFormatsFixture(): FormatsFixture {
  const names = new Set<string>();
  for (const extension of FORMAT_BY_EXTENSION.keys()) {
    names.add(`note${extension}`);
    names.add(`NOTE${extension.toUpperCase()}`);
  }
  for (const name of FORMAT_SPECIAL_NAMES) {
    names.add(name);
  }
  return {
    description:
      "A document's format detected from its name (detectFormat). See fixtures/README.md. " +
      'Generated by scripts/generate-fixtures.ts; do not edit by hand.',
    formats: DOCUMENT_FORMATS,
    cases: [...names].map((name) => ({ name, format: detectFormat(name) })),
  };
}

// --- node-names.json ------------------------------------------------------------------------------------------

export type NodeNameCase = FixtureString & {
  description: string;
  /** NodeName accepts the string as it is (the stored and wire form). */
  valid: boolean;
  /** normalizeNodeName's result: the trimmed name, or null when it is rejected. */
  normalized: string | null;
};

export interface NodeNamesFixture {
  description: string;
  whiteSpace: string[];
  cases: NodeNameCase[];
}

const NODE_NAME_INPUTS: ReadonlyArray<readonly [description: string, name: string]> = [
  ['plain name', 'Projects'],
  ['non-ASCII and emoji', 'Weekly groceries — 日本 😀.md'],
  ['255 code units', 'x'.repeat(255)],
  ['256 code units', 'x'.repeat(256)],
  ['emoji ending at 255 code units', `${'x'.repeat(253)}😀`],
  ['emoji ending at 256 code units', `${'x'.repeat(254)}😀`],
  ['255 code units once trimmed', ` ${'x'.repeat(255)} `],
  ['empty', ''],
  ['spaces only', '   '],
  ['leading space is trimmed', ' leading'],
  ['trailing space is trimmed', 'trailing '],
  ['tabs around are trimmed', '\t tabbed \t'],
  ['tab inside is a control character', 'a\tb'],
  ['line feed inside', 'a\nb'],
  ['carriage return inside', 'a\rb'],
  ['U+0000 inside', 'a\u0000b'],
  ['DEL inside', 'a\u007fb'],
  ['U+0080 (C1 control) inside is allowed', 'a\u0080b'],
  ['U+FEFF alone is not white space', '\ufeff'],
  ['leading U+FEFF is kept', '\ufeffname'],
  ['trailing U+FEFF is kept', 'name\ufeff'],
  ['U+200B alone is not white space', '\u200b'],
  ['U+180E is not white space', 'a\u180e'],
  ['U+0085 alone is white space', '\u0085'],
  ['U+0085 inside is allowed', 'a\u0085b'],
  ['trailing U+0085 is trimmed', 'a\u0085'],
  ['no-break spaces are trimmed', '\u00a0a\u00a0'],
  ['line and paragraph separators are trimmed', '\u2028a\u2029'],
  ['ideographic spaces are trimmed', '\u3000全角\u3000'],
  ['every white-space character only', WHITE_SPACE_CHARACTERS.join('')],
  [
    'every white-space character around',
    `${WHITE_SPACE_CHARACTERS.join('')}a${WHITE_SPACE_CHARACTERS.join('')}`,
  ],
  ['unpaired high surrogate at the end', 'a\ud800'],
  ['unpaired low surrogate alone', '\udc00'],
  ['reversed surrogate pair', 'a\udc00\ud800b'],
  ['unpaired surrogate inside spaces', ' \ud800 '],
];

export function buildNodeNamesFixture(): NodeNamesFixture {
  return {
    description:
      'Node names: NodeName (the stored and wire form) and normalizeNodeName (trim, then validate). See ' +
      'fixtures/README.md. Generated by scripts/generate-fixtures.ts; do not edit by hand.',
    whiteSpace: [...WHITE_SPACE_CHARACTERS],
    cases: NODE_NAME_INPUTS.map(([description, name]) => ({
      description,
      ...toFixtureString(name),
      valid: NodeName.safeParse(name).success,
      normalized: normalizeNodeName(name),
    })),
  };
}

// --- note-text.json -------------------------------------------------------------------------------------------

export type NoteTextCase = FixtureString & {
  description: string;
  /** isStorableText: PostgreSQL stores the text unchanged. */
  storable: boolean;
  /** toStorableText: U+0000 and unpaired surrogates replaced with U+FFFD. */
  storableText: string;
  /** contentChecksum of the text as given. */
  checksum: string;
};

export interface NoteTextFixture {
  description: string;
  cases: NoteTextCase[];
}

const NOTE_TEXT_INPUTS: ReadonlyArray<readonly [description: string, text: string]> = [
  ['empty', ''],
  ['ASCII', 'abc\n'],
  ['emoji (a surrogate pair is well-formed)', 'note 😀\n'],
  ['U+FFFD itself is storable', 'a\ufffdb'],
  ['U+FEFF is storable', '\ufefftext'],
  ['C1 controls and DEL are storable', 'a\u0080\u007f\u0085b'],
  ['line endings do not matter for storability', 'a\r\nb\rc'],
  ['U+0000 inside', 'a\u0000b'],
  ['U+0000 alone', '\u0000'],
  ['several U+0000', '\u0000\u0000x\u0000'],
  ['unpaired high surrogate', 'x\ud800y'],
  ['unpaired low surrogate', 'x\udc00y'],
  ['high surrogate at the end', 'end\ud83d'],
  ['reversed pair is two unpaired surrogates', '\udc00\ud800'],
  ['pair, then an unpaired high surrogate', '😀\ud83d'],
  ['U+0000 and an unpaired surrogate', '\u0000\udfff'],
];

export function buildNoteTextFixture(): NoteTextFixture {
  return {
    description:
      'Storable note text (isStorableText, toStorableText) and the checksum of text that is not storable. See ' +
      'fixtures/README.md. Generated by scripts/generate-fixtures.ts; do not edit by hand.',
    cases: NOTE_TEXT_INPUTS.map(([description, text]) => ({
      description,
      ...toFixtureString(text),
      storable: isStorableText(text),
      storableText: toStorableText(text),
      checksum: contentChecksum(text),
    })),
  };
}

// --- note-links.json ------------------------------------------------------------------------------------------

export interface NoteLinkCase {
  description: string;
  text: string;
  links: NoteLink[];
}

export interface NoteLinksFixture {
  description: string;
  prefix: string;
  cases: NoteLinkCase[];
}

const A = '0192f0c4-1234-7abc-8def-0123456789ab';
const B = '01a13069-88e3-7296-af4f-6b32291fd002';
const LINK_A = `nimpad://note/${A}`;

const NOTE_LINK_INPUTS: ReadonlyArray<readonly [description: string, text: string]> = [
  ['empty text', ''],
  ['no link', 'just text'],
  ['the whole text', LINK_A],
  ['bare link ending a sentence', `See ${LINK_A}.`],
  ['Markdown link', `[Plan](${LINK_A})`],
  ['angle brackets', `<${LINK_A}>`],
  ['followed by a slash, a hash or a query', `${LINK_A}/x ${LINK_A}#h ${LINK_A}?q`],
  ['uppercase prefix and UUID', `NIMPAD://NOTE/${A.toUpperCase()}`],
  ['mixed-case prefix', `NimPad://Note/${A}`],
  ['two links and a repeat', `${LINK_A} and nimpad://note/${B}, again ${LINK_A}`],
  ['line breaks around', `\n${LINK_A}\r\n`],
  ['preceded by an ASCII letter', `x${LINK_A}`],
  ['preceded by a digit', `1${LINK_A}`],
  ['preceded by an underscore', `_${LINK_A}`],
  ['preceded by a hyphen', `-${LINK_A}`],
  ['preceded by a non-ASCII letter', `é${LINK_A}`],
  ['preceded by an Arabic-Indic digit (Nd)', `٠${LINK_A}`],
  ['preceded by superscript two (No, not a digit)', `²${LINK_A}`],
  ['preceded by an astral letter (surrogate halves are not letters)', `\u{1d400}${LINK_A}`],
  ['inside a word', `foo${LINK_A}bar`],
  ['followed by a letter', `${LINK_A}c`],
  ['followed by a digit', `${LINK_A}0`],
  ['followed by a hyphen', `${LINK_A}-`],
  ['followed by an underscore', `${LINK_A}_`],
  ['followed by a combining mark (not a letter)', `${LINK_A}́`],
  ['followed by a letter number (Nl, not a letter)', `${LINK_A}Ⅻ`],
  ['followed by a non-ASCII letter', `${LINK_A}é`],
  ['UUID cut short by the end of the text', LINK_A.slice(0, -1)],
  ['non-hex digit', `nimpad://note/${A.slice(0, -1)}g`],
  ['missing hyphen', `nimpad://note/${A.replace(/-/g, '')}`],
  ['braces', `nimpad://note/{${A}}`],
  ['plus sign in a group', `nimpad://note/+${A.slice(1)}`],
  ['0x in a group', `nimpad://note/0x${A.slice(2)}`],
  ['plus sign in a middle group', `nimpad://note/${A.slice(0, 9)}+${A.slice(10)}`],
  ['fullwidth digit', `nimpad://note/０${A.slice(1)}`],
  ['dotless i in the prefix', `nımpad://note/${A}`],
  ['prefix twice: the second one is a link', `nimpad://note/${LINK_A}`],
  ['back to back: each touches a letter or digit', `${LINK_A}nimpad://note/${B}`],
  ['space between back-to-back links', `${LINK_A} nimpad://note/${B}`],
  ['other scheme', `https://note/${A}`],
];

export function buildNoteLinksFixture(): NoteLinksFixture {
  return {
    description:
      'Internal note links (findNoteLinks): nimpad://note/{uuid}. Offsets and lengths count UTF-16 code units; ' +
      'targets are lowercase. See fixtures/README.md. Generated by scripts/generate-fixtures.ts; do not edit by hand.',
    prefix: NOTE_LINK_PREFIX,
    cases: NOTE_LINK_INPUTS.map(([description, text]) => ({ description, text, links: findNoteLinks(text) })),
  };
}
