using System.Collections.ObjectModel;

namespace NimPad.Presentation.Infrastructure;

/// <summary>
/// Brings an <see cref="ObservableCollection{T}"/> to a desired list with single-item changes (WPF's CollectionView
/// rejects range changes), reusing the objects already in it (compared by reference) so that list items keep their
/// containers, scroll position and selection.
/// </summary>
/// <remarks>
/// Items that are no longer wanted are removed, the fewest items possible are moved (every item outside one longest
/// run of items already in the right relative order), and new items are inserted. One item that moved any distance
/// costs one <c>Move</c>.
/// </remarks>
internal static class CollectionReconciler
{
    /// <summary>
    /// Makes <paramref name="items"/> equal to <paramref name="desired"/> unless that takes more than
    /// <paramref name="maxMoves"/> moves; then nothing is changed.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when more than <paramref name="maxMoves"/> moves are needed (the caller replaces the
    /// whole list instead).
    /// </returns>
    public static bool TryReconcile<T>(ObservableCollection<T> items, IReadOnlyList<T> desired, int maxMoves)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(desired);

        // Most updates touch a few rows: leave the common start and end alone (cheap reference checks).
        var shorter = Math.Min(items.Count, desired.Count);
        var prefix = 0;
        while (prefix < shorter && ReferenceEquals(items[prefix], desired[prefix]))
        {
            prefix++;
        }

        if (prefix == items.Count && prefix == desired.Count)
        {
            return true;
        }

        var suffix = 0;
        while (suffix < shorter - prefix && ReferenceEquals(items[items.Count - 1 - suffix], desired[desired.Count - 1 - suffix]))
        {
            suffix++;
        }

        var itemsEnd = items.Count - suffix;
        var desiredEnd = desired.Count - suffix;
        var desiredIndex = new Dictionary<T, int>(desiredEnd - prefix, ReferenceEqualityComparer.Instance);
        for (var i = prefix; i < desiredEnd; i++)
        {
            desiredIndex.TryAdd(desired[i], i);
        }

        // The middle items that stay, in their current order, as positions in `desired`.
        var kept = new List<T>(itemsEnd - prefix);
        var keptPositions = new List<int>(itemsEnd - prefix);
        for (var i = prefix; i < itemsEnd; i++)
        {
            if (desiredIndex.TryGetValue(items[i], out var position))
            {
                kept.Add(items[i]);
                keptPositions.Add(position);
            }
        }

        var inOrder = LongestIncreasingRun(keptPositions);
        if (kept.Count - inOrder.Count > maxMoves)
        {
            return false;
        }

        for (var i = itemsEnd - 1; i >= prefix; i--)
        {
            if (!desiredIndex.ContainsKey(items[i]))
            {
                items.RemoveAt(i);
            }
        }

        // Move every item outside the run right after its predecessor among the kept items (in desired order). By
        // induction the kept items before it are already in their relative order, so each needs exactly one move.
        var stays = new HashSet<T>(inOrder.Select(index => kept[index]), ReferenceEqualityComparer.Instance);
        var keptInDesiredOrder = kept.OrderBy(item => desiredIndex[item]).ToList();
        for (var t = 0; t < keptInDesiredOrder.Count; t++)
        {
            var item = keptInDesiredOrder[t];
            if (stays.Contains(item))
            {
                continue;
            }

            var from = IndexOf(items, item, prefix);
            int to;
            if (t == 0)
            {
                to = prefix;
            }
            else
            {
                var predecessor = IndexOf(items, keptInDesiredOrder[t - 1], prefix);
                to = from > predecessor ? predecessor + 1 : predecessor;
            }

            if (from != to)
            {
                items.Move(from, to);
            }
        }

        // The middle now holds the kept items in desired order; insert the new ones where they belong.
        for (var i = prefix; i < desiredEnd; i++)
        {
            if (i >= items.Count || !ReferenceEquals(items[i], desired[i]))
            {
                items.Insert(i, desired[i]);
            }
        }

        return true;
    }

    // The indexes (into `values`) of one longest strictly increasing subsequence, in order (patience sorting).
    internal static List<int> LongestIncreasingRun(IReadOnlyList<int> values)
    {
        var tails = new List<int>();
        var previous = new int[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (values[tails[mid]] < value)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            previous[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
            {
                tails.Add(i);
            }
            else
            {
                tails[lo] = i;
            }
        }

        var run = new List<int>(tails.Count);
        for (var i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i])
        {
            run.Add(i);
        }

        run.Reverse();
        return run;
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item, int start)
        where T : class
    {
        for (var i = start; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
