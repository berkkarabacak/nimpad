// Internal links between notes: `nimpad://note/{uuid}` in the text. A link names its target by ID, never by path,
// so moving or renaming a note never breaks it. The server keeps `document_links` current from the same rules as
// the desktop app; fixtures/note-links.json pins them.

import { Uuid } from './ids.ts';

/** The prefix of every note link (matched case-insensitively for ASCII letters). */
export const NOTE_LINK_PREFIX = 'nimpad://note/';

/** One `nimpad://note/{uuid}` reference found in a text. Offsets count UTF-16 code units. */
export interface NoteLink {
  /** Offset of the first character (the `n` of `nimpad`). */
  start: number;
  /** Length of the whole URI. */
  length: number;
  /** The referenced note's ID, lowercase. */
  target: Uuid;
}

const UUID_LENGTH = 36;
const HYPHENATED_UUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
// One UTF-16 code unit: a Unicode letter (any L category) or decimal digit (Nd), as .NET's char.IsLetterOrDigit.
// A surrogate half is neither.
const LETTER_OR_DIGIT = /^[\p{L}\p{Nd}]$/u;

/** The link URI of a note: `nimpad://note/{id}` with the lowercase ID. */
export function noteLinkUri(id: Uuid): string {
  if (!Uuid.safeParse(id).success) {
    throw new TypeError(`Expected a lowercase hyphenated UUID: ${id}`);
  }
  return `${NOTE_LINK_PREFIX}${id}`;
}

function isLetterOrDigit(ch: string): boolean {
  return LETTER_OR_DIGIT.test(ch);
}

/**
 * Every note link in `text`, in order of appearance. A link is recognized when:
 *
 * - `nimpad://note/` (ASCII letters in any case) is directly followed by a hyphenated 8-4-4-4-12 UUID whose hex
 *   digits may be in any case (nothing else: no braces, signs or `0x`);
 * - the character before the prefix, if any, is not a letter or digit;
 * - the character after the UUID, if any, is not a letter, digit, `-` or `_`.
 *
 * So `[Plan](nimpad://note/…)`, `<nimpad://note/…>` and a bare URI ending a sentence are all found. Scanning
 * resumes after each link, or one character after a rejected candidate.
 */
export function findNoteLinks(text: string): NoteLink[] {
  // Folding only A-Z keeps every offset and matches the desktop: no non-ASCII character folds to a prefix letter.
  const folded = text.replace(/[A-Z]/g, (ch) => ch.toLowerCase());
  const links: NoteLink[] = [];
  let from = 0;
  while (from < text.length) {
    const start = folded.indexOf(NOTE_LINK_PREFIX, from);
    if (start < 0) {
      break;
    }
    const uuidStart = start + NOTE_LINK_PREFIX.length;
    const end = uuidStart + UUID_LENGTH;
    const candidate = text.slice(uuidStart, end);
    const before = text.charAt(start - 1);
    const after = text.charAt(end);
    if (
      end <= text.length &&
      (start === 0 || !isLetterOrDigit(before)) &&
      (end === text.length || !(isLetterOrDigit(after) || after === '-' || after === '_')) &&
      HYPHENATED_UUID.test(candidate)
    ) {
      links.push({ start, length: end - start, target: candidate.toLowerCase() });
      from = end;
    } else {
      from = start + 1;
    }
  }
  return links;
}

/** The distinct notes `text` links to, lowercase, in order of first appearance. */
export function extractLinkTargets(text: string): Uuid[] {
  return [...new Set(findNoteLinks(text).map((link) => link.target))];
}
