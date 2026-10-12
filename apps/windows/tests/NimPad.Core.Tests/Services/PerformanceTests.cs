using System.Diagnostics;
using NimPad.Core.Domain;
using NimPad.Core.Services;
using NimPad.Core.Sync;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Services;

[Collection(PerformanceCollection.Name)]
public class PerformanceTests(ITestOutputHelper output)
{
    private const int FolderCount = 100;
    private const int DocumentsPerFolder = 99;
    private const int NodeCount = FolderCount * (DocumentsPerFolder + 1);

    [Fact]
    public async Task LoadsTenThousandNodesWellUnderTheBudget()
    {
        using var store = await TestStore.CreateAsync();
        var workspaceId = store.Workspace.Id;

        // Same code path as the public create methods (validation, positions, content, FTS, pending operations),
        // batched into a few transactions so the test does not spend its time on 10,000 fsyncs.
        var createTimer = Stopwatch.StartNew();
        for (var batch = 0; batch < 10; batch++)
        {
            await store.Database.Writer.ExecuteAsync(context =>
            {
                var changes = new ChangeCollector();
                for (var f = 0; f < FolderCount / 10; f++)
                {
                    var folder = store.Nodes.CreateNode(
                        context, workspaceId, null, NodeKind.Folder, $"Folder {batch:D2}-{f:D2}", InsertPosition.End, null, null, LocalActorKind.Import, changes);
                    for (var d = 0; d < DocumentsPerFolder; d++)
                    {
                        store.Nodes.CreateNode(
                            context, workspaceId, folder.Id, NodeKind.Document, $"Note {d:D3}.md", InsertPosition.End, null,
                            $"# Note {d}\nSome text for note {d} in folder {f}.", LocalActorKind.Import, changes);
                    }
                }
            });
        }

        createTimer.Stop();
        output.WriteLine($"Created {NodeCount} nodes in {createTimer.ElapsedMilliseconds} ms");

        var cold = Stopwatch.StartNew();
        var nodes = await store.Nodes.GetAllNodesAsync(workspaceId);
        cold.Stop();
        Assert.Equal(NodeCount, nodes.Count);

        var timings = new List<double>();
        for (var run = 0; run < 7; run++)
        {
            var timer = Stopwatch.StartNew();
            var loaded = await store.Nodes.GetAllNodesAsync(workspaceId);
            timer.Stop();
            Assert.Equal(NodeCount, loaded.Count);
            timings.Add(timer.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        output.WriteLine(
            $"GetAllNodesAsync({NodeCount}): first {cold.Elapsed.TotalMilliseconds:F1} ms, median {median:F1} ms, " +
            $"min {timings[0]:F1} ms, max {timings[^1]:F1} ms");

        Assert.True(cold.Elapsed.TotalMilliseconds < 250, $"First load took {cold.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(median < 250, $"Median load took {median:F1} ms");
        Assert.Equal(NodeCount, await store.Queue.CountAsync());
        Assert.Equal(FolderCount, nodes.Count(node => node.ParentId is null));
        Assert.All(nodes.Where(node => node.IsDocument), node => Assert.Equal(DocumentFormat.Markdown, node.Format));
    }

    [Fact]
    public async Task PublicCreatesStayResponsive()
    {
        using var store = await TestStore.CreateAsync();
        var folder = await store.Nodes.CreateFolderAsync(store.Workspace.Id, null, "Folder");
        var timer = Stopwatch.StartNew();
        var tasks = new List<Task<NodeInfo>>();
        for (var i = 0; i < 200; i++)
        {
            tasks.Add(store.Nodes.CreateDocumentAsync(store.Workspace.Id, folder.Id, $"n{i:D3}"));
        }

        var created = await Task.WhenAll(tasks);
        timer.Stop();
        output.WriteLine($"200 individual create transactions: {timer.ElapsedMilliseconds} ms");

        var children = await store.Nodes.GetChildrenAsync(store.Workspace.Id, folder.Id);
        Assert.Equal(created.Select(node => node.Id), children.Select(node => node.Id));
        Assert.Equal(children.Order(SiblingComparer.Instance), children);
        Assert.Equal(201, await store.Queue.CountAsync());
    }
}
