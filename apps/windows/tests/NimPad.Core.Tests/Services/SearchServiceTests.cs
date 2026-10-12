using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Services;

public sealed class SearchServiceTests : IAsyncLifetime
{
    private TestStore _store = null!;

    private SearchService Search => _store.Search;

    private NodeService Nodes => _store.Nodes;

    private WorkspaceId Ws => _store.Workspace.Id;

    public async ValueTask InitializeAsync() => _store = await TestStore.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------ query sanitizing

    [Theory]
    [InlineData("")]
    [InlineData("   \t\n ")]
    [InlineData("*")]
    [InlineData("\"")]
    [InlineData("- + : ^ ( ) { }")]
    [InlineData("\"\"\"")]
    public async Task InputWithoutWordsFindsNothing(string query)
    {
        await Nodes.CreateDocumentAsync(Ws, null, "note", initialText: "anything at all");
        Assert.Empty(await Search.SearchAsync(query));
    }

    // Each input as FTS5 syntax would mean something else (or be a syntax error, which the service would swallow);
    // as text, it matches only the note that contains those words.
    [Theory]
    [InlineData("NEAR(beta alpha)", "syntax")] // NEAR would also match "plain"
    [InlineData("alpha OR", "syntax")] // a syntax error
    [InlineData("AND", "syntax")] // a syntax error
    [InlineData("\"unbalanced", "syntax")] // a syntax error
    [InlineData("name:alpha", "syntax")] // a column filter: no note is named alpha
    [InlineData("body : alpha", "syntax")] // a column filter matching both
    [InlineData("alpha*", "plain", "syntax")]
    [InlineData("-alpha", "plain", "syntax")]
    [InlineData("^alpha", "plain", "syntax")] // an initial-token match: only "plain"
    [InlineData("a\"b\"c", "syntax")]
    [InlineData("{name body}:alpha", "syntax")] // a column filter matching both
    [InlineData("alpha) OR (beta", "syntax")] // alpha OR beta would match both
    [InlineData("'; DROP TABLE nodes; --", "syntax")]
    public async Task FtsSyntaxInTheInputIsTreatedAsText(string query, params string[] expected)
    {
        await Nodes.CreateDocumentAsync(Ws, null, "plain", initialText: "alpha beta");
        await Nodes.CreateDocumentAsync(
            Ws, null, "syntax",
            initialText: "Docs: NEAR(beta alpha) OR name:alpha AND {name body}:alpha, -alpha ^alpha alpha* a\"b\"c, \"unbalanced; DROP TABLE nodes; --");

        var results = await Search.SearchAsync(query);

        Assert.Equal(expected.Order(), results.Select(result => result.Name).Order());
        Assert.Equal(0, Search.RejectedExpressionCount);
        Assert.Equal(2, await _store.CountAsync("SELECT count(*) FROM nodes;"));
    }

    [Fact]
    public void QueriesAreQuotedPrefixTermsJoinedWithAnd()
    {
        var query = SearchQuery.Parse("  Crème  say\"hi\"  OR *  crème ");

        Assert.NotNull(query);
        Assert.Equal("\"Crème\"* AND \"say\"\"hi\"\"\"* AND \"OR\"*", query.FtsExpression);
        Assert.Equal(new[] { new[] { "creme" }, ["say", "hi"], ["or"] }, query.Terms.Select(term => term.Words));
    }

    // ------------------------------------------------------------ matching and ranking

    [Fact]
    public async Task EveryTermMustMatchAsAPrefixInTheNameOrBody()
    {
        var both = await Nodes.CreateDocumentAsync(Ws, null, "Quarterly budget", initialText: "Marketing spend for Q3");
        await Nodes.CreateDocumentAsync(Ws, null, "Budget only", initialText: "nothing else");
        await Nodes.CreateDocumentAsync(Ws, null, "Other", initialText: "marketing plan");

        var results = await Search.SearchAsync("budg MARKET");

        Assert.Equal([both.Id], results.Select(result => result.NodeId));
    }

    [Fact]
    public async Task MatchesIgnoreCaseAndDiacritics()
    {
        var note = await Nodes.CreateDocumentAsync(Ws, null, "Dessert", initialText: "Crème brûlée for the café");

        var result = Assert.Single(await Search.SearchAsync("creme BRULEE"));

        Assert.Equal(note.Id, result.NodeId);
        Assert.Equal("Crème brûlée for the café", result.Snippet);
        Assert.Equal([new TextRange(0, 5), new TextRange(6, 6)], result.SnippetHighlights);
    }

    [Fact]
    public async Task NameMatchesRankAboveBodyMatches()
    {
        var bodyOnly = await Nodes.CreateDocumentAsync(Ws, null, "Weekly sync", initialText: "We discussed the roadmap and the roadmap again.");
        var named = await Nodes.CreateDocumentAsync(Ws, null, "Roadmap 2027", initialText: "Plans.");

        var results = await Search.SearchAsync("roadmap");

        Assert.Equal([named.Id, bodyOnly.Id], results.Select(result => result.NodeId));
        Assert.True(results[0].Score < results[1].Score);
        Assert.Equal([new TextRange(0, 7)], results[0].NameHighlights);
        Assert.Empty(results[1].NameHighlights);
    }

    [Fact]
    public async Task TrashedNodesAreExcludedUntilRestored()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Archive");
        var note = await Nodes.CreateDocumentAsync(Ws, folder.Id, "Old plan", initialText: "obsolete");
        await Nodes.TrashAsync([folder.Id]);

        Assert.Empty(await Search.SearchAsync("obsolete"));
        Assert.Empty(await Search.SearchAsync("archive"));

        await Nodes.RestoreAsync(folder.Id);
        Assert.Equal([note.Id], (await Search.SearchAsync("obsolete")).Select(result => result.NodeId));
    }

    [Fact]
    public async Task FoldersMatchByNameWithAnEmptySnippet()
    {
        var folder = await Nodes.CreateFolderAsync(Ws, null, "Recipes");

        var result = Assert.Single(await Search.SearchAsync("recip"));

        Assert.Equal(folder.Id, result.NodeId);
        Assert.Equal(NodeKind.Folder, result.Kind);
        Assert.Equal(string.Empty, result.Snippet);
        Assert.Empty(result.SnippetHighlights);
        Assert.Null(result.ContentUpdatedAt);
        Assert.Equal([new TextRange(0, 5)], result.NameHighlights);
    }

    [Fact]
    public async Task ResultsCarryTheAncestorPathAndMetadata()
    {
        var a = await Nodes.CreateFolderAsync(Ws, null, "Projects");
        var b = await Nodes.CreateFolderAsync(Ws, a.Id, "NimPad");
        var c = await Nodes.CreateFolderAsync(Ws, b.Id, "Design");
        var note = await Nodes.CreateDocumentAsync(Ws, c.Id, "Tokens.md", initialText: "color tokens");
        var rootNote = await Nodes.CreateDocumentAsync(Ws, null, "Root tokens");

        var results = await Search.SearchAsync(Ws, "tokens");

        var nested = results.Single(result => result.NodeId == note.Id);
        Assert.Equal(["Projects", "NimPad", "Design"], nested.Path);
        Assert.Equal("Tokens.md", nested.Name);
        Assert.Equal(NodeKind.Document, nested.Kind);
        Assert.Equal(Ws, nested.WorkspaceId);
        Assert.Equal(TestStore.Start, nested.ContentUpdatedAt);
        Assert.Empty(results.Single(result => result.NodeId == rootNote.Id).Path);
    }

    [Fact]
    public async Task LimitCapsTheResults()
    {
        for (var i = 0; i < 12; i++)
        {
            await Nodes.CreateDocumentAsync(Ws, null, $"Note {i}", initialText: "shared word");
        }

        Assert.Equal(5, (await Search.SearchAsync("shared", 5)).Count);
        Assert.Equal(12, (await Search.SearchAsync("shared")).Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Search.SearchAsync("shared", 0));
    }

    [Fact]
    public async Task SearchesAWorkspaceOrAll()
    {
        var other = WorkspaceId.New();
        await _store.ExecuteRawAsync(
            "INSERT INTO workspaces (id, name, created_at, updated_at) VALUES ($id, 'Other', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');",
            ("$id", other.ToString()));
        await Nodes.CreateDocumentAsync(Ws, null, "mine", initialText: "galaxy");
        await Nodes.CreateDocumentAsync(other, null, "theirs", initialText: "galaxy");

        Assert.Equal(2, (await Search.SearchAsync("galaxy")).Count);
        Assert.Equal("theirs", Assert.Single(await Search.SearchAsync(other, "galaxy")).Name);
    }

    // ------------------------------------------------------------ snippets

    [Fact]
    public async Task TheSnippetIsTheTrimmedLineOfTheFirstMatch()
    {
        await Nodes.CreateDocumentAsync(Ws, null, "Launch", initialText: "# Launch\n\n   Ship the beta to 50 users on Thursday.   \nThe beta waits.");

        var result = Assert.Single(await Search.SearchAsync("beta"));

        Assert.Equal("Ship the beta to 50 users on Thursday.", result.Snippet);
        Assert.Equal([new TextRange(9, 4)], result.SnippetHighlights);
    }

    [Fact]
    public async Task LongLinesAreCutAroundTheMatchWithEllipses()
    {
        var line = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"word{i}")) + " needle " + string.Join(' ', Enumerable.Range(0, 60).Select(i => $"tail{i}"));
        await Nodes.CreateDocumentAsync(Ws, null, "Long", initialText: "intro\n" + line + "\nnext");

        var result = Assert.Single(await Search.SearchAsync("needle"));

        Assert.True(result.Snippet.Length <= 120, $"{result.Snippet.Length}: {result.Snippet}");
        Assert.StartsWith("…", result.Snippet);
        Assert.EndsWith("…", result.Snippet);
        Assert.DoesNotContain('\n', result.Snippet);
        var highlight = Assert.Single(result.SnippetHighlights);
        Assert.Equal("needle", result.Snippet.Substring(highlight.Start, highlight.Length));
        Assert.InRange(highlight.Start, 20, 40);
    }

    [Fact]
    public async Task HighlightsEveryMatchedTermInTheSnippetAndOnlyTheTypedPrefix()
    {
        await Nodes.CreateDocumentAsync(Ws, null, "Notes", initialText: "Projects list: project Atlas, projected costs, and a PROJECTOR.");

        var result = Assert.Single(await Search.SearchAsync("proj atl"));

        var highlighted = result.SnippetHighlights.Select(range => result.Snippet.Substring(range.Start, range.Length)).ToList();
        Assert.Equal(["Proj", "proj", "Atl", "proj", "PROJ"], highlighted);
    }

    [Fact]
    public async Task MultiWordTermsHighlightTheWholePhrase()
    {
        await Nodes.CreateDocumentAsync(Ws, null, "Ops", initialText: "Rotate the e-mail relay keys.");

        var result = Assert.Single(await Search.SearchAsync("e-mail"));

        var range = Assert.Single(result.SnippetHighlights);
        Assert.Equal("e-mail", result.Snippet.Substring(range.Start, range.Length));
    }

    [Fact]
    public async Task ANameOnlyMatchShowsThePreviewLine()
    {
        await Nodes.CreateDocumentAsync(Ws, null, "Groceries", initialText: "\n\n  milk, eggs\nbread");

        var result = Assert.Single(await Search.SearchAsync("grocer"));

        Assert.Equal("milk, eggs", result.Snippet);
        Assert.Empty(result.SnippetHighlights);
    }

    [Fact]
    public async Task SnippetsNeverSplitSurrogatePairs()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 100));
        await Nodes.CreateDocumentAsync(Ws, null, "Emoji", initialText: emoji + " marker " + emoji);

        var result = Assert.Single(await Search.SearchAsync("marker"));

        Assert.True(result.Snippet.Length <= 120);
        Assert.False(char.IsLowSurrogate(result.Snippet[1]));
        Assert.False(char.IsHighSurrogate(result.Snippet[^2]));
    }

    // ------------------------------------------------------------ cancellation

    [Fact]
    public async Task ACancelledSearchThrowsOperationCanceled()
    {
        await Nodes.CreateDocumentAsync(Ws, null, "note", initialText: "alpha");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Search.SearchAsync("alpha", cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task CancellingInterruptsARunningQuery()
    {
        // A batch of notes that all match, so the ranking query runs many SQLite instructions.
        await _store.Database.Writer.ExecuteAsync(context =>
        {
            var changes = new ChangeCollector();
            for (var i = 0; i < 3000; i++)
            {
                Nodes.CreateNode(context, Ws, null, NodeKind.Document, $"Doc {i}", default, null,
                    string.Join(' ', Enumerable.Repeat($"common term{i % 7} filler", 40)), NimPad.Core.Sync.LocalActorKind.Import, changes);
            }
        });

        // Uncancelled, the search makes SQLite call the progress handler many times.
        var baseline = new ProgressProbe(blockOnFirstCall: false);
        Search.InstrumentConnection = baseline.Attach;
        Assert.NotEmpty(await Search.SearchAsync("c", 1000));
        Assert.True(baseline.Calls > 50, $"only {baseline.Calls} progress callbacks");

        // Cancel while a statement is provably running (the handler is called from inside it and holds it).
        using var cancellation = new CancellationTokenSource();
        var probe = new ProgressProbe(blockOnFirstCall: true);
        Search.InstrumentConnection = probe.Attach;
        var search = Search.SearchAsync("c", 1000, cancellation.Token);
        Assert.True(probe.Running.Wait(TimeSpan.FromSeconds(10)), "the query never started");
        await cancellation.CancelAsync();
        probe.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        // sqlite3_interrupt stopped the statement at once: it did not run on to completion.
        Assert.True(probe.CallsAfterRelease <= 1, $"{probe.CallsAfterRelease} progress callbacks after the cancellation");

        Search.InstrumentConnection = null;
        Assert.NotEmpty(await Search.SearchAsync("common", 5));
    }

    // A SQLite progress handler: called every 100 virtual-machine instructions of a running statement.
    private sealed class ProgressProbe(bool blockOnFirstCall)
    {
        private readonly ManualResetEventSlim _release = new();
        private int _calls;
        private int _callsAfterRelease;

        public ManualResetEventSlim Running { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public int CallsAfterRelease => Volatile.Read(ref _callsAfterRelease);

        public IDisposable Attach(Microsoft.Data.Sqlite.SqliteConnection connection)
        {
            var handle = connection.Handle!;
            SQLitePCL.raw.sqlite3_progress_handler(handle, 100, OnProgress, null);
            return new Detach(() => SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null));
        }

        public void Release() => _release.Set();

        private int OnProgress(object user)
        {
            if (Interlocked.Increment(ref _calls) == 1 && blockOnFirstCall)
            {
                Running.Set();
                _release.Wait(TimeSpan.FromSeconds(10));
            }
            else if (_release.IsSet)
            {
                Interlocked.Increment(ref _callsAfterRelease);
            }

            return 0; // never stop the statement from here: only the cancellation may
        }

        private sealed class Detach(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }
}
