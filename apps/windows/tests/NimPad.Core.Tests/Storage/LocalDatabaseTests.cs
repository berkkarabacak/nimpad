using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using NimPad.Core.Storage;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Storage;

public class LocalDatabaseTests
{
    private static readonly string[] ExpectedTables =
    [
        "device", "workspaces", "nodes", "document_contents", "document_sync_base", "document_links", "notes_fts",
        "pending_operations", "session_tabs", "recently_closed", "ui_state", "settings", "external_file_buffers",
    ];

    [Fact]
    public async Task OpensWithWalFullSyncAndForeignKeys()
    {
        using var temp = new TempDirectory();
        using var database = LocalDatabase.Open(temp.Combine("db", "nimpad.db"), temp.Combine("backups"));

        Assert.Equal("wal", database.JournalMode);
        var pragmas = await database.Writer.ExecuteAsync(context => (
            JournalMode: (string)context.Scalar("PRAGMA journal_mode;")!,
            Synchronous: (long)context.Scalar("PRAGMA synchronous;")!,
            ForeignKeys: (long)context.Scalar("PRAGMA foreign_keys;")!,
            BusyTimeout: (long)context.Scalar("PRAGMA busy_timeout;")!));

        Assert.Equal("wal", pragmas.JournalMode);
        Assert.Equal(2, pragmas.Synchronous); // FULL
        Assert.Equal(1, pragmas.ForeignKeys);
        Assert.Equal(LocalDatabase.BusyTimeoutMilliseconds, pragmas.BusyTimeout);
        Assert.Equal(StartupRecovery.Healthy, database.Recovery);
        Assert.True(File.Exists(database.DatabasePath));
    }

    [Fact]
    public void OpensAtTheResolvedAppPaths()
    {
        using var temp = new TempDirectory();
        var paths = AppPaths.Resolve(temp.Combine("data"), temp.Path, temp.Path);

        using var database = LocalDatabase.Open(paths);

        Assert.Equal(paths.DatabasePath, database.DatabasePath);
        Assert.Equal(paths.BackupsDir, database.BackupsDirectory);
        Assert.True(File.Exists(Path.Combine(temp.Path, "data", "profiles", "local", "nimpad.db")));
    }

    [Fact]
    public async Task Fts5IsCompiledInAndUsable()
    {
        using var temp = new TempDirectory();
        using var database = LocalDatabase.Open(temp.Combine("nimpad.db"), temp.Combine("backups"));

        var options = await database.Readers.ExecuteAsync(LocalDatabase.GetCompileOptions);
        Assert.Contains("ENABLE_FTS5", options);

        var tokenize = await database.Readers.ExecuteAsync(connection =>
            (string?)connection.ExecuteScalar("SELECT sql FROM sqlite_master WHERE name = 'notes_fts';"));
        Assert.Contains("fts5", tokenize);
        Assert.Contains("unicode61 remove_diacritics 2", tokenize);

        await database.Writer.ExecuteAsync(context =>
            context.Execute("INSERT INTO notes_fts (rowid, name, body) VALUES (1, 'Café notes', 'Crème brûlée recipe');"));
        var hits = await database.Readers.ExecuteAsync(connection =>
            (long)connection.ExecuteScalar("SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'creme AND cafe';")!);
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task CreatesEveryTableAndRecordsTheSchemaVersion()
    {
        using var temp = new TempDirectory();
        using var database = LocalDatabase.Open(temp.Combine("nimpad.db"), temp.Combine("backups"));

        var tables = await database.Readers.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view');";
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        });

        Assert.All(ExpectedTables, table => Assert.Contains(table, tables));
        Assert.Equal(LocalDatabase.LatestSchemaVersion, database.SchemaVersion);
        Assert.True(LocalDatabase.LatestSchemaVersion >= 1);
        var userVersion = await database.Readers.ExecuteAsync(connection => (long)connection.ExecuteScalar("PRAGMA user_version;")!);
        Assert.Equal(LocalDatabase.LatestSchemaVersion, userVersion);
    }

    [Fact]
    public async Task MigrationsAreIdempotentAcrossReopens()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("nimpad.db");
        using (var first = LocalDatabase.Open(path, temp.Combine("backups")))
        {
            await first.Writer.ExecuteAsync(context =>
                context.Execute("INSERT INTO settings (key, value, updated_at) VALUES ('theme', '\"dark\"', '2026-10-12T13:59:43.123Z');"));
        }

        using var second = LocalDatabase.Open(path, temp.Combine("backups"));
        Assert.Equal(LocalDatabase.LatestSchemaVersion, second.SchemaVersion);
        var value = await second.Readers.ExecuteAsync(connection =>
            (string?)connection.ExecuteScalar("SELECT value FROM settings WHERE key = 'theme';"));
        Assert.Equal("\"dark\"", value);

        // Applying again on an up-to-date connection changes nothing.
        var version = await second.Writer.ExecuteWithoutTransactionAsync(SchemaMigrations.Apply);
        Assert.Equal(LocalDatabase.LatestSchemaVersion, version);
    }

    // Later migrations use ALTER TABLE ADD COLUMN, which SQLite cannot make replayable; replays cannot happen
    // because each migration commits together with its user_version bump. What must hold is that applying the files
    // in order on an empty database yields exactly the schema LocalDatabase.Open creates.
    [Fact]
    public async Task MigrationFilesAppliedInOrderMatchTheOpenedSchema()
    {
        using var temp = new TempDirectory();
        string manual;
        using (var connection = new SqliteConnection($"Data Source={temp.Combine("manual.db")};Pooling=False"))
        {
            connection.Open();
            foreach (var migration in SchemaMigrations.All)
            {
                connection.ExecuteNonQuery(migration.Sql);
            }

            manual = SchemaText(connection);
        }

        using var database = LocalDatabase.Open(temp.Combine("opened.db"), temp.Combine("backups"));
        var opened = await database.Readers.ExecuteAsync(SchemaText);
        Assert.Equal(manual, opened);
        Assert.Contains("version INTEGER NOT NULL DEFAULT 1", opened);

        static string SchemaText(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name;";
            using var reader = command.ExecuteReader();
            var lines = new List<string>();
            while (reader.Read())
            {
                lines.Add($"{reader.GetString(0)} {reader.GetString(1)}: {reader.GetString(2)}");
            }

            return string.Join("\n", lines);
        }
    }

    [Fact]
    public async Task RefusesADatabaseFromANewerVersion()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("nimpad.db");
        using (var database = LocalDatabase.Open(path, temp.Combine("backups")))
        {
            await database.Writer.ExecuteWithoutTransactionAsync(connection => connection.ExecuteNonQuery("PRAGMA user_version = 999;"));
        }

        var error = Assert.Throws<SchemaVersionException>(() => LocalDatabase.Open(path, temp.Combine("backups")));
        Assert.Equal(999, error.DatabaseVersion);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ReadConnectionsAreQueryOnly()
    {
        using var temp = new TempDirectory();
        using var database = LocalDatabase.Open(temp.Combine("nimpad.db"), temp.Combine("backups"));

        var error = await Assert.ThrowsAsync<SqliteException>(() => database.Readers.ExecuteAsync(connection =>
            connection.ExecuteNonQuery("INSERT INTO ui_state (key, value, updated_at) VALUES ('x', '1', 'now');")));
        Assert.Contains("readonly", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GarbageDatabaseIsMovedAsideAndTheNewestBackupRestored()
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(TestStore.Start);
        var path = temp.Combine("profiles", "local", "nimpad.db");
        var backups = temp.Combine("profiles", "local", "backups");
        using (var database = LocalDatabase.Open(path, backups, clock))
        {
            await database.Writer.ExecuteAsync(context =>
                context.Execute("INSERT INTO settings (key, value, updated_at) VALUES ('kept', '1', '2026-10-12T13:59:43.123Z');"));
            Assert.NotNull(await new BackupService(database).RunDailyBackupAsync());
        }

        File.WriteAllBytes(path, Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 % 251)).ToArray());
        clock.Advance(TimeSpan.FromHours(1));

        using var recovered = LocalDatabase.Open(path, backups, clock);

        Assert.Equal(StartupRecoveryKind.RestoredFromBackup, recovered.Recovery.Kind);
        Assert.True(recovered.Recovery.Recovered);
        Assert.Equal(Path.Combine(backups, "nimpad-20261012.db"), recovered.Recovery.RestoredBackupPath);
        Assert.False(string.IsNullOrEmpty(recovered.Recovery.Problem));
        var corrupt = recovered.Recovery.CorruptDatabasePath!;
        Assert.Equal("nimpad.corrupt-20261012T145943123Z.db", Path.GetFileName(corrupt));
        Assert.True(File.Exists(corrupt));
        Assert.Equal(8192, new FileInfo(corrupt).Length);
        var kept = await recovered.Readers.ExecuteAsync(connection =>
            (string?)connection.ExecuteScalar("SELECT value FROM settings WHERE key = 'kept';"));
        Assert.Equal("1", kept);
        Assert.Equal(LocalDatabase.LatestSchemaVersion, recovered.SchemaVersion);
    }

    [Fact]
    public void GarbageDatabaseWithoutBackupsIsReplacedByAFreshOne()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("nimpad.db");
        File.WriteAllText(path, "this is definitely not an SQLite database, just some text that is long enough");

        using var database = LocalDatabase.Open(path, temp.Combine("backups"));

        Assert.Equal(StartupRecoveryKind.CreatedFresh, database.Recovery.Kind);
        Assert.Null(database.Recovery.RestoredBackupPath);
        Assert.True(File.Exists(database.Recovery.CorruptDatabasePath));
        Assert.StartsWith("nimpad.corrupt-", Path.GetFileName(database.Recovery.CorruptDatabasePath));
        Assert.Equal(LocalDatabase.LatestSchemaVersion, database.SchemaVersion);
    }

    [Fact]
    public async Task DamagedPagesAreDetectedAndADamagedNewestBackupIsSkipped()
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(TestStore.Start);
        var path = temp.Combine("nimpad.db");
        var backups = temp.Combine("backups");
        using (var database = LocalDatabase.Open(path, backups, clock))
        {
            await database.Writer.ExecuteAsync(context =>
            {
                for (var i = 0; i < 2000; i++)
                {
                    context.Execute(
                        "INSERT INTO settings (key, value, updated_at) VALUES ($key, '\"some longer value to fill pages\"', 'x');",
                        ("$key", $"key-{i:D5}"));
                }
            });
            var backupService = new BackupService(database);
            var older = await backupService.RunDailyBackupAsync();
            clock.Advance(TimeSpan.FromDays(1));
            var newer = await backupService.RunDailyBackupAsync();
            Assert.NotNull(older);
            Assert.NotNull(newer);
            await backupService.CheckpointAndOptimizeAsync();
        }

        // Damage the newest backup completely and the middle of the live database (the header stays intact).
        File.WriteAllBytes(Path.Combine(backups, "nimpad-20261013.db"), new byte[4096]);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            Assert.True(stream.Length > 16 * 4096);
            stream.Position = 4096;
            stream.Write(Enumerable.Repeat((byte)0xA5, 12 * 4096).ToArray());
        }

        using var recovered = LocalDatabase.Open(path, backups, clock);

        Assert.Equal(StartupRecoveryKind.RestoredFromBackup, recovered.Recovery.Kind);
        Assert.Equal(Path.Combine(backups, "nimpad-20261012.db"), recovered.Recovery.RestoredBackupPath);
        var count = await recovered.Readers.ExecuteAsync(connection => (long)connection.ExecuteScalar("SELECT count(*) FROM settings;")!);
        Assert.Equal(2000, count);
    }

    [Fact]
    public async Task ADamagedDatabaseWithAPendingLogIsRestoredWithoutReplayingTheLog()
    {
        using var crashed = new TempDirectory();
        var clock = new FakeTimeProvider(TestStore.Start);
        await CrashWithAPendingLogAsync(crashed, clock);

        using var recovered = LocalDatabase.Open(crashed.Combine("nimpad.db"), crashed.Combine("backups"), clock);

        // The check folds the log into the damaged file (moved aside), so nothing of it reaches the restored backup.
        Assert.Equal(StartupRecoveryKind.RestoredFromBackup, recovered.Recovery.Kind);
        Assert.True(File.Exists(recovered.Recovery.CorruptDatabasePath));
        await AssertRestoredFromBackupAsync(recovered);
    }

    [Fact]
    public async Task ALogThatOutlivedTheCheckMovesAsideWithTheDamagedDatabase()
    {
        using var crashed = new TempDirectory();
        var clock = new FakeTimeProvider(TestStore.Start);
        await CrashWithAPendingLogAsync(crashed, clock);
        var path = crashed.Combine("nimpad.db");
        var log = await File.ReadAllBytesAsync(path + "-wal");
        await File.WriteAllBytesAsync(path + "-shm", new byte[32 * 1024]);

        // As if the check had not consumed the log (for example it could not take the lock to checkpoint on close).
        var recovery = DatabaseIntegrity.Recover(path, crashed.Combine("backups"), "damaged", clock);

        Assert.Equal(StartupRecoveryKind.RestoredFromBackup, recovery.Kind);
        Assert.Equal(log, await File.ReadAllBytesAsync(recovery.CorruptDatabasePath + "-wal"));
        Assert.True(File.Exists(recovery.CorruptDatabasePath + "-shm"));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        using var reopened = LocalDatabase.Open(path, crashed.Combine("backups"), clock);
        Assert.Equal(StartupRecoveryKind.None, reopened.Recovery.Kind);
        await AssertRestoredFromBackupAsync(reopened);
    }

    // A database with a backup, then a change that is only in its -wal file, copied as a crash would leave it (the
    // database still open), with the main file's header damaged. The change does not alter the database size, so the
    // log holds that row's page and not page 1: the header damage is real.
    private static async Task CrashWithAPendingLogAsync(TempDirectory crashed, TimeProvider clock)
    {
        using var live = new TempDirectory();
        using (var database = LocalDatabase.Open(live.Combine("nimpad.db"), live.Combine("backups"), clock))
        {
            await database.Writer.ExecuteAsync(context =>
                context.Execute("INSERT INTO settings (key, value, updated_at) VALUES ('kept', '1', 'x');"));
            var backups = new BackupService(database);
            Assert.NotNull(await backups.RunDailyBackupAsync());
            Assert.True(await backups.CheckpointAndOptimizeAsync());
            await database.Writer.ExecuteAsync(context => context.Execute("UPDATE settings SET value = '2' WHERE key = 'kept';"));

            Assert.True(new FileInfo(live.Combine("nimpad.db-wal")).Length > 0);
            File.Copy(live.Combine("nimpad.db"), crashed.Combine("nimpad.db"));
            File.Copy(live.Combine("nimpad.db-wal"), crashed.Combine("nimpad.db-wal"));
            Directory.CreateDirectory(crashed.Combine("backups"));
            foreach (var backup in Directory.GetFiles(live.Combine("backups")))
            {
                File.Copy(backup, crashed.Combine("backups", Path.GetFileName(backup)));
            }
        }

        using var stream = new FileStream(crashed.Combine("nimpad.db"), FileMode.Open, FileAccess.Write);
        stream.Write(Enumerable.Repeat((byte)0x5A, 100).ToArray());
    }

    private static async Task AssertRestoredFromBackupAsync(LocalDatabase database)
    {
        var (check, value) = await database.Readers.ExecuteAsync(connection => (
            (string?)connection.ExecuteScalar("PRAGMA quick_check;"),
            (string?)connection.ExecuteScalar("SELECT value FROM settings WHERE key = 'kept';")));
        Assert.Equal("ok", check);
        Assert.Equal("1", value);
    }

    [Fact]
    public async Task ALockedDatabaseFailsToOpenAndIsLeftAlone()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("nimpad.db");
        var backups = temp.Combine("backups");
        using (var database = LocalDatabase.Open(path, backups))
        {
            await database.Writer.ExecuteAsync(context =>
                context.Execute("INSERT INTO settings (key, value, updated_at) VALUES ('kept', '1', 'x');"));
            Assert.NotNull(await new BackupService(database).RunDailyBackupAsync());
        }

        var before = await File.ReadAllBytesAsync(path);
        // Another process holds the database exclusively (in WAL mode, only exclusive locking mode blocks readers).
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            holder.Open();
            holder.ExecuteNonQuery("PRAGMA locking_mode = EXCLUSIVE; BEGIN EXCLUSIVE; COMMIT;");
            var filesBefore = Directory.GetFiles(temp.Path).Order().ToList();

            var error = Assert.Throws<SqliteException>(() => LocalDatabase.Open(path, backups, null, TimeSpan.FromSeconds(1)));

            Assert.Equal(5, error.SqliteErrorCode & 0xFF); // SQLITE_BUSY: locked, not damaged
            Assert.Equal(filesBefore, Directory.GetFiles(temp.Path).Order().ToList());
            Assert.Empty(Directory.GetFiles(temp.Path, "nimpad.corrupt-*"));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        using var reopened = LocalDatabase.Open(path, backups);
        Assert.Equal(StartupRecoveryKind.None, reopened.Recovery.Kind);
        Assert.Equal("1", await reopened.Readers.ExecuteAsync(connection => (string?)connection.ExecuteScalar("SELECT value FROM settings WHERE key = 'kept';")));
    }

    [Fact]
    public void DisposeReleasesTheFiles()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("nimpad.db");
        var database = LocalDatabase.Open(path, temp.Combine("backups"));
        using (var reader = database.Readers.Open())
        {
            reader.ExecuteScalar("SELECT count(*) FROM nodes;");
        }

        database.Dispose();
        database.Dispose();

        // A clean close checkpoints the WAL and removes the -wal/-shm files; the file can then be moved.
        Assert.False(File.Exists(path + "-wal"));
        File.Move(path, path + ".moved");
        Assert.True(File.Exists(path + ".moved"));
    }
}
