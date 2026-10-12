using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Storage;
using NimPad.Core.Sync;
using NimPad.Core.Tests.Support;
using NimPad.Core.Text;
using Xunit;

namespace NimPad.Core.Tests.Services;

public sealed class DocumentServiceTests : IAsyncLifetime
{
    private TestStore _store = null!;

    private DocumentService Documents => _store.Documents;

    private NodeService Nodes => _store.Nodes;

    private WorkspaceId Ws => _store.Workspace.Id;

    public async ValueTask InitializeAsync() => _store = await TestStore.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------ read and save

    [Fact]
    public async Task ReadsTheCreatedContent()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "todo.md", initialText: "# Todo\r\n- milk");

        var content = await Documents.GetContentAsync(note.Id);

        Assert.Equal(note.Id, content.NodeId);
        Assert.Equal("# Todo\n- milk", content.Text);
        Assert.Equal(DocumentFormat.Markdown, content.Format);
        Assert.Equal(ContentCanonicalizer.Checksum("# Todo\n- milk"), content.Checksum);
        Assert.Equal(TestStore.Start, content.UpdatedAt);
        Assert.Equal(1, content.Version);
    }

    [Fact]
    public async Task SaveCanonicalizesAndUpdatesEveryDerivedValueInOneTransaction()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "plan.txt");
        _store.Clock.Advance(TimeSpan.FromSeconds(5));
        var text = "\r\n   \r\n  First line of the plan  \r\nSecond\rThird";

        var result = await Documents.SaveContentAsync(note.Id, text);

        var canonical = "\n   \n  First line of the plan  \nSecond\nThird";
        var now = TestStore.Start.AddSeconds(5);
        Assert.True(result.Changed);
        Assert.Equal(ContentCanonicalizer.Checksum(canonical), result.Checksum);
        Assert.Equal(2, result.Version);
        Assert.Equal(now, result.UpdatedAt);

        var row = (await _store.QueryAsync(
            "SELECT text, checksum, char_count, preview, updated_at, version, fts_dirty FROM document_contents WHERE node_id = $id;",
            r => (Text: r.GetString(0), Checksum: r.GetString(1), Chars: r.GetInt64(2), Preview: r.GetString(3), UpdatedAt: r.GetString(4), Version: r.GetInt64(5), Dirty: r.GetInt64(6)),
            ("$id", note.Id.ToString()))).Single();
        Assert.Equal(canonical, row.Text);
        Assert.Equal(result.Checksum, row.Checksum);
        Assert.Equal(canonical.Length, row.Chars);
        Assert.Equal("First line of the plan", row.Preview);
        Assert.Equal("2026-10-12T13:59:48.123Z", row.UpdatedAt);
        Assert.Equal(2, row.Version);
        Assert.Equal(0, row.Dirty);

        var node = await Nodes.GetNodeAsync(note.Id);
        Assert.Equal(now, node!.ContentUpdatedAt);
        Assert.Equal(now, node.UpdatedAt);
        Assert.Equal(canonical, (await Documents.GetContentAsync(note.Id)).Text);
    }

    [Fact]
    public async Task PreviewIsTheFirstNonBlankLineCutTo120Characters()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "long");
        await Documents.SaveContentAsync(note.Id, "\n\n" + new string('w', 300) + "\nsecond");

        var preview = (await _store.QueryAsync("SELECT preview FROM document_contents WHERE node_id = $id;", r => r.GetString(0), ("$id", note.Id.ToString()))).Single();
        Assert.Equal(new string('w', 120), preview);
    }

    [Fact]
    public async Task SavingTheSameTextIsANoOp()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "same", initialText: "a\nb");
        await _store.Services.Notifier.WhenDeliveredAsync();
        var events = new List<NodeChange>();
        _store.Services.Notifier.Changed += (_, e) => events.AddRange(e.Changes);
        var queued = await _store.Queue.CountAsync();
        _store.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await Documents.SaveContentAsync(note.Id, "a\r\nb");

        Assert.False(result.Changed);
        Assert.Equal(1, result.Version);
        Assert.Equal(TestStore.Start, result.UpdatedAt);
        Assert.Equal(TestStore.Start, (await Nodes.GetNodeAsync(note.Id))!.ContentUpdatedAt);
        Assert.Equal(queued, await _store.Queue.CountAsync());
        await _store.Services.Notifier.WhenDeliveredAsync();
        Assert.Empty(events);
    }

    [Fact]
    public async Task RepeatedSavesCoalesceIntoOneUpdateContentOperation()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "synced.md");
        // Pretend the server acknowledged the create, so content changes need their own operation.
        await _store.ExecuteRawAsync("DELETE FROM pending_operations;");
        await _store.ExecuteRawAsync("UPDATE nodes SET meta_server_rev = 3, content_server_rev = 7 WHERE id = $id;", ("$id", note.Id.ToString()));

        await Documents.SaveContentAsync(note.Id, "one");
        await Documents.SaveContentAsync(note.Id, "two");
        var last = await Documents.SaveContentAsync(note.Id, "three");

        var operations = await _store.Queue.GetForNodeAsync(note.Id);
        var update = Assert.Single(operations);
        Assert.Equal(PendingOperationKind.UpdateContent, update.Kind);
        Assert.Equal(LocalActorKind.User, update.ActorKind);
        Assert.Equal(3, update.BaseMetaRev);
        Assert.Equal(7, update.BaseContentRev);
        Assert.Equal(last.Checksum, update.Payload["checksum"]!.GetValue<string>());
        Assert.Equal("markdown", update.Payload["format"]!.GetValue<string>());
        Assert.Equal(4, last.Version);
    }

    [Fact]
    public async Task ContentOfANeverSyncedNoteFoldsIntoItsCreate()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "fresh.md");

        var result = await Documents.SaveContentAsync(note.Id, "hello");

        var create = Assert.Single(await _store.Queue.GetForNodeAsync(note.Id));
        Assert.Equal(PendingOperationKind.Create, create.Kind);
        Assert.Equal(result.Checksum, create.Payload["checksum"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveReportsAContentChange()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "n");
        await _store.Services.Notifier.WhenDeliveredAsync();
        var events = new List<WorkspaceChangedEventArgs>();
        _store.Services.Notifier.Changed += (_, e) => events.Add(e);

        await Documents.SaveContentAsync(note.Id, "changed");
        await _store.Services.Notifier.WhenDeliveredAsync();

        var change = Assert.Single(Assert.Single(events).Changes);
        Assert.Equal(new NodeChange(note.Id, NodeChangeKind.ContentChanged), change);
    }

    [Fact]
    public async Task FoldersAndMissingNodesHaveNoContent()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var missing = NodeId.New();

        await Assert.ThrowsAsync<NodeNotFoundException>(() => Documents.GetContentAsync(folder.Id));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Documents.GetContentAsync(missing));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Documents.SaveContentAsync(folder.Id, "x"));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Documents.SaveContentAsync(missing, "x"));
    }

    [Fact]
    public async Task EditsTypedJustBeforeTrashingAreStillSaved()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "doomed");
        await Nodes.TrashAsync([note.Id]);

        var result = await Documents.SaveContentAsync(note.Id, "last words");

        Assert.True(result.Changed);
        Assert.Equal("last words", (await Documents.GetContentAsync(note.Id)).Text);
        await Nodes.RestoreAsync(note.Id);
        Assert.Equal("last words", (await Documents.GetContentAsync(note.Id)).Text);
    }

    [Fact]
    public async Task LargeTextIsPreparedOffTheCallingThreadAndSmallTextOnIt()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "big.log");
        var large = string.Concat(Enumerable.Repeat("line of a large log file\n", 100_000));
        using var returned = new ManualResetEventSlim();
        bool? largeWaitedForTheCaller = null;
        bool? smallRanBeforeReturning = null;

        // A large text: preparation must not start on the caller, so it can wait for the call to return.
        Documents.PreparingText = text => largeWaitedForTheCaller = returned.Wait(TimeSpan.FromSeconds(10));
        var save = Documents.SaveContentAsync(note.Id, large);
        returned.Set();
        var result = await save;

        Assert.True(largeWaitedForTheCaller);
        Assert.Equal(ContentCanonicalizer.Checksum(large), result.Checksum);
        Assert.Equal(large, (await Documents.GetContentAsync(note.Id)).Text);

        // A short text is cheaper to prepare right away, before the call returns.
        returned.Reset();
        Documents.PreparingText = text => smallRanBeforeReturning = !returned.IsSet;
        var small = Documents.SaveContentAsync(note.Id, "short");
        returned.Set();
        await small;
        Assert.True(smallRanBeforeReturning);
    }

    [Fact]
    public void TextAlreadyInStoredFormIsNotCopied()
    {
        var text = string.Concat(Enumerable.Repeat("line of a large log file 😀\n", 10_000));

        var prepared = DocumentService.PreparedText.From(text);

        Assert.Same(text, prepared.Text);
        Assert.Equal(ContentCanonicalizer.Checksum(text), prepared.Checksum);
    }

    [Fact]
    public async Task SavesOfOneNoteCommitInCallOrder()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "race.txt");
        var large = string.Concat(Enumerable.Repeat("an older, longer version of the text\n", 50_000));

        // The large text is prepared on the thread pool; the small one is ready at once but must not overtake it.
        for (var i = 0; i < 5; i++)
        {
            var first = Documents.SaveContentAsync(note.Id, large);
            var second = Documents.SaveContentAsync(note.Id, $"newer {i}");
            await Task.WhenAll(first, second);

            Assert.Equal($"newer {i}", (await Documents.GetContentAsync(note.Id)).Text);
            Assert.True((await second).Version > (await first).Version);
        }
    }

    [Fact]
    public async Task ASaveBasedOnTextThatChangedElsewhereFailsWithoutWriting()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "shared.md", initialText: "v1");
        var loaded = await Documents.GetContentAsync(note.Id);
        await Documents.SaveContentAsync(note.Id, "changed elsewhere");
        var queued = (await _store.Queue.GetForNodeAsync(note.Id)).Single().PayloadJson;

        var conflict = await Assert.ThrowsAsync<DocumentConflictException>(
            () => Documents.SaveContentAsync(note.Id, "v1 and my edits", loaded.Checksum));

        Assert.Equal(note.Id, conflict.NodeId);
        Assert.Equal(loaded.Checksum, conflict.ExpectedChecksum);
        Assert.Equal(ContentCanonicalizer.Checksum("changed elsewhere"), conflict.StoredChecksum);
        var stored = await Documents.GetContentAsync(note.Id);
        Assert.Equal("changed elsewhere", stored.Text);
        Assert.Equal(queued, (await _store.Queue.GetForNodeAsync(note.Id)).Single().PayloadJson);

        // Based on the stored text, the same save goes through; the same text as stored is a no-op whatever the base.
        var saved = await Documents.SaveContentAsync(note.Id, "v1 and my edits", stored.Checksum);
        Assert.True(saved.Changed);
        Assert.False((await Documents.SaveContentAsync(note.Id, "v1 and my edits", loaded.Checksum)).Changed);
        // A format change is not a text change: it does not invalidate the base.
        await Documents.SetFormatAsync(note.Id, DocumentFormat.Plain);
        Assert.True((await Documents.SaveContentAsync(note.Id, "after the format change", saved.Checksum)).Changed);
    }

    [Fact]
    public async Task TextIsStoredInAFormTheServerCanStore()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "pasted.txt", initialText: "x\0y");
        Assert.Equal("x\uFFFDy", (await Documents.GetContentAsync(note.Id)).Text);

        var result = await Documents.SaveContentAsync(note.Id, "a\0b\uD800c\r\n");

        var content = await Documents.GetContentAsync(note.Id);
        Assert.Equal("a\uFFFDb\uFFFDc\n", content.Text);
        Assert.Equal(ContentCanonicalizer.Checksum("a\uFFFDb\uFFFDc\n"), result.Checksum);
        Assert.Equal(result.Checksum, content.Checksum);
        Assert.Equal(result.Checksum, (await _store.Queue.GetForNodeAsync(note.Id)).Single().Payload["checksum"]!.GetValue<string>());
        // The editor still holds the original characters; saving them again changes nothing.
        Assert.False((await Documents.SaveContentAsync(note.Id, "a\0b\uD800c\n")).Changed);
    }

    // ------------------------------------------------------------ search index

    [Fact]
    public async Task SmallDocumentsAreIndexedInTheSameTransaction()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "n", initialText: "old words");

        await Documents.SaveContentAsync(note.Id, "fresh vocabulary");

        Assert.Equal(1, await FtsCountAsync("body:fresh"));
        Assert.Equal(0, await FtsCountAsync("body:old"));
    }

    [Fact]
    public async Task LargeDocumentsAreFlaggedAndCaughtUpByTheIndexer()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "n", initialText: "small original");
        var large = "needle " + new string('x', FtsIndex.LargeDocumentThreshold);

        await Documents.SaveContentAsync(note.Id, large);

        Assert.Equal(1, await _store.CountAsync("SELECT fts_dirty FROM document_contents WHERE node_id = $id;", ("$id", note.Id.ToString())));
        Assert.Equal(0, await FtsCountAsync("body:needle"));
        Assert.Equal(1, await _store.Services.SearchIndexer.CountPendingAsync());

        Assert.Equal(1, await _store.Services.SearchIndexer.CatchUpAsync());

        Assert.Equal(1, await FtsCountAsync("body:needle"));
        Assert.Equal(0, await FtsCountAsync("body:original"));
        Assert.Equal(0, await _store.Services.SearchIndexer.CountPendingAsync());

        // Shrinking below the threshold indexes immediately again.
        await Documents.SaveContentAsync(note.Id, "small again");
        Assert.Equal(1, await FtsCountAsync("body:again"));
        Assert.Equal(0, await FtsCountAsync("body:needle"));
    }

    // ------------------------------------------------------------ format

    [Fact]
    public async Task SetFormatOverridesAndQueuesAContentUpdate()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "notes.txt", initialText: "x");
        _store.Clock.Advance(TimeSpan.FromSeconds(1));

        var updated = await Documents.SetFormatAsync(note.Id, DocumentFormat.Markdown);

        Assert.Equal(DocumentFormat.Markdown, updated.Format);
        var content = await Documents.GetContentAsync(note.Id);
        Assert.Equal(DocumentFormat.Markdown, content.Format);
        Assert.Equal(2, content.Version);
        Assert.Equal("markdown", Assert.Single(await _store.Queue.GetForNodeAsync(note.Id)).Payload["format"]!.GetValue<string>());

        // Overridden: a rename to another extension no longer changes it.
        await Nodes.RenameAsync(note.Id, "notes.json");
        Assert.Equal(DocumentFormat.Markdown, (await Documents.GetContentAsync(note.Id)).Format);

        // Setting the same format is a no-op.
        Assert.Equal(DocumentFormat.Markdown, (await Documents.SetFormatAsync(note.Id, DocumentFormat.Markdown)).Format);
        Assert.Equal(2, (await Documents.GetContentAsync(note.Id)).Version);
    }

    [Fact]
    public async Task ARenameThatChangesTheFormatBumpsTheVersion()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "data.txt", initialText: "{}");

        await Nodes.RenameAsync(note.Id, "data.json");
        await Nodes.RenameAsync(note.Id, "data2.json");

        var content = await Documents.GetContentAsync(note.Id);
        Assert.Equal(DocumentFormat.Json, content.Format);
        Assert.Equal(2, content.Version);
    }

    [Fact]
    public async Task SetFormatRejectsTrashedNotesAndFolders()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "n");
        var folder = await Nodes.CreateFolderAsync(Ws, null, "f");
        await Nodes.TrashAsync([note.Id]);

        await Assert.ThrowsAsync<NodeNotFoundException>(() => Documents.SetFormatAsync(note.Id, DocumentFormat.Json));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Documents.SetFormatAsync(folder.Id, DocumentFormat.Json));
    }

    // ------------------------------------------------------------ links

    [Fact]
    public async Task SavesKeepLinksCurrentAndBacklinksFollowThem()
    {
        var target = await Nodes.CreateDocumentAsync(Ws, null, "Target");
        var other = await Nodes.CreateDocumentAsync(Ws, null, "Other");
        var source = await Nodes.CreateDocumentAsync(Ws, null, "Source");

        await Documents.SaveContentAsync(source.Id, $"see {LinkParser.CreateUri(target.Id)} and {LinkParser.CreateUri(other.Id)}");
        Assert.Equal([source.Id], (await Documents.GetBacklinksAsync(target.Id)).Select(node => node.Id));
        Assert.Equal([source.Id], (await Documents.GetBacklinksAsync(other.Id)).Select(node => node.Id));

        await Documents.SaveContentAsync(source.Id, $"only {LinkParser.CreateUri(other.Id)}");
        Assert.Empty(await Documents.GetBacklinksAsync(target.Id));
        Assert.Equal([other.Id], await Documents.GetOutgoingLinksAsync(source.Id));
    }

    [Fact]
    public async Task BacklinksSkipSelfLinksAndTrashedSourcesButKeepDanglingTargets()
    {
        var target = await Nodes.CreateDocumentAsync(Ws, null, "Target");
        var b = await Nodes.CreateDocumentAsync(Ws, null, "b source");
        var a = await Nodes.CreateDocumentAsync(Ws, null, "A source");
        var ghost = NodeId.New();
        var links = $"{LinkParser.CreateUri(target.Id)} {LinkParser.CreateUri(ghost)}";
        await Documents.SaveContentAsync(a.Id, links);
        await Documents.SaveContentAsync(b.Id, links);
        await Documents.SaveContentAsync(target.Id, $"myself: {LinkParser.CreateUri(target.Id)}");

        Assert.Equal([a.Id, b.Id], (await Documents.GetBacklinksAsync(target.Id)).Select(node => node.Id));
        Assert.Equal(2, (await Documents.GetBacklinksAsync(ghost)).Count);

        await Nodes.TrashAsync([b.Id]);
        Assert.Equal([a.Id], (await Documents.GetBacklinksAsync(target.Id)).Select(node => node.Id));

        await Nodes.DeleteForeverAsync([a.Id]);
        Assert.Empty(await Documents.GetBacklinksAsync(target.Id));
        Assert.Equal(0, await _store.CountAsync("SELECT count(*) FROM document_links WHERE source_id = $id;", ("$id", a.Id.ToString())));
    }

    [Fact]
    public async Task CreatedAndCopiedDocumentsGetTheirLinks()
    {
        var target = await Nodes.CreateDocumentAsync(Ws, null, "Target");
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var source = await Nodes.CreateDocumentAsync(Ws, folder.Id, "Source", initialText: $"[t]({LinkParser.CreateUri(target.Id)})");

        var copies = await Nodes.CopyAsync([folder.Id], null);
        var copiedSource = Assert.Single(await Nodes.GetChildrenAsync(Ws, copies[0].Id));

        Assert.Equal(
            new[] { source.Id, copiedSource.Id }.Select(id => id.ToString()).Order(),
            (await Documents.GetBacklinksAsync(target.Id)).Select(node => node.Id.ToString()).Order());
    }

    [Fact]
    public async Task MovingOrRenamingANoteNeverChangesItsLinks()
    {
        var projects = await Nodes.CreateFolderAsync(Ws, null, "Projects");
        var archive = await Nodes.CreateFolderAsync(Ws, null, "Archive");
        var deep = await Nodes.CreateFolderAsync(Ws, archive.Id, "2026");
        var target = await Nodes.CreateDocumentAsync(Ws, projects.Id, "Architecture.md", initialText: "# Architecture");
        var source = await Nodes.CreateDocumentAsync(Ws, null, "Index.md");
        var text = $"- [Architecture]({LinkParser.CreateUri(target.Id)})";
        await Documents.SaveContentAsync(source.Id, text);
        var linksBefore = await LinkRowsAsync();

        await Nodes.MoveAsync(target.Id, deep.Id);
        await Nodes.RenameAsync(target.Id, "Architecture (old).md");
        await Nodes.MoveAsync(source.Id, projects.Id);
        await Nodes.MoveAsync(projects.Id, archive.Id);

        Assert.Equal(linksBefore, await LinkRowsAsync());
        Assert.Equal(text, (await Documents.GetContentAsync(source.Id)).Text);
        Assert.Equal([source.Id], (await Documents.GetBacklinksAsync(target.Id)).Select(node => node.Id));
        var resolved = LinkParser.ExtractTargets((await Documents.GetContentAsync(source.Id)).Text).Single();
        var resolvedNode = await Nodes.GetNodeAsync(resolved);
        Assert.Equal("Architecture (old).md", resolvedNode!.Name);
        Assert.Equal(deep.Id, resolvedNode.ParentId);
    }

    private Task<List<string>> LinkRowsAsync() =>
        _store.QueryAsync("SELECT source_id || ' -> ' || target_id FROM document_links ORDER BY 1;", r => r.GetString(0));

    private Task<long> FtsCountAsync(string match) =>
        _store.CountAsync("SELECT count(*) FROM notes_fts WHERE notes_fts MATCH $m;", ("$m", match));
}
