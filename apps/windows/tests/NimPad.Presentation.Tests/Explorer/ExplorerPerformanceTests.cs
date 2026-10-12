using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Presentation.Explorer;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Explorer;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance (runs alone)";
}

[Collection(PerformanceCollection.Name)]
public sealed class ExplorerPerformanceTests(ITestOutputHelper output)
{
    private static readonly WorkspaceId Workspace = WorkspaceId.New();

    // 10 top folders × 10 subfolders × 98 notes + the 110 folders = 9,910 nodes... plus 90 root notes = 10,000.
    private static (List<NodeInfo> Nodes, List<NodeId> Folders) BuildTree()
    {
        var nodes = new List<NodeInfo>(10_000);
        var folders = new List<NodeId>();
        var now = ShellHarness.Start;
        var position = 0;
        NodeInfo Node(NodeId? parent, NodeKind kind, string name) => new(
            NodeId.New(), Workspace, parent, kind, name, "a" + (position++).ToString("D6", CultureInfo.InvariantCulture), false, null,
            now, now, kind == NodeKind.Document ? now.AddSeconds(position) : null, null, null, null, null,
            kind == NodeKind.Document ? DocumentFormat.Plain : null);

        for (var i = 0; i < 10; i++)
        {
            var top = Node(null, NodeKind.Folder, $"Folder {i}");
            nodes.Add(top);
            folders.Add(top.Id);
            for (var j = 0; j < 10; j++)
            {
                var sub = Node(top.Id, NodeKind.Folder, $"Sub {i}.{j}");
                nodes.Add(sub);
                folders.Add(sub.Id);
                for (var k = 0; k < 98; k++)
                {
                    nodes.Add(Node(sub.Id, NodeKind.Document, $"Note {i}.{j}.{k}"));
                }
            }
        }

        while (nodes.Count < 10_000)
        {
            nodes.Add(Node(null, NodeKind.Document, $"Root note {nodes.Count}"));
        }

        return (nodes, folders);
    }

    private static (ViewModelContext Context, ExplorerViewModel Explorer) CreateExplorer()
    {
        var dispatcher = new ImmediateUiDispatcher();
        var context = new ViewModelContext(new FakePlatform(dispatcher).Services, new FakeTimeProvider(ShellHarness.Start), ShellHarness.Culture);
        return (context, new ExplorerViewModel(context, new TabsViewModel(context)));
    }

    private static Dictionary<NodeId, string> Previews(List<NodeInfo> nodes) =>
        nodes.Where(node => node.IsDocument).ToDictionary(node => node.Id, node => "Preview of " + node.Name);

    // The fastest of several runs: garbage collections and a busy machine only ever add time, while a slower
    // algorithm slows every run.
    private static TimeSpan Fastest(int runs, Func<TimeSpan> run)
    {
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < runs; i++)
        {
            var elapsed = run();
            best = elapsed < best ? elapsed : best;
        }

        return best;
    }

    private static TimeSpan Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed;
    }

    [Theory]
    [InlineData(ExplorerSortOrder.Manual)]
    [InlineData(ExplorerSortOrder.Name)]
    [InlineData(ExplorerSortOrder.LastEdited)]
    public void AFullyExpandedTreeOfTenThousandNodesFlattensInUnder100Milliseconds(ExplorerSortOrder order)
    {
        var (nodes, folders) = BuildTree();
        Assert.Equal(10_000, nodes.Count);
        var previews = Previews(nodes);
        var expandAll = UiState.Default with { ExpandedFolderIds = folders, SortOrder = order };

        // Loading the in-memory tree and flattening it, from scratch (the first run also warms up the JIT).
        ExplorerViewModel? explorer = null;
        ViewModelContext? context = null;
        var initial = Fastest(6, () =>
        {
            (context, explorer) = CreateExplorer();
            return Time(() =>
            {
                context.Model.Load(nodes, previews);
                explorer.Load(expandAll);
            });
        });
        output.WriteLine($"{order}: load and flatten {explorer!.Rows.Count} rows: {initial.TotalMilliseconds:F1} ms");

        // Headers (Recent + workspace) + 8 recent rows + every node.
        Assert.Equal(2 + ExplorerViewModel.RecentCount + 10_000, explorer.Rows.Count);
        Assert.True(initial < TimeSpan.FromMilliseconds(100), $"Flattening took {initial.TotalMilliseconds:F1} ms.");

        var rebuild = Fastest(5, () => Time(explorer.RebuildTree));
        output.WriteLine($"{order}: rebuild without changes: {rebuild.TotalMilliseconds:F1} ms");
        Assert.True(rebuild < TimeSpan.FromMilliseconds(100), $"Rebuilding took {rebuild.TotalMilliseconds:F1} ms.");

        // A live change: a note renamed (Name order) or moved (Manual order) to the other end of the root.
        var resets = 0;
        explorer.Rows.CollectionChanged += (_, e) => resets += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;
        var note = context!.Model.GetChildren(null).First(node => node.IsDocument);
        var flip = false;
        var live = Fastest(5, () =>
        {
            flip = !flip;
            var changed = note with { Name = flip ? "zzz" : note.Name, Position = flip ? "z" : note.Position };
            return Time(() => context.Model.Upsert(changed, NodeChangeKind.Moved));
        });
        output.WriteLine($"{order}: apply one moved or renamed note: {live.TotalMilliseconds:F1} ms");
        Assert.Equal(0, resets);
        Assert.True(live < TimeSpan.FromMilliseconds(100), $"A live change took {live.TotalMilliseconds:F1} ms.");

        var collapse = Fastest(5, () =>
        {
            var elapsed = Time(() => explorer.SetExpanded(folders[0], false));
            Assert.Equal(2 + ExplorerViewModel.RecentCount + 10_000 - 990, explorer.Rows.Count);
            explorer.SetExpanded(folders[0], true);
            return elapsed;
        });
        output.WriteLine($"{order}: collapse a folder with 990 descendants: {collapse.TotalMilliseconds:F1} ms");
        Assert.True(collapse < TimeSpan.FromMilliseconds(100), $"Collapsing took {collapse.TotalMilliseconds:F1} ms.");
    }
}
