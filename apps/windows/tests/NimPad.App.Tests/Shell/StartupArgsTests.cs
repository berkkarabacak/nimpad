using System.IO;
using NimPad.App.Services;
using Xunit;

namespace NimPad.App.Tests.Shell;

public class StartupArgsTests
{
    private static readonly string Cwd = Path.Combine(Path.GetTempPath(), "nimpad-cwd");

    [Fact]
    public void NoArgumentsMeansDefaults()
    {
        var args = StartupArgs.Parse([], Cwd);
        Assert.Null(args.DataDirectory);
        Assert.False(args.NoSingleInstance);
        Assert.False(args.NoHotkey);
        Assert.False(args.Background);
        Assert.Empty(args.Files);
        Assert.Same(StartupArgs.Empty, StartupArgs.Parse(null));
    }

    [Fact]
    public void ParsesTheTestCommandLine()
    {
        var args = StartupArgs.Parse(["--data-dir", @"C:\Temp\nimpad data", "--no-single-instance", "--no-hotkey"], Cwd);
        Assert.Equal(@"C:\Temp\nimpad data", args.DataDirectory);
        Assert.True(args.NoSingleInstance);
        Assert.True(args.NoHotkey);
        Assert.False(args.Background);
        Assert.Empty(args.Files);
        Assert.Empty(args.UnknownOptions);
        Assert.Empty(args.Problems);
    }

    [Fact]
    public void AcceptsTheEqualsFormAndAnyCase()
    {
        var args = StartupArgs.Parse(["--DATA-DIR=D:\\notes", "--Background"], Cwd);
        Assert.Equal("D:\\notes", args.DataDirectory);
        Assert.True(args.Background);
    }

    [Fact]
    public void FilesBecomeAbsoluteAndDoubleDashEndsOptions()
    {
        var args = StartupArgs.Parse(["notes.txt", "--no-hotkey", "--", "--weird-name.md"], Cwd);
        Assert.Equal([Path.GetFullPath("notes.txt", Cwd), Path.GetFullPath("--weird-name.md", Cwd)], args.Files);
        Assert.True(args.NoHotkey);
    }

    [Fact]
    public void UnknownOptionsAndAMissingValueAreReportedNotThrown()
    {
        var args = StartupArgs.Parse(["--fancy", "--data-dir"], Cwd);
        Assert.Null(args.DataDirectory);
        Assert.Equal(["--fancy"], args.UnknownOptions);
        Assert.Single(args.Problems);
    }

    [Fact]
    public void RoundTripsThroughToArguments()
    {
        var original = StartupArgs.Parse(["--data-dir", "D:\\x", "--no-hotkey", "--background", "a.txt"], Cwd);
        var again = StartupArgs.Parse(original.ToArguments().ToList(), Cwd);
        Assert.Equal(original.DataDirectory, again.DataDirectory);
        Assert.Equal(original.NoHotkey, again.NoHotkey);
        Assert.Equal(original.Background, again.Background);
        Assert.Equal(original.Files, again.Files);
    }
}
