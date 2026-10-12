using Microsoft.Data.Sqlite;

namespace NimPad.Core.Storage;

/// <summary>
/// Pooled read connections (architecture.md, "Readers"). Each connection is opened with <c>PRAGMA query_only</c>, so
/// an accidental write fails instead of bypassing the <see cref="SqliteWriter"/>. WAL mode lets readers run while
/// the writer commits; a reader sees everything committed before it started its read transaction.
/// </summary>
public sealed class ReadConnections
{
    private const string OpenPragmas = "PRAGMA query_only = ON; PRAGMA busy_timeout = 5000;";

    private readonly string _connectionString;

    internal ReadConnections(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Opens a pooled, query-only connection. Dispose it to return it to the pool.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            connection.ExecuteNonQuery(OpenPragmas);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Runs <paramref name="query"/> on a pooled read connection on the thread pool.</summary>
    /// <param name="query">The read; it must not keep the connection or open readers after returning.</param>
    /// <param name="cancellationToken">Cancels the query if it has not started yet.</param>
    public Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var connection = Open();
                return query(connection);
            },
            cancellationToken);
    }

    /// <summary>Closes the idle pooled connections of this database.</summary>
    internal void ClearPool()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
