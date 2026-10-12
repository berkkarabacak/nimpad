using Microsoft.Data.Sqlite;
using NimPad.Core.Ids;
using NimPad.Core.Time;

namespace NimPad.Core.Storage;

/// <summary>
/// Binding and reading helpers. IDs are always bound as lowercase text and timestamps as ISO-8601 text
/// (architecture.md: never bind a <see cref="Guid"/> parameter).
/// </summary>
internal static class SqliteExtensions
{
    /// <summary>Adds parameters, converting IDs, timestamps, booleans and enums to their storage form.</summary>
    public static void AddParameters(this SqliteCommand command, ReadOnlySpan<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, ToDbValue(value));
        }
    }

    /// <summary>Adds one parameter, converted as in <see cref="AddParameters"/>.</summary>
    public static SqliteCommand With(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, ToDbValue(value));
        return command;
    }

    /// <summary>Converts a CLR value to the value stored in SQLite.</summary>
    public static object ToDbValue(object? value) => value switch
    {
        null => DBNull.Value,
        NodeId id => id.ToString(),
        WorkspaceId id => id.ToString(),
        TrashBatchId id => id.ToString(),
        Guid guid => Uuid.Format(guid),
        DateTimeOffset instant => Timestamps.ToText(instant),
        bool flag => flag ? 1L : 0L,
        // Enums are stored by wire name (ToWireName); binding the numeric value would silently corrupt rows.
        Enum => throw new ArgumentException($"Bind {value.GetType().Name} values by their wire name.", nameof(value)),
        _ => value,
    };

    public static string? GetStringOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? GetInt64OrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static DateTimeOffset GetTimestamp(this SqliteDataReader reader, int ordinal) =>
        Timestamps.Parse(reader.GetString(ordinal));

    public static DateTimeOffset? GetTimestampOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Timestamps.Parse(reader.GetString(ordinal));

    public static NodeId GetNodeId(this SqliteDataReader reader, int ordinal) => NodeId.Parse(reader.GetString(ordinal));

    public static NodeId? GetNodeIdOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : NodeId.Parse(reader.GetString(ordinal));

    /// <summary>Runs a statement on a connection without a transaction (pragmas, maintenance).</summary>
    public static int ExecuteNonQuery(this SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a query on a connection without a transaction and returns the first column of the first row.</summary>
    public static object? ExecuteScalar(this SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>The primary SQLite result code of an exception (extended codes keep it in the low byte).</summary>
    public static int PrimaryErrorCode(this SqliteException exception) => exception.SqliteErrorCode & 0xFF;
}
