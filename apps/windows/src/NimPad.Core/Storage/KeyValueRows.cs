using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NimPad.Core.Time;

namespace NimPad.Core.Storage;

/// <summary>
/// The <c>key → JSON value</c> tables (<c>settings</c>, <c>ui_state</c>). Each setting is its own row, so a save
/// writes only the keys it knows and rows written by a newer NimPad survive untouched.
/// </summary>
internal static class KeyValueRows
{
    /// <summary>The tables that use this layout.</summary>
    public const string Settings = "settings";

    /// <inheritdoc cref="Settings"/>
    public const string UiState = "ui_state";

    /// <summary>Reads every row of <paramref name="table"/>.</summary>
    public static Dictionary<string, JsonElement> ReadAll(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT key, value FROM {CheckTable(table)};";
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        while (reader.Read())
        {
            try
            {
                using var document = JsonDocument.Parse(reader.GetString(1));
                values[reader.GetString(0)] = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                // The column has a json_valid check, so this means a damaged row; the default is used instead.
                Trace.TraceWarning($"Ignoring the unreadable {table} value '{reader.GetString(0)}': {ex.Message}");
            }
        }

        return values;
    }

    /// <summary>Inserts or replaces one row.</summary>
    public static void Upsert(WriteContext context, string table, string key, string jsonValue, DateTimeOffset now) =>
        context.Execute(
            $"""
            INSERT INTO {CheckTable(table)} (key, value, updated_at) VALUES ($key, $value, $now)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            WHERE value IS NOT excluded.value;
            """,
            ("$key", key),
            ("$value", jsonValue),
            ("$now", Timestamps.ToText(now)));

    /// <summary>Reads a value of type <typeparamref name="T"/>; <see langword="false"/> when missing or of another shape.</summary>
    public static bool TryGet<T>(IReadOnlyDictionary<string, JsonElement> values, string key, out T value)
    {
        value = default!;
        if (!values.TryGetValue(key, out var element))
        {
            return false;
        }

        try
        {
            var parsed = element.Deserialize<T>();
            if (parsed is null)
            {
                return false;
            }

            value = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            Trace.TraceWarning($"Ignoring the value of '{key}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Serializes a value as JSON.</summary>
    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value);

    private static string CheckTable(string table) =>
        table is Settings or UiState ? table : throw new ArgumentOutOfRangeException(nameof(table), table, "Not a key/value table.");
}
