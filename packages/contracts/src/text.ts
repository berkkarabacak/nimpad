import { createHash } from 'node:crypto';
import { z } from 'zod';

/**
 * Normalizes line endings for managed notes: CRLF and lone CR become LF. Nothing else changes (no Unicode
 * normalization, no BOM or whitespace stripping).
 */
export function canonicalizeText(text: string): string {
  return text.replace(/\r\n?/g, '\n');
}

/** True when `text` is already canonical (contains no CR). */
export function isCanonicalText(text: string): boolean {
  return !text.includes('\r');
}

// In a `u` regex a surrogate pair is one astral code point, so \p{Cs} matches exactly the unpaired surrogates.
const UNPAIRED_SURROGATE = /\p{Cs}/u;
const UNSTORABLE_CHARACTERS = /\p{Cs}|\0/gu;

/**
 * True when `text` holds no unpaired surrogate (it is well-formed UTF-16), so UTF-8 can carry it unchanged.
 * Encoders (Node's `Buffer`, .NET's `Encoding.UTF8`, pg) silently replace an unpaired surrogate with U+FFFD.
 */
export function isWellFormedText(text: string): boolean {
  return !UNPAIRED_SURROGATE.test(text);
}

/**
 * True when PostgreSQL `text` stores `text` exactly: it is well-formed UTF-16 and contains no U+0000 (which
 * PostgreSQL rejects). Managed note text and names must be storable; the server rejects anything else rather than
 * store a different string under the same checksum.
 */
export function isStorableText(text: string): boolean {
  return isWellFormedText(text) && !text.includes('\0');
}

/**
 * Replaces every U+0000 and every unpaired surrogate with U+FFFD and keeps everything else, giving text for which
 * {@link isStorableText} holds. Clients apply it before saving managed note text that came from a paste or an
 * import; the checksum is computed on the result.
 */
export function toStorableText(text: string): string {
  return text.replace(UNSTORABLE_CHARACTERS, '\ufffd');
}

/**
 * The Unicode White_Space characters, all in the Basic Multilingual Plane. This is exactly the set .NET's
 * `char.IsWhiteSpace` (and so `string.Trim()`) uses. JavaScript's `String.prototype.trim` differs: it also strips
 * U+FEFF and keeps U+0085, so the contracts never use it.
 */
export const WHITE_SPACE_CHARACTERS: readonly string[] = [
  '\t',
  '\n',
  '\v',
  '\f',
  '\r',
  ' ',
  '\u0085',
  '\u00a0',
  '\u1680',
  '\u2000',
  '\u2001',
  '\u2002',
  '\u2003',
  '\u2004',
  '\u2005',
  '\u2006',
  '\u2007',
  '\u2008',
  '\u2009',
  '\u200a',
  '\u2028',
  '\u2029',
  '\u202f',
  '\u205f',
  '\u3000',
];

const WHITE_SPACE = new Set(WHITE_SPACE_CHARACTERS);

/** True when `ch` (one UTF-16 code unit) is one of {@link WHITE_SPACE_CHARACTERS}. */
export function isWhiteSpace(ch: string): boolean {
  return WHITE_SPACE.has(ch);
}

/** Removes {@link WHITE_SPACE_CHARACTERS} from both ends, like .NET `string.Trim()`. */
export function trimWhiteSpace(value: string): string {
  let start = 0;
  let end = value.length;
  while (start < end && WHITE_SPACE.has(value.charAt(start))) {
    start++;
  }
  while (end > start && WHITE_SPACE.has(value.charAt(end - 1))) {
    end--;
  }
  return value.slice(start, end);
}

/** `sha256:` followed by 64 lowercase hex digits. */
export const ContentChecksum = z
  .string()
  .regex(/^sha256:[0-9a-f]{64}$/, 'Expected sha256: followed by 64 lowercase hex digits');
export type ContentChecksum = z.infer<typeof ContentChecksum>;

/**
 * The content checksum: `sha256:` + lowercase hex SHA-256 of the UTF-8 bytes (no BOM) of the canonical text.
 * Defined for every string: an unpaired surrogate is hashed as U+FFFD (as every UTF-8 encoder writes it) and
 * U+0000 as the byte 0x00. Only storable text ({@link isStorableText}) is ever synced, so equal checksums of synced
 * texts mean equal texts.
 */
export function contentChecksum(text: string): ContentChecksum {
  return `sha256:${createHash('sha256').update(canonicalizeText(text), 'utf8').digest('hex')}`;
}
