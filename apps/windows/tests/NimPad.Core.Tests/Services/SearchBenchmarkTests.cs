using System.Diagnostics;
using System.Globalization;
using NimPad.Core.Domain;
using NimPad.Core.Services;
using NimPad.Core.Sync;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Services;

/// <summary>
/// Spec §13: "Search: under 200 ms for common queries over 10,000 locally indexed notes". Measures SearchAsync end to
/// end (ranking, details, ancestor paths, snippets and highlights for up to 50 results).
/// </summary>
[Collection(PerformanceCollection.Name)]
public class SearchBenchmarkTests(ITestOutputHelper output)
{
    private const int NoteCount = 10_000;
    private const int FolderCount = 100;

    private static readonly string[] Queries =
    [
        "meeting", "roadmap", "budget q3", "deploy", "invoice", "recipe", "kubernetes cluster", "todo", "api design",
        "customer feedback", "pro", "release notes", "bug fix", "sprint planning", "password", "travel", "book",
        "performance review", "migration", "launch checklist",
    ];

    [Fact]
    public async Task TwentyTypicalQueriesOverTenThousandNotesHaveAMedianUnder200Ms()
    {
        using var store = await TestStore.CreateAsync();
        var text = new RealisticText(seed: 20261012);
        var workspaceId = store.Workspace.Id;

        // The same code path as the public create methods, batched into transactions so setup does not spend its
        // time on 10,000 fsyncs.
        var setup = Stopwatch.StartNew();
        var totalChars = 0L;
        var folders = await store.Database.Writer.ExecuteAsync(context =>
        {
            var changes = new ChangeCollector();
            var created = new List<NodeInfo>();
            for (var i = 0; i < FolderCount; i++)
            {
                // Some folders nest inside earlier ones, so result paths have depth.
                var parent = i >= 10 && i % 3 == 0 ? created[i / 3].Id : (NimPad.Core.Ids.NodeId?)null;
                created.Add(store.Nodes.CreateNode(context, workspaceId, parent, NodeKind.Folder, text.FolderName(), default, null, null, LocalActorKind.Import, changes));
            }

            return created;
        });
        for (var batch = 0; batch < 20; batch++)
        {
            totalChars += await store.Database.Writer.ExecuteAsync(context =>
            {
                var changes = new ChangeCollector();
                var chars = 0L;
                for (var i = 0; i < NoteCount / 20; i++)
                {
                    var body = text.NoteBody();
                    chars += body.Length;
                    store.Nodes.CreateNode(
                        context, workspaceId, folders[(batch * 500 + i) % FolderCount].Id, NodeKind.Document, text.NoteName(), default, null, body,
                        LocalActorKind.Import, changes);
                }

                return chars;
            });
        }

        setup.Stop();
        output.WriteLine($"Setup: {NoteCount} notes, {totalChars / NoteCount} chars on average, {setup.ElapsedMilliseconds} ms");
        Assert.Equal(NoteCount, await store.CountAsync("SELECT count(*) FROM document_contents;"));

        // One unrelated query to load the code paths, then each typical query once.
        await store.Search.SearchAsync("warmup");
        var timings = new List<(string Query, double Ms, int Results)>();
        foreach (var query in Queries)
        {
            var watch = Stopwatch.StartNew();
            var results = await store.Search.SearchAsync(query);
            watch.Stop();
            timings.Add((query, watch.Elapsed.TotalMilliseconds, results.Count));
            Assert.NotEmpty(results);
            Assert.All(results, result => Assert.True(result.Snippet.Length <= 120));
        }

        var sorted = timings.Select(timing => timing.Ms).Order().ToList();
        var median = (sorted[9] + sorted[10]) / 2;
        foreach (var (query, ms, count) in timings)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {query,-20} {ms,7:F1} ms  {count} results"));
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"SearchAsync over {NoteCount} notes: median {median:F1} ms, min {sorted[0]:F1} ms, p90 {sorted[17]:F1} ms, max {sorted[^1]:F1} ms"));
        foreach (var result in (await store.Search.SearchAsync("budget q3")).Take(3))
        {
            var marked = result.Snippet;
            foreach (var range in result.SnippetHighlights.Reverse())
            {
                marked = marked.Insert(range.End, "]").Insert(range.Start, "[");
            }

            output.WriteLine($"  {string.Join(" / ", result.Path.Append(result.Name))}: {marked}");
        }

        Assert.True(median < 200, $"Median search took {median:F1} ms");
    }
}
