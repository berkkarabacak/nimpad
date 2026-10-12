using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using NimPad.Core.Time;

namespace NimPad.Core.Storage;

/// <summary>The startup integrity check and the recovery from a damaged database file.</summary>
internal static class DatabaseIntegrity
{
    /// <summary>How long the startup check waits for a database another process keeps locked.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>
    /// Runs <c>PRAGMA quick_check</c> on the file at <paramref name="path"/>. Only genuine damage counts: a file that
    /// is locked (busy) is not damaged, and recovering from it would replace a healthy database with a backup.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="lockTimeout">How long to wait for a lock held by another connection (whole seconds, at least 1).</param>
    /// <returns><see langword="null"/> when healthy, otherwise a description of the damage.</returns>
    /// <exception cref="SqliteException">An error that does not indicate damage (for example the file is locked).</exception>
    public static string? Check(string path, TimeSpan? lockTimeout = null)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling((lockTimeout ?? DefaultLockTimeout).TotalSeconds));
            using var reader = command.ExecuteReader();
            var messages = new List<string>();
            while (reader.Read())
            {
                messages.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }

            return messages is ["ok"] ? null : "quick_check: " + string.Join("; ", messages.Take(10));
        }
        catch (SqliteException ex) when (ex.PrimaryErrorCode() is SqliteCorrupt or SqliteNotADatabase)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Moves the damaged database (with its <c>-wal</c> and <c>-shm</c> files) aside as
    /// <c>{name}.corrupt-{timestamp}{ext}</c>, then copies the newest healthy backup into its place. When no backup
    /// passes the check, nothing is put in place and the caller creates a fresh database.
    /// </summary>
    public static StartupRecovery Recover(string databasePath, string backupsDirectory, string problem, TimeProvider timeProvider)
    {
        // Pooled connections would keep the damaged file open (and block the move on Windows).
        SqliteConnection.ClearAllPools();

        var corruptPath = MoveAside(databasePath, Timestamps.Now(timeProvider));
        foreach (var backup in BackupService.ListBackups(backupsDirectory))
        {
            string? backupProblem;
            try
            {
                backupProblem = Check(backup);
            }
            catch (SqliteException ex)
            {
                backupProblem = ex.Message;
            }

            if (backupProblem is not null)
            {
                continue;
            }

            File.Copy(backup, databasePath, overwrite: false);
            return new StartupRecovery(StartupRecoveryKind.RestoredFromBackup, problem, corruptPath, backup);
        }

        return new StartupRecovery(StartupRecoveryKind.CreatedFresh, problem, corruptPath, null);
    }

    private static string MoveAside(string databasePath, DateTimeOffset now)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(databasePath);
        var extension = Path.GetExtension(databasePath);
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

        var target = Path.Combine(directory, $"{stem}.corrupt-{stamp}{extension}");
        for (var attempt = 2; File.Exists(target); attempt++)
        {
            target = Path.Combine(directory, new StringBuilder()
                .Append(stem).Append(".corrupt-").Append(stamp).Append('-')
                .Append(attempt.ToString(CultureInfo.InvariantCulture)).Append(extension).ToString());
        }

        File.Move(databasePath, target);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (File.Exists(databasePath + suffix))
            {
                File.Move(databasePath + suffix, target + suffix, overwrite: true);
            }
        }

        return target;
    }
}
