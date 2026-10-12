import { existsSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  buildChecksumFixture,
  buildFormatsFixture,
  buildFractionalIndexFixture,
  buildNodeNamesFixture,
  buildNoteLinksFixture,
  buildNoteTextFixture,
  fromFixtureString,
  toFixtureString,
} from '../scripts/fixture-builders.ts';
import {
  checksumFixture,
  FIXTURE_FILES,
  formatsFixture,
  fractionalIndexFixture,
  nodeNamesFixture,
  noteLinksFixture,
  noteTextFixture,
  readFixtureText,
} from './fixtures.ts';

describe('generated fixtures are up to date', () => {
  // If one of these fails after an intentional change, run `pnpm --filter @nimpad/contracts fixtures`, then
  // `pnpm format`, and review the diff: the C# test-suite reads the same files.
  it.each([
    ['fractional-index.json', fractionalIndexFixture, buildFractionalIndexFixture],
    ['checksum.json', checksumFixture, buildChecksumFixture],
    ['note-text.json', noteTextFixture, buildNoteTextFixture],
    ['node-names.json', nodeNamesFixture, buildNodeNamesFixture],
    ['formats.json', formatsFixture, buildFormatsFixture],
    ['note-links.json', noteLinksFixture, buildNoteLinksFixture],
  ] as const)('%s', (_name, committed, build) => {
    expect(committed).toEqual(build());
  });
});

describe('fixture files', () => {
  it.each([...FIXTURE_FILES, 'README.md'])('%s is UTF-8 without BOM, with LF line endings', (name) => {
    const text = readFixtureText(name);
    expect(text.charCodeAt(0)).not.toBe(0xfeff);
    expect(text).not.toContain('\r');
    expect(text.endsWith('\n')).toBe(true);
  });

  it.each(FIXTURE_FILES)(
    '%s escapes invisible characters and holds only well-formed JSON strings',
    (name) => {
      const text = readFixtureText(name);
      // No raw white space other than the ASCII space and LF, no BOM, and no escaped unpaired surrogate (strict JSON
      // readers refuse those; such strings are written as utf16 arrays).
      expect(text).not.toMatch(/[\u00a0\u0085\u2000-\u200f\u2028\u2029\u3000\ufeff\t]/);
      expect(text).not.toMatch(/\\ud[89ab][0-9a-f]{2}(?!\\ud[c-f])/i);
      expect(text).not.toMatch(/(?<!\\ud[89ab][0-9a-f]{2})\\ud[c-f][0-9a-f]{2}/i);
    },
  );

  it('documents every file in the README', () => {
    expect(existsSync(new URL('../fixtures/README.md', import.meta.url))).toBe(true);
    const readme = readFixtureText('README.md');
    for (const name of FIXTURE_FILES) {
      expect(readme).toContain(`## \`${name}\``);
      expect(readme).toContain(`| \`${name}\` |`);
    }
  });
});

describe('fixture strings', () => {
  it('writes well-formed strings as text and others as UTF-16 code units, and reads both back', () => {
    expect(toFixtureString('a😀')).toEqual({ text: 'a😀' });
    expect(toFixtureString('a\ud800')).toEqual({ utf16: [0x61, 0xd800] });
    for (const value of ['', 'a😀', 'a\ud800', '\udc00\ud800', `x${'\u0000'}`]) {
      expect(fromFixtureString(toFixtureString(value))).toBe(value);
    }
  });
});
