using NimPad.Core.Storage;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Storage;

public sealed class FtsIndexTests : IAsyncLifetime
{
    private TestStore _store = null!;

    public async ValueTask InitializeAsync() => _store = await TestStore.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task NamesAndBodiesAreIndexedByNodeRowId()
    {
        var folder = await _store.Nodes.CreateFolderAsync(_store.Workspace.Id, null, "Recipes");
        var document = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, folder.Id, "Crème brûlée.md", initialText: "Caramelised sugar on top");

        var rows = await _store.QueryAsync(
            "SELECT n.id FROM notes_fts f JOIN nodes n ON n.row_id = f.rowid WHERE notes_fts MATCH $q ORDER BY n.id;",
            reader => reader.GetString(0),
            ("$q", "creme"));
        Assert.Equal([document.Id.ToString()], rows);
        Assert.Equal(1, await CountAsync("name:recipes"));
        Assert.Equal(1, await CountAsync("body:caramelised"));
        Assert.Equal(2, await _store.CountAsync("SELECT count(*) FROM notes_fts;"));
    }

    [Fact]
    public async Task LargeDocumentsAreDeferredUntilReindexed()
    {
        var body = "needle " + new string('x', FtsIndex.LargeDocumentThreshold);
        var document = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "big.txt", initialText: body);

        Assert.Equal(1, await _store.CountAsync("SELECT fts_dirty FROM document_contents WHERE node_id = $id;", ("$id", document.Id.ToString())));
        Assert.Equal(0, await CountAsync("body:needle"));
        Assert.Equal(1, await CountAsync("name:big"));

        await _store.Database.Writer.ExecuteAsync(context => FtsIndex.Reindex(context, document.Id));

        Assert.Equal(1, await CountAsync("body:needle"));
        Assert.Equal(0, await _store.CountAsync("SELECT fts_dirty FROM document_contents WHERE node_id = $id;", ("$id", document.Id.ToString())));
    }

    [Fact]
    public async Task RebuildRestoresTheIndexAndRemoveDropsOneRow()
    {
        var a = await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "alpha", initialText: "first body");
        await _store.Nodes.CreateDocumentAsync(_store.Workspace.Id, null, "beta", initialText: "second body");

        await _store.Database.Writer.ExecuteAsync(context => FtsIndex.Remove(context, a.Id));
        Assert.Equal(0, await CountAsync("alpha"));

        await _store.Database.Writer.ExecuteAsync(FtsIndex.RebuildAll);
        Assert.Equal(1, await CountAsync("alpha"));
        Assert.Equal(2, await CountAsync("body"));
        Assert.Equal(2, await _store.CountAsync("SELECT count(*) FROM notes_fts;"));
    }

    private Task<long> CountAsync(string match) =>
        _store.CountAsync("SELECT count(*) FROM notes_fts WHERE notes_fts MATCH $match;", ("$match", match));
}
