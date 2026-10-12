using System.Diagnostics;
using System.Text;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Find;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Find;

/// <summary>Find and replace across tab switches, with very many matches and over long texts.</summary>
public sealed class FindReplaceAdvancedTests
{
    // Applies the panel's edits to the active tab's buffer, as the App does.
    private static List<IReadOnlyList<TextEdit>> ApplyEditsToActiveTab(ShellHarness harness)
    {
        var applied = new List<IReadOnlyList<TextEdit>>();
        harness.Shell.FindReplace.ApplyEditsRequested += (_, e) =>
        {
            var tab = harness.Tabs.SelectedTab!;
            ((FakeEditorBuffer)tab.Buffer!).Apply(e.Edits);
            tab.OnBufferChanged();
            applied.Add(e.Edits);
        };
        return applied;
    }

    private static async Task<TabViewModel> NoteWithTextAsync(ShellHarness harness, string text)
    {
        var tab = harness.Tabs.Tabs[0];
        var buffer = await harness.ShowAsync(tab);
        buffer.SetText(text);
        tab.OnBufferChanged();
        return tab;
    }

    [Fact]
    public async Task FindFollowsATabThatLoadsAfterItBecameActive()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var first = await NoteWithTextAsync(harness, "one beta here");
            var other = await harness.Core.Nodes.CreateDocumentAsync(harness.Shell.Context.Workspace.Id, null, "other.txt", initialText: "beta and beta");
            await harness.SettleAsync();
            var lazy = (await harness.Tabs.OpenAsync(other.Id, activate: false))!;
            var find = harness.Shell.FindReplace;
            harness.Shell.FindCommand.Execute(null);
            find.Query = "beta";
            Assert.Equal("1 of 1", find.StatusText);

            harness.Tabs.SelectedTab = lazy;
            Assert.Null(lazy.Buffer);
            Assert.Equal("No results", find.StatusText);

            // The App attaches the tab's document once its text is loaded.
            await harness.ShowAsync(lazy);

            Assert.Equal("1 of 2", find.StatusText);
            Assert.Equal([new TextMatch(0, 4), new TextMatch(9, 4)], find.Matches);
            Assert.True(find.FindNextCommand.CanExecute(null));

            // Back to the first tab: its own matches.
            harness.Tabs.SelectedTab = first;
            Assert.Equal("1 of 1", find.StatusText);
            Assert.Equal([new TextMatch(4, 4)], find.Matches);
        });
    }

    [Fact]
    public async Task FindStartsFromTheCaretOfTheTabItSwitchesTo()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            await NoteWithTextAsync(harness, "x");
            var second = await harness.Tabs.NewUntitledAsync();
            var buffer = await harness.ShowAsync(second);
            buffer.SetText("beta beta beta");
            second.OnBufferChanged();
            second.UpdateViewState(caretOffset: 6, selectionStart: 6, selectionLength: 0, verticalOffset: 0, horizontalOffset: 0);
            harness.Tabs.SelectedTab = harness.Tabs.Tabs[0];
            var find = harness.Shell.FindReplace;
            find.Open();
            find.Query = "beta";
            Assert.Equal("No results", find.StatusText);

            harness.Tabs.SelectedTab = second;

            Assert.Equal("3 of 3", find.StatusText);
        });
    }

    [Fact]
    public async Task MoreMatchesThanTheCountShowsAreAllReplaced()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var count = TextSearchEngine.MaxMatches + 1;
            var tab = await NoteWithTextAsync(harness, string.Concat(Enumerable.Repeat("a ", count)));
            var applied = ApplyEditsToActiveTab(harness);
            var find = harness.Shell.FindReplace;
            find.OpenReplace();
            find.Query = "a";
            find.Replacement = "b";

            Assert.Equal(TextSearchEngine.MaxMatches, find.MatchCount);
            Assert.Equal("1 of 100000+", find.StatusText);

            await find.ReplaceAllCommand.ExecuteAsync(null);

            Assert.Equal(count, Assert.Single(applied).Count);
            var text = ((FakeEditorBuffer)tab.Buffer!).Text;
            Assert.DoesNotContain('a', text);
            Assert.Equal(count, text.Count(ch => ch == 'b'));
            Assert.Equal("Replaced 100,001 matches", harness.Shell.Toasts.Toasts[^1].Message);
            Assert.Equal("No results", find.StatusText);
        });
    }

    [Fact]
    public async Task LongTextsAreSearchedInTheBackgroundAndANewerQueryWins()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var text = new StringBuilder(new string('x', 1_200_000)).Append(" alpha omega omega").ToString();
            await NoteWithTextAsync(harness, text);
            var find = harness.Shell.FindReplace;
            find.Open();
            var searching = new List<bool>();
            find.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FindReplaceViewModel.IsSearching))
                {
                    searching.Add(find.IsSearching);
                }
            };

            find.Query = "alpha";
            find.Query = "omega";
            await harness.SettleAsync();

            Assert.True(searching[0]);
            Assert.False(find.IsSearching);
            Assert.Equal("1 of 2", find.StatusText);
            Assert.Equal(2, find.MatchCount);
            Assert.Equal(1_200_007, find.Matches[0].Offset);
        });
    }

    [Fact]
    public async Task ReplaceAllOverALongTextRunsOffTheUiThread()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var text = new string('x', 1_100_000) + "\nold old\n";
            var tab = await NoteWithTextAsync(harness, text);
            var applied = ApplyEditsToActiveTab(harness);
            var find = harness.Shell.FindReplace;
            find.OpenReplace();
            find.Query = "old";
            find.Replacement = "new";
            await harness.SettleAsync();
            Assert.Equal("1 of 2", find.StatusText);

            var searching = new List<bool>();
            find.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FindReplaceViewModel.IsSearching))
                {
                    searching.Add(find.IsSearching);
                }
            };

            await find.ReplaceAllCommand.ExecuteAsync(null);
            await harness.SettleAsync();

            Assert.True(searching[0]);
            Assert.Single(applied);
            Assert.EndsWith("\nnew new\n", ((FakeEditorBuffer)tab.Buffer!).Text, StringComparison.Ordinal);
            Assert.Equal("Replaced 2 matches", harness.Shell.Toasts.Toasts[^1].Message);
            Assert.False(find.IsSearching);
        });
    }

    [Fact]
    public async Task ReplaceAllDropsItsEditsWhenTheTextChangedMeanwhile()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var text = new string('x', 1_100_000) + "\nold old\n";
            var tab = await NoteWithTextAsync(harness, text);
            var applied = ApplyEditsToActiveTab(harness);
            var find = harness.Shell.FindReplace;
            find.OpenReplace();
            find.Query = "old";
            find.Replacement = "new";
            await harness.SettleAsync();

            var run = find.ReplaceAllCommand.ExecuteAsync(null);
            ShellHarness.Type(tab, "typed meanwhile");
            await run;
            await harness.SettleAsync();

            Assert.Empty(applied);
            Assert.EndsWith("\nold old\ntyped meanwhile", ((FakeEditorBuffer)tab.Buffer!).Text, StringComparison.Ordinal);
            Assert.Contains(
                harness.Shell.Toasts.Toasts,
                toast => toast.Kind == ToastKind.Error && toast.Message == "The text changed while NimPad was replacing, so nothing was replaced. Try again.");
        });
    }

    [Fact]
    public void ReplaceAllReportsAPatternThatTakesTooLongInsteadOfThrowing()
    {
        var text = new string('a', 40) + "!";
        var watch = Stopwatch.StartNew();
        var result = TextSearchEngine.ReplaceEveryMatch(text, new TextSearchOptions("(a+)+$", UseRegex: true), "b");

        Assert.Empty(result.Edits);
        Assert.Equal("The pattern takes too long to search. Try a simpler one.", result.Error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }
}
