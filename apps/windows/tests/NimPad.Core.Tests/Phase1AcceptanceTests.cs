using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using NimPad.Core.Domain;
using NimPad.Core.Files;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Storage;
using NimPad.Core.Tests.Support;
using NimPad.Core.Text;
using Xunit;

namespace NimPad.Core.Tests;

/// <summary>
/// Spec §12, Phase 1 acceptance: "A user can create hundreds of notes, organize them in nested folders, close the
/// application, reopen it, and recover everything without using the internet." Everything goes through the public
/// services, the way the App drives them.
/// </summary>
public class Phase1AcceptanceTests(ITestOutputHelper output)
{
    private const int NoteCount = 500;
    private const int Depth = 6;

    // Every table that holds user data, with a total order for comparison.
    private static readonly (string Table, string OrderBy)[] Tables =
    [
        ("device", "device_id"), ("workspaces", "id"), ("nodes", "id"), ("document_contents", "node_id"),
        ("document_links", "source_id, target_id"), ("pending_operations", "seq"), ("session_tabs", "tab_id"),
        ("recently_closed", "seq"), ("ui_state", "key"), ("settings", "key"), ("external_file_buffers", "path"),
        ("notes_fts", "rowid"),
    ];

    [Fact]
    public async Task FiveHundredNotesInASixLevelTreeComeBackExactlyAfterAReopen()
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(TestStore.Start);
        Snapshot before;
        WorkspaceId workspaceId;
        World world;
        using (var store = await TestStore.CreateAsync(temp, clock))
        {
            workspaceId = store.Workspace.Id;
            world = await BuildWorkspaceAsync(store, new RealisticText(seed: 1));
            await OrganizeAsync(store, world);
            await RecordSessionAsync(store, world);
            before = await SnapshotAsync(store);

            Assert.Equal(2, world.DeletedForever);
            Assert.Equal(NoteCount + world.Duplicates - world.DeletedForever, before.Contents.Count);
            Assert.Equal(Depth, before.MaxFolderDepth);
            Assert.True(before.Pinned.Count >= 10);
            Assert.NotEmpty(before.Trash);
            output.WriteLine(
                $"Before closing: {before.Live.Count} live nodes, {before.Trash.Count} Trash items, {before.Contents.Count} notes, " +
                $"{before.Pinned.Count} pinned, {before.Tabs.Count} tabs, {before.Tables["pending_operations"].Count} queued operations");
        }

        // Closed: services and database disposed, every pooled connection released.
        SqliteConnection.ClearAllPools();

        using var reopened = await TestStore.CreateAsync(temp, clock);
        Assert.Equal(StartupRecovery.Healthy, reopened.Database.Recovery);
        Assert.Equal(workspaceId, reopened.Workspace.Id);
        var after = await SnapshotAsync(reopened);

        Assert.Equal(before.Live, after.Live);
        Assert.Equal(before.Pinned, after.Pinned);
        Assert.Equal(before.Trash, after.Trash);
        Assert.Equal(before.Contents, after.Contents);
        Assert.Equal(before.Tabs, after.Tabs);
        Assert.Equal(before.RecentlyClosed, after.RecentlyClosed);
        Assert.Equal(before.UiState, after.UiState);
        Assert.Equal(before.Settings, after.Settings);
        Assert.Equal(before.Pending, after.Pending);
        Assert.Equal(before.Searches, after.Searches);
        foreach (var (table, _) in Tables)
        {
            Assert.Equal(before.Tables[table], after.Tables[table]);
        }

        // Beyond "the same as before closing": exactly what the test did, and a queue the server could replay to the
        // same tree from scratch (nothing was ever synced).
        await AssertMatchesWhatWasDoneAsync(reopened, world);
        await new ServerReplica().AssertQueueConvergesAsync(reopened);

        // Every stored checksum matches its text, and the tree is intact.
        Assert.All(after.Contents.Values, content => Assert.Equal(ContentCanonicalizer.Checksum(content.Text), content.Checksum));
        var folders = after.Live.Where(node => node.IsFolder).Select(node => node.Id).ToHashSet();
        Assert.All(after.Live, node => Assert.True(node.ParentId is null || folders.Contains(node.ParentId.Value)));
    }

    [Fact]
    public async Task CommittedWorkSurvivesACrashWithoutAnyShutdown()
    {
        using var temp = new TempDirectory();
        using var crashCopy = new TempDirectory();
        var clock = new FakeTimeProvider(TestStore.Start);
        using var store = await TestStore.CreateAsync(temp, clock);
        var text = new RealisticText(seed: 2);
        var folder = await store.Nodes.CreateFolderAsync(store.Workspace.Id, null, "Crash test");
        var notes = new List<NodeInfo>();
        for (var i = 0; i < 20; i++)
        {
            notes.Add(await store.Nodes.CreateDocumentAsync(store.Workspace.Id, folder.Id, text.NoteName()));
        }

        // Typing through the autosave scheduler: each save is awaited through its SaveSucceeded event.
        var autosave = store.Services.Autosave;
        var saved = new Dictionary<NodeId, TaskCompletionSource>();
        autosave.SaveSucceeded += (_, e) =>
        {
            lock (saved)
            {
                saved[e.NodeId].TrySetResult();
            }
        };
        var expected = new Dictionary<NodeId, string>();
        foreach (var note in notes.Take(15))
        {
            var buffer = new EditorBuffer(text.NoteBody(300, 900));
            lock (saved)
            {
                saved[note.Id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            autosave.NotifyChanged(note.Id, buffer);
            expected[note.Id] = buffer.GetSnapshot();
        }

        clock.Advance(AutosaveScheduler.DefaultDebounce);
        await Task.WhenAll(saved.Values.Select(done => done.Task)).WaitAsync(TimeSpan.FromSeconds(30));

        // Direct saves, a move, a pin and the session, all awaited.
        foreach (var note in notes.Skip(15))
        {
            var body = text.NoteBody(300, 900);
            await store.Documents.SaveContentAsync(note.Id, body);
            expected[note.Id] = body;
        }

        await store.Nodes.MoveAsync(notes[0].Id, null);
        await store.Nodes.PinAsync(notes[1].Id);
        var tabs = new[] { SessionTab.ForNote(notes[2].Id, notes[2].Name) with { IsActive = true, CaretOffset = 5 } };
        await store.Sessions.SaveTabsAsync(tabs);

        // An edit still inside its debounce window has not been saved yet: a crash now loses only that.
        var pending = new EditorBuffer("typed 100 ms before the crash", version: 2);
        autosave.NotifyChanged(notes[3].Id, pending);

        // The crash: no flush, no checkpoint, no dispose. Copy what is on disk right now, as a power cut would leave
        // it. The database file and its write-ahead log are copied; the shared-memory index is rebuilt on open.
        var walPath = store.DatabasePath + "-wal";
        Assert.True(new FileInfo(walPath).Length > 0, "The committed work should still be in the write-ahead log.");
        var copyPath = crashCopy.Combine("profiles", "local", "nimpad.db");
        Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
        File.Copy(store.DatabasePath, copyPath);
        File.Copy(walPath, copyPath + "-wal");

        using var recovered = await TestStore.CreateAsync(crashCopy, new FakeTimeProvider(clock.GetUtcNow()));
        Assert.Equal(StartupRecovery.Healthy, recovered.Database.Recovery);
        foreach (var (id, body) in expected)
        {
            var content = await recovered.Documents.GetContentAsync(id);
            Assert.Equal(ContentCanonicalizer.NormalizeLineEndings(body), content.Text);
            Assert.Equal(ContentCanonicalizer.Checksum(body), content.Checksum);
        }

        Assert.Null((await recovered.Nodes.GetNodeAsync(notes[0].Id))!.ParentId);
        Assert.True((await recovered.Nodes.GetNodeAsync(notes[1].Id))!.IsPinned);
        Assert.Equal(tabs, await recovered.Sessions.LoadTabsAsync());
        Assert.NotEqual(pending.GetSnapshot(), (await recovered.Documents.GetContentAsync(notes[3].Id)).Text);
        Assert.Equal(
            await store.Queue.GetPendingAsync(10_000),
            await recovered.Queue.GetPendingAsync(10_000));
        Assert.Equal(21, (await recovered.Nodes.GetAllNodesAsync(recovered.Workspace.Id)).Count);
    }

    // ------------------------------------------------------------------ building the workspace

    private sealed record World(List<NodeInfo> Folders, Dictionary<NodeInfo, int> FolderDepth, List<NodeInfo> Notes, ExpectedTree Expected)
    {
        public int DeletedForever { get; set; }

        public int Duplicates { get; set; }

        // The node moved before its first sibling, which must still come first after the reopen.
        public NodeId? ReorderedFirst { get; set; }
    }

    /// <summary>
    /// What the test did, kept independently of the store: every node's parent, name, kind, pin and Trash state and
    /// every document's text and format, following the documented rules (canonical text, format from the extension,
    /// restore by batch, a restored item whose parent is gone goes to the root).
    /// </summary>
    private sealed class ExpectedTree
    {
        private int _nextBatch;

        public Dictionary<NodeId, Node> Nodes { get; } = [];

        public void Created(NodeInfo node, string? text = null) =>
            Nodes[node.Id] = new Node(node.Id, node.Kind)
            {
                ParentId = node.ParentId,
                Name = node.Name,
                Text = node.IsDocument ? ContentCanonicalizer.NormalizeLineEndings(text ?? string.Empty) : null,
                Format = node.IsDocument ? DocumentFormats.FromName(node.Name) : null,
            };

        public void Renamed(NodeId id, string name)
        {
            var node = Nodes[id];
            if (node.Format is { } format && format == DocumentFormats.FromName(node.Name))
            {
                node.Format = DocumentFormats.FromName(name);
            }

            node.Name = name;
        }

        public void Trashed(NodeId id)
        {
            var batch = ++_nextBatch;
            foreach (var node in Subtree(id).Where(node => node.Batch is null))
            {
                node.Batch = batch;
            }
        }

        public void Restored(NodeId id)
        {
            var batch = Nodes[id].Batch!.Value;
            var members = Nodes.Values.Where(node => node.Batch == batch).ToList();
            members.ForEach(node => node.Batch = null);
            foreach (var root in members.Where(node => node.ParentId is not { } parent || !members.Exists(member => member.Id == parent)))
            {
                if (root.ParentId is { } parentId && (!Nodes.TryGetValue(parentId, out var parent) || parent.Batch is not null))
                {
                    root.ParentId = null;
                }
            }
        }

        public void DeletedForever(NodeId id) => Subtree(id).ForEach(node => Nodes.Remove(node.Id));

        private List<Node> Subtree(NodeId id)
        {
            var result = new List<Node> { Nodes[id] };
            for (var i = 0; i < result.Count; i++)
            {
                var parentId = result[i].Id;
                result.AddRange(Nodes.Values.Where(node => node.ParentId == parentId));
            }

            return result;
        }

        public sealed class Node(NodeId id, NodeKind kind)
        {
            public NodeId Id { get; } = id;

            public NodeKind Kind { get; } = kind;

            public NodeId? ParentId { get; set; }

            public string Name { get; set; } = string.Empty;

            public string? Text { get; set; }

            public DocumentFormat? Format { get; set; }

            public bool IsPinned { get; set; }

            public int? Batch { get; set; }
        }
    }

    // The reopened store holds exactly what the test did, node by node.
    private static async Task AssertMatchesWhatWasDoneAsync(TestStore store, World world)
    {
        var stored = await store.QueryAsync("SELECT id FROM nodes;", reader => NodeId.Parse(reader.GetString(0)));
        Assert.Equal(world.Expected.Nodes.Keys.Select(id => id.ToString()).Order(), stored.Select(id => id.ToString()).Order());
        foreach (var expected in world.Expected.Nodes.Values)
        {
            var node = (await store.Nodes.GetNodeAsync(expected.Id))!;
            Assert.Equal(expected.Kind, node.Kind);
            Assert.Equal(expected.Name, node.Name);
            Assert.Equal(expected.ParentId, node.ParentId);
            Assert.Equal(expected.IsPinned, node.IsPinned);
            Assert.Equal(expected.Batch is not null, node.IsTrashed);
            if (expected.Text is not null)
            {
                var content = await store.Documents.GetContentAsync(expected.Id);
                Assert.Equal(expected.Text, content.Text);
                Assert.Equal(expected.Format, content.Format);
            }
        }

        var batches = world.Expected.Nodes.Values.Where(node => node.Batch is not null).GroupBy(node => node.Batch);
        foreach (var batch in batches)
        {
            Assert.Single(batch.Select(node => store.Nodes.GetNodeAsync(node.Id).Result!.TrashBatchId).Distinct());
        }

        var firstChildren = await store.Nodes.GetChildrenAsync(store.Workspace.Id, world.Folders[0].Id);
        Assert.Equal(world.ReorderedFirst, firstChildren[0].Id);
    }

    private static async Task<World> BuildWorkspaceAsync(TestStore store, RealisticText text)
    {
        var ws = store.Workspace.Id;
        var folders = new List<NodeInfo>();
        var depth = new Dictionary<NodeInfo, int>();
        var expected = new ExpectedTree();

        // Three top-level folders, each with two subfolders, down to six levels (189 folders).
        async Task AddFolderAsync(NodeId? parent, int level)
        {
            var folder = await store.Nodes.CreateFolderAsync(ws, parent, text.FolderName());
            expected.Created(folder);
            folders.Add(folder);
            depth[folder] = level;
            if (level < Depth)
            {
                await AddFolderAsync(folder.Id, level + 1);
                await AddFolderAsync(folder.Id, level + 1);
            }
        }

        for (var i = 0; i < 3; i++)
        {
            await AddFolderAsync(null, 1);
        }

        var notes = new List<NodeInfo>();
        for (var i = 0; i < NoteCount; i++)
        {
            // Spread notes over every level, a few at the root.
            NodeId? parent = i % 25 == 0 ? null : folders[(i * 7) % folders.Count].Id;
            var name = text.NoteName();
            NodeInfo note;
            if (i % 2 == 0)
            {
                var body = text.NoteBody(200, 1500).Replace("\n", "\r\n", StringComparison.Ordinal);
                note = await store.Nodes.CreateDocumentAsync(ws, parent, name, initialText: body);
                expected.Created(note, body);
            }
            else
            {
                note = await store.Nodes.CreateDocumentAsync(ws, parent, name);
                store.Clock.Advance(TimeSpan.FromMilliseconds(3));
                var body = text.NoteBody(200, 1500);
                await store.Documents.SaveContentAsync(note.Id, body);
                expected.Created(note, body);
            }

            notes.Add(note);
            store.Clock.Advance(TimeSpan.FromMilliseconds(7));
        }

        // Some notes link to others.
        for (var i = 0; i < 40; i++)
        {
            var source = expected.Nodes[notes[i * 11 % notes.Count].Id];
            var target = notes[(i * 13 + 5) % notes.Count];
            source.Text += $"\nRelated: [{target.Name}]({LinkParser.CreateUri(target.Id)})\n";
            await store.Documents.SaveContentAsync(source.Id, source.Text);
        }

        return new World(folders, depth, notes, expected);
    }

    private static async Task OrganizeAsync(TestStore store, World world)
    {
        var nodes = store.Nodes;
        var notes = world.Notes;
        var expected = world.Expected;

        // Pin ten notes and three folders, then reorder the Pinned section.
        foreach (var node in notes.Where((_, i) => i % 50 == 3).Concat(world.Folders.Where((_, i) => i % 60 == 1)))
        {
            await nodes.PinAsync(node.Id);
            expected.Nodes[node.Id].IsPinned = true;
        }

        await nodes.ReorderPinnedAsync(notes[3].Id, InsertPosition.End);

        // Move notes between folders, reorder siblings, and move a level-5 folder under another level-4 folder.
        for (var i = 0; i < 30; i++)
        {
            var note = notes[(i * 17 + 2) % notes.Count];
            var target = world.Folders[(i * 31 + 9) % world.Folders.Count];
            await nodes.MoveAsync(note.Id, target.Id);
            expected.Nodes[note.Id].ParentId = target.Id;
        }

        var siblings = await nodes.GetChildrenAsync(store.Workspace.Id, world.Folders[0].Id);
        await nodes.MoveAsync(siblings[^1].Id, world.Folders[0].Id, InsertPosition.Before(siblings[0].Id));
        world.ReorderedFirst = siblings[^1].Id;
        var level5 = world.Folders.First(folder => world.FolderDepth[folder] == 5);
        var otherLevel4 = world.Folders.First(folder => world.FolderDepth[folder] == 4 && folder.Id != level5.ParentId);
        await nodes.MoveAsync(level5.Id, otherLevel4.Id);
        expected.Nodes[level5.Id].ParentId = otherLevel4.Id;

        // Rename, duplicate and override a format.
        await nodes.RenameAsync(notes[10].Id, "Renamed — Überblick.md");
        expected.Renamed(notes[10].Id, "Renamed — Überblick.md");
        var original = expected.Nodes[notes[11].Id];
        var duplicate = await nodes.DuplicateAsync(notes[11].Id);
        Assert.Equal(NodeNames.CopyName(original.Name, NodeKind.Document, [original.Name]), duplicate.Name);
        expected.Nodes[duplicate.Id] = new ExpectedTree.Node(duplicate.Id, NodeKind.Document)
        {
            ParentId = original.ParentId,
            Name = duplicate.Name,
            Text = original.Text,
            Format = original.Format,
        };
        world.Duplicates++;
        await store.Documents.SetFormatAsync(notes[12].Id, DocumentFormat.Yaml);
        expected.Nodes[notes[12].Id].Format = DocumentFormat.Yaml;

        // Trash twenty notes one by one and two folder subtrees; restore half of the notes and one folder.
        var trashed = notes.Where((_, i) => i % 25 == 7).Take(20).ToList();
        foreach (var note in trashed)
        {
            store.Clock.Advance(TimeSpan.FromMinutes(1));
            await nodes.TrashAsync([note.Id]);
            expected.Trashed(note.Id);
        }

        var trashedFolders = world.Folders.Where(folder => world.FolderDepth[folder] == 3).Take(2).ToList();
        foreach (var folder in trashedFolders)
        {
            store.Clock.Advance(TimeSpan.FromMinutes(1));
            await nodes.TrashAsync([folder.Id]);
            expected.Trashed(folder.Id);
        }

        foreach (var note in trashed.Where((_, i) => i % 2 == 0))
        {
            await nodes.RestoreAsync(note.Id);
            expected.Restored(note.Id);
        }

        await nodes.RestoreAsync(trashedFolders[0].Id);
        expected.Restored(trashedFolders[0].Id);

        // Delete two trashed notes forever (they must stay gone).
        var forever = trashed.Where((_, i) => i % 2 == 1).Take(2).ToList();
        world.DeletedForever = await nodes.DeleteForeverAsync(forever.Select(note => note.Id).ToList());
        forever.ForEach(note => expected.DeletedForever(note.Id));
    }

    private static async Task RecordSessionAsync(TestStore store, World world)
    {
        var live = await store.Nodes.GetAllNodesAsync(store.Workspace.Id);
        var openNotes = live.Where(node => node.IsDocument).Take(8).ToList();
        var tabs = openNotes
            .Select((note, i) => SessionTab.ForNote(note.Id, note.Name) with
            {
                IsPinned = i < 2,
                IsActive = i == 5,
                CaretOffset = 10 * i,
                SelectionStart = 10 * i,
                SelectionLength = i % 3,
                VerticalScrollOffset = 120.5 * i,
                HorizontalScrollOffset = i * 0.25,
            })
            .Append(SessionTab.ForFile(@"C:\Users\me\Documents\hosts.txt", "hosts.txt") with { CaretOffset = 3 })
            .ToList();
        await store.Sessions.SaveTabsAsync(tabs);
        await store.Sessions.PushRecentlyClosedAsync(SessionTab.ForNote(live.First(node => node.IsDocument && !openNotes.Contains(node)).Id, "closed") with { CaretOffset = 4 });
        await store.Sessions.PushRecentlyClosedAsync(SessionTab.ForFile("/tmp/closed.log", "closed.log"));

        var expanded = world.Folders.Where((_, i) => i % 7 == 0).Select(folder => folder.Id).ToList();
        await store.Sessions.SaveUiStateAsync(new UiState
        {
            ExpandedFolderIds = expanded,
            SidebarWidth = 301,
            SortOrder = ExplorerSortOrder.Manual,
            SelectedNodeId = openNotes[5].Id,
            IsContextPanelVisible = false,
        });
        await store.Settings.SaveAsync(AppSettings.Default with { Theme = ThemePreference.Dark, EditorFontSize = 15, WordWrap = true, TabSize = 2, Zoom = 110 });
        await store.Files.SaveRecoveryBufferAsync(@"C:\Users\me\Documents\hosts.txt", "127.0.0.1 localhost\r\n", TextEncodingInfo.Utf8, LineEndingStyle.Crlf);
    }

    // ------------------------------------------------------------------ comparing

    private sealed record Snapshot(
        IReadOnlyList<NodeInfo> Live,
        IReadOnlyList<NodeInfo> Pinned,
        IReadOnlyList<TrashItem> Trash,
        IReadOnlyDictionary<NodeId, DocumentContent> Contents,
        IReadOnlyList<SessionTab> Tabs,
        IReadOnlyList<RecentlyClosedTab> RecentlyClosed,
        UiState UiState,
        AppSettings Settings,
        IReadOnlyList<NimPad.Core.Sync.PendingOperation> Pending,
        IReadOnlyList<string> Searches,
        IReadOnlyDictionary<string, List<string>> Tables,
        int MaxFolderDepth);

    private static async Task<Snapshot> SnapshotAsync(TestStore store)
    {
        var ws = store.Workspace.Id;
        var live = await store.Nodes.GetAllNodesAsync(ws);
        var trash = await store.Nodes.GetTrashAsync(ws);
        var documentIds = await store.QueryAsync("SELECT node_id FROM document_contents ORDER BY node_id;", reader => NodeId.Parse(reader.GetString(0)));
        var contents = new Dictionary<NodeId, DocumentContent>();
        foreach (var id in documentIds)
        {
            contents[id] = await store.Documents.GetContentAsync(id);
        }

        var searches = new List<string>();
        foreach (var query in new[] { "meeting", "budget q3", "überblick", "crème", "kubernetes" })
        {
            foreach (var result in await store.Search.SearchAsync(query))
            {
                searches.Add($"{query}|{result.NodeId}|{string.Join('/', result.Path)}|{result.Snippet}|{string.Join(',', result.SnippetHighlights)}|{result.Score.ToString("R", CultureInfo.InvariantCulture)}");
            }
        }

        var tables = new Dictionary<string, List<string>>();
        foreach (var (table, orderBy) in Tables)
        {
            tables[table] = await DumpAsync(store, table, orderBy);
        }

        var byId = live.ToDictionary(node => node.Id);
        int DepthOf(NodeInfo node) => node.ParentId is { } parent ? 1 + DepthOf(byId[parent]) : 1;

        return new Snapshot(
            live,
            await store.Nodes.GetPinnedAsync(ws),
            trash,
            contents,
            await store.Sessions.LoadTabsAsync(),
            await store.Sessions.GetRecentlyClosedAsync(),
            await store.Sessions.LoadUiStateAsync(),
            await store.Settings.LoadAsync(),
            await store.Queue.GetPendingAsync(100_000),
            searches,
            tables,
            live.Where(node => node.IsFolder).Max(DepthOf));
    }

    private static Task<List<string>> DumpAsync(TestStore store, string table, string orderBy) =>
        store.QueryAsync(
            $"SELECT * FROM {table} ORDER BY {orderBy};",
            reader =>
            {
                var row = new StringBuilder();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row.Append(reader.GetName(i)).Append('=').Append(value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture)).Append('|');
                }

                return row.ToString();
            });

    /// <summary>A stand-in for the editor's text buffer.</summary>
    private sealed class EditorBuffer(string text, long version = 1) : IAutosaveSource
    {
        public long Version => version;

        public string GetSnapshot() => text;
    }
}
