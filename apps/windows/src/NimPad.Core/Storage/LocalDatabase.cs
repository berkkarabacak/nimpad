using Microsoft.Data.Sqlite;

namespace NimPad.Core.Storage;

/// <summary>
/// The local SQLite database: opened, checked, migrated, and split into the single <see cref="Writer"/> and the
/// pooled <see cref="Readers"/>.
/// </summary>
/// <remarks>
/// <see cref="Open(string, string, TimeProvider?)"/> does, in order:
/// <list type="number">
/// <item>Runs <c>PRAGMA quick_check</c> on an existing file. When it reports damage, the file (and its
/// <c>-wal</c>/<c>-shm</c>) is moved aside as <c>nimpad.corrupt-{timestamp}.db</c> and the newest healthy backup is
/// restored, or a fresh database is created; <see cref="Recovery"/> describes what happened.</item>
/// <item>Opens the write connection with <c>journal_mode=WAL</c>, <c>synchronous=FULL</c>, <c>foreign_keys=ON</c>
/// and a busy timeout.</item>
/// <item>Fails loudly when SQLite was built without FTS5.</item>
/// <item>Applies pending schema migrations (<c>PRAGMA user_version</c>).</item>
/// </list>
/// </remarks>
public sealed class LocalDatabase : IDisposable
{
    /// <summary>How long a connection waits for a lock before failing with <c>SQLITE_BUSY</c> (the pragmas spell it out).</summary>
    public const int BusyTimeoutMilliseconds = 5000;

    private int _disposed;

    private LocalDatabase(
        string databasePath,
        string backupsDirectory,
        TimeProvider timeProvider,
        StartupRecovery recovery,
        int schemaVersion,
        string journalMode,
        SqliteWriter writer)
    {
        DatabasePath = databasePath;
        BackupsDirectory = backupsDirectory;
        TimeProvider = timeProvider;
        Recovery = recovery;
        SchemaVersion = schemaVersion;
        JournalMode = journalMode;
        Writer = writer;
        Readers = new ReadConnections(databasePath);
    }

    /// <summary>The newest schema version this build creates.</summary>
    public static int LatestSchemaVersion => SchemaMigrations.LatestVersion;

    /// <summary>The full path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Where daily backups live (see <see cref="BackupService"/>).</summary>
    public string BackupsDirectory { get; }

    /// <summary>The clock used for recovery file names and backups.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>What the startup integrity check did.</summary>
    public StartupRecovery Recovery { get; }

    /// <summary>The schema version after migrations (<c>PRAGMA user_version</c>).</summary>
    public int SchemaVersion { get; }

    /// <summary>The journal mode SQLite accepted: <c>wal</c>, unless the file system cannot support it.</summary>
    public string JournalMode { get; }

    /// <summary>The single background writer that owns the write connection.</summary>
    public SqliteWriter Writer { get; }

    /// <summary>Pooled read connections.</summary>
    public ReadConnections Readers { get; }

    /// <summary>Opens the database at the locations described by <paramref name="paths"/>.</summary>
    public static LocalDatabase Open(AppPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Open(paths.DatabasePath, paths.BackupsDir, timeProvider);
    }

    /// <summary>Opens (creating if needed), checks and migrates the database at <paramref name="databasePath"/>.</summary>
    /// <param name="databasePath">The database file; its directory is created if missing.</param>
    /// <param name="backupsDirectory">Where backups are kept; used to restore a damaged database.</param>
    /// <param name="timeProvider">The clock; <see cref="TimeProvider.System"/> by default.</param>
    /// <exception cref="InvalidOperationException">SQLite lacks FTS5.</exception>
    /// <exception cref="SchemaVersionException">The database was created by a newer NimPad.</exception>
    /// <exception cref="SqliteException">
    /// The database could not be checked or opened for a reason other than damage, for example another process
    /// keeps it locked; the file is left as it is.
    /// </exception>
    public static LocalDatabase Open(string databasePath, string backupsDirectory, TimeProvider? timeProvider = null) =>
        Open(databasePath, backupsDirectory, timeProvider, DatabaseIntegrity.DefaultLockTimeout);

    /// <summary>
    /// <see cref="Open(string, string, TimeProvider?)"/> with how long the integrity check waits for a locked file
    /// before failing (tests use a short wait).
    /// </summary>
    internal static LocalDatabase Open(string databasePath, string backupsDirectory, TimeProvider? timeProvider, TimeSpan lockTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        timeProvider ??= TimeProvider.System;
        databasePath = Path.GetFullPath(databasePath);
        backupsDirectory = Path.GetFullPath(backupsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var recovery = StartupRecovery.Healthy;
        if (File.Exists(databasePath) && new FileInfo(databasePath).Length > 0
            && DatabaseIntegrity.Check(databasePath, lockTimeout) is { } problem)
        {
            recovery = DatabaseIntegrity.Recover(databasePath, backupsDirectory, problem, timeProvider);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // The writer owns this connection for the whole session; pooling would only delay closing the file.
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            var journalMode = connection.ExecuteScalar("PRAGMA journal_mode = WAL;") as string ?? "unknown";
            connection.ExecuteNonQuery("PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
            EnsureFts5(connection);
            var version = SchemaMigrations.Apply(connection);
            var writer = new SqliteWriter(connection);
            return new LocalDatabase(databasePath, backupsDirectory, timeProvider, recovery, version, journalMode, writer);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Lists <c>PRAGMA compile_options</c> of the bundled SQLite.</summary>
    internal static IReadOnlyList<string> GetCompileOptions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA compile_options;";
        using var reader = command.ExecuteReader();
        var options = new List<string>();
        while (reader.Read())
        {
            options.Add(reader.GetString(0));
        }

        return options;
    }

    /// <summary>
    /// Drains and stops the writer (closing the write connection) and closes idle read connections. Call
    /// <see cref="BackupService.CheckpointAndOptimizeAsync"/> first on a normal exit.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Writer.Dispose();
        Readers.ClearPool();
    }

    private static void EnsureFts5(SqliteConnection connection)
    {
        if (!GetCompileOptions(connection).Contains("ENABLE_FTS5"))
        {
            throw new InvalidOperationException(
                "The bundled SQLite was built without FTS5 (ENABLE_FTS5 is missing from PRAGMA compile_options). " +
                "NimPad's search index requires it.");
        }
    }
}
