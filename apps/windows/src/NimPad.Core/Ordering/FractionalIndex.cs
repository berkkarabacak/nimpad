using System.Text;

namespace NimPad.Core.Ordering;

/// <summary>
/// Fractional indexing: sibling <c>position</c> keys that sort by ordinal string comparison
/// (architecture.md, "Sibling ordering").
/// </summary>
/// <remarks>
/// <para>
/// A faithful port of rocicorp/fractional-indexing 3.2.0 (CC0), itself based on David Greenspan's "Implementing
/// Fractional Indexing". The digit set is fixed to <see cref="Base62Digits"/>. Control flow, validation and error
/// messages follow the reference so that this class, the TypeScript port in <c>packages/contracts</c> and the
/// reference all produce identical keys.
/// </para>
/// <para>
/// A key is an "integer part" followed by an optional "fractional part". The integer part's head character encodes
/// its length: <c>'a'..'z'</c> mean 2..27 characters (positive integers) and <c>'A'..'Z'</c> mean 27..2 characters
/// (negative integers). The fractional part never ends with the zero digit <c>'0'</c>.
/// </para>
/// <para>
/// Deviations, both for inputs the reference does not handle meaningfully: a negative count passed to
/// <see cref="GenerateNKeysBetween"/> throws <see cref="ArgumentOutOfRangeException"/>, and a key whose characters
/// are outside the digit set may throw <see cref="FractionalIndexException"/> where the reference would produce a
/// key containing the text "undefined".
/// </para>
/// </remarks>
public static class FractionalIndex
{
    /// <summary>The digit alphabet, in ascending character-code order.</summary>
    public const string Base62Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private const string Digits = Base62Digits;
    private const char Zero = '0';
    private const char LargestDigit = 'z';

    /// <summary>The smallest integer part. Not a valid key on its own, because nothing could sort before it.</summary>
    private static readonly string SmallestInteger = "A" + new string(Zero, 26);

    /// <summary>Returns a key that sorts strictly between <paramref name="a"/> and <paramref name="b"/>.</summary>
    /// <param name="a">The key to sort after, or <see langword="null"/> for "the start".</param>
    /// <param name="b">The key to sort before, or <see langword="null"/> for "the end".</param>
    /// <exception cref="FractionalIndexException">
    /// A key is invalid or <c>a &gt;= b</c>, with the reference implementation's message.
    /// </exception>
    public static string GenerateKeyBetween(string? a, string? b)
    {
        if (a is not null)
        {
            ValidateOrderKey(a);
        }

        if (b is not null)
        {
            ValidateOrderKey(b);
        }

        if (a is not null && b is not null && Compare(a, b) >= 0)
        {
            throw new FractionalIndexException($"{a} >= {b}");
        }

        if (a is null)
        {
            if (b is null)
            {
                return "a" + Zero;
            }

            var ib = GetIntegerPart(b);
            var fb = b[ib.Length..];
            if (ib == SmallestInteger)
            {
                return ib + Midpoint(string.Empty, fb);
            }

            if (Compare(ib, b) < 0)
            {
                return ib;
            }

            return DecrementInteger(ib) ?? throw new FractionalIndexException("cannot decrement any more");
        }

        if (b is null)
        {
            var ia = GetIntegerPart(a);
            var fa = a[ia.Length..];
            var i = IncrementInteger(ia);
            return i ?? ia + Midpoint(fa, null);
        }

        {
            var ia = GetIntegerPart(a);
            var fa = a[ia.Length..];
            var ib = GetIntegerPart(b);
            var fb = b[ib.Length..];
            if (ia == ib)
            {
                return ia + Midpoint(fa, fb);
            }

            var i = IncrementInteger(ia) ?? throw new FractionalIndexException("cannot increment any more");
            if (Compare(i, b) < 0)
            {
                return i;
            }

            return ia + Midpoint(fa, null);
        }
    }

    /// <summary>
    /// Returns <paramref name="n"/> distinct keys in ascending order, all strictly between <paramref name="a"/> and
    /// <paramref name="b"/>.
    /// </summary>
    /// <remarks>
    /// With both bounds null the result is <c>a0, a1, …</c>; with one bound null it is consecutive integer keys;
    /// otherwise the keys are spread evenly by recursive bisection so they stay short.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="n"/> is negative.</exception>
    /// <exception cref="FractionalIndexException">Under the same conditions as <see cref="GenerateKeyBetween"/>.</exception>
    public static string[] GenerateNKeysBetween(string? a, string? b, int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (n == 0)
        {
            return [];
        }

        if (n == 1)
        {
            return [GenerateKeyBetween(a, b)];
        }

        if (b is null)
        {
            var c = GenerateKeyBetween(a, b);
            var result = new string[n];
            result[0] = c;
            for (var i = 1; i < n; i++)
            {
                c = GenerateKeyBetween(c, b);
                result[i] = c;
            }

            return result;
        }

        if (a is null)
        {
            var c = GenerateKeyBetween(a, b);
            var result = new string[n];
            result[n - 1] = c;
            for (var i = n - 2; i >= 0; i--)
            {
                c = GenerateKeyBetween(a, c);
                result[i] = c;
            }

            return result;
        }

        var mid = n / 2;
        var middle = GenerateKeyBetween(a, b);
        return [.. GenerateNKeysBetween(a, middle, mid), middle, .. GenerateNKeysBetween(middle, b, n - mid - 1)];
    }

    /// <summary>Ordinal (UTF-16 code unit) comparison: the sort order for position keys in every component.</summary>
    /// <returns>A negative number, zero or a positive number.</returns>
    public static int Compare(string a, string b) => string.CompareOrdinal(a, b);

    /// <summary>
    /// True when <paramref name="key"/> is a well-formed position key: only base-62 digits, and accepted by the
    /// reference validation (valid head, complete integer part, no trailing zero, not the bare smallest integer).
    /// </summary>
    /// <remarks>Stricter than <see cref="GenerateKeyBetween"/>, which (like the reference) does not check the character set.</remarks>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        foreach (var ch in key)
        {
            if (!char.IsAsciiLetterOrDigit(ch))
            {
                return false;
            }
        }

        try
        {
            ValidateOrderKey(key);
            return true;
        }
        catch (FractionalIndexException)
        {
            return false;
        }
    }

    // `a` may be the empty string; `b` is null or a non-empty string; `a < b` when `b` is non-null.
    // Neither may end with the zero digit.
    private static string Midpoint(string a, string? b)
    {
        if (b is not null && Compare(a, b) >= 0)
        {
            throw new FractionalIndexException($"{a} >= {b}");
        }

        if (EndsWithZero(a) || (!string.IsNullOrEmpty(b) && EndsWithZero(b)))
        {
            throw new FractionalIndexException("trailing zero");
        }

        if (!string.IsNullOrEmpty(b))
        {
            // Remove the longest common prefix, padding `a` with zeros as we go. `b` needs no padding because it
            // cannot end before `a` while traversing the common prefix.
            var n = 0;
            while (n < b.Length && (n < a.Length ? a[n] : Zero) == b[n])
            {
                n++;
            }

            if (n > 0)
            {
                return b[..n] + Midpoint(SliceFrom(a, n), b[n..]);
            }
        }

        // The first digits (or lack of a digit) differ.
        var digitA = a.Length > 0 ? Digits.IndexOf(a[0], StringComparison.Ordinal) : 0;
        var digitB = b is null
            ? Digits.Length
            : b.Length > 0 ? Digits.IndexOf(b[0], StringComparison.Ordinal) : -1;
        if (digitB - digitA > 1)
        {
            // JavaScript's Math.round rounds halves up; digitA + digitB is never negative here.
            var midDigit = (digitA + digitB + 1) / 2;
            return Digits[midDigit].ToString();
        }

        // The first digits are consecutive.
        if (b is not null && b.Length > 1)
        {
            return b[..1];
        }

        // `b` is null or a single digit. The first digit of `a` is the digit before `b` (or the largest digit when
        // `b` is null), so keep it and recurse: Midpoint("49", "5") is "4" + Midpoint("9", null) = "495".
        if (digitA < 0)
        {
            throw new FractionalIndexException($"invalid order key: {a}");
        }

        return Digits[digitA] + Midpoint(SliceFrom(a, 1), null);
    }

    private static void ValidateInteger(string integer)
    {
        if (integer.Length != GetIntegerLength(integer.Length > 0 ? integer[0] : null))
        {
            throw new FractionalIndexException($"invalid integer part of order key: {integer}");
        }
    }

    private static int GetIntegerLength(char? head)
    {
        if (head is >= 'a' and <= 'z')
        {
            return head.Value - 'a' + 2;
        }

        if (head is >= 'A' and <= 'Z')
        {
            return 'Z' - head.Value + 2;
        }

        // The reference interpolates a missing head as "undefined".
        throw new FractionalIndexException($"invalid order key head: {(head is null ? "undefined" : head.Value.ToString())}");
    }

    private static string GetIntegerPart(string key)
    {
        var integerPartLength = GetIntegerLength(key.Length > 0 ? key[0] : null);
        if (integerPartLength > key.Length)
        {
            throw new FractionalIndexException($"invalid order key: {key}");
        }

        return key[..integerPartLength];
    }

    private static void ValidateOrderKey(string key)
    {
        if (key == SmallestInteger)
        {
            throw new FractionalIndexException($"invalid order key: {key}");
        }

        // GetIntegerPart throws if the head is invalid or the key is too short.
        var i = GetIntegerPart(key);
        var f = key[i.Length..];
        if (EndsWithZero(f))
        {
            throw new FractionalIndexException($"invalid order key: {key}");
        }
    }

    // Returns null when `x` is already the largest integer.
    private static string? IncrementInteger(string x)
    {
        ValidateInteger(x);
        var head = x[0];
        var digs = new StringBuilder(x, 1, x.Length - 1, x.Length + 1);
        var carry = true;
        for (var i = digs.Length - 1; carry && i >= 0; i--)
        {
            var d = Digits.IndexOf(digs[i], StringComparison.Ordinal) + 1;
            if (d == Digits.Length)
            {
                digs[i] = Zero;
            }
            else
            {
                digs[i] = Digits[d];
                carry = false;
            }
        }

        if (!carry)
        {
            return head + digs.ToString();
        }

        if (head == 'Z')
        {
            return "a" + Zero;
        }

        if (head == 'z')
        {
            return null;
        }

        var h = (char)(head + 1);
        if (h > 'a')
        {
            digs.Append(Zero);
        }
        else if (digs.Length > 0)
        {
            digs.Length--;
        }

        return h + digs.ToString();
    }

    // Returns null when `x` is already the smallest integer.
    private static string? DecrementInteger(string x)
    {
        ValidateInteger(x);
        var head = x[0];
        var digs = new StringBuilder(x, 1, x.Length - 1, x.Length + 1);
        var borrow = true;
        for (var i = digs.Length - 1; borrow && i >= 0; i--)
        {
            var d = Digits.IndexOf(digs[i], StringComparison.Ordinal) - 1;
            if (d == -1)
            {
                digs[i] = LargestDigit;
            }
            else
            {
                digs[i] = Digits[d];
                borrow = false;
            }
        }

        if (!borrow)
        {
            return head + digs.ToString();
        }

        if (head == 'a')
        {
            return "Z" + LargestDigit;
        }

        if (head == 'A')
        {
            return null;
        }

        var h = (char)(head - 1);
        if (h < 'Z')
        {
            digs.Append(LargestDigit);
        }
        else if (digs.Length > 0)
        {
            digs.Length--;
        }

        return h + digs.ToString();
    }

    private static bool EndsWithZero(string s) => s.Length > 0 && s[^1] == Zero;

    private static string SliceFrom(string s, int start) => start >= s.Length ? string.Empty : s[start..];
}

/// <summary>Thrown when a fractional-index key is invalid or no key exists between two bounds.</summary>
/// <param name="message">The reference implementation's error message, e.g. <c>a1 &gt;= a0</c>.</param>
public sealed class FractionalIndexException(string message) : ArgumentException(message);
