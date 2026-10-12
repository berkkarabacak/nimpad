using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace NimPad.App.Services;

/// <summary>How serious a log line is.</summary>
public enum LogLevel
{
    /// <summary>Normal operation (startup, shutdown).</summary>
    Info,

    /// <summary>Something unexpected that NimPad handled.</summary>
    Warning,

    /// <summary>A failure.</summary>
    Error,
}

/// <summary>
/// A small rolling file log: one UTF-8 file per day (<c>nimpad-yyyy-MM-dd.log</c>) under the logs folder, files older
/// than <see cref="RetentionDays"/> days are deleted when the log starts. Thread-safe; every line is flushed at once
/// so a crash or a kill loses nothing. Writing never throws (a failing disk only loses log lines).
/// </summary>
/// <remarks>
/// Privacy: callers log events, exception types, messages and stack traces, never note text. <see cref="TraceListener"/>
/// forwards <see cref="Trace"/> output (the view models report their failures there).
/// </remarks>
public sealed class FileLog : IDisposable
{
    /// <summary>How many days of log files are kept.</summary>
    public const int RetentionDays = 14;

    /// <summary>The file name prefix.</summary>
    public const string FilePrefix = "nimpad-";

    private const string DateFormat = "yyyy-MM-dd";
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    /// <summary>Opens (or creates) today's file in <paramref name="directory"/> and deletes expired files.</summary>
    /// <param name="directory">The logs folder (<c>AppPaths.LogsDir</c>); created when missing.</param>
    /// <param name="clock">The clock (tests); the system clock by default.</param>
    public FileLog(string directory, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        _clock = clock ?? TimeProvider.System;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            DeleteExpiredFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"The log folder could not be prepared: {ex.Message}");
        }
    }

    /// <summary>The log of the running app, once <see cref="App"/> created it.</summary>
    public static FileLog? Current { get; set; }

    /// <summary>The logs folder.</summary>
    public string Directory { get; }

    /// <summary>The file written today.</summary>
    public string CurrentFilePath => PathFor(Today());

    /// <summary>Writes an informational line to <see cref="Current"/>, if any.</summary>
    public static void Info(string message) => Current?.Write(LogLevel.Info, message);

    /// <summary>Writes a warning to <see cref="Current"/>, if any.</summary>
    public static void Warn(string message, Exception? error = null) => Current?.Write(LogLevel.Warning, message, error);

    /// <summary>Writes an error to <see cref="Current"/>, if any.</summary>
    public static void Error(string message, Exception? error = null) => Current?.Write(LogLevel.Error, message, error);

    /// <summary>Appends one entry (and the exception, with its inner exceptions and stack traces).</summary>
    public void Write(LogLevel level, string message, Exception? error = null)
    {
        var now = _clock.GetLocalNow();
        var text = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level switch { LogLevel.Warning => "WARN ", LogLevel.Error => "ERROR", _ => "INFO " })
            .Append(" [")
            .Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture))
            .Append("] ")
            .Append(message);
        if (error is not null)
        {
            text.AppendLine().Append(error.ToString());
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter(DateOnly.FromDateTime(now.DateTime)).WriteLine(text.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // The log must never take the app down; drop the line.
                CloseWriter();
            }
        }
    }

    /// <summary>Flushes and closes the file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriter();
        }

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }

    /// <summary>Deletes log files whose date is more than <see cref="RetentionDays"/> days before today.</summary>
    /// <returns>How many files were deleted.</returns>
    public int DeleteExpiredFiles()
    {
        var cutoff = Today().AddDays(-RetentionDays);
        var deleted = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, FilePrefix + "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!DateOnly.TryParseExact(name[FilePrefix.Length..], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another NimPad may hold it; it goes next time.
            }
        }

        return deleted;
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    private string PathFor(DateOnly date) =>
        Path.Combine(Directory, FilePrefix + date.ToString(DateFormat, CultureInfo.InvariantCulture) + ".log");

    private StreamWriter EnsureWriter(DateOnly date)
    {
        if (_writer is not null && _writerDate == date)
        {
            return _writer;
        }

        CloseWriter();
        System.IO.Directory.CreateDirectory(Directory);
        var stream = new FileStream(PathFor(date), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        _writerDate = date;
        return _writer;
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nothing left to save.
        }

        _writer = null;
    }

    /// <summary>Forwards <see cref="Trace"/> output to a <see cref="FileLog"/>.</summary>
    public sealed class TraceListener(FileLog log) : System.Diagnostics.TraceListener
    {
        /// <inheritdoc/>
        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                log.Write(LogLevel.Info, message.TrimEnd());
            }
        }

        /// <inheritdoc/>
        public override void WriteLine(string? message) => Write(message);

        /// <inheritdoc/>
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var level = eventType switch
            {
                TraceEventType.Critical or TraceEventType.Error => LogLevel.Error,
                TraceEventType.Warning => LogLevel.Warning,
                _ => LogLevel.Info,
            };
            log.Write(level, message.TrimEnd());
        }

        /// <inheritdoc/>
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            var message = format;
            if (args is { Length: > 0 } && format is not null)
            {
                try
                {
                    message = string.Format(CultureInfo.InvariantCulture, format, args);
                }
                catch (FormatException)
                {
                    // Keep the raw format text.
                }
            }

            TraceEvent(eventCache, source, eventType, id, message);
        }
    }
}
