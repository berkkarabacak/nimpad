using Microsoft.Data.Sqlite;

namespace NimPad.Core.Storage;

/// <summary>
/// The state of one write job on the <see cref="SqliteWriter"/> thread: the write connection and the open
/// <c>BEGIN IMMEDIATE</c> transaction. Only valid for the duration of the job and only on the writer thread.
/// </summary>
public sealed class WriteContext
{
    private readonly List<Action> _commitCallbacks = [];

    internal WriteContext(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    /// <summary>The single write connection.</summary>
    public SqliteConnection Connection { get; }

    /// <summary>The job's transaction. Commands must be enlisted in it (see <see cref="CreateCommand"/>).</summary>
    public SqliteTransaction Transaction { get; }

    internal int CommitCallbackCount => _commitCallbacks.Count;

    /// <summary>Creates a command enlisted in the job's transaction.</summary>
    public SqliteCommand CreateCommand(string sql)
    {
        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Runs a statement and returns the number of rows changed.</summary>
    /// <param name="sql">The SQL text with <c>$name</c> parameters.</param>
    /// <param name="parameters">Parameter names (with the <c>$</c>) and values; <see langword="null"/> binds SQL NULL.</param>
    public int Execute(string sql, params ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        using var command = CreateCommand(sql);
        command.AddParameters(parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a query and returns the first column of the first row, or <see langword="null"/> when there are no rows or it is NULL.</summary>
    public object? Scalar(string sql, params ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        using var command = CreateCommand(sql);
        command.AddParameters(parameters);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>
    /// Registers <paramref name="callback"/> to run on the writer thread right after the job's transaction commits.
    /// It never runs when the job (or the nested job that registered it) rolls back. Keep callbacks short: they
    /// delay the next write. Exceptions they throw are reported to <see cref="SqliteWriter.CallbackFailed"/>.
    /// </summary>
    public void OnCommitted(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _commitCallbacks.Add(callback);
    }

    internal void TruncateCommitCallbacks(int count) =>
        _commitCallbacks.RemoveRange(count, _commitCallbacks.Count - count);

    internal IReadOnlyList<Action> TakeCommitCallbacks()
    {
        var callbacks = _commitCallbacks.ToArray();
        _commitCallbacks.Clear();
        return callbacks;
    }
}
