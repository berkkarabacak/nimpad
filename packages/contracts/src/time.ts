import { z } from 'zod';

const ISO_TIMESTAMP_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;

/** True when `value` is a real UTC instant written as `YYYY-MM-DDTHH:mm:ss.sssZ`. */
export function isIsoTimestamp(value: string): boolean {
  if (!ISO_TIMESTAMP_PATTERN.test(value)) {
    return false;
  }
  const ms = Date.parse(value);
  // Round-tripping rejects calendar-invalid values such as 2026-02-30 or 24:00.
  return Number.isFinite(ms) && new Date(ms).toISOString() === value;
}

/** A UTC timestamp with millisecond precision and a `Z` suffix, e.g. `2026-10-12T13:59:43.123Z`. */
export const IsoTimestamp = z
  .string()
  .refine(isIsoTimestamp, 'Expected a UTC timestamp like 2026-10-12T13:59:43.123Z');
export type IsoTimestamp = z.infer<typeof IsoTimestamp>;

/** Formats an instant as an {@link IsoTimestamp}. */
export function toIsoTimestamp(instant: Date | number): IsoTimestamp {
  const value = new Date(instant).toISOString();
  if (!isIsoTimestamp(value)) {
    throw new RangeError(`Instant is outside the four-digit-year range: ${value}`);
  }
  return value;
}
