// Regenerates the computed fixtures from the implementation. Run with `pnpm --filter @nimpad/contracts fixtures`
// after an intentional change, then review the diff: the C# test-suite consumes the same files.
//
// pending-operations.json is hand-written and not touched here.

import { writeFileSync } from 'node:fs';
import {
  buildChecksumFixture,
  buildFormatsFixture,
  buildFractionalIndexFixture,
  buildNodeNamesFixture,
  buildNoteLinksFixture,
  buildNoteTextFixture,
} from './fixture-builders.ts';

const fixturesDir = new URL('../fixtures/', import.meta.url);

// Invisible or easily mangled characters (DEL, C1 controls, white space other than the ASCII space, soft hyphen,
// combining marks, zero-width and bidi formatting characters, line/paragraph separators, BOM, U+FFFD) are written
// as \u escapes so that no editor or tool can silently strip or normalize them. Visible non-ASCII text (é, emoji,
// CJK) stays literal for readability.
const INVISIBLE_RANGES: ReadonlyArray<readonly [number, number]> = [
  [0x007f, 0x00a0],
  [0x00ad, 0x00ad],
  [0x0300, 0x036f],
  [0x1680, 0x1680],
  [0x180e, 0x180e],
  [0x2000, 0x200f],
  [0x2028, 0x202f],
  [0x205f, 0x206f],
  [0x3000, 0x3000],
  [0xfeff, 0xfeff],
  [0xfffd, 0xfffd],
];

function escapeInvisible(json: string): string {
  // Iterating by code point keeps surrogate pairs (emoji) intact; none of the ranges contains a surrogate.
  return Array.from(json, (ch) => {
    const code = ch.codePointAt(0) as number;
    const invisible = INVISIBLE_RANGES.some(([low, high]) => code >= low && code <= high);
    return invisible ? `\\u${code.toString(16).padStart(4, '0')}` : ch;
  }).join('');
}

/** Pretty JSON with each array element of the top-level object on one line, so diffs stay one case per line. */
function serialize(fixture: object): string {
  const entries = Object.entries(fixture).map(([key, value]) => {
    const body = Array.isArray(value)
      ? `[\n${value.map((item) => `    ${JSON.stringify(item)}`).join(',\n')}\n  ]`
      : JSON.stringify(value);
    return `  ${JSON.stringify(key)}: ${body}`;
  });
  return escapeInvisible(`{\n${entries.join(',\n')}\n}\n`);
}

const generated: ReadonlyArray<readonly [name: string, build: () => object]> = [
  ['fractional-index.json', buildFractionalIndexFixture],
  ['checksum.json', buildChecksumFixture],
  ['formats.json', buildFormatsFixture],
  ['node-names.json', buildNodeNamesFixture],
  ['note-text.json', buildNoteTextFixture],
  ['note-links.json', buildNoteLinksFixture],
];
for (const [name, build] of generated) {
  writeFileSync(new URL(name, fixturesDir), serialize(build()));
}
console.log(`Wrote ${generated.map(([name]) => `fixtures/${name}`).join(', ')}`);
