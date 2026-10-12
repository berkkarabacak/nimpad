using NimPad.Presentation.Integration;
using Xunit;

namespace NimPad.Presentation.Tests.Integration;

public sealed class ExternalFileChangesTests
{
    private static readonly DateTime Saved = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheWriteTimeOfOurOwnSaveMeansNobodyElseTouchedTheFile()
    {
        Assert.Equal(ExternalFileChangeKind.OwnSave, ExternalFileChanges.Evaluate(new(true, Saved), Saved, reportedMissing: false));
    }

    [Fact]
    public void AnyOtherWriteTimeIsAChange()
    {
        Assert.Equal(ExternalFileChangeKind.Changed, ExternalFileChanges.Evaluate(new(true, Saved.AddTicks(1)), Saved, false));
        Assert.Equal(ExternalFileChangeKind.Changed, ExternalFileChanges.Evaluate(new(true, Saved.AddSeconds(-5)), Saved, false));

        // Never saved by NimPad in this session: the tab compares with the time it loaded.
        Assert.Equal(ExternalFileChangeKind.Changed, ExternalFileChanges.Evaluate(new(true, Saved), null, false));
    }

    [Fact]
    public void AMissingFileIsReportedOnceAndItsReturnIsReported()
    {
        Assert.Equal(ExternalFileChangeKind.Missing, ExternalFileChanges.Evaluate(ExternalFileState.Missing, Saved, false));
        Assert.Equal(ExternalFileChangeKind.None, ExternalFileChanges.Evaluate(ExternalFileState.Missing, Saved, true));
        Assert.Equal(ExternalFileChangeKind.Reappeared, ExternalFileChanges.Evaluate(new(true, Saved), Saved, true));
    }

    [Fact]
    public void AnUnreadableFileReportsNothing()
    {
        Assert.Equal(ExternalFileChangeKind.None, ExternalFileChanges.Evaluate(null, Saved, false));
        Assert.Equal(ExternalFileChangeKind.None, ExternalFileChanges.Evaluate(null, null, true));
    }

    [Fact]
    public void ReadsTheStateOfAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "nimpad-state-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Assert.Equal(ExternalFileState.Missing, ExternalFileState.Read(path));
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, Saved);
            Assert.Equal(new ExternalFileState(true, Saved), ExternalFileState.Read(path));
            Assert.Equal(ExternalFileState.Missing, ExternalFileState.Read(Path.Combine(path + ".dir", "child.txt")));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
