using NimPad.Presentation.Integration;
using Xunit;

namespace NimPad.Presentation.Tests.Integration;

public sealed class AutostartCommandTests
{
    private static readonly string Exe = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Program Files", "NimPad", "NimPad.exe"));

    [Fact]
    public void BuildsAQuotedCommandThatStartsInTheBackground()
    {
        Assert.Equal($"\"{Exe}\" --background", AutostartCommand.Build(Exe));
        Assert.Equal(Exe, AutostartCommand.GetExecutablePath(AutostartCommand.Build(Exe)));
        Assert.Throws<ArgumentException>(() => AutostartCommand.Build("a\"b.exe"));
        Assert.Throws<ArgumentException>(() => AutostartCommand.Build(" "));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("\"\" --background", null)]
    [InlineData("C:\\Tools\\NimPad.exe --background", "C:\\Tools\\NimPad.exe")]
    [InlineData("  \"C:\\My Tools\\NimPad.exe\"", "C:\\My Tools\\NimPad.exe")]
    [InlineData("\"C:\\Unclosed\\NimPad.exe", "C:\\Unclosed\\NimPad.exe")]
    [InlineData("C:\\Tools\\NimPad.exe", "C:\\Tools\\NimPad.exe")]
    public void ReadsTheExecutableOfARunValue(string? command, string? expected)
    {
        Assert.Equal(expected, AutostartCommand.GetExecutablePath(command));
    }

    [Fact]
    public void ARunValueCountsOnlyWhenItStartsThisExecutable()
    {
        Assert.True(AutostartCommand.PointsTo(AutostartCommand.Build(Exe), Exe));
        Assert.True(AutostartCommand.PointsTo($"\"{Exe.ToUpperInvariant()}\" --background", Exe));
        Assert.True(AutostartCommand.PointsTo($"\"{Path.Combine(Path.GetDirectoryName(Exe)!, ".", "NimPad.exe")}\"", Exe));
        Assert.False(AutostartCommand.PointsTo(AutostartCommand.Build(Path.Combine(Path.GetTempPath(), "Old", "NimPad.exe")), Exe));
        Assert.False(AutostartCommand.PointsTo(null, Exe));
        Assert.False(AutostartCommand.PointsTo("", Exe));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(new byte[0], false)]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 }, true)]
    [InlineData(new byte[] { 0x07 }, true)]
    public void ReadsTaskManagersStartupApprovedMark(byte[]? data, bool turnedOff)
    {
        Assert.Equal(turnedOff, AutostartCommand.IsTurnedOffByUser(data));
    }
}
