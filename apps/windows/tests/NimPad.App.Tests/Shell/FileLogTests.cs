using System.Diagnostics;
using System.IO;
using NimPad.App.Services;
using Xunit;

namespace NimPad.App.Tests.Shell;

public class FileLogTests
{
    [Fact]
    public void WritesOneFilePerDayAndDeletesFilesOlderThanTwoWeeks()
    {
        using var data = new TempDataDir();
        var logs = Path.Combine(data.Path, "logs");
        Directory.CreateDirectory(logs);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero));
        var expired = Path.Combine(logs, "nimpad-2026-09-27.log");
        var kept = Path.Combine(logs, "nimpad-2026-09-28.log");
        var foreign = Path.Combine(logs, "other.log");
        File.WriteAllText(expired, "old");
        File.WriteAllText(kept, "recent");
        File.WriteAllText(foreign, "not ours");

        using (var log = new FileLog(logs, clock))
        {
            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(kept));
            Assert.True(File.Exists(foreign));

            log.Write(LogLevel.Info, "first day");
            clock.Now = clock.Now.AddDays(1);
            log.Write(LogLevel.Error, "second day", new InvalidOperationException("boom"));
        }

        var day1 = File.ReadAllText(Path.Combine(logs, "nimpad-2026-10-12.log"));
        var day2 = File.ReadAllText(Path.Combine(logs, "nimpad-2026-10-13.log"));
        Assert.Contains("INFO  ", day1, StringComparison.Ordinal);
        Assert.Contains("first day", day1, StringComparison.Ordinal);
        Assert.DoesNotContain("second day", day1, StringComparison.Ordinal);
        Assert.Contains("ERROR", day2, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: boom", day2, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceOutputReachesTheLogAndAClosedLogIgnoresWrites()
    {
        using var data = new TempDataDir();
        var log = new FileLog(data.Path);
        var listener = new FileLog.TraceListener(log);
        listener.TraceEvent(new TraceEventCache(), "test", TraceEventType.Warning, 0, "Saving {0} failed", "the session");
        log.Dispose();
        log.Write(LogLevel.Info, "after dispose");
        var text = File.ReadAllText(log.CurrentFilePath);
        Assert.Contains("WARN ", text, StringComparison.Ordinal);
        Assert.Contains("Saving the session failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("after dispose", text, StringComparison.Ordinal);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
