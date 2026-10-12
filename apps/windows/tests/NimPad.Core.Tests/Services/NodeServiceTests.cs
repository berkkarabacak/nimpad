using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Ordering;
using NimPad.Core.Services;
using NimPad.Core.Tests.Support;
using NimPad.Core.Text;
using Xunit;

namespace NimPad.Core.Tests.Services;

public sealed class NodeServiceTests : IAsyncLifetime
{
    private TestStore _store = null!;

    private NodeService Nodes => _store.Nodes;

    private WorkspaceId Ws => _store.Workspace.Id;

    public async ValueTask InitializeAsync() => _store = await TestStore.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------ reads

    [Fact]
    public async Task GetNodesReturnsExistingNodesInAnyStateInOneCall()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var live = await Nodes.CreateDocumentAsync(Ws, folder.Id, "Live");
        var trashed = await Nodes.CreateDocumentAsync(Ws, null, "Trashed");
        await Nodes.TrashAsync([trashed.Id]);
        var missing = NodeId.New();

        var nodes = await Nodes.GetNodesAsync([live.Id, trashed.Id, missing, live.Id, folder.Id]);

        Assert.Equal(
            new[] { folder.Id, live.Id, trashed.Id }.Select(id => id.ToString()).Order(),
            nodes.Select(node => node.Id.ToString()).Order());
        Assert.True(nodes.Single(node => node.Id == trashed.Id).IsTrashed);
        Assert.Equal(await Nodes.GetNodeAsync(live.Id), nodes.Single(node => node.Id == live.Id));
        Assert.Empty(await Nodes.GetNodesAsync([]));
    }

    [Fact]
    public async Task GetPreviewsReturnsTheStoredPreviewLines()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var first = await Nodes.CreateDocumentAsync(Ws, folder.Id, "First", initialText: "\n  Hello world\nsecond line");
        var empty = await Nodes.CreateDocumentAsync(Ws, null, "Empty");
        var trashed = await Nodes.CreateDocumentAsync(Ws, null, "Trashed", initialText: "gone soon");
        await Nodes.TrashAsync([trashed.Id]);
        await _store.Documents.SaveContentAsync(empty.Id, "now filled");

        var all = await _store.Documents.GetPreviewsAsync(Ws);
        var some = await _store.Documents.GetPreviewsAsync([first.Id, folder.Id, NodeId.New()]);

        Assert.Equal(3, all.Count);
        Assert.Equal(DocumentPreview.Create("\n  Hello world\nsecond line"), all[first.Id]);
        Assert.Equal("now filled", all[empty.Id]);
        Assert.Equal("gone soon", all[trashed.Id]);
        Assert.Equal(all[first.Id], Assert.Single(some).Value);
        Assert.Empty(await _store.Documents.GetPreviewsAsync(Array.Empty<NodeId>()));
    }

    // ------------------------------------------------------------ create

    [Fact]
    public async Task CreatesFoldersAndDocumentsWithMetadata()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "  Projects ");
        var document = await Nodes.CreateDocumentAsync(Ws, folder.Id, "config.json", initialText: "{\r\n  \"a\": 1\r\n}");

        Assert.Equal("Projects", folder.Name);
        Assert.Equal(NodeKind.Folder, folder.Kind);
        Assert.Null(folder.ParentId);
        Assert.Null(folder.Format);
        Assert.Null(folder.ContentUpdatedAt);
        Assert.Equal(TestStore.Start, folder.CreatedAt);
        Assert.Equal(TestStore.Start, folder.UpdatedAt);
        Assert.True(folder.IsNeverSynced);

        Assert.Equal(folder.Id, document.ParentId);
        Assert.Equal(DocumentFormat.Json, document.Format);
        Assert.Equal(TestStore.Start, document.ContentUpdatedAt);
        Assert.Equal(document, await Nodes.GetNodeAsync(document.Id));

        var content = (await _store.QueryAsync(
            "SELECT text, format, checksum, char_count, preview, fts_dirty FROM document_contents WHERE node_id = $id;",
            reader => (Text: reader.GetString(0), Format: reader.GetString(1), Checksum: reader.GetString(2), Chars: reader.GetInt64(3), Preview: reader.GetString(4), Dirty: reader.GetInt64(5)),
            ("$id", document.Id.ToString()))).Single();
        Assert.Equal("{\n  \"a\": 1\n}", content.Text);
        Assert.Equal("json", content.Format);
        Assert.Equal(ContentCanonicalizer.Checksum("{\n  \"a\": 1\n}"), content.Checksum);
        Assert.Equal(12, content.Chars);
        Assert.Equal("{", content.Preview);
        Assert.Equal(0, content.Dirty);
    }

    [Fact]
    public async Task ExplicitFormatOverridesDetection()
    {
        var document = await Nodes.CreateDocumentAsync(Ws, null, "notes.txt", DocumentFormat.Markdown);
        Assert.Equal(DocumentFormat.Markdown, document.Format);
    }

    [Fact]
    public async Task UnnamedDocumentsGetTheNextFreeUntitledNumber()
    {
        var first = await Nodes.CreateDocumentAsync(Ws, null);
        var second = await Nodes.CreateDocumentAsync(Ws, null);
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var third = await Nodes.CreateDocumentAsync(Ws, folder.Id);
        await Nodes.TrashAsync([second.Id]);

        Assert.Equal("Untitled 1", first.Name);
        Assert.Equal("Untitled 2", second.Name);
        Assert.Equal("Untitled 3", third.Name);
        Assert.Equal(DocumentFormat.Plain, first.Format);
        Assert.Equal("Untitled 2", await Nodes.GetNextUntitledNameAsync(Ws));
        Assert.Equal("Untitled 2", (await Nodes.CreateDocumentAsync(Ws, null)).Name);
    }

    [Fact]
    public async Task SiblingsAreOrderedByFractionalPositions()
    {
        var b = await Nodes.CreateFolderAsync(Ws, null, "b");
        var d = await Nodes.CreateFolderAsync(Ws, null, "d");
        var a = await Nodes.CreateFolderAsync(Ws, null, "a", InsertPosition.Start);
        var c = await Nodes.CreateFolderAsync(Ws, null, "c", InsertPosition.Before(d.Id));
        var e = await Nodes.CreateFolderAsync(Ws, null, "e", InsertPosition.After(d.Id));
        var bb = await Nodes.CreateFolderAsync(Ws, null, "bb", InsertPosition.After(b.Id));

        Assert.Equal("a0", b.Position);
        Assert.Equal("a1", d.Position);
        var children = await Nodes.GetChildrenAsync(Ws, null);
        Assert.Equal(["a", "b", "bb", "c", "d", "e"], children.Select(node => node.Name));
        Assert.Equal(children.Order(SiblingComparer.Instance), children);
        Assert.All(children, node => Assert.True(FractionalIndex.IsValidKey(node.Position)));
        Assert.Equal(new[] { a, b, bb, c, d, e }.Select(node => node.Id), children.Select(node => node.Id));
    }

    [Fact]
    public async Task RejectsInvalidNamesAndParents()
    {
        var document = await Nodes.CreateDocumentAsync(Ws, null, "doc");
        var trashedFolder = await Nodes.CreateFolderAsync(Ws, null, "gone");
        await Nodes.TrashAsync([trashedFolder.Id]);

        await Assert.ThrowsAsync<InvalidNodeNameException>(() => Nodes.CreateFolderAsync(Ws, null, "  "));
        await Assert.ThrowsAsync<InvalidNodeNameException>(() => Nodes.CreateDocumentAsync(Ws, null, "a\nb"));
        await Assert.ThrowsAsync<InvalidNodeNameException>(() => Nodes.CreateFolderAsync(Ws, null, new string('x', 256)));
        await Assert.ThrowsAsync<NodeOperationException>(() => Nodes.CreateDocumentAsync(Ws, document.Id, "child"));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Nodes.CreateFolderAsync(Ws, trashedFolder.Id, "child"));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Nodes.CreateFolderAsync(Ws, NodeId.New(), "child"));
        await Assert.ThrowsAsync<NodeOperationException>(() => Nodes.CreateFolderAsync(WorkspaceId.New(), null, "x"));
        await Assert.ThrowsAsync<NodeOperationException>(() =>
            Nodes.CreateFolderAsync(Ws, null, "x", InsertPosition.Before(trashedFolder.Id)));

        // Nothing partial was written by the failed calls.
        Assert.Single(await Nodes.GetAllNodesAsync(Ws));
    }

    // ------------------------------------------------------------ rename

    [Fact]
    public async Task RenameUpdatesNameTimestampAndSearchIndex()
    {
        var document = await Nodes.CreateDocumentAsync(Ws, null, "draft", initialText: "alpha body");
        _store.Clock.Advance(TimeSpan.FromMinutes(5));

        var renamed = await Nodes.RenameAsync(document.Id, " Final report ");

        Assert.Equal("Final report", renamed.Name);
        Assert.Equal(TestStore.Start.AddMinutes(5), renamed.UpdatedAt);
        Assert.Equal(TestStore.Start, renamed.ContentUpdatedAt);
        Assert.Equal(renamed, await Nodes.GetNodeAsync(document.Id));
        Assert.Equal(1, await FtsCountAsync("name:final"));
        Assert.Equal(0, await FtsCountAsync("name:draft"));
        Assert.Equal(1, await FtsCountAsync("body:alpha"));
    }

    [Fact]
    public async Task RenameToTheSameNameChangesNothing()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Same");
        _store.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(folder, await Nodes.RenameAsync(folder.Id, "Same"));
        Assert.Single(await _store.Queue.GetForNodeAsync(folder.Id));
    }

    [Fact]
    public async Task RenameMakesTheFormatFollowTheExtensionUnlessOverridden()
    {
        var plain = await Nodes.CreateDocumentAsync(Ws, null, "Untitled 1");
        var json = await Nodes.RenameAsync(plain.Id, "settings.json");
        Assert.Equal(DocumentFormat.Json, json.Format);

        var overridden = await Nodes.CreateDocumentAsync(Ws, null, "notes.txt", DocumentFormat.Markdown);
        var stillMarkdown = await Nodes.RenameAsync(overridden.Id, "notes.yaml");
        Assert.Equal(DocumentFormat.Markdown, stillMarkdown.Format);
        Assert.Equal(DocumentFormat.Markdown, (await Nodes.GetNodeAsync(overridden.Id))!.Format);
    }

    [Fact]
    public async Task RenameOfATrashedNodeIsRejected()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Old");
        await Nodes.TrashAsync([folder.Id]);

        await Assert.ThrowsAsync<NodeNotFoundException>(() => Nodes.RenameAsync(folder.Id, "New"));
        await Assert.ThrowsAsync<InvalidNodeNameException>(() => Nodes.RenameAsync(folder.Id, ""));
    }

    // ------------------------------------------------------------ move

    [Fact]
    public async Task MovesIntoFoldersAndReorders()
    {
        var projects = await Nodes.CreateFolderAsync(Ws, null, "Projects");
        var a = await Nodes.CreateDocumentAsync(Ws, null, "a");
        var b = await Nodes.CreateDocumentAsync(Ws, null, "b");
        var c = await Nodes.CreateDocumentAsync(Ws, null, "c");

        await Nodes.MoveAsync(c.Id, null, InsertPosition.Before(a.Id));
        Assert.Equal(["Projects", "c", "a", "b"], await ChildNamesAsync(null));

        await Nodes.MoveAsync([a.Id, b.Id], projects.Id);
        Assert.Equal(["Projects", "c"], await ChildNamesAsync(null));
        Assert.Equal(["a", "b"], await ChildNamesAsync(projects.Id));

        await Nodes.MoveAsync(b.Id, projects.Id, InsertPosition.Start);
        Assert.Equal(["b", "a"], await ChildNamesAsync(projects.Id));

        var moved = (await Nodes.GetNodeAsync(b.Id))!;
        Assert.Equal(projects.Id, moved.ParentId);
    }

    [Fact]
    public async Task MovingSeveralNodesKeepsTheGivenOrderAndDropsNestedSelections()
    {
        var target = await Nodes.CreateFolderAsync(Ws, null, "Target");
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var inner = await Nodes.CreateDocumentAsync(Ws, folder.Id, "inner");
        var x = await Nodes.CreateDocumentAsync(Ws, null, "x");
        var y = await Nodes.CreateDocumentAsync(Ws, null, "y");
        var existing = await Nodes.CreateDocumentAsync(Ws, target.Id, "existing");

        await Nodes.MoveAsync([y.Id, inner.Id, folder.Id, x.Id], target.Id, InsertPosition.Before(existing.Id));

        Assert.Equal(["y", "Folder", "x", "existing"], await ChildNamesAsync(target.Id));
        Assert.Equal(folder.Id, (await Nodes.GetNodeAsync(inner.Id))!.ParentId);
    }

    [Fact]
    public async Task MovingAFolderIntoItselfOrADescendantIsRejected()
    {
        var root = await Nodes.CreateFolderAsync(Ws, null, "Root");
        var child = await Nodes.CreateFolderAsync(Ws, root.Id, "Child");
        var grandchild = await Nodes.CreateFolderAsync(Ws, child.Id, "Grandchild");
        var document = await Nodes.CreateDocumentAsync(Ws, null, "doc");

        var self = await Assert.ThrowsAsync<NodeCycleException>(() => Nodes.MoveAsync(root.Id, root.Id));
        Assert.Equal(root.Id, self.NodeId);
        var deep = await Assert.ThrowsAsync<NodeCycleException>(() => Nodes.MoveAsync(root.Id, grandchild.Id));
        Assert.Equal(grandchild.Id, deep.TargetParentId);
        await Assert.ThrowsAsync<NodeCycleException>(() => Nodes.MoveAsync([document.Id, child.Id], grandchild.Id));
        await Assert.ThrowsAsync<NodeOperationException>(() => Nodes.MoveAsync(child.Id, document.Id));

        // Nothing moved, not even the document listed first.
        Assert.Null((await Nodes.GetNodeAsync(document.Id))!.ParentId);
        Assert.Equal(root.Id, (await Nodes.GetNodeAsync(child.Id))!.ParentId);

        // Moving a descendant up is fine.
        await Nodes.MoveAsync(grandchild.Id, null);
        Assert.Null((await Nodes.GetNodeAsync(grandchild.Id))!.ParentId);
    }

    [Fact]
    public async Task MovingANodeToWhereItAlreadyIsChangesNothing()
    {
        var a = await Nodes.CreateDocumentAsync(Ws, null, "a");
        var b = await Nodes.CreateDocumentAsync(Ws, null, "b");

        await Nodes.MoveAsync(b.Id, null);
        await Nodes.MoveAsync(a.Id, null, InsertPosition.Before(b.Id));

        Assert.Equal(b, await Nodes.GetNodeAsync(b.Id));
        Assert.Equal(a, await Nodes.GetNodeAsync(a.Id));
        Assert.DoesNotContain(await _store.Queue.GetPendingAsync(), op => op.Kind == NimPad.Core.Sync.PendingOperationKind.Move);
    }

    [Fact]
    public async Task InsertingNextToTiedPositionsStillProducesAValidKey()
    {
        var a = await Nodes.CreateDocumentAsync(Ws, null, "a");
        var b = await Nodes.CreateDocumentAsync(Ws, null, "b");
        // Simulate two devices that produced the same key concurrently.
        await _store.ExecuteRawAsync("UPDATE nodes SET position = 'a0' WHERE id = $id;", ("$id", b.Id.ToString()));

        var first = await Nodes.CreateDocumentAsync(Ws, null, "after-first", position: InsertPosition.After(OrderedFirst(a, b)));
        var between = await Nodes.CreateDocumentAsync(Ws, null, "before-last", position: InsertPosition.Before(OrderedLast(a, b)));

        Assert.True(FractionalIndex.IsValidKey(first.Position));
        Assert.True(FractionalIndex.IsValidKey(between.Position));
        Assert.Equal(4, (await Nodes.GetChildrenAsync(Ws, null)).Count);

        static NodeId OrderedFirst(NodeInfo x, NodeInfo y) => string.CompareOrdinal(x.Id.ToString(), y.Id.ToString()) < 0 ? x.Id : y.Id;
        static NodeId OrderedLast(NodeInfo x, NodeInfo y) => string.CompareOrdinal(x.Id.ToString(), y.Id.ToString()) < 0 ? y.Id : x.Id;
    }

    // ------------------------------------------------------------ copy

    [Fact]
    public async Task CopyDeepCopiesTheLiveSubtreeWithNewIds()
    {
        var source = await Nodes.CreateFolderAsync(Ws, null, "Source");
        var sub = await Nodes.CreateFolderAsync(Ws, source.Id, "Sub");
        var doc1 = await Nodes.CreateDocumentAsync(Ws, source.Id, "one.md", initialText: "# One");
        var doc2 = await Nodes.CreateDocumentAsync(Ws, sub.Id, "two.py", initialText: "print('two')");
        var trashed = await Nodes.CreateDocumentAsync(Ws, sub.Id, "trashed");
        await Nodes.TrashAsync([trashed.Id]);
        await Nodes.PinAsync(doc1.Id);
        var target = await Nodes.CreateFolderAsync(Ws, null, "Target");

        var copies = await Nodes.CopyAsync([source.Id], target.Id);

        var copy = Assert.Single(copies);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Source", copy.Name); // no conflict at the destination
        Assert.Equal(target.Id, copy.ParentId);
        var all = await Nodes.GetAllNodesAsync(Ws);
        var copiedChildren = all.Where(node => node.ParentId == copy.Id).ToList();
        Assert.Equal(["Sub", "one.md"], copiedChildren.Select(node => node.Name));
        var copiedSub = copiedChildren.Single(node => node.Name == "Sub");
        var copiedDoc1 = copiedChildren.Single(node => node.Name == "one.md");
        var copiedDoc2 = all.Single(node => node.ParentId == copiedSub.Id);
        Assert.Equal("two.py", copiedDoc2.Name);
        Assert.Equal(DocumentFormat.Python, copiedDoc2.Format);
        Assert.Equal(sub.Position, copiedSub.Position);
        Assert.False(copiedDoc1.IsPinned);
        Assert.DoesNotContain(all, node => node.Name == "trashed" && node.ParentId == copiedSub.Id);
        Assert.All(new[] { source.Id, sub.Id, doc1.Id, doc2.Id }, id => Assert.Contains(all, node => node.Id == id));

        Assert.Equal("# One", await TextOfAsync(copiedDoc1.Id));
        Assert.Equal("print('two')", await TextOfAsync(copiedDoc2.Id));
        Assert.Equal(2, await FtsCountAsync("body:one"));
        Assert.Equal(5 + 4, all.Count); // 5 live originals (the trashed one excluded) + 4 copies
    }

    [Fact]
    public async Task DuplicateAddsCopySuffixesNextToTheOriginal()
    {
        var a = await Nodes.CreateDocumentAsync(Ws, null, "report.md", initialText: "text");
        var z = await Nodes.CreateDocumentAsync(Ws, null, "z");

        var first = await Nodes.DuplicateAsync(a.Id);
        var second = await Nodes.DuplicateAsync(a.Id);
        var third = await Nodes.DuplicateAsync(first.Id);

        Assert.Equal("report (copy).md", first.Name);
        Assert.Equal("report (copy 2).md", second.Name);
        Assert.Equal("report (copy 3).md", third.Name);
        Assert.Equal(DocumentFormat.Markdown, first.Format);
        Assert.Equal(["report.md", "report (copy 2).md", "report (copy).md", "report (copy 3).md", "z"], await ChildNamesAsync(null));
        Assert.Equal("text", await TextOfAsync(third.Id));
        Assert.NotEqual(z.Id, third.Id);
    }

    [Fact]
    public async Task CopyIntoOwnSubtreeIsRejected()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var child = await Nodes.CreateFolderAsync(Ws, folder.Id, "Child");

        await Assert.ThrowsAsync<NodeCycleException>(() => Nodes.CopyAsync([folder.Id], child.Id));
        Assert.Equal(2, (await Nodes.GetAllNodesAsync(Ws)).Count);
    }

    // ------------------------------------------------------------ pins

    [Fact]
    public async Task PinsKeepTheirOwnOrder()
    {
        var a = await Nodes.CreateDocumentAsync(Ws, null, "a");
        var b = await Nodes.CreateFolderAsync(Ws, null, "b");
        var c = await Nodes.CreateDocumentAsync(Ws, null, "c");

        await Nodes.PinAsync(c.Id);
        await Nodes.PinAsync(a.Id);
        var pinnedB = await Nodes.PinAsync(b.Id);
        Assert.True(pinnedB.IsPinned);
        Assert.Equal(["c", "a", "b"], (await Nodes.GetPinnedAsync(Ws)).Select(node => node.Name));

        await Nodes.ReorderPinnedAsync(b.Id, InsertPosition.Start);
        Assert.Equal(["b", "c", "a"], (await Nodes.GetPinnedAsync(Ws)).Select(node => node.Name));
        await Nodes.ReorderPinnedAsync(b.Id, InsertPosition.After(c.Id));
        Assert.Equal(["c", "b", "a"], (await Nodes.GetPinnedAsync(Ws)).Select(node => node.Name));

        var unpinned = await Nodes.UnpinAsync(b.Id);
        Assert.False(unpinned.IsPinned);
        Assert.Null(unpinned.PinPosition);
        Assert.Equal(["c", "a"], (await Nodes.GetPinnedAsync(Ws)).Select(node => node.Name));

        // Pinning twice is a no-op; reordering an unpinned node is rejected.
        var again = await Nodes.PinAsync(c.Id);
        Assert.Equal((await Nodes.GetPinnedAsync(Ws))[0], again);
        await Assert.ThrowsAsync<NodeOperationException>(() => Nodes.ReorderPinnedAsync(b.Id, InsertPosition.Start));
    }

    [Fact]
    public async Task TrashedPinnedNodesLeaveThePinnedListUntilRestored()
    {
        var a = await Nodes.CreateDocumentAsync(Ws, null, "a");
        await Nodes.PinAsync(a.Id);
        await Nodes.TrashAsync([a.Id]);
        Assert.Empty(await Nodes.GetPinnedAsync(Ws));

        await Nodes.RestoreAsync(a.Id);
        Assert.Equal([a.Id], (await Nodes.GetPinnedAsync(Ws)).Select(node => node.Id));
    }

    // ------------------------------------------------------------ trash

    [Fact]
    public async Task TrashMarksEachTopLevelSubtreeWithItsOwnBatch()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var sub = await Nodes.CreateFolderAsync(Ws, folder.Id, "Sub");
        var doc = await Nodes.CreateDocumentAsync(Ws, sub.Id, "doc");
        var loose = await Nodes.CreateDocumentAsync(Ws, null, "loose");
        var keep = await Nodes.CreateDocumentAsync(Ws, null, "keep");
        _store.Clock.Advance(TimeSpan.FromHours(1));

        var batches = await Nodes.TrashAsync([folder.Id, loose.Id, doc.Id]);

        // doc travels with its ancestor; folder and loose are separate Trash items, each restorable on its own.
        Assert.Equal(2, batches.Count);
        Assert.Equal([keep.Id], (await Nodes.GetAllNodesAsync(Ws)).Select(node => node.Id));
        foreach (var (id, batch) in new[] { (folder.Id, batches[0]), (sub.Id, batches[0]), (doc.Id, batches[0]), (loose.Id, batches[1]) })
        {
            var node = (await Nodes.GetNodeAsync(id))!;
            Assert.Equal(batch, node.TrashBatchId);
            Assert.Equal(TestStore.Start.AddHours(1), node.DeletedAt);
        }

        var trash = await Nodes.GetTrashAsync(Ws);
        Assert.Equal(["Folder", "loose"], trash.Select(item => item.Node.Name));
        var folderItem = trash[0];
        Assert.Equal(batches[0], folderItem.BatchId);
        Assert.Equal(batches[1], trash[1].BatchId);
        Assert.Equal(2, folderItem.DescendantCount);
        Assert.Equal(30, folderItem.DaysLeft);
        Assert.Equal(TestStore.Start.AddHours(1).AddDays(30), folderItem.PurgeAt);
        Assert.True(folderItem.OriginalParentAvailable);

        await Assert.ThrowsAsync<NodeNotFoundException>(() => Nodes.TrashAsync([doc.Id]));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Nodes.MoveAsync(doc.Id, null));
    }

    [Fact]
    public async Task RestoreBringsBackTheBatchUnderTheOriginalParent()
    {
        var parent = await Nodes.CreateFolderAsync(Ws, null, "Parent");
        var folder = await Nodes.CreateFolderAsync(Ws, parent.Id, "Folder");
        var doc = await Nodes.CreateDocumentAsync(Ws, folder.Id, "doc");
        var earlier = await Nodes.CreateDocumentAsync(Ws, folder.Id, "earlier");
        await Nodes.TrashAsync([earlier.Id]);
        var batch = await Nodes.TrashAsync([folder.Id]);

        var restored = await Nodes.RestoreAsync(folder.Id);

        Assert.Equal(parent.Id, restored.ParentId);
        Assert.Equal(folder.Position, restored.Position);
        Assert.False(restored.IsTrashed);
        Assert.False((await Nodes.GetNodeAsync(doc.Id))!.IsTrashed);
        // Separately trashed earlier, so it stays in the Trash.
        Assert.True((await Nodes.GetNodeAsync(earlier.Id))!.IsTrashed);
        Assert.Equal(["earlier"], (await Nodes.GetTrashAsync(Ws)).Select(item => item.Node.Name));
        Assert.Empty(await Nodes.RestoreBatchAsync(batch));
    }

    [Fact]
    public async Task RestoreGoesToTheRootWhenTheOriginalParentIsTrashed()
    {
        var parent = await Nodes.CreateFolderAsync(Ws, null, "Parent");
        var child = await Nodes.CreateDocumentAsync(Ws, parent.Id, "child");
        var other = await Nodes.CreateDocumentAsync(Ws, null, "other");
        await Nodes.TrashAsync([child.Id]);
        await Nodes.TrashAsync([parent.Id]);

        var trash = await Nodes.GetTrashAsync(Ws);
        Assert.False(trash.Single(item => item.Node.Id == child.Id).OriginalParentAvailable);

        var restored = await Nodes.RestoreAsync(child.Id);

        Assert.Null(restored.ParentId);
        Assert.Equal(["other", "child"], await ChildNamesAsync(null));
        Assert.True(string.CompareOrdinal(other.Position, restored.Position) < 0);
        Assert.True((await Nodes.GetNodeAsync(parent.Id))!.IsTrashed);
    }

    [Fact]
    public async Task RestoreBatchUndoesATrashOfSeveralItems()
    {
        var a = await Nodes.CreateDocumentAsync(Ws, null, "a");
        var b = await Nodes.CreateFolderAsync(Ws, null, "b");
        var bChild = await Nodes.CreateDocumentAsync(Ws, b.Id, "b-child");
        var batches = await Nodes.TrashAsync([a.Id, b.Id]);

        var restored = await Nodes.RestoreBatchAsync(batches);

        Assert.Equal([a.Id, b.Id], restored.Select(node => node.Id));
        Assert.Equal(3, (await Nodes.GetAllNodesAsync(Ws)).Count);
        Assert.Empty(await Nodes.GetTrashAsync(Ws));
        Assert.Equal(b.Id, (await Nodes.GetNodeAsync(bChild.Id))!.ParentId);
        await Assert.ThrowsAsync<NodeOperationException>(() => Nodes.RestoreAsync(a.Id));
        Assert.Empty(await Nodes.RestoreBatchAsync(batches[0]));
    }

    [Fact]
    public async Task RestoringANodeInsideATrashedFolderRestoresTheFolderItWasTrashedWith()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var child = await Nodes.CreateDocumentAsync(Ws, folder.Id, "child");
        var other = await Nodes.CreateDocumentAsync(Ws, null, "other");
        await Nodes.TrashAsync([folder.Id, other.Id]);

        var restored = await Nodes.RestoreAsync(child.Id);

        Assert.Equal(folder.Id, restored.ParentId);
        Assert.False(restored.IsTrashed);
        Assert.False((await Nodes.GetNodeAsync(folder.Id))!.IsTrashed);
        Assert.Equal([other.Id], (await Nodes.GetTrashAsync(Ws)).Select(item => item.Node.Id));
    }

    [Fact]
    public async Task DeleteForeverRemovesTheSubtreeContentAndIndex()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var doc = await Nodes.CreateDocumentAsync(Ws, folder.Id, "doc", initialText: "zebra");
        var keep = await Nodes.CreateDocumentAsync(Ws, null, "keep", initialText: "giraffe");
        await Nodes.TrashAsync([folder.Id]);

        var deleted = await Nodes.DeleteForeverAsync([folder.Id]);

        Assert.Equal(2, deleted);
        Assert.Null(await Nodes.GetNodeAsync(folder.Id));
        Assert.Null(await Nodes.GetNodeAsync(doc.Id));
        Assert.Equal(1, await _store.CountAsync("SELECT count(*) FROM document_contents;"));
        Assert.Equal(0, await FtsCountAsync("zebra"));
        Assert.Equal(1, await FtsCountAsync("giraffe"));
        Assert.Equal(1, await _store.CountAsync("SELECT count(*) FROM notes_fts;"));
        Assert.Empty(await Nodes.GetTrashAsync(Ws));
        Assert.Equal([keep.Id], (await Nodes.GetAllNodesAsync(Ws)).Select(node => node.Id));
    }

    [Fact]
    public async Task ClosingAnUntouchedUntitledNoteDiscardsItSilently()
    {
        var keep = await Nodes.CreateFolderAsync(Ws, null, "keep");
        var note = await Nodes.CreateDocumentAsync(Ws, null);
        await _store.Services.Notifier.WhenDeliveredAsync();
        var events = new List<NodeChange>();
        _store.Services.Notifier.Changed += (_, e) =>
        {
            lock (events)
            {
                events.AddRange(e.Changes);
            }
        };

        Assert.True(await Nodes.DiscardIfPristineAsync(note.Id));

        Assert.Null(await Nodes.GetNodeAsync(note.Id));
        Assert.Empty(await Nodes.GetTrashAsync(Ws));
        Assert.Equal([keep.Id], (await _store.Queue.GetPendingAsync()).Select(op => op.NodeId));
        await _store.Services.Notifier.WhenDeliveredAsync();
        Assert.Equal([new NodeChange(note.Id, NodeChangeKind.Deleted)], events);
        Assert.False(await Nodes.DiscardIfPristineAsync(note.Id));
    }

    [Fact]
    public async Task NotesThatWereTouchedAreNeverDiscarded()
    {
        async Task<NodeInfo> UntitledAsync()
        {
            _store.Clock.Advance(TimeSpan.FromSeconds(1));
            var created = await Nodes.CreateDocumentAsync(Ws, null);
            _store.Clock.Advance(TimeSpan.FromSeconds(1));
            return created;
        }

        var typed = await UntitledAsync();
        await _store.Documents.SaveContentAsync(typed.Id, "words");
        var typedAndCleared = await UntitledAsync();
        await _store.Documents.SaveContentAsync(typedAndCleared.Id, "words");
        await _store.Documents.SaveContentAsync(typedAndCleared.Id, string.Empty);
        var formatChanged = await UntitledAsync();
        await _store.Documents.SetFormatAsync(formatChanged.Id, DocumentFormat.Json);
        var renamed = await UntitledAsync();
        await Nodes.RenameAsync(renamed.Id, "Ideas");
        var renamedToUntitled = await UntitledAsync();
        await Nodes.RenameAsync(renamedToUntitled.Id, "Untitled 99");
        var moved = await UntitledAsync();
        await Nodes.MoveAsync(moved.Id, (await Nodes.CreateFolderAsync(Ws, null, "Folder")).Id);
        var pinned = await UntitledAsync();
        await Nodes.PinAsync(pinned.Id);
        var linked = await UntitledAsync();
        await Nodes.CreateDocumentAsync(Ws, null, "linker", initialText: LinkParser.CreateUri(linked.Id));
        var synced = await UntitledAsync();
        await _store.ExecuteRawAsync("UPDATE nodes SET meta_server_rev = 1 WHERE id = $id;", ("$id", synced.Id.ToString()));
        var sent = await UntitledAsync();
        await _store.ExecuteRawAsync("UPDATE pending_operations SET attempts = 1 WHERE node_id = $id;", ("$id", sent.Id.ToString()));
        var trashed = await UntitledAsync();
        await Nodes.TrashAsync([trashed.Id]);
        var named = await Nodes.CreateDocumentAsync(Ws, null, "Empty on purpose.md");
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Untitled 5");

        foreach (var node in new[] { typed, typedAndCleared, formatChanged, renamed, renamedToUntitled, moved, pinned, linked, synced, sent, trashed, named, folder })
        {
            Assert.False(await Nodes.DiscardIfPristineAsync(node.Id), node.Name);
            Assert.NotNull(await Nodes.GetNodeAsync(node.Id));
        }

        Assert.False(await Nodes.DiscardIfPristineAsync(NodeId.New()));
    }

    [Fact]
    public async Task DuplicatingANameAtTheLengthLimitKeepsItValid()
    {
        var name = new string('x', 244) + "😀yy.md";
        var doc = await Nodes.CreateDocumentAsync(Ws, null, name);

        var copy = await Nodes.DuplicateAsync(doc.Id);

        Assert.Equal(new string('x', 244) + " (copy).md", copy.Name);
        Assert.Equal(copy.Name, (await Nodes.GetNodeAsync(copy.Id))!.Name);
        var longExtension = await Nodes.CreateDocumentAsync(Ws, null, "a." + new string('b', 252));
        var second = await Nodes.DuplicateAsync(longExtension.Id);
        Assert.Equal(NodeNames.MaxLength, second.Name.Length);
        Assert.Equal(second.Name, (await Nodes.GetNodeAsync(second.Id))!.Name);
    }

    [Fact]
    public async Task DeleteForeverOfALiveNodeTrashesItFirst()
    {
        var doc = await Nodes.CreateDocumentAsync(Ws, null);

        Assert.Equal(1, await Nodes.DeleteForeverAsync([doc.Id]));
        Assert.Null(await Nodes.GetNodeAsync(doc.Id));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Nodes.DeleteForeverAsync([doc.Id]));
    }

    [Fact]
    public async Task EmptyTrashDeletesEverythingTrashedIncludingNestedBatches()
    {
        var parent = await Nodes.CreateFolderAsync(Ws, null, "Parent");
        var child = await Nodes.CreateDocumentAsync(Ws, parent.Id, "child");
        var loose = await Nodes.CreateDocumentAsync(Ws, null, "loose");
        var live = await Nodes.CreateDocumentAsync(Ws, null, "live");
        await Nodes.TrashAsync([child.Id]);
        await Nodes.TrashAsync([parent.Id]);
        await Nodes.TrashAsync([loose.Id]);

        Assert.Equal(3, await Nodes.EmptyTrashAsync(Ws));

        Assert.Empty(await Nodes.GetTrashAsync(Ws));
        Assert.Equal(1, await _store.CountAsync("SELECT count(*) FROM nodes;"));
        Assert.Equal(live.Id, (await Nodes.GetAllNodesAsync(Ws)).Single().Id);
    }

    [Fact]
    public async Task PurgeExpiredTrashDeletesOnlyItemsOlderThanThirtyDays()
    {
        var old = await Nodes.CreateFolderAsync(Ws, null, "old");
        var oldChild = await Nodes.CreateDocumentAsync(Ws, old.Id, "old child");
        var recent = await Nodes.CreateDocumentAsync(Ws, null, "recent");
        await Nodes.TrashAsync([old.Id]);
        _store.Clock.Advance(TimeSpan.FromDays(10));
        await Nodes.TrashAsync([recent.Id]);

        _store.Clock.Advance(TimeSpan.FromDays(19));
        var trash = await Nodes.GetTrashAsync(Ws);
        Assert.Equal([11, 1], trash.Select(item => item.DaysLeft).OrderDescending());
        Assert.Equal(0, await Nodes.PurgeExpiredTrashAsync());

        _store.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await Nodes.GetTrashAsync(Ws)).Single(item => item.Node.Id == old.Id).DaysLeft);
        Assert.Equal(2, await Nodes.PurgeExpiredTrashAsync());

        Assert.Null(await Nodes.GetNodeAsync(old.Id));
        Assert.Null(await Nodes.GetNodeAsync(oldChild.Id));
        Assert.Equal([recent.Id], (await Nodes.GetTrashAsync(Ws)).Select(item => item.Node.Id));
        Assert.Equal(10, (await Nodes.GetTrashAsync(Ws))[0].DaysLeft);

        _store.Clock.Advance(TimeSpan.FromDays(20));
        Assert.Equal(1, await Nodes.PurgeExpiredTrashAsync());
        Assert.Empty(await Nodes.GetTrashAsync(Ws));
    }

    // ------------------------------------------------------------ reads

    [Fact]
    public async Task GetPathReturnsAncestorsFromTheRoot()
    {
        var a = await Nodes.CreateFolderAsync(Ws, null, "a");
        var b = await Nodes.CreateFolderAsync(Ws, a.Id, "b");
        var c = await Nodes.CreateDocumentAsync(Ws, b.Id, "c");

        Assert.Equal(["a", "b", "c"], (await Nodes.GetPathAsync(c.Id)).Select(node => node.Name));
        Assert.Equal(["a"], (await Nodes.GetPathAsync(a.Id)).Select(node => node.Name));
        Assert.Empty(await Nodes.GetPathAsync(NodeId.New()));
    }

    [Fact]
    public async Task GetAllNodesReturnsOnlyLiveNodesGroupedByParent()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "folder");
        await Nodes.CreateDocumentAsync(Ws, folder.Id, "y");
        await Nodes.CreateDocumentAsync(Ws, folder.Id, "x", position: InsertPosition.Start);
        var trashed = await Nodes.CreateDocumentAsync(Ws, null, "trashed");
        await Nodes.TrashAsync([trashed.Id]);

        var all = await Nodes.GetAllNodesAsync(Ws);

        Assert.Equal(["folder", "x", "y"], all.Select(node => node.Name));
        Assert.Equal(await Nodes.GetChildrenAsync(Ws, folder.Id), all.Where(node => node.ParentId == folder.Id).ToList());
    }

    // ------------------------------------------------------------ notifications

    [Fact]
    public async Task ChangesArePublishedAfterCommitOffTheWriterThread()
    {
        var events = new List<WorkspaceChangedEventArgs>();
        var writerThreads = new List<bool>();
        _store.Services.Notifier.Changed += (_, args) =>
        {
            lock (events)
            {
                events.Add(args);
                writerThreads.Add(_store.Database.Writer.IsWriterThread);
            }
        };

        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var doc = await Nodes.CreateDocumentAsync(Ws, folder.Id, "doc");
        await Nodes.RenameAsync(doc.Id, "doc.md");
        await Assert.ThrowsAsync<NodeCycleException>(() => Nodes.MoveAsync(folder.Id, folder.Id));
        await Nodes.TrashAsync([folder.Id]);
        await _store.Services.Notifier.WhenDeliveredAsync();

        lock (events)
        {
            Assert.All(events, args => Assert.Equal(Ws, args.WorkspaceId));
            Assert.All(writerThreads, isWriterThread => Assert.False(isWriterThread));
            Assert.Equal(
                [
                    [new NodeChange(folder.Id, NodeChangeKind.Created)],
                    [new NodeChange(doc.Id, NodeChangeKind.Created)],
                    [new NodeChange(doc.Id, NodeChangeKind.Renamed), new NodeChange(doc.Id, NodeChangeKind.FormatChanged)],
                    [new NodeChange(folder.Id, NodeChangeKind.Trashed), new NodeChange(doc.Id, NodeChangeKind.Trashed)],
                ],
                events.Select(args => args.Changes.ToArray()).ToArray());
        }
    }

    [Fact]
    public async Task AFailingHandlerDoesNotStopDelivery()
    {
        Exception? failure = null;
        var delivered = 0;
        _store.Services.Notifier.HandlerFailed += (_, ex) => failure = ex;
        _store.Services.Notifier.Changed += (_, _) => throw new InvalidOperationException("handler");
        _store.Services.Notifier.Changed += (_, _) => Interlocked.Increment(ref delivered);

        await Nodes.CreateFolderAsync(Ws, null, "a");
        await Nodes.CreateFolderAsync(Ws, null, "b");
        await _store.Services.Notifier.WhenDeliveredAsync();

        Assert.Equal(2, delivered);
        Assert.Equal("handler", failure?.Message);
    }

    // ------------------------------------------------------------ helpers

    private async Task<List<string>> ChildNamesAsync(NodeId? parentId) =>
        (await Nodes.GetChildrenAsync(Ws, parentId)).Select(node => node.Name).ToList();

    private async Task<string> TextOfAsync(NodeId id) =>
        (await _store.QueryAsync("SELECT text FROM document_contents WHERE node_id = $id;", reader => reader.GetString(0), ("$id", id.ToString()))).Single();

    private Task<long> FtsCountAsync(string match) =>
        _store.CountAsync("SELECT count(*) FROM notes_fts WHERE notes_fts MATCH $match;", ("$match", match));
}
