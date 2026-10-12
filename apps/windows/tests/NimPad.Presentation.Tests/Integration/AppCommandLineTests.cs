using NimPad.Presentation.Integration;
using Xunit;

namespace NimPad.Presentation.Tests.Integration;

public sealed class AppCommandLineTests
{
    private static readonly string Here = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nimpad-cwd"));

    [Fact]
    public void ParsesEveryOptionInAnyOrderAndKeepsFiles()
    {
        var line = AppCommandLine.Parse(["notes.txt", "--NO-HOTKEY", "--data-dir", "C:/data dir", "--background", "--no-single-instance", "--restarted", "b.md"]);

        Assert.Equal("C:/data dir", line.DataDirectory);
        Assert.True(line.NoHotkey);
        Assert.True(line.NoSingleInstance);
        Assert.True(line.Background);
        Assert.True(line.Restarted);
        Assert.Equal(["notes.txt", "b.md"], line.Files);
        Assert.Empty(line.UnknownOptions);
    }

    [Fact]
    public void AcceptsEqualsSyntaxDoubleDashAndReportsUnknownOptions()
    {
        var line = AppCommandLine.Parse(["--data-dir=/tmp/x", "--verbose", "--background=yes", "--", "--not-an-option.txt", "--background"]);

        Assert.Equal("/tmp/x", line.DataDirectory);
        Assert.False(line.Background);
        Assert.Equal(["--verbose", "--background=yes"], line.UnknownOptions);
        Assert.Equal(["--not-an-option.txt", "--background"], line.Files);
    }

    [Fact]
    public void NeverThrowsOnEmptyOrIncompleteInput()
    {
        Assert.Empty(AppCommandLine.Parse(null).Files);
        Assert.Empty(AppCommandLine.Parse([]).Files);
        var line = AppCommandLine.Parse(["", "  ", "--data-dir"]);
        Assert.Null(line.DataDirectory);
        Assert.Empty(line.Files);
        Assert.Null(AppCommandLine.Parse(["--data-dir="]).DataDirectory);
    }

    [Fact]
    public void ResolvesFilesAgainstTheLaunchDirectoryAndDropsUnusablePaths()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "abs.txt");
        var line = AppCommandLine.Parse(["a.txt", Path.Combine("sub", "..", "b.txt"), absolute, "bad\0name"]);

        var files = line.ResolveFiles(Here);

        Assert.Equal([Path.Combine(Here, "a.txt"), Path.Combine(Here, "b.txt"), Path.GetFullPath(absolute)], files);
    }

    [Fact]
    public void RestartArgumentsKeepTheProfileAndTestFlagsButNotFilesOrBackground()
    {
        Assert.Equal("--restarted", AppCommandLine.Parse(["x.txt", "--background"]).BuildRestartArguments(Here));

        var line = AppCommandLine.Parse(["--data-dir", "my data", "--no-single-instance", "--no-hotkey", "x.txt"]);
        var arguments = line.BuildRestartArguments(Here);

        var split = WindowsCommandLine.Split(arguments);
        Assert.Equal(["--restarted", "--data-dir", Path.Combine(Here, "my data"), "--no-single-instance", "--no-hotkey"], split);
        var reparsed = AppCommandLine.Parse(split);
        Assert.True(reparsed.Restarted);
        Assert.Equal(Path.Combine(Here, "my data"), reparsed.DataDirectory);
        Assert.Empty(reparsed.Files);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData(@"C:\Program Files\NimPad\", "\"C:\\Program Files\\NimPad\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"back\\""slash", "\"back\\\\\\\\\\\"slash\"")]
    [InlineData(@"C:\no-spaces\", @"C:\no-spaces\")]
    public void QuotesArgumentsSoTheyRoundTrip(string argument, string expected)
    {
        var quoted = WindowsCommandLine.Quote(argument);

        Assert.Equal(expected, quoted);
        Assert.Equal([argument], WindowsCommandLine.Split(quoted));
    }

    [Fact]
    public void SplitHandlesSeveralArgumentsAndQuotedRuns()
    {
        string[] arguments = ["a b", "", "c\"d", @"e\", @"f\\", "plain", "tab\there"];
        var line = string.Join(' ', arguments.Select(WindowsCommandLine.Quote));

        Assert.Equal(arguments, WindowsCommandLine.Split(line));
        Assert.Equal(["ab c", "d"], WindowsCommandLine.Split("a\"b c\"  d "));
        Assert.Equal(["x\"y"], WindowsCommandLine.Split("\"x\"\"y\""));
    }
}
