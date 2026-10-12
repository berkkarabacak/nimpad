import { randomBytes } from 'node:crypto';
import { z } from 'zod';

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/**
 * A UUID in canonical form: lowercase, hyphenated. Any version is accepted on input (for example auth subjects);
 * IDs that NimPad creates are version 7 (see {@link isUuidV7}).
 */
export const Uuid = z.string().regex(UUID_PATTERN, 'Expected a lowercase hyphenated UUID');
export type Uuid = z.infer<typeof Uuid>;

/** True when `value` is a canonical UUID with version 7 and the RFC 9562 variant. */
export function isUuidV7(value: string): boolean {
  return UUID_PATTERN.test(value) && value[14] === '7' && '89ab'.includes(value[19] as string);
}

/** A canonical UUID that is also version 7: the form of every ID NimPad itself generates. */
export const UuidV7 = Uuid.refine(isUuidV7, 'Expected a version 7 UUID');
export type UuidV7 = z.infer<typeof UuidV7>;

const MAX_UUID_V7_TIMESTAMP = 2 ** 48 - 1;

/**
 * Creates a UUID version 7: a 48-bit Unix millisecond timestamp followed by 74 random bits, so IDs created later
 * sort later (IDs created within the same millisecond are in random order).
 *
 * @param timestampMs Unix time in milliseconds; defaults to now.
 */
export function generateUuidV7(timestampMs: number = Date.now()): string {
  if (!Number.isInteger(timestampMs) || timestampMs < 0 || timestampMs > MAX_UUID_V7_TIMESTAMP) {
    throw new RangeError(`UUIDv7 timestamp must be an integer in [0, 2^48): ${timestampMs}`);
  }
  const bytes = randomBytes(16);
  let t = timestampMs;
  for (let i = 5; i >= 0; i--) {
    bytes[i] = t % 256;
    t = Math.floor(t / 256);
  }
  bytes[6] = ((bytes[6] as number) & 0x0f) | 0x70;
  bytes[8] = ((bytes[8] as number) & 0x3f) | 0x80;
  const hex = bytes.toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
