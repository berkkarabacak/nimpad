using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NimPad.Core.Storage;

/// <summary>
/// Daily backups and shutdown maintenance. Backups are <c>VACUUM INTO</c> copies named <c>nimpad-YYYYMMDD.db</c>
/// (UTC date) in <see cref="LocalDatabase.BackupsDirectory"/>; the newest <see cref="KeepCount"/> are kept. Both
/// operations run on the writer thread, between write transactions.
/// </summary>
public sealed partial class BackupService
{
    /// <summary>How many daily backups are kept by default.</summary>
    public const int DefaultKeepCount = 7;

    private readonly LocalDatabase _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The database to back up.</param>
    /// <param name="timeProvider">The clock that decides "today"; the database's clock by default.</param>
    /// <param name="keepCount">How many backups to keep (at least 1).</param>
    public BackupService(LocalDatabase database, TimeProvider? timeProvider = null, int keepCount = DefaultKeepCount)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepCount, 1);
        _database = database;
        _timeProvider = timeProvider ?? database.TimeProvider;
        KeepCount = keepCount;
    }

    /// <summary>How many backups are kept.</summary>
    public int KeepCount { get; }

    /// <summary>The backup file name for a date: <c>nimpad-YYYYMMDD.db</c>.</summary>
    public static string FileNameFor(DateOnly date) =>
        "nimpad-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".db";

    /// <summary>The backups in <paramref name="backupsDirectory"/>, newest first. Other files are ignored.</summary>
    public static IReadOnlyList<string> ListBackups(string backupsDirectory)
    {
        if (!Directory.Exists(backupsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(backupsDirectory, "nimpad-*.db")
            .Where(path => BackupFileName().IsMatch(Path.GetFileName(path)))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Creates today's backup unless one exists, then deletes all but the newest <see cref="KeepCount"/> backups.
    /// </summary>
    /// <returns>The new backup's path, or <see langword="null"/> when today's backup already existed.</returns>
    public Task<string?> RunDailyBackupAsync(CancellationToken cancellationToken = default)
    {
        var directory = _database.BackupsDirectory;
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var target = Path.Combine(directory, FileNameFor(today));
        return _database.Writer.ExecuteWithoutTransactionAsync<string?>(
            connection =>
            {
                string? created = null;
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(directory);
                    // Write to a temporary name first so a crash never leaves a partial file that looks like a backup.
                    var temporary = target + ".tmp";
                    File.Delete(temporary);
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "VACUUM INTO $path;";
                        command.Parameters.AddWithValue("$path", temporary);
                        command.ExecuteNonQuery();
                    }

                    File.Move(temporary, target);
                    created = target;
                }

                foreach (var old in ListBackups(directory).Skip(KeepCount))
                {
                    File.Delete(old);
                }

                return created;
            },
            cancellationToken);
    }

    /// <summary>
    /// Shutdown maintenance: <c>PRAGMA optimize</c>, then <c>PRAGMA wal_checkpoint(TRUNCATE)</c>, which copies the
    /// WAL into the database and truncates it to zero bytes.
    /// </summary>
    /// <returns>True when the checkpoint completed (no reader blocked it).</returns>
    public Task<bool> CheckpointAndOptimizeAsync(CancellationToken cancellationToken = default) =>
        _database.Writer.ExecuteWithoutTransactionAsync(
            connection =>
            {
                connection.ExecuteNonQuery("PRAGMA optimize;");
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using SqliteDataReader reader = command.ExecuteReader();
                // Columns: busy (0 = completed), log frames, checkpointed frames.
                return reader.Read() && reader.GetInt64(0) == 0;
            },
            cancellationToken);

    [GeneratedRegex(@"^nimpad-\d{8}\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupFileName();
}
