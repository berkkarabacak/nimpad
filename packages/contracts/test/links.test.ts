import { describe, expect, it } from 'vitest';
import { extractLinkTargets, findNoteLinks, NOTE_LINK_PREFIX, noteLinkUri } from '../src/links.ts';
import { noteLinksFixture } from './fixtures.ts';

const A = '0192f0c4-1234-7abc-8def-0123456789ab';
const B = '01a13069-88e3-7296-af4f-6b32291fd002';
const LINK_A = `nimpad://note/${A}`;

describe('noteLinkUri', () => {
  it('builds the link of a note', () => {
    expect(noteLinkUri(A)).toBe(LINK_A);
    expect(NOTE_LINK_PREFIX).toBe('nimpad://note/');
  });

  it('rejects an ID that is not a lowercase hyphenated UUID', () => {
    expect(() => noteLinkUri(A.toUpperCase())).toThrow(TypeError);
    expect(() => noteLinkUri('not-a-uuid')).toThrow(TypeError);
  });
});

describe('findNoteLinks', () => {
  it('finds bare, Markdown and angle-bracket links with UTF-16 offsets', () => {
    expect(findNoteLinks(`See ${LINK_A}.`)).toEqual([{ start: 4, length: 50, target: A }]);
    expect(findNoteLinks(`[Plan](${LINK_A})`)).toEqual([{ start: 7, length: 50, target: A }]);
    expect(findNoteLinks(`<${LINK_A}>`)).toEqual([{ start: 1, length: 50, target: A }]);
    // An emoji is two UTF-16 code units.
    expect(findNoteLinks(`😀 ${LINK_A}`)).toEqual([{ start: 3, length: 50, target: A }]);
  });

  it('accepts any letter case and reports the target in lowercase', () => {
    expect(findNoteLinks(`NIMPAD://NOTE/${A.toUpperCase()}`)).toEqual([{ start: 0, length: 50, target: A }]);
  });

  it('requires the link to stand on its own', () => {
    for (const text of [`x${LINK_A}`, `1${LINK_A}`, `é${LINK_A}`, `${LINK_A}c`, `${LINK_A}-`, `${LINK_A}_`]) {
      expect(findNoteLinks(text)).toEqual([]);
    }
    for (const text of [`_${LINK_A}`, `-${LINK_A}`, `(${LINK_A})`, `${LINK_A}/`, `${LINK_A}#x`]) {
      expect(findNoteLinks(text)).toHaveLength(1);
    }
  });

  it('accepts only the plain 8-4-4-4-12 form', () => {
    for (const uuid of [
      `{${A}}`,
      `+${A.slice(1)}`,
      `0x${A.slice(2)}`,
      A.replace(/-/g, ''),
      `${A.slice(0, -1)}g`,
    ]) {
      expect(findNoteLinks(`nimpad://note/${uuid}`)).toEqual([]);
    }
    expect(findNoteLinks(LINK_A.slice(0, -1))).toEqual([]);
  });

  it('keeps scanning after a rejected candidate', () => {
    expect(findNoteLinks(`nimpad://note/${LINK_A}`)).toEqual([{ start: 14, length: 50, target: A }]);
  });
});

describe('extractLinkTargets', () => {
  it('lists distinct targets in order of first appearance', () => {
    expect(extractLinkTargets(`${LINK_A} nimpad://note/${B} ${LINK_A.toUpperCase()}`)).toEqual([A, B]);
    expect(extractLinkTargets('no links')).toEqual([]);
  });
});

describe('note-links.json', () => {
  it('uses the same prefix', () => {
    expect(noteLinksFixture.prefix).toBe(NOTE_LINK_PREFIX);
  });

  it.each(noteLinksFixture.cases)('$description', ({ text, links }) => {
    expect(findNoteLinks(text)).toEqual(links);
  });
});
