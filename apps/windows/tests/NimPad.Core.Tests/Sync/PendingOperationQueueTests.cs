using System.Text.Json;
using System.Text.Json.Nodes;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Sync;
using NimPad.Core.Tests.Support;
using NimPad.Core.Text;
using Xunit;

namespace NimPad.Core.Tests.Sync;

public sealed class PendingOperationQueueTests : IAsyncLifetime
{
    private readonly ServerReplica _server = new();
    private TestStore _store = null!;

    private NodeService Nodes => _store.Nodes;

    private PendingOperationQueue Queue => _store.Queue;

    private WorkspaceId Ws => _store.Workspace.Id;

    public async ValueTask InitializeAsync() => _store = await TestStore.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreatesQueueTheFullInitialState()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Projects");
        var document = await Nodes.CreateDocumentAsync(Ws, folder.Id, "groceries.md", initialText: "- milk\r\n");

        var operations = await Queue.GetPendingAsync();
        Assert.Equal(2, operations.Count);
        var deviceId = await _store.Services.Workspaces.GetDeviceIdAsync();

        var createFolder = operations[0];
        Assert.Equal(PendingOperationKind.Create, createFolder.Kind);
        Assert.Equal(folder.Id, createFolder.NodeId);
        Assert.Equal(Ws, createFolder.WorkspaceId);
        Assert.Null(createFolder.BaseMetaRev);
        Assert.Null(createFolder.BaseContentRev);
        Assert.Equal(deviceId, createFolder.DeviceId);
        Assert.Equal(LocalActorKind.User, createFolder.ActorKind);
        Assert.Equal(TestStore.Start, createFolder.CreatedAt);
        Assert.Equal(0, createFolder.Attempts);
        Assert.Null(createFolder.LastError);
        Assert.Equal(7, createFolder.OpId.Version);
        AssertJson("""{"nodeKind":"folder","parentId":null,"name":"Projects","position":"a0"}""", createFolder.PayloadJson);

        var createDocument = operations[1];
        Assert.True(createDocument.Seq > createFolder.Seq);
        AssertJson(
            $$"""
            {"nodeKind":"document","parentId":"{{folder.Id}}","name":"groceries.md","position":"a0","format":"markdown","checksum":"{{ContentCanonicalizer.Checksum("- milk\n")}}"}
            """,
            createDocument.PayloadJson);
        Assert.Equal(2, await Queue.CountAsync());
    }

    [Fact]
    public async Task RenamesAndFormatChangesFoldIntoAPendingCreate()
    {
        var document = await Nodes.CreateDocumentAsync(Ws, null, "Untitled 1", initialText: "{}");
        await Nodes.RenameAsync(document.Id, "settings.json");
        await Nodes.RenameAsync(document.Id, "app-settings.json");

        var operation = Assert.Single(await Queue.GetPendingAsync());
        Assert.Equal(PendingOperationKind.Create, operation.Kind);
        AssertJson(
            $$"""
            {"nodeKind":"document","parentId":null,"name":"app-settings.json","position":"a0","format":"json","checksum":"{{ContentCanonicalizer.Checksum("{}")}}"}
            """,
            operation.PayloadJson);
    }

    [Fact]
    public async Task ContentUpdatesFoldIntoAPendingCreate()
    {
        var document = await Nodes.CreateDocumentAsync(Ws, null, "notes.md");
        await UpdateContentAsync(document.Id, "first", DocumentFormat.Markdown);
        await UpdateContentAsync(document.Id, "second", DocumentFormat.Plain);

        var operation = Assert.Single(await Queue.GetPendingAsync());
        Assert.Equal("plain", operation.Payload["format"]!.GetValue<string>());
        Assert.Equal(ContentCanonicalizer.Checksum("second"), operation.Payload["checksum"]!.GetValue<string>());
        Assert.Equal("notes.md", operation.Payload["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASyncedNodeHasAtMostOneUpdateContentKeepingTheOriginalBase()
    {
        var document = await CreateSyncedDocumentAsync("doc.md", metaRev: 3, contentRev: 7);

        await UpdateContentAsync(document.Id, "v1", DocumentFormat.Markdown);
        var first = Assert.Single(await Queue.GetPendingAsync());
        // The server moved on (for example after a pull); the queued update keeps the base it was made against.
        await _store.ExecuteRawAsync("UPDATE nodes SET content_server_rev = 8 WHERE id = $id;", ("$id", document.Id.ToString()));
        await UpdateContentAsync(document.Id, "v2", DocumentFormat.Markdown);
        await UpdateContentAsync(document.Id, "v3", DocumentFormat.Json);

        var operation = Assert.Single(await Queue.GetPendingAsync());
        Assert.Equal(PendingOperationKind.UpdateContent, operation.Kind);
        Assert.Equal(first.Seq, operation.Seq);
        Assert.Equal(first.OpId, operation.OpId);
        Assert.Equal(3, operation.BaseMetaRev);
        Assert.Equal(7, operation.BaseContentRev);
        AssertJson($$"""{"checksum":"{{ContentCanonicalizer.Checksum("v3")}}","format":"json"}""", operation.PayloadJson);
    }

    [Fact]
    public async Task AnAttemptedUpdateIsNeverRewritten()
    {
        var document = await CreateSyncedDocumentAsync("doc.md", metaRev: 1, contentRev: 1);
        await UpdateContentAsync(document.Id, "sent", DocumentFormat.Markdown);
        await _store.ExecuteRawAsync("UPDATE pending_operations SET attempts = 1, last_error = 'timeout';");

        await UpdateContentAsync(document.Id, "newer", DocumentFormat.Markdown);

        var operations = await Queue.GetPendingAsync();
        Assert.Equal(2, operations.Count);
        Assert.Equal(ContentCanonicalizer.Checksum("sent"), operations[0].Payload["checksum"]!.GetValue<string>());
        Assert.Equal("timeout", operations[0].LastError);
        Assert.Equal(ContentCanonicalizer.Checksum("newer"), operations[1].Payload["checksum"]!.GetValue<string>());
    }

    [Fact]
    public async Task MetadataChangesOfASyncedNodeCarryItsBaseRevisions()
    {
        var folder = await CreateSyncedFolderAsync("Folder", metaRev: 4);
        var target = await CreateSyncedFolderAsync("Target", metaRev: 2);

        await Nodes.RenameAsync(folder.Id, "Renamed");
        await Nodes.MoveAsync(folder.Id, target.Id);
        await Nodes.PinAsync(folder.Id);
        await Nodes.UnpinAsync(folder.Id);

        var operations = await Queue.GetForNodeAsync(folder.Id);
        Assert.Equal(
            [PendingOperationKind.Rename, PendingOperationKind.Move, PendingOperationKind.Pin, PendingOperationKind.Pin],
            operations.Select(op => op.Kind));
        Assert.All(operations, op => Assert.Equal(4, op.BaseMetaRev));
        AssertJson("""{"name":"Renamed"}""", operations[0].PayloadJson);
        AssertJson($$"""{"parentId":"{{target.Id}}","position":"a0"}""", operations[1].PayloadJson);
        AssertJson("""{"pinned":true,"pinPosition":"a0"}""", operations[2].PayloadJson);
        AssertJson("""{"pinned":false,"pinPosition":null}""", operations[3].PayloadJson);
    }

    [Fact]
    public async Task TrashQueuesOneDeletePerTopLevelNodeEachWithItsOwnBatch()
    {
        var parent = await CreateSyncedFolderAsync("Parent", metaRev: 1);
        var a = await CreateSyncedFolderAsync("a", metaRev: 1, parent.Id);
        var aChild = await CreateSyncedFolderAsync("a-child", metaRev: 1, a.Id);
        var b = await CreateSyncedFolderAsync("b", metaRev: 1);
        _store.Clock.Advance(TimeSpan.FromSeconds(1));

        var batches = await Nodes.TrashAsync([a.Id, aChild.Id, b.Id]);

        Assert.Equal(2, batches.Count);
        Assert.NotEqual(batches[0], batches[1]);
        Assert.All(batches, batch => Assert.Equal(7, batch.Value.Version));
        var operations = await Queue.GetPendingAsync();
        Assert.Equal(
            [(a.Id, PendingOperationKind.Delete), (b.Id, PendingOperationKind.Delete)],
            operations.Select(op => (op.NodeId, op.Kind)));
        AssertJson($$"""{"trashBatchId":"{{batches[0]}}","deletedAt":"2026-10-12T13:59:44.123Z"}""", operations[0].PayloadJson);
        AssertJson($$"""{"trashBatchId":"{{batches[1]}}","deletedAt":"2026-10-12T13:59:44.123Z"}""", operations[1].PayloadJson);
        Assert.Equal(batches[0], (await Nodes.GetNodeAsync(aChild.Id))!.TrashBatchId);
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task RestoringAnyNodeOfABatchRestoresTheWholeBatchWithOneOperation()
    {
        var parent = await CreateSyncedFolderAsync("Parent", metaRev: 1);
        var a = await CreateSyncedFolderAsync("a", metaRev: 1, parent.Id);
        var aChild = await CreateSyncedFolderAsync("a-child", metaRev: 1, a.Id);
        var b = await CreateSyncedFolderAsync("b", metaRev: 1);
        var batches = await Nodes.TrashAsync([a.Id, b.Id]);
        await Nodes.TrashAsync([parent.Id]);

        // Restoring a's child brings back a's batch (a with its child), and only that batch.
        var restored = await Nodes.RestoreAsync(aChild.Id);

        Assert.Equal(a.Id, restored.ParentId);
        Assert.False((await Nodes.GetNodeAsync(a.Id))!.IsTrashed);
        Assert.True((await Nodes.GetNodeAsync(b.Id))!.IsTrashed);
        var operations = await Queue.GetPendingAsync();
        Assert.Equal(
            [
                (a.Id, PendingOperationKind.Delete),
                (b.Id, PendingOperationKind.Delete),
                (parent.Id, PendingOperationKind.Delete),
                (a.Id, PendingOperationKind.Restore),
                (a.Id, PendingOperationKind.Move), // its parent is in the Trash, so it went to the root
            ],
            operations.Select(op => (op.NodeId, op.Kind)));
        AssertJson($$"""{"trashBatchId":"{{batches[0]}}"}""", operations[3].PayloadJson);
        // b and Parent are in the Trash, so the root has no live children and a gets the first key.
        AssertJson("""{"parentId":null,"position":"a0"}""", operations[4].PayloadJson);
        await _server.AssertQueueConvergesAsync(_store);

        // Undo of the first trash: the remaining batch comes back too; a's batch no longer exists.
        Assert.Equal([b.Id], (await Nodes.RestoreBatchAsync(batches)).Select(node => node.Id));
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task PurgingANeverSyncedNodeDropsAllItsOperations()
    {
        var keep = await Nodes.CreateFolderAsync(Ws, null, "keep");
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var doc = await Nodes.CreateDocumentAsync(Ws, folder.Id, "doc");
        await Nodes.RenameAsync(doc.Id, "doc.md");
        await Nodes.MoveAsync(doc.Id, null);
        await Nodes.MoveAsync(doc.Id, folder.Id);
        await Nodes.PinAsync(doc.Id);
        await UpdateContentAsync(doc.Id, "text", DocumentFormat.Markdown);
        await Nodes.TrashAsync([folder.Id]);
        Assert.NotEmpty(await Queue.GetForNodeAsync(doc.Id));
        Assert.NotEmpty(await Queue.GetForNodeAsync(folder.Id));

        await Nodes.DeleteForeverAsync([folder.Id]);

        Assert.Empty(await Queue.GetForNodeAsync(doc.Id));
        Assert.Empty(await Queue.GetForNodeAsync(folder.Id));
        var remaining = Assert.Single(await Queue.GetPendingAsync());
        Assert.Equal(keep.Id, remaining.NodeId);
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task PurgingASyncedSubtreeQueuesOnePurgeTheServerCanApply()
    {
        var root = await CreateSyncedFolderAsync("Root", metaRev: 5);
        var syncedChild = await CreateSyncedFolderAsync("Synced child", metaRev: 2, root.Id);
        var localChild = await Nodes.CreateFolderAsync(Ws, root.Id, "Local child");
        var localGrandchild = await Nodes.CreateDocumentAsync(Ws, localChild.Id, "draft.md", initialText: "draft");
        var syncedGrandchild = await CreateSyncedFolderAsync("Moved in", metaRev: 9);
        await Nodes.MoveAsync(syncedGrandchild.Id, localChild.Id);
        await Nodes.TrashAsync([root.Id]);
        _store.Clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(5, await Nodes.PurgeExpiredTrashAsync());

        // The synced node was moved into the never-synced folder, so that folder must exist on the server for the
        // move; it then goes with the root. The never-synced note nothing refers to is cancelled outright.
        var operations = await Queue.GetPendingAsync();
        Assert.Equal(
            [
                (localChild.Id, PendingOperationKind.Create),
                (syncedGrandchild.Id, PendingOperationKind.Move),
                (root.Id, PendingOperationKind.Delete),
                (root.Id, PendingOperationKind.Purge),
            ],
            operations.Select(op => (op.NodeId, op.Kind)));
        Assert.Equal(LocalActorKind.System, operations[3].ActorKind);
        Assert.Equal("{}", operations[3].PayloadJson);
        Assert.Equal(5, operations[3].BaseMetaRev);
        Assert.DoesNotContain(operations, op => op.NodeId == syncedChild.Id || op.NodeId == localGrandchild.Id);
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task PurgingAFolderAnotherNoteWasDraggedOutOfKeepsTheFolderForThatNotesCreate()
    {
        // Create a folder and a note in it, drag the note out, delete the folder and empty the Trash.
        var folder = await Nodes.CreateFolderAsync(Ws, null, "F");
        var doc = await Nodes.CreateDocumentAsync(Ws, folder.Id, "D.txt", initialText: "kept");
        await Nodes.MoveAsync(doc.Id, null);
        await Nodes.TrashAsync([folder.Id]);

        await Nodes.EmptyTrashAsync(Ws);

        var operations = await Queue.GetPendingAsync();
        Assert.Equal(
            [
                (folder.Id, PendingOperationKind.Create),
                (doc.Id, PendingOperationKind.Create),
                (doc.Id, PendingOperationKind.Move),
                (folder.Id, PendingOperationKind.Delete),
                (folder.Id, PendingOperationKind.Purge),
            ],
            operations.Select(op => (op.NodeId, op.Kind)));
        Assert.Equal(folder.Id.ToString(), operations[1].Payload["parentId"]!.GetValue<string>());
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task PurgingAFolderASyncedNodePassedThroughKeepsTheFolderForThatMove()
    {
        var x = await CreateSyncedFolderAsync("X", metaRev: 3);
        var g = await Nodes.CreateFolderAsync(Ws, null, "G");
        await Nodes.MoveAsync(x.Id, g.Id);
        await Nodes.MoveAsync(x.Id, null);
        await Nodes.TrashAsync([g.Id]);

        await Nodes.DeleteForeverAsync([g.Id]);

        var operations = await Queue.GetPendingAsync();
        Assert.Equal(
            [
                (g.Id, PendingOperationKind.Create),
                (x.Id, PendingOperationKind.Move),
                (x.Id, PendingOperationKind.Move),
                (g.Id, PendingOperationKind.Delete),
                (g.Id, PendingOperationKind.Purge),
            ],
            operations.Select(op => (op.NodeId, op.Kind)));
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task ANestedChainOfNeverSyncedFoldersIsKeptUpToTheOneThatIsReferenced()
    {
        // outer ⊃ middle ⊃ inner, all new; a note created in inner leaves before outer is deleted forever.
        var outer = await Nodes.CreateFolderAsync(Ws, null, "outer");
        var middle = await Nodes.CreateFolderAsync(Ws, outer.Id, "middle");
        var inner = await Nodes.CreateFolderAsync(Ws, middle.Id, "inner");
        var sibling = await Nodes.CreateFolderAsync(Ws, outer.Id, "sibling");
        var note = await Nodes.CreateDocumentAsync(Ws, inner.Id, "escapee");
        await Nodes.MoveAsync(note.Id, null);

        await Nodes.DeleteForeverAsync([outer.Id]);

        var kept = (await Queue.GetPendingAsync()).Select(op => op.NodeId).Distinct().ToHashSet();
        Assert.Contains(outer.Id, kept);
        Assert.Contains(middle.Id, kept);
        Assert.Contains(inner.Id, kept);
        Assert.DoesNotContain(sibling.Id, kept);
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task ANodeWithAnAttemptedOperationIsTreatedAsKnownToTheServer()
    {
        // The create was sent but its acknowledgement was lost: the server may have the note.
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Sent");
        await _store.ExecuteRawAsync("UPDATE pending_operations SET attempts = 1, last_error = 'timeout' WHERE node_id = $id;", ("$id", folder.Id.ToString()));

        await Nodes.DeleteForeverAsync([folder.Id]);

        var operations = await Queue.GetForNodeAsync(folder.Id);
        Assert.Equal(
            [PendingOperationKind.Create, PendingOperationKind.Delete, PendingOperationKind.Purge],
            operations.Select(op => op.Kind));
        Assert.Equal(1, operations[0].Attempts);
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task PurgeDropsQueuedContentUpdatesWhoseTextIsGone()
    {
        var doc = await CreateSyncedDocumentAsync("doc.md", metaRev: 2, contentRev: 4);
        var sent = await CreateSyncedDocumentAsync("sent.md", metaRev: 2, contentRev: 4);
        await _store.Documents.SaveContentAsync(doc.Id, "edited");
        await _store.Documents.SaveContentAsync(sent.Id, "edited and sent");
        await _store.ExecuteRawAsync("UPDATE pending_operations SET attempts = 1 WHERE node_id = $id;", ("$id", sent.Id.ToString()));
        await Nodes.RenameAsync(doc.Id, "renamed.md");

        await Nodes.DeleteForeverAsync([doc.Id, sent.Id]);

        Assert.Equal(
            [PendingOperationKind.Rename, PendingOperationKind.Delete, PendingOperationKind.Purge],
            (await Queue.GetForNodeAsync(doc.Id)).Select(op => op.Kind));
        // An attempted update may already have been applied, so it stays.
        Assert.Equal(
            [PendingOperationKind.UpdateContent, PendingOperationKind.Delete, PendingOperationKind.Purge],
            (await Queue.GetForNodeAsync(sent.Id)).Select(op => op.Kind));
    }

    [Fact]
    public async Task DeleteForeverOfALiveSyncedNodeQueuesItsDeleteBeforeThePurge()
    {
        var doc = await CreateSyncedDocumentAsync("doc.md", metaRev: 6, contentRev: 2);

        await Nodes.DeleteForeverAsync([doc.Id]);

        var operations = await Queue.GetPendingAsync();
        Assert.Equal([PendingOperationKind.Delete, PendingOperationKind.Purge], operations.Select(op => op.Kind));
        Assert.All(operations, op => Assert.Equal(doc.Id, op.NodeId));
        Assert.All(operations, op => Assert.Equal(6, op.BaseMetaRev));
        Assert.All(operations, op => Assert.Equal(2, op.BaseContentRev));
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task CopyingASubtreeQueuesACreatePerCopyParentsFirst()
    {
        var target = await CreateSyncedFolderAsync("Target", metaRev: 1);
        var folder = await CreateSyncedFolderAsync("Folder", metaRev: 1);
        var sub = await CreateSyncedFolderAsync("Sub", metaRev: 1, folder.Id);
        var doc = await CreateSyncedDocumentAsync("doc.md", metaRev: 1, contentRev: 1, sub.Id, "body");

        var copy = Assert.Single(await Nodes.CopyAsync([folder.Id], target.Id));
        var duplicate = await Nodes.DuplicateAsync(doc.Id);

        var operations = await Queue.GetPendingAsync();
        Assert.All(operations, op => Assert.Equal(PendingOperationKind.Create, op.Kind));
        Assert.Equal(4, operations.Count);
        var subCopy = (await Nodes.GetChildrenAsync(Ws, copy.Id)).Single();
        var docCopy = (await Nodes.GetChildrenAsync(Ws, subCopy.Id)).Single();
        Assert.Equal([copy.Id, subCopy.Id, docCopy.Id, duplicate.Id], operations.Select(op => op.NodeId));
        AssertJson($$"""{"nodeKind":"folder","parentId":"{{target.Id}}","name":"Folder","position":"a0"}""", operations[0].PayloadJson);
        AssertJson($$"""{"nodeKind":"folder","parentId":"{{copy.Id}}","name":"Sub","position":"{{sub.Position}}"}""", operations[1].PayloadJson);
        AssertJson(
            $$"""{"nodeKind":"document","parentId":"{{subCopy.Id}}","name":"doc.md","position":"{{doc.Position}}","format":"markdown","checksum":"{{ContentCanonicalizer.Checksum("body")}}"}""",
            operations[2].PayloadJson);
        AssertJson(
            $$"""{"nodeKind":"document","parentId":"{{sub.Id}}","name":"doc (copy).md","position":"{{duplicate.Position}}","format":"markdown","checksum":"{{ContentCanonicalizer.Checksum("body")}}"}""",
            operations[3].PayloadJson);
        await _server.AssertQueueConvergesAsync(_store);
    }

    [Fact]
    public async Task AFailedOperationQueuesNothing()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Folder");
        var before = await Queue.CountAsync();

        await Assert.ThrowsAsync<NodeCycleException>(() => Nodes.MoveAsync(folder.Id, folder.Id));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Nodes.RenameAsync(NodeId.New(), "x"));

        Assert.Equal(before, await Queue.CountAsync());
    }

#if DEBUG
    [Fact]
    public async Task DebugBuildsRefuseToQueueAnOperationThatBreaksTheContract()
    {
        var folder = await CreateSyncedFolderAsync("Folder", metaRev: 2);
        var before = await Queue.CountAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _store.Database.Writer.ExecuteAsync(context =>
        {
            context.Execute("UPDATE nodes SET name = ' untrimmed' WHERE id = $id;", ("$id", folder.Id));
            Queue.EnqueueRename(context, NodeRows.Get(context, folder.Id)!, LocalActorKind.User);
        }));

        Assert.Contains("payload.name", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await Queue.CountAsync());
        Assert.Equal("Folder", (await Nodes.GetNodeAsync(folder.Id))!.Name);
    }
#endif

    [Fact]
    public async Task GetPendingReturnsTheOldestFirstUpToTheLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await Nodes.CreateFolderAsync(Ws, null, $"F{i}");
        }

        var firstThree = await Queue.GetPendingAsync(limit: 3);
        var all = await Queue.GetPendingAsync();

        Assert.Equal(3, firstThree.Count);
        Assert.Equal(all.Take(3), firstThree);
        Assert.Equal(all.OrderBy(op => op.Seq), all);
        Assert.Equal(5, await Queue.CountAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Queue.GetPendingAsync(limit: 0));
    }

    [Fact]
    public async Task ContractJsonHasExactlyTheContractFields()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Projects");
        var operation = Assert.Single(await Queue.GetPendingAsync());
        var deviceId = await _store.Services.Workspaces.GetDeviceIdAsync();

        AssertJson(
            $$"""
            {
              "opId": "{{operation.OpId}}",
              "workspaceId": "{{Ws}}",
              "nodeId": "{{folder.Id}}",
              "kind": "create",
              "payload": { "nodeKind": "folder", "parentId": null, "name": "Projects", "position": "a0" },
              "baseMetaRev": null,
              "baseContentRev": null,
              "deviceId": "{{deviceId}}",
              "actorKind": "user",
              "createdAt": "2026-10-12T13:59:43.123Z"
            }
            """,
            operation.ToContractJson());
        Assert.Equal(operation, PendingOperation.FromContractJson(operation.ToContractJson(), operation.Seq));
    }

    [Fact]
    public void SharedFixtureOperationsRoundTripAndMatchThePayloadBuilders()
    {
        var valid = ContractFixtures.Load("pending-operations.json").GetProperty("valid").EnumerateArray().ToList();
        Assert.NotEmpty(valid);
        var kinds = new HashSet<PendingOperationKind>();
        foreach (var example in valid)
        {
            var description = example.GetProperty("description").GetString();
            var json = example.GetProperty("operation").GetRawText();
            var operation = PendingOperation.FromContractJson(json);
            kinds.Add(operation.Kind);

            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(operation.ToContractJson())), $"round trip: {description}");
            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(operation.PayloadJson), JsonNode.Parse(Rebuild(operation))),
                $"payload builder: {description}");
        }

        Assert.Equal(Enum.GetValues<PendingOperationKind>().Length, kinds.Count);
    }

    // Rebuilds a fixture payload from its values with the same builders the queue uses.
    private static string Rebuild(PendingOperation operation)
    {
        var payload = operation.Payload;
        string Text(string name) => payload[name]!.GetValue<string>();
        NodeId? Parent() => payload["parentId"] is { } parent ? NodeId.Parse(parent.GetValue<string>()) : null;
        return operation.Kind switch
        {
            PendingOperationKind.Create when Text("nodeKind") == "folder" =>
                PendingOperationPayloads.Create(NodeKind.Folder, Parent(), Text("name"), Text("position"), null, null),
            PendingOperationKind.Create =>
                PendingOperationPayloads.Create(NodeKind.Document, Parent(), Text("name"), Text("position"), DocumentFormats.Parse(Text("format")), Text("checksum")),
            PendingOperationKind.UpdateContent => PendingOperationPayloads.UpdateContent(Text("checksum"), DocumentFormats.Parse(Text("format"))),
            PendingOperationKind.Rename => PendingOperationPayloads.Rename(Text("name")),
            PendingOperationKind.Move => PendingOperationPayloads.Move(Parent(), Text("position")),
            PendingOperationKind.Pin => PendingOperationPayloads.Pin(payload["pinned"]!.GetValue<bool>(), payload["pinPosition"]?.GetValue<string>()),
            PendingOperationKind.Delete => PendingOperationPayloads.Delete(TrashBatchId.Parse(Text("trashBatchId")), NimPad.Core.Time.Timestamps.Parse(Text("deletedAt"))),
            PendingOperationKind.Restore => PendingOperationPayloads.Restore(TrashBatchId.Parse(Text("trashBatchId"))),
            PendingOperationKind.Purge => PendingOperationPayloads.Purge(),
            _ => throw new InvalidOperationException(),
        };
    }

    // The Stage 2 DocumentService path: change the text and queue update_content in one transaction.
    private Task UpdateContentAsync(NodeId nodeId, string text, DocumentFormat format) =>
        _store.Database.Writer.ExecuteAsync(context =>
        {
            context.Execute(
                "UPDATE document_contents SET text = $text, format = $format, checksum = $checksum WHERE node_id = $id;",
                ("$text", text),
                ("$format", format.ToWireName()),
                ("$checksum", ContentCanonicalizer.Checksum(text)),
                ("$id", nodeId));
            var node = NodeRows.Get(context, nodeId)!;
            Queue.EnqueueUpdateContent(context, node, ContentCanonicalizer.Checksum(text), format, LocalActorKind.User);
        });

    // Creates nodes and then marks them as acknowledged by the server (revisions set, create removed from the queue,
    // and the server replica holding them as they are now).
    private async Task<NodeInfo> CreateSyncedDocumentAsync(string name, long metaRev, long contentRev, NodeId? parentId = null, string? text = null)
    {
        var node = await Nodes.CreateDocumentAsync(Ws, parentId, name, initialText: text);
        return await MarkSyncedAsync(node.Id, metaRev, contentRev);
    }

    private async Task<NodeInfo> CreateSyncedFolderAsync(string name, long metaRev, NodeId? parentId = null)
    {
        var node = await Nodes.CreateFolderAsync(Ws, parentId, name);
        return await MarkSyncedAsync(node.Id, metaRev, null);
    }

    private async Task<NodeInfo> MarkSyncedAsync(NodeId id, long metaRev, long? contentRev)
    {
        await _store.ExecuteRawAsync(
            """
            UPDATE nodes SET meta_server_rev = $meta, content_server_rev = $content WHERE id = $id;
            DELETE FROM pending_operations WHERE node_id = $id;
            """,
            ("$meta", metaRev),
            ("$content", contentRev),
            ("$id", id.ToString()));
        var node = (await Nodes.GetNodeAsync(id))!;
        _server.AddSynced(node);
        return node;
    }

    private static void AssertJson(string expected, string actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"Expected {JsonSerializer.Serialize(JsonNode.Parse(expected))} but got {actual}");
}
