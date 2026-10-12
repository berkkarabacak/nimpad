using System.Collections.ObjectModel;
using System.Collections.Specialized;
using NimPad.Presentation.Infrastructure;
using Xunit;

namespace NimPad.Presentation.Tests.Infrastructure;

public sealed class CollectionReconcilerTests
{
    private sealed record Item(int Id);

    private static (List<NotifyCollectionChangedEventArgs> Changes, bool Reconciled) Reconcile(
        ObservableCollection<Item> items,
        IReadOnlyList<Item> desired,
        int maxMoves = int.MaxValue)
    {
        var changes = new List<NotifyCollectionChangedEventArgs>();
        items.CollectionChanged += (_, e) => changes.Add(e);
        var reconciled = CollectionReconciler.TryReconcile(items, desired, maxMoves);
        return (changes, reconciled);
    }

    [Fact]
    public void OneItemMovedAnyDistanceIsOneMove()
    {
        var items = Enumerable.Range(0, 100).Select(i => new Item(i)).ToList();
        var collection = new ObservableCollection<Item>(items);
        var desired = items.Skip(1).Append(items[0]).ToList();

        var (changes, reconciled) = Reconcile(collection, desired);

        Assert.True(reconciled);
        var move = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Move, move.Action);
        Assert.Equal(0, move.OldStartingIndex);
        Assert.Equal(99, move.NewStartingIndex);
        Assert.Equal(desired, collection);

        (changes, _) = Reconcile(collection, items);
        Assert.Equal(NotifyCollectionChangedAction.Move, Assert.Single(changes).Action);
        Assert.Equal(items, collection);
    }

    [Fact]
    public void TooManyMovesChangeNothing()
    {
        var items = Enumerable.Range(0, 10).Select(i => new Item(i)).ToList();
        var collection = new ObservableCollection<Item>(items);
        var reversed = Enumerable.Reverse(items).ToList();

        var (changes, reconciled) = Reconcile(collection, reversed, maxMoves: 8);

        Assert.False(reconciled);
        Assert.Empty(changes);
        Assert.Equal(items, collection);
        Assert.True(CollectionReconciler.TryReconcile(collection, reversed, maxMoves: 9));
        Assert.Equal(reversed, collection);
    }

    [Fact]
    public void RandomChangesReachTheDesiredListWithTheFewestMoves()
    {
        var random = new Random(20261012);
        var next = 1000;
        for (var round = 0; round < 500; round++)
        {
            var items = Enumerable.Range(0, random.Next(0, 40)).Select(i => new Item(i)).ToList();
            var collection = new ObservableCollection<Item>(items);

            // Keep a random subset, shuffle part of it, and add new items at random places.
            var desired = items.Where(_ => random.NextDouble() < 0.8).ToList();
            for (var swaps = random.Next(0, 4); swaps > 0 && desired.Count > 1; swaps--)
            {
                var from = random.Next(desired.Count);
                var item = desired[from];
                desired.RemoveAt(from);
                desired.Insert(random.Next(desired.Count + 1), item);
            }

            for (var added = random.Next(0, 5); added > 0; added--)
            {
                desired.Insert(random.Next(desired.Count + 1), new Item(next++));
            }

            var kept = items.Where(desired.Contains).ToList();
            var expectedMoves = kept.Count - CollectionReconciler.LongestIncreasingRun(kept.Select(item => desired.IndexOf(item)).ToList()).Count;

            var (changes, reconciled) = Reconcile(collection, desired);

            Assert.True(reconciled);
            Assert.Equal(desired, collection);
            Assert.All(changes, change => Assert.NotEqual(NotifyCollectionChangedAction.Reset, change.Action));
            Assert.All(changes, change => Assert.True((change.NewItems?.Count ?? 0) <= 1 && (change.OldItems?.Count ?? 0) <= 1));
            Assert.Equal(expectedMoves, changes.Count(change => change.Action == NotifyCollectionChangedAction.Move));
            Assert.Equal(items.Count - kept.Count, changes.Count(change => change.Action == NotifyCollectionChangedAction.Remove));
            Assert.Equal(desired.Count - kept.Count, changes.Count(change => change.Action == NotifyCollectionChangedAction.Add));
        }
    }

    [Fact]
    public void TheLongestRunIsLongestAndIncreasing()
    {
        Assert.Equal([], CollectionReconciler.LongestIncreasingRun([]));
        Assert.Equal([0, 1, 2], CollectionReconciler.LongestIncreasingRun([1, 2, 3]));
        var values = new[] { 5, 1, 6, 2, 7, 3, 8 };
        var run = CollectionReconciler.LongestIncreasingRun(values);
        Assert.Equal(4, run.Count);
        Assert.True(run.Zip(run.Skip(1)).All(pair => pair.First < pair.Second && values[pair.First] < values[pair.Second]));
    }
}
