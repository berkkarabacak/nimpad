using NimPad.Presentation.Find;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Find;

public sealed class FindReplaceViewModelTests
{
    private const string Text = "Decisions\n- Ship the beta to 50 users\n[ ] Mara send the beta invite list\n";

    private static async Task<(FindReplaceViewModel Find, FakeEditorBuffer Buffer, List<TextMatch> Selected, List<IReadOnlyList<TextEdit>> Edits)> OpenAsync(ShellHarness harness)
    {
        var tab = harness.Tabs.Tabs[0];
        var buffer = await harness.ShowAsync(tab);
        buffer.SetText(Text);
        tab.OnBufferChanged();
        var find = harness.Shell.FindReplace;
        var selected = new List<TextMatch>();
        var edits = new List<IReadOnlyList<TextEdit>>();
        find.SelectMatchRequested += (_, e) =>
        {
            selected.Add(e.Match);
            tab.UpdateViewState(e.Match.End, e.Match.Offset, e.Match.Length, 0, 0);
        };
        find.ApplyEditsRequested += (_, e) =>
        {
            edits.Add(e.Edits);
            buffer.Apply(e.Edits);
            tab.OnBufferChanged();
        };
        return (find, buffer, selected, edits);
    }

    [Fact]
    public async Task FindCountsMatchesAndWrapsAround()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (find, _, selected, _) = await OpenAsync(harness);

            harness.Shell.FindCommand.Execute(null);
            Assert.True(find.IsOpen);
            Assert.False(find.IsReplaceMode);
            Assert.Equal(string.Empty, find.StatusText);

            find.Query = "beta";
            Assert.Equal("1 of 2", find.StatusText);
            Assert.Equal([new TextMatch(21, 4)], selected);

            find.FindNext();
            Assert.Equal("2 of 2", find.StatusText);
            find.FindNextCommand.Execute(null);
            Assert.Equal("1 of 2", find.StatusText);
            find.FindPrevious();
            Assert.Equal("2 of 2", find.StatusText);
            Assert.Equal(new TextMatch(56, 4), selected[^1]);

            find.Query = "gamma";
            Assert.Equal("No results", find.StatusText);
            Assert.False(find.HasMatches);
            Assert.False(find.FindNextCommand.CanExecute(null));

            find.UseRegex = true;
            find.Query = "(beta";
            Assert.True(find.HasError);
            Assert.Equal("Unmatched “(”", find.StatusText);

            find.Query = @"b\w+a";
            Assert.False(find.HasError);
            Assert.Equal(2, find.MatchCount);

            var closed = 0;
            find.Closed += (_, _) => closed++;
            find.CloseCommand.Execute(null);
            Assert.False(find.IsOpen);
            Assert.Empty(find.Matches);
            Assert.Equal(1, closed);
        });
    }

    [Fact]
    public async Task OpeningPrefillsASingleLineSelection()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (find, _, _, _) = await OpenAsync(harness);

            harness.Shell.ReplaceCommand.Execute("Mara");
            Assert.True(find.IsReplaceMode);
            Assert.Equal("Mara", find.Query);
            Assert.Equal("1 of 1", find.StatusText);

            find.Close();
            harness.Shell.FindCommand.Execute("two\nlines");
            Assert.Equal("Mara", find.Query);
        });
    }

    [Fact]
    public async Task OptionsChangeTheMatches()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (find, buffer, _, _) = await OpenAsync(harness);
            buffer.SetText("Beta beta alphabeta");
            harness.Tabs.Tabs[0].OnBufferChanged();
            find.Open();

            find.Query = "beta";
            Assert.Equal(3, find.MatchCount);
            find.ToggleMatchCaseCommand.Execute(null);
            Assert.Equal(2, find.MatchCount);
            find.ToggleWholeWordCommand.Execute(null);
            Assert.Equal(1, find.MatchCount);
            Assert.Equal(new TextMatch(5, 4), find.CurrentMatch);
        });
    }

    [Fact]
    public async Task DocumentChangesRecomputeMatches150MillisecondsLater()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (find, _, selected, _) = await OpenAsync(harness);
            find.Open();
            find.Query = "beta";
            selected.Clear();

            ShellHarness.Type(harness.Tabs.Tabs[0], "one more beta\n");
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(149));
            Assert.Equal(2, find.MatchCount);

            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(1));
            Assert.Equal(3, find.MatchCount);
            Assert.Equal("1 of 3", find.StatusText);
            // Typing never moves the editor's selection.
            Assert.Empty(selected);
        });
    }

    [Fact]
    public async Task ReplaceChangesTheCurrentMatchAndMovesToTheNext()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (find, buffer, selected, edits) = await OpenAsync(harness);
            find.OpenReplace();
            find.Query = "beta";
            find.Replacement = "preview";

            find.ReplaceCommand.Execute(null);

            Assert.Equal([new TextEdit(21, 4, "preview")], Assert.Single(edits));
            Assert.Contains("Ship the preview to 50", buffer.Text);
            Assert.Equal("1 of 1", find.StatusText);
            Assert.Equal(new TextMatch(59, 4), selected[^1]);

            find.ReplaceCommand.Execute(null);
            Assert.Equal("No results", find.StatusText);
            Assert.DoesNotContain("beta", buffer.Text);
        });
    }

    [Fact]
    public async Task ReplaceAllAppliesEveryEditAsOneStep()
    {
        await using var harness = await ShellHarness.StartAsync();
        await harness.RunAsync(async () =>
        {
            var (find, buffer, _, edits) = await OpenAsync(harness);
            find.OpenReplace();
            find.UseRegex = true;
            find.Query = @"(\w+) the";
            find.Replacement = "the $1";

            var planned = find.ReplaceAll();
            Assert.Equal([new TextEdit(12, 8, "the Ship"), new TextEdit(47, 8, "the send")], planned);
            Assert.Equal(Text, buffer.Text);

            find.ReplaceAllCommand.Execute(null);

            Assert.Equal(planned, Assert.Single(edits));
            Assert.Equal("Decisions\n- the Ship beta to 50 users\n[ ] Mara the send beta invite list\n", buffer.Text);
            Assert.Equal("Replaced 2 matches", harness.Shell.Toasts.Toasts[^1].Message);
            // "Mara the send" matches the pattern now.
            Assert.Equal("1 of 1", find.StatusText);
        });
    }
}
