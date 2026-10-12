using System.Text;
using Microsoft.Data.Sqlite;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Storage;

namespace NimPad.Core.Services;

/// <summary>One search hit.</summary>
/// <param name="NodeId">The matching note or folder.</param>
/// <param name="WorkspaceId">Its workspace.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Folder or document.</param>
/// <param name="Path">The names of its ancestor folders, from the workspace root down to its parent.</param>
/// <param name="Snippet">
/// One line of the document around the first match, at most 120 characters (with <c>…</c> where the line was cut);
/// the preview line when only the name matched; empty for folders.
/// </param>
/// <param name="SnippetHighlights">The matched text in <paramref name="Snippet"/>.</param>
/// <param name="NameHighlights">The matched text in <paramref name="Name"/>.</param>
/// <param name="ContentUpdatedAt">The last text change of a document; <see langword="null"/> for folders.</param>
/// <param name="Score">The bm25 rank (lower is better), with name matches weighted up.</param>
public sealed record SearchResult(
    NodeId NodeId,
    WorkspaceId WorkspaceId,
    string Name,
    NodeKind Kind,
    IReadOnlyList<string> Path,
    string Snippet,
    IReadOnlyList<TextRange> SnippetHighlights,
    IReadOnlyList<TextRange> NameHighlights,
    DateTimeOffset? ContentUpdatedAt,
    double Score);

/// <summary>
/// Full-text search over note names and bodies (<c>notes_fts</c>, FTS5 with <c>unicode61 remove_diacritics 2</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>User input is never FTS syntax: each whitespace-separated piece is quoted and prefix-matched, and every
/// piece must match (in the name or the body). Blank input, or input without letters or digits, finds nothing.</item>
/// <item>Results are ranked by bm25 with name matches weighted <see cref="NameWeight"/> times body matches. Items in
/// the Trash are excluded.</item>
/// <item>Snippets come from the first 256 K characters of a document; a match beyond that shows the preview line.</item>
/// <item>Queries run on a pooled read connection; cancelling interrupts a running query.</item>
/// </list>
/// </remarks>
public sealed class SearchService
{
    /// <summary>The number of results returned by default.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The bm25 weight of the name column relative to the body (1.0).</summary>
    public const double NameWeight = 10.0;

    // How much of a document is searched for the snippet.
    private const int SnippetScanLength = 256 * 1024;

    private const int SqliteInterrupt = 9;

    private readonly LocalDatabase _database;
    private int _rejectedExpressions;

    /// <summary>Creates the service.</summary>
    public SearchService(LocalDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>How many search expressions FTS5 rejected (the query builder should make that impossible; tests only).</summary>
    internal int RejectedExpressionCount => Volatile.Read(ref _rejectedExpressions);

    /// <summary>
    /// Called with each search's read connection before its statements run; the result is disposed after them
    /// (tests only, to observe a running statement).
    /// </summary>
    internal Func<SqliteConnection, IDisposable?>? InstrumentConnection { get; set; }

    /// <summary>Searches every workspace (see the class remarks).</summary>
    /// <param name="query">The user's input, as typed.</param>
    /// <param name="limit">The maximum number of results (at least 1).</param>
    /// <param name="cancellationToken">Cancels the search, interrupting a running query.</param>
    /// <returns>The best matches first; empty when the query has nothing searchable.</returns>
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit = DefaultLimit, CancellationToken cancellationToken = default) =>
        SearchCoreAsync(null, query, limit, cancellationToken);

    /// <summary>Searches one workspace (see <see cref="SearchAsync(string, int, CancellationToken)"/>).</summary>
    public Task<IReadOnlyList<SearchResult>> SearchAsync(WorkspaceId workspaceId, string query, int limit = DefaultLimit, CancellationToken cancellationToken = default) =>
        SearchCoreAsync(workspaceId, query, limit, cancellationToken);

    private Task<IReadOnlyList<SearchResult>> SearchCoreAsync(WorkspaceId? workspaceId, string query, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (SearchQuery.Parse(query) is not { } parsed)
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        }

        return _database.Readers.ExecuteAsync(
            connection =>
            {
                // Cancellation interrupts whatever statement is running on this connection.
                var handle = connection.Handle;
                using var interrupt = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle));
                using var instrumentation = InstrumentConnection?.Invoke(connection);
                try
                {
                    return Search(connection, workspaceId, parsed, limit, cancellationToken);
                }
                catch (SqliteException ex) when (ex.PrimaryErrorCode() == SqliteInterrupt && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (SqliteException ex) when (ex.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase))
                {
                    // The query builder only emits quoted strings, so this is not expected; but a search box must
                    // never fail because of what was typed into it.
                    System.Diagnostics.Trace.TraceWarning($"FTS5 rejected the search expression '{parsed.FtsExpression}': {ex.Message}");
                    Interlocked.Increment(ref _rejectedExpressions);
                    return [];
                }
            },
            cancellationToken);
    }

    private static IReadOnlyList<SearchResult> Search(
        SqliteConnection connection,
        WorkspaceId? workspaceId,
        SearchQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        // One read transaction, so the ranking and the details see the same snapshot.
        using var transaction = connection.BeginTransaction(deferred: true);
        var hits = Rank(connection, transaction, workspaceId, query, limit);
        if (hits.Count == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rowIds = "[" + string.Join(',', hits.Select(hit => hit.RowId)) + "]";
        var bodies = LoadBodies(connection, transaction, rowIds);
        var paths = LoadPaths(connection, transaction, rowIds);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<SearchResult>(hits.Count);
        foreach (var hit in hits)
        {
            var snippet = string.Empty;
            IReadOnlyList<TextRange> snippetHighlights = [];
            if (hit.Kind == NodeKind.Document && bodies.TryGetValue(hit.RowId, out var body))
            {
                if (SearchText.BuildSnippet(body.Text, query) is { } built)
                {
                    (snippet, snippetHighlights) = built;
                }
                else
                {
                    snippet = body.Preview;
                    snippetHighlights = SearchText.Highlight(snippet, query);
                }
            }

            results.Add(new SearchResult(
                hit.Id,
                hit.WorkspaceId,
                hit.Name,
                hit.Kind,
                paths.TryGetValue(hit.RowId, out var path) ? path : [],
                snippet,
                snippetHighlights,
                SearchText.Highlight(hit.Name, query),
                hit.ContentUpdatedAt,
                hit.Score));
        }

        return results;
    }

    private static List<Hit> Rank(SqliteConnection connection, SqliteTransaction transaction, WorkspaceId? workspaceId, SearchQuery query, int limit)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT n.row_id, n.id, n.workspace_id, n.name, n.kind, n.content_updated_at,
                   bm25(notes_fts, $nameWeight, 1.0) AS score
            FROM notes_fts JOIN nodes n ON n.row_id = notes_fts.rowid
            WHERE notes_fts MATCH $match AND n.deleted_at IS NULL{(workspaceId is null ? string.Empty : " AND n.workspace_id = $ws")}
            ORDER BY score, n.content_updated_at DESC, n.row_id
            LIMIT $limit;
            """;
        command.With("$nameWeight", NameWeight).With("$match", query.FtsExpression).With("$limit", limit);
        if (workspaceId is { } ws)
        {
            command.With("$ws", ws);
        }

        using var reader = command.ExecuteReader();
        var hits = new List<Hit>();
        while (reader.Read())
        {
            hits.Add(new Hit(
                reader.GetInt64(0),
                reader.GetNodeId(1),
                WorkspaceId.Parse(reader.GetString(2)),
                reader.GetString(3),
                NodeKinds.Parse(reader.GetString(4)),
                reader.GetTimestampOrNull(5),
                reader.GetDouble(6)));
        }

        return hits;
    }

    private static Dictionary<long, (string Text, string Preview)> LoadBodies(SqliteConnection connection, SqliteTransaction transaction, string rowIds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT n.row_id, substr(d.text, 1, $scan), d.preview
            FROM nodes n JOIN document_contents d ON d.node_id = n.id
            WHERE n.row_id IN (SELECT value FROM json_each($rows));
            """;
        command.With("$scan", SnippetScanLength).With("$rows", rowIds);
        using var reader = command.ExecuteReader();
        var bodies = new Dictionary<long, (string, string)>();
        while (reader.Read())
        {
            bodies[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2));
        }

        return bodies;
    }

    private static Dictionary<long, IReadOnlyList<string>> LoadPaths(SqliteConnection connection, SqliteTransaction transaction, string rowIds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            WITH RECURSIVE ancestors(row_id, ancestor_id, depth) AS (
                SELECT n.row_id, n.parent_id, 1 FROM nodes n
                WHERE n.row_id IN (SELECT value FROM json_each($rows)) AND n.parent_id IS NOT NULL
                UNION ALL
                SELECT a.row_id, p.parent_id, a.depth + 1 FROM ancestors a JOIN nodes p ON p.id = a.ancestor_id
                WHERE p.parent_id IS NOT NULL AND a.depth < 10000
            )
            SELECT a.row_id, p.name FROM ancestors a JOIN nodes p ON p.id = a.ancestor_id
            ORDER BY a.row_id, a.depth DESC;
            """;
        command.With("$rows", rowIds);
        using var reader = command.ExecuteReader();
        var paths = new Dictionary<long, IReadOnlyList<string>>();
        while (reader.Read())
        {
            var rowId = reader.GetInt64(0);
            if (!paths.TryGetValue(rowId, out var path))
            {
                path = new List<string>();
                paths[rowId] = path;
            }

            ((List<string>)path).Add(reader.GetString(1));
        }

        return paths;
    }

    private sealed record Hit(
        long RowId,
        NodeId Id,
        WorkspaceId WorkspaceId,
        string Name,
        NodeKind Kind,
        DateTimeOffset? ContentUpdatedAt,
        double Score);
}
