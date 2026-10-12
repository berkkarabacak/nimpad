import { describe, expect, it } from 'vitest';
import {
  BASE_62_DIGITS,
  compareKeys,
  compareSiblings,
  generateKeyBetween,
  generateNKeysBetween,
  isValidOrderKey,
} from '../src/fractional-index.ts';
import { fractionalIndexFixture } from './fixtures.ts';

/** Same seeded generator as the fixture builder, so failures are reproducible. */
function lcg(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    return state / 2 ** 32;
  };
}

describe('generateKeyBetween', () => {
  // Published examples from the reference implementation's own test-suite (rocicorp/fractional-indexing 3.2.0,
  // src/index.test.js), checked here independently of the generated fixture file.
  it.each([
    [null, null, 'a0'],
    [null, 'a0', 'Zz'],
    [null, 'Zz', 'Zy'],
    ['a0', null, 'a1'],
    ['a1', null, 'a2'],
    ['a0', 'a1', 'a0V'],
    ['a1', 'a2', 'a1V'],
    ['a0V', 'a1', 'a0l'],
    ['Zz', 'a0', 'ZzV'],
    ['Zz', 'a1', 'a0'],
    [null, 'Y00', 'Xzzz'],
    ['bzz', null, 'c000'],
    ['a0', 'a0V', 'a0G'],
    ['a0', 'a0G', 'a08'],
    ['b125', 'b129', 'b127'],
    ['a0', 'a1V', 'a1'],
    ['Zz', 'a01', 'a0'],
    [null, 'a0V', 'a0'],
    [null, 'b999', 'b99'],
    [null, 'A000000000000000000000000001', 'A000000000000000000000000000V'],
    ['zzzzzzzzzzzzzzzzzzzzzzzzzzy', null, 'zzzzzzzzzzzzzzzzzzzzzzzzzzz'],
    ['zzzzzzzzzzzzzzzzzzzzzzzzzzz', null, 'zzzzzzzzzzzzzzzzzzzzzzzzzzzV'],
  ])('reference example (%s, %s) -> %s', (a, b, expected) => {
    expect(generateKeyBetween(a, b)).toBe(expected);
  });

  it.each([
    [null, 'A00000000000000000000000000', 'invalid order key: A00000000000000000000000000'],
    ['a00', null, 'invalid order key: a00'],
    ['a00', 'a1', 'invalid order key: a00'],
    ['0', '1', 'invalid order key head: 0'],
    ['a1', 'a0', 'a1 >= a0'],
  ])('reference error (%s, %s) throws "%s"', (a, b, message) => {
    expect(() => generateKeyBetween(a, b)).toThrow(new Error(message));
  });

  it('treats undefined like null, as the reference does', () => {
    expect(generateKeyBetween(undefined, undefined)).toBe('a0');
    expect(generateKeyBetween('a0', undefined)).toBe('a1');
    expect(generateKeyBetween(undefined, 'a0')).toBe('Zz');
  });

  it('rounds the middle digit half up, not to even', () => {
    // Digits 0 and 5 average to 2.5: half-up gives '3', banker's rounding would give '2'.
    expect(generateKeyBetween('a0', 'a05')).toBe('a03');
    // Digits 2 and 7 average to 4.5: half-up gives '5', banker's rounding would give '4'.
    expect(generateKeyBetween('a02', 'a07')).toBe('a05');
  });

  it('keeps a random edit session sorted, valid and strictly between neighbours', () => {
    const random = lcg(42);
    const list: string[] = [];
    for (let i = 0; i < 2000; i++) {
      const index = Math.floor(random() * (list.length + 1));
      const before = list[index - 1] ?? null;
      const after = list[index] ?? null;
      const key = generateKeyBetween(before, after);
      expect(isValidOrderKey(key)).toBe(true);
      if (before !== null) {
        expect(compareKeys(before, key)).toBe(-1);
      }
      if (after !== null) {
        expect(compareKeys(key, after)).toBe(-1);
      }
      list.splice(index, 0, key);
    }
    expect([...list].sort(compareKeys)).toEqual(list);
    expect(new Set(list).size).toBe(list.length);
  });

  it('keeps keys short when always appending or prepending', () => {
    let last: string | null = null;
    let first: string | null = null;
    for (let i = 0; i < 5000; i++) {
      last = generateKeyBetween(last, null);
      first = generateKeyBetween(null, first);
    }
    // 62 one-digit integers ('a0'..'az'), then 62^2 two-digit ones ('b00'..'bzz'), then three digits.
    expect(last).toMatch(/^c[0-9A-Za-z]{3}$/);
    expect(first).toMatch(/^X[0-9A-Za-z]{3}$/);
  });
});

describe('generateNKeysBetween', () => {
  it.each([
    [null, null, 5, 'a0 a1 a2 a3 a4'],
    ['a4', null, 10, 'a5 a6 a7 a8 a9 aA aB aC aD aE'],
    [null, 'a0', 5, 'Zv Zw Zx Zy Zz'],
    ['a0', 'a2', 20, 'a04 a08 a0G a0K a0O a0V a0Z a0d a0l a0t a1 a14 a18 a1G a1O a1V a1Z a1d a1l a1t'],
  ])('reference example (%s, %s, %i)', (a, b, n, expected) => {
    expect(generateNKeysBetween(a, b, n).join(' ')).toBe(expected);
  });

  it('returns an empty list for n = 0 without validating anything else', () => {
    expect(generateNKeysBetween('a1', 'a0', 0)).toEqual([]);
  });

  it('throws like generateKeyBetween for invalid bounds', () => {
    expect(() => generateNKeysBetween('a1', 'a0', 3)).toThrow('a1 >= a0');
    expect(() => generateNKeysBetween('a00', null, 2)).toThrow('invalid order key: a00');
  });

  it('returns n sorted distinct keys strictly between random bounds', () => {
    const random = lcg(7);
    for (let round = 0; round < 200; round++) {
      const pair = generateNKeysBetween(null, null, 2 + Math.floor(random() * 50));
      const a = random() < 0.2 ? null : (pair[0] as string);
      const b = random() < 0.2 ? null : (pair[pair.length - 1] as string);
      const n = Math.floor(random() * 40);
      const keys = generateNKeysBetween(a, b, n);
      expect(keys).toHaveLength(n);
      const bounded = [...(a === null ? [] : [a]), ...keys, ...(b === null ? [] : [b])];
      for (let i = 1; i < bounded.length; i++) {
        expect(compareKeys(bounded[i - 1] as string, bounded[i] as string)).toBe(-1);
      }
      for (const key of keys) {
        expect(isValidOrderKey(key)).toBe(true);
      }
    }
  });
});

describe('fixture parity (fixtures/fractional-index.json)', () => {
  const { between, nKeys, invalid } = fractionalIndexFixture;

  it('uses the architecture digit set', () => {
    expect(fractionalIndexFixture.digits).toBe(BASE_62_DIGITS);
    expect(BASE_62_DIGITS).toBe('0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz');
  });

  it('has the required coverage', () => {
    expect(between.length).toBeGreaterThanOrEqual(60);
    expect(between).toContainEqual({ a: null, b: null, expected: 'a0' });
    // Append and prepend chains of 30+ keys each.
    expect(between.filter((c) => c.a !== null && c.b === null).length).toBeGreaterThanOrEqual(30);
    expect(between.filter((c) => c.a === null && c.b !== null).length).toBeGreaterThanOrEqual(30);
    // Integer-part boundaries.
    for (const key of ['Zz', 'a0', 'az', 'b00']) {
      expect(between.some((c) => c.a === key || c.b === key || c.expected === key)).toBe(true);
    }
    // Long fractional parts.
    expect(between.some((c) => c.expected.length >= 12)).toBe(true);
    expect(nKeys.length).toBeGreaterThanOrEqual(5);
    expect(invalid.length).toBeGreaterThanOrEqual(5);
  });

  it.each(between)('between($a, $b) = $expected', ({ a, b, expected }) => {
    expect(generateKeyBetween(a, b)).toBe(expected);
    expect(isValidOrderKey(expected)).toBe(true);
    if (a !== null) {
      expect(compareKeys(a, expected)).toBe(-1);
    }
    if (b !== null) {
      expect(compareKeys(expected, b)).toBe(-1);
    }
  });

  it.each(nKeys)('nKeys($a, $b, $n)', ({ a, b, n, expected }) => {
    expect(generateNKeysBetween(a, b, n)).toEqual(expected);
  });

  it.each(invalid)('invalid($a, $b) throws "$message"', ({ a, b, error, message }) => {
    expect(error).toBe(true);
    expect(() => generateKeyBetween(a, b)).toThrow(new Error(message));
  });
});

describe('compareKeys and compareSiblings', () => {
  it('compares ordinally (uppercase before lowercase, shorter prefix first)', () => {
    expect(compareKeys('Zz', 'a0')).toBe(-1);
    expect(compareKeys('a0', 'a0V')).toBe(-1);
    expect(compareKeys('a0V', 'a1')).toBe(-1);
    expect(compareKeys('a1', 'a1')).toBe(0);
    expect(compareKeys('b00', 'az')).toBe(1);
    // Culture-aware comparison would put 'a' before 'B'; ordinal comparison does not.
    expect(['a0', 'B000'].sort(compareKeys)).toEqual(['B000', 'a0']);
  });

  it('breaks position ties by id', () => {
    const x = { position: 'a0', id: '01a13069-8113-7d4b-b45c-a607c0eb8f23' };
    const y = { position: 'a0', id: '01a13069-84fb-75e2-ab6f-dba1d5a80d3e' };
    const z = { position: 'Zz', id: 'ffffffff-ffff-7fff-bfff-ffffffffffff' };
    expect([y, x, z].sort(compareSiblings)).toEqual([z, x, y]);
    expect(compareSiblings(x, x)).toBe(0);
  });
});

describe('isValidOrderKey', () => {
  it.each([
    'a0',
    'a1',
    'Zz',
    'a0V',
    'b00',
    'Xzzz',
    'zzzzzzzzzzzzzzzzzzzzzzzzzzzV',
    'A000000000000000000000000000V',
  ])('accepts %s', (key) => {
    expect(isValidOrderKey(key)).toBe(true);
  });

  it.each(['', 'a', 'a00', 'a10', 'b0', '0', 'A00000000000000000000000000', 'a0!', 'a0 ', 'a0é'])(
    'rejects %j',
    (key) => {
      expect(isValidOrderKey(key)).toBe(false);
    },
  );
});
