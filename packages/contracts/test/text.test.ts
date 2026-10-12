import { describe, expect, it } from 'vitest';
import { fromFixtureString } from '../scripts/fixture-builders.ts';
import {
  ContentChecksum,
  canonicalizeText,
  contentChecksum,
  isCanonicalText,
  isStorableText,
  isWellFormedText,
  isWhiteSpace,
  toStorableText,
  trimWhiteSpace,
  WHITE_SPACE_CHARACTERS,
} from '../src/text.ts';
import { checksumFixture, noteTextFixture } from './fixtures.ts';

const ch = (codePoint: number): string => String.fromCodePoint(codePoint);
const NUL = ch(0);
const REPLACEMENT = ch(0xfffd);

describe('canonicalizeText', () => {
  it.each([
    ['', ''],
    ['a\r\nb', 'a\nb'],
    ['a\rb', 'a\nb'],
    ['a\nb', 'a\nb'],
    ['a\r\n\r\nb', 'a\n\nb'],
    ['a\n\rb', 'a\n\nb'],
    ['a\r\r\nb', 'a\n\nb'],
    ['\r', '\n'],
    ['end\r\n', 'end\n'],
  ])('%j -> %j', (input, expected) => {
    expect(canonicalizeText(input)).toBe(expected);
    expect(isCanonicalText(expected)).toBe(true);
  });

  it('changes nothing but line endings', () => {
    const lineSeparator = String.fromCodePoint(0x2028);
    const nextLine = String.fromCodePoint(0x85);
    const bom = String.fromCodePoint(0xfeff);
    const decomposed = `cafe${String.fromCodePoint(0x301)}`;
    for (const text of [lineSeparator, nextLine, `${bom}x`, decomposed, '\t  x  \t', '😀日本']) {
      expect(canonicalizeText(text)).toBe(text);
    }
  });

  it('is idempotent', () => {
    const text = 'a\r\nb\rc\n\r\r\n';
    expect(canonicalizeText(canonicalizeText(text))).toBe(canonicalizeText(text));
  });
});

describe('contentChecksum', () => {
  // Independent SHA-256 test vectors (FIPS 180-2 "abc", the empty string, and "hello\n").
  it.each([
    ['', 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'],
    ['abc', 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'],
    ['hello\n', '5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03'],
    ['hello\r\n', '5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03'],
    ['hello\r', '5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03'],
  ])('checksum of %j', (text, hex) => {
    expect(contentChecksum(text)).toBe(`sha256:${hex}`);
  });

  it('hashes UTF-8 bytes, not UTF-16 code units', async () => {
    const { createHash } = await import('node:crypto');
    const text = 'é😀日';
    const utf8 = Buffer.from([0xc3, 0xa9, 0xf0, 0x9f, 0x98, 0x80, 0xe6, 0x97, 0xa5]);
    expect(Buffer.from(text, 'utf8')).toEqual(utf8);
    expect(contentChecksum(text)).toBe(`sha256:${createHash('sha256').update(utf8).digest('hex')}`);
  });

  it('distinguishes precomposed and decomposed text (no Unicode normalization)', () => {
    expect(contentChecksum('café')).not.toBe(contentChecksum(`cafe${String.fromCodePoint(0x301)}`));
  });

  it('produces a valid ContentChecksum', () => {
    expect(ContentChecksum.safeParse(contentChecksum('x')).success).toBe(true);
  });
});

describe('ContentChecksum schema', () => {
  it.each([
    'sha256:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855',
    'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
    'sha256:e3b0c442',
    'sha1:da39a3ee5e6b4b0d3255bfef95601890afd80709',
    'SHA256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
  ])('rejects %s', (value) => {
    expect(ContentChecksum.safeParse(value).success).toBe(false);
  });
});

describe('fixture parity (fixtures/checksum.json)', () => {
  const { cases } = checksumFixture;

  it('covers the required inputs', () => {
    const inputs = cases.map((c) => c.input);
    expect(inputs).toContain('');
    expect(inputs.some((s) => s.includes('\r\n'))).toBe(true);
    expect(inputs.some((s) => /\r(?!\n)/.test(s))).toBe(true);
    expect(inputs.some((s) => s.includes('\r\n') && /\r(?!\n)/.test(s) && /(?<!\r)\n/.test(s))).toBe(true);
    expect(inputs.some((s) => s.includes('é'))).toBe(true);
    expect(inputs.some((s) => /\p{Extended_Pictographic}/u.test(s))).toBe(true);
    expect(inputs.some((s) => /\p{Script=Han}/u.test(s))).toBe(true);
    expect(inputs.some((s) => s.endsWith('\n'))).toBe(true);
  });

  it.each(cases)('$name', ({ input, canonical, checksum }) => {
    expect(canonicalizeText(input)).toBe(canonical);
    expect(contentChecksum(input)).toBe(checksum);
    expect(contentChecksum(canonical)).toBe(checksum);
    expect(ContentChecksum.parse(checksum)).toBe(checksum);
  });
});

describe('storable text', () => {
  it('is well-formed UTF-16 without U+0000', () => {
    for (const text of [
      '',
      'abc',
      'note 😀',
      `a${REPLACEMENT}b`,
      `${ch(0xfeff)}x`,
      'a\r\nb',
      `${ch(0x80)}${ch(0x7f)}`,
    ]) {
      expect(isStorableText(text)).toBe(true);
      expect(toStorableText(text)).toBe(text);
    }
  });

  it.each([
    ['U+0000', `a${NUL}b`, `a${REPLACEMENT}b`],
    ['an unpaired high surrogate', 'x\ud800y', `x${REPLACEMENT}y`],
    ['an unpaired low surrogate', 'x\udc00y', `x${REPLACEMENT}y`],
    ['a reversed pair (two unpaired surrogates)', '\udc00\ud800', `${REPLACEMENT}${REPLACEMENT}`],
    ['a high surrogate after a pair', '😀\ud83d', `😀${REPLACEMENT}`],
  ])('rejects %s and replaces it with U+FFFD', (_label, text, storable) => {
    expect(isStorableText(text)).toBe(false);
    expect(toStorableText(text)).toBe(storable);
    expect(isStorableText(storable)).toBe(true);
  });

  it('detects unpaired surrogates without flagging pairs', () => {
    expect(isWellFormedText('😀')).toBe(true);
    expect(isWellFormedText('\ud83d')).toBe(false);
    expect(isWellFormedText('\ude00')).toBe(false);
    expect(isWellFormedText(NUL)).toBe(true);
  });

  it('hashes an unpaired surrogate as U+FFFD and U+0000 as the byte 0x00', async () => {
    const { createHash } = await import('node:crypto');
    expect(contentChecksum('x\ud800y')).toBe(contentChecksum(`x${REPLACEMENT}y`));
    expect(contentChecksum(`a${NUL}b`)).toBe(
      `sha256:${createHash('sha256')
        .update(Buffer.from([0x61, 0x00, 0x62]))
        .digest('hex')}`,
    );
    // Equal checksums mean equal text only for storable text: the replacement changes the hash of U+0000.
    expect(contentChecksum(`a${NUL}b`)).not.toBe(contentChecksum(toStorableText(`a${NUL}b`)));
  });

  it.each(noteTextFixture.cases)('matches note-text.json: $description', (testCase) => {
    const text = fromFixtureString(testCase);
    expect(isStorableText(text)).toBe(testCase.storable);
    expect(toStorableText(text)).toBe(testCase.storableText);
    expect(contentChecksum(text)).toBe(testCase.checksum);
  });

  it('note-text.json covers U+0000 and unpaired surrogates on both sides of the rule', () => {
    const cases = noteTextFixture.cases;
    expect(cases.some((c) => c.utf16 !== undefined)).toBe(true);
    expect(cases.some((c) => c.text?.includes(NUL))).toBe(true);
    expect(cases.filter((c) => c.storable).length).toBeGreaterThan(3);
  });
});

describe('white space', () => {
  it('is exactly the Unicode White_Space set (the same set as .NET char.IsWhiteSpace)', () => {
    const expected: string[] = [];
    for (let code = 0; code <= 0xffff; code++) {
      if (/\p{White_Space}/u.test(ch(code))) {
        expected.push(ch(code));
      }
    }
    expect(WHITE_SPACE_CHARACTERS).toEqual(expected);
    expect(WHITE_SPACE_CHARACTERS).toHaveLength(25);
  });

  it('differs from String.prototype.trim on U+FEFF and U+0085', () => {
    expect(isWhiteSpace(ch(0xfeff))).toBe(false);
    expect(ch(0xfeff).trim()).toBe('');
    expect(isWhiteSpace(ch(0x85))).toBe(true);
    expect(ch(0x85).trim()).toBe(ch(0x85));
    expect(isWhiteSpace(ch(0x200b))).toBe(false);
    expect(isWhiteSpace(ch(0x180e))).toBe(false);
  });

  it('trims both ends only', () => {
    expect(trimWhiteSpace(`${ch(0x3000)} a b${ch(0xa0)}${ch(0x2028)}\t`)).toBe('a b');
    expect(trimWhiteSpace(`${ch(0xfeff)}a${ch(0xfeff)}`)).toBe(`${ch(0xfeff)}a${ch(0xfeff)}`);
    expect(trimWhiteSpace(WHITE_SPACE_CHARACTERS.join(''))).toBe('');
    expect(trimWhiteSpace('')).toBe('');
  });

  it('trims in linear time', () => {
    const text = `a${' '.repeat(200_000)}b${' '.repeat(200_000)}`;
    const started = performance.now();
    expect(trimWhiteSpace(text)).toBe(`a${' '.repeat(200_000)}b`);
    expect(performance.now() - started).toBeLessThan(1_000);
  });
});
