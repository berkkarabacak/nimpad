using NimPad.Core.Services;
using NimPad.Core.Storage;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Services;

public sealed class SearchIndexerTests : IAsyncLifetime
{
    private TestStore _store = null!;

    private SearchIndexer Indexer => _store.Services.SearchIndexer;

    public async ValueTask InitializeAsync() => _store = await TestStore.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task IndexesLargeDocumentsOnceWritesHaveBeenQuietForTheIdleDelay()
    {
        var caughtUp = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Indexer.CaughtUp += (_, count) => caughtUp.TrySetResult(count);
        var note = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "big.txt");
        await _store.Documents.SaveContentAsync(note.Id, "haystack " + new string('x', FtsIndex.LargeDocumentThreshold));
        await _store.Services.Notifier.WhenDeliveredAsync();
        Assert.Equal(1, await Indexer.CountPendingAsync());

        // Still typing: every save restarts the idle timer.
        _store.Clock.Advance(TimeSpan.FromSeconds(1.5));
        await _store.Documents.SaveContentAsync(note.Id, "needle " + new string('y', FtsIndex.LargeDocumentThreshold));
        await _store.Services.Notifier.WhenDeliveredAsync();
        _store.Clock.Advance(TimeSpan.FromSeconds(1.5));
        await Task.Delay(50);
        Assert.False(caughtUp.Task.IsCompleted);
        Assert.Equal(1, await Indexer.CountPendingAsync());

        _store.Clock.Advance(TimeSpan.FromSeconds(0.5));

        Assert.Equal(1, await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await Indexer.CountPendingAsync());
        Assert.Equal(1, await _store.CountAsync("SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'body:needle';"));
        Assert.Equal(0, await _store.CountAsync("SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'body:haystack';"));
    }

    [Fact]
    public async Task CatchesUpAtStartupOnDocumentsLeftFlaggedByAnEarlierSession()
    {
        using var temp = new TempDirectory();
        using (var first = await TestStore.CreateAsync(temp))
        {
            await first.Nodes.CreateDocumentAsync(first.Workspace.Id, null, "a", initialText: "alpha " + new string('a', FtsIndex.LargeDocumentThreshold));
            await first.Nodes.CreateDocumentAsync(first.Workspace.Id, null, "b", initialText: "bravo " + new string('b', FtsIndex.LargeDocumentThreshold));
        }

        using var second = await TestStore.CreateAsync(temp);
        var caughtUp = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        second.Services.SearchIndexer.CaughtUp += (_, count) => caughtUp.TrySetResult(count);
        Assert.Equal(2, await second.Services.SearchIndexer.CountPendingAsync());

        second.Clock.Advance(SearchIndexer.DefaultIdleDelay);

        Assert.Equal(2, await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await second.CountAsync("SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'alpha';"));
        Assert.Equal(1, await second.CountAsync("SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'bravo';"));
    }

    [Fact]
    public async Task ReindexRebuildsAMissingRowAndClearsTheFlag()
    {
        var note = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "n", initialText: "zulu " + new string('z', FtsIndex.LargeDocumentThreshold));
        await _store.Database.Writer.ExecuteAsync(context => FtsIndex.Remove(context, note.Id));

        Assert.True(await _store.Database.Writer.ExecuteAsync(context => FtsIndex.Reindex(context, note.Id)));
        Assert.False(await _store.Database.Writer.ExecuteAsync(context => FtsIndex.Reindex(context, note.Id)));

        Assert.Equal(1, await _store.CountAsync("SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'zulu AND name:n';"));
        Assert.Equal(1, await _store.CountAsync("SELECT count(*) FROM notes_fts;"));
    }

    [Fact]
    public async Task CatchUpWithNothingFlaggedDoesNothing()
    {
        await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "small", initialText: "tiny");
        Assert.Equal(0, await Indexer.CatchUpAsync());
    }
}
