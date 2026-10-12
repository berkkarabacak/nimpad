import { describe, expect, it } from 'vitest';
import { generateUuidV7, isUuidV7, Uuid, UuidV7 } from '../src/ids.ts';
import { IsoTimestamp, isIsoTimestamp, toIsoTimestamp } from '../src/time.ts';

describe('Uuid', () => {
  it.each([
    '01a13069-8113-7d4b-b45c-a607c0eb8f23',
    // Any version is accepted on input (for example a v4 auth subject).
    '3f0c2a4e-9b1d-4c6e-8f7a-2b5d9e1c0a34',
    '00000000-0000-0000-0000-000000000000',
  ])('accepts %s', (value) => {
    expect(Uuid.safeParse(value).success).toBe(true);
  });

  it.each([
    '01A13069-8113-7D4B-B45C-A607C0EB8F23',
    '{01a13069-8113-7d4b-b45c-a607c0eb8f23}',
    '01a1306981137d4bb45ca607c0eb8f23',
    '01a13069-8113-7d4b-b45c-a607c0eb8f2',
    '01a13069-8113-7d4b-b45c-a607c0eb8f23 ',
    'g1a13069-8113-7d4b-b45c-a607c0eb8f23',
  ])('rejects %j', (value) => {
    expect(Uuid.safeParse(value).success).toBe(false);
  });
});

describe('isUuidV7 / UuidV7', () => {
  it('requires version 7 and the RFC 9562 variant', () => {
    expect(isUuidV7('01a13069-8113-7d4b-b45c-a607c0eb8f23')).toBe(true);
    expect(isUuidV7('01a13069-8113-7d4b-845c-a607c0eb8f23')).toBe(true);
    expect(isUuidV7('3f0c2a4e-9b1d-4c6e-8f7a-2b5d9e1c0a34')).toBe(false);
    expect(isUuidV7('01a13069-8113-7d4b-c45c-a607c0eb8f23')).toBe(false);
    expect(isUuidV7('01A13069-8113-7D4B-B45C-A607C0EB8F23')).toBe(false);
    expect(UuidV7.safeParse('3f0c2a4e-9b1d-4c6e-8f7a-2b5d9e1c0a34').success).toBe(false);
    expect(UuidV7.safeParse('01a13069-8113-7d4b-b45c-a607c0eb8f23').success).toBe(true);
  });
});

describe('generateUuidV7', () => {
  it('creates canonical v7 UUIDs carrying the timestamp', () => {
    const ms = Date.parse('2026-10-12T13:59:43.123Z');
    const id = generateUuidV7(ms);
    expect(isUuidV7(id)).toBe(true);
    expect(Uuid.parse(id)).toBe(id);
    expect(Number.parseInt(id.slice(0, 8) + id.slice(9, 13), 16)).toBe(ms);
  });

  it('sorts by creation time across milliseconds', () => {
    const ids = [0, 1, 1000, 1_700_000_000_000, 2 ** 48 - 1].map((ms) => generateUuidV7(ms));
    expect([...ids].sort()).toEqual(ids);
  });

  it('is random within a millisecond', () => {
    const ids = new Set(Array.from({ length: 1000 }, () => generateUuidV7(1_700_000_000_000)));
    expect(ids.size).toBe(1000);
  });

  it.each([-1, 1.5, 2 ** 48, Number.NaN])('rejects timestamp %s', (ms) => {
    expect(() => generateUuidV7(ms)).toThrow(RangeError);
  });
});

describe('IsoTimestamp', () => {
  it.each(['2026-10-12T13:59:43.123Z', '1970-01-01T00:00:00.000Z', '2028-02-29T23:59:59.999Z'])(
    'accepts %s',
    (value) => {
      expect(isIsoTimestamp(value)).toBe(true);
      expect(IsoTimestamp.safeParse(value).success).toBe(true);
    },
  );

  it.each([
    '2026-10-12T13:59:43Z',
    '2026-10-12T13:59:43.1234Z',
    '2026-10-12T13:59:43.123+00:00',
    '2026-10-12T13:59:43.123z',
    '2026-10-12 13:59:43.123Z',
    '2026-02-30T00:00:00.000Z',
    '2026-10-12T24:00:00.000Z',
    '2027-02-29T00:00:00.000Z',
    '',
  ])('rejects %j', (value) => {
    expect(isIsoTimestamp(value)).toBe(false);
    expect(IsoTimestamp.safeParse(value).success).toBe(false);
  });

  it('formats instants', () => {
    expect(toIsoTimestamp(0)).toBe('1970-01-01T00:00:00.000Z');
    expect(toIsoTimestamp(new Date('2026-10-12T15:59:43.123+02:00'))).toBe('2026-10-12T13:59:43.123Z');
    expect(() => toIsoTimestamp(Date.UTC(10000, 0, 1))).toThrow(RangeError);
  });
});
