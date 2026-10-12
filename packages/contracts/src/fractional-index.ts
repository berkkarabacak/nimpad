// Fractional indexing: sibling `position` keys that sort by ordinal string comparison.
//
// A faithful port of rocicorp/fractional-indexing 3.2.0 (CC0), itself based on David Greenspan's
// "Implementing Fractional Indexing" (https://observablehq.com/@dgreensp/implementing-fractional-indexing).
// The digit set is fixed to BASE_62_DIGITS. Control flow, validation and error messages follow the reference so
// that this module, the C# port in NimPad.Core and the reference all produce identical keys; the shared cases
// live in fixtures/fractional-index.json.
//
// A key is an "integer part" followed by an optional "fractional part":
// - The integer part's head character encodes its length: 'a'..'z' mean 2..27 characters (positive integers),
//   'A'..'Z' mean 27..2 characters (negative integers).
// - The fractional part never ends with the zero digit '0'.

/** The digit alphabet, in ascending character-code order. */
export const BASE_62_DIGITS = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';

const DIGITS = BASE_62_DIGITS;
const ZERO = '0';
/** The smallest integer part. It is not a valid key on its own, because nothing could sort before it. */
const SMALLEST_INTEGER = `A${ZERO.repeat(26)}`;

// `a` may be the empty string; `b` is null or a non-empty string; `a < b` when `b` is non-null.
// Neither may end with the zero digit.
function midpoint(a: string, b: string | null): string {
  if (b != null && a >= b) {
    throw new Error(`${a} >= ${b}`);
  }
  if (a.slice(-1) === ZERO || (b && b.slice(-1) === ZERO)) {
    throw new Error('trailing zero');
  }
  if (b) {
    // Remove the longest common prefix, padding `a` with zeros as we go. `b` needs no padding because it cannot
    // end before `a` while traversing the common prefix.
    let n = 0;
    while ((a[n] || ZERO) === b[n]) {
      n++;
    }
    if (n > 0) {
      return b.slice(0, n) + midpoint(a.slice(n), b.slice(n));
    }
  }
  // The first digits (or lack of a digit) differ.
  const digitA = a ? DIGITS.indexOf(a[0] as string) : 0;
  const digitB = b != null ? DIGITS.indexOf(b[0] as string) : DIGITS.length;
  if (digitB - digitA > 1) {
    const midDigit = Math.round(0.5 * (digitA + digitB));
    return DIGITS[midDigit] as string;
  }
  // The first digits are consecutive.
  if (b && b.length > 1) {
    return b.slice(0, 1);
  }
  // `b` is null or a single digit. The first digit of `a` is the digit before `b` (or the largest digit when `b`
  // is null), so keep it and recurse: midpoint('49', '5') is '4' + midpoint('9', null) = '495'.
  return (DIGITS[digitA] as string) + midpoint(a.slice(1), null);
}

function validateInteger(int: string): void {
  if (int.length !== getIntegerLength(int[0])) {
    throw new Error(`invalid integer part of order key: ${int}`);
  }
}

function getIntegerLength(head: string | undefined): number {
  if (head !== undefined && head >= 'a' && head <= 'z') {
    return head.charCodeAt(0) - 'a'.charCodeAt(0) + 2;
  }
  if (head !== undefined && head >= 'A' && head <= 'Z') {
    return 'Z'.charCodeAt(0) - head.charCodeAt(0) + 2;
  }
  throw new Error(`invalid order key head: ${head}`);
}

function getIntegerPart(key: string): string {
  const integerPartLength = getIntegerLength(key[0]);
  if (integerPartLength > key.length) {
    throw new Error(`invalid order key: ${key}`);
  }
  return key.slice(0, integerPartLength);
}

function validateOrderKey(key: string): void {
  if (key === SMALLEST_INTEGER) {
    throw new Error(`invalid order key: ${key}`);
  }
  // getIntegerPart throws if the head is invalid or the key is too short.
  const i = getIntegerPart(key);
  const f = key.slice(i.length);
  if (f.slice(-1) === ZERO) {
    throw new Error(`invalid order key: ${key}`);
  }
}

// Returns null when `x` is already the largest integer.
function incrementInteger(x: string): string | null {
  validateInteger(x);
  const head = x[0] as string;
  const digs = x.slice(1).split('');
  let carry = true;
  for (let i = digs.length - 1; carry && i >= 0; i--) {
    const d = DIGITS.indexOf(digs[i] as string) + 1;
    if (d === DIGITS.length) {
      digs[i] = ZERO;
    } else {
      digs[i] = DIGITS[d] as string;
      carry = false;
    }
  }
  if (carry) {
    if (head === 'Z') {
      return `a${ZERO}`;
    }
    if (head === 'z') {
      return null;
    }
    const h = String.fromCharCode(head.charCodeAt(0) + 1);
    if (h > 'a') {
      digs.push(ZERO);
    } else {
      digs.pop();
    }
    return h + digs.join('');
  }
  return head + digs.join('');
}

// Returns null when `x` is already the smallest integer.
function decrementInteger(x: string): string | null {
  validateInteger(x);
  const head = x[0] as string;
  const digs = x.slice(1).split('');
  const largestDigit = DIGITS.slice(-1);
  let borrow = true;
  for (let i = digs.length - 1; borrow && i >= 0; i--) {
    const d = DIGITS.indexOf(digs[i] as string) - 1;
    if (d === -1) {
      digs[i] = largestDigit;
    } else {
      digs[i] = DIGITS[d] as string;
      borrow = false;
    }
  }
  if (borrow) {
    if (head === 'a') {
      return `Z${largestDigit}`;
    }
    if (head === 'A') {
      return null;
    }
    const h = String.fromCharCode(head.charCodeAt(0) - 1);
    if (h < 'Z') {
      digs.push(largestDigit);
    } else {
      digs.pop();
    }
    return h + digs.join('');
  }
  return head + digs.join('');
}

/**
 * Returns a key that sorts strictly between `a` and `b`.
 *
 * @param a The key to sort after, or null for "the start".
 * @param b The key to sort before, or null for "the end".
 * @throws Error when a key is invalid or `a >= b`, exactly as the reference implementation does.
 */
export function generateKeyBetween(a: string | null | undefined, b: string | null | undefined): string {
  if (a != null) {
    validateOrderKey(a);
  }
  if (b != null) {
    validateOrderKey(b);
  }
  if (a != null && b != null && a >= b) {
    throw new Error(`${a} >= ${b}`);
  }
  if (a == null) {
    if (b == null) {
      return `a${ZERO}`;
    }
    const ib = getIntegerPart(b);
    const fb = b.slice(ib.length);
    if (ib === SMALLEST_INTEGER) {
      return ib + midpoint('', fb);
    }
    if (ib < b) {
      return ib;
    }
    const res = decrementInteger(ib);
    if (res == null) {
      throw new Error('cannot decrement any more');
    }
    return res;
  }

  if (b == null) {
    const ia = getIntegerPart(a);
    const fa = a.slice(ia.length);
    const i = incrementInteger(ia);
    return i == null ? ia + midpoint(fa, null) : i;
  }

  const ia = getIntegerPart(a);
  const fa = a.slice(ia.length);
  const ib = getIntegerPart(b);
  const fb = b.slice(ib.length);
  if (ia === ib) {
    return ia + midpoint(fa, fb);
  }
  const i = incrementInteger(ia);
  if (i == null) {
    throw new Error('cannot increment any more');
  }
  if (i < b) {
    return i;
  }
  return ia + midpoint(fa, null);
}

/**
 * Returns `n` distinct keys in ascending order, all strictly between `a` and `b`.
 *
 * With both bounds null the result is `a0, a1, …`; with one bound null it is consecutive integer keys; otherwise
 * the keys are spread evenly (by recursive bisection) so they stay short.
 *
 * @throws Error under the same conditions as {@link generateKeyBetween}.
 */
export function generateNKeysBetween(
  a: string | null | undefined,
  b: string | null | undefined,
  n: number,
): string[] {
  if (n === 0) {
    return [];
  }
  if (n === 1) {
    return [generateKeyBetween(a, b)];
  }
  if (b == null) {
    let c = generateKeyBetween(a, b);
    const result = [c];
    for (let i = 0; i < n - 1; i++) {
      c = generateKeyBetween(c, b);
      result.push(c);
    }
    return result;
  }
  if (a == null) {
    let c = generateKeyBetween(a, b);
    const result = [c];
    for (let i = 0; i < n - 1; i++) {
      c = generateKeyBetween(a, c);
      result.push(c);
    }
    result.reverse();
    return result;
  }
  const mid = Math.floor(n / 2);
  const c = generateKeyBetween(a, b);
  return [...generateNKeysBetween(a, c, mid), c, ...generateNKeysBetween(c, b, n - mid - 1)];
}

/** Ordinal (UTF-16 code unit) comparison, the sort order for `position` keys in every component. */
export function compareKeys(a: string, b: string): -1 | 0 | 1 {
  if (a < b) {
    return -1;
  }
  return a > b ? 1 : 0;
}

/** Sibling order: by `position`, ties broken by `id`, both compared ordinally. */
export function compareSiblings(
  x: { readonly position: string; readonly id: string },
  y: { readonly position: string; readonly id: string },
): -1 | 0 | 1 {
  return compareKeys(x.position, y.position) || compareKeys(x.id, y.id);
}

const BASE_62_KEY = /^[0-9A-Za-z]+$/;

/**
 * True when `key` is a well-formed position key: only base-62 digits, and accepted by the reference validation
 * (valid head, complete integer part, no trailing zero, not the bare smallest integer).
 *
 * This is stricter than {@link generateKeyBetween}, which (like the reference) does not check the character set.
 */
export function isValidOrderKey(key: string): boolean {
  if (!BASE_62_KEY.test(key)) {
    return false;
  }
  try {
    validateOrderKey(key);
    return true;
  } catch {
    return false;
  }
}
