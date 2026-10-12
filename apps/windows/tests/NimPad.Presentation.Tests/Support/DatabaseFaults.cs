using Microsoft.Data.Sqlite;
using NimPad.Core.Ids;

namespace NimPad.Presentation.Tests.Support;

/// <summary>
/// Real storage failures for the failure paths of the view models: a trigger in the test database makes Core's own
/// writer fail (as a full disk or a locked file would), with the real services in between and no mock.
/// </summary>
internal static class DatabaseFaults
{
    /// <summary>Makes every save of <paramref name="nodeId"/>'s text fail until the result is disposed.</summary>
    public static IDisposable FailSavesOf(ShellHarness harness, NodeId nodeId)
    {
        var name = "test_fail_save_" + nodeId.ToString().Replace("-", string.Empty, StringComparison.Ordinal);
        Execute(
            harness,
            $"""
            CREATE TRIGGER {name} BEFORE UPDATE OF text ON document_contents
            WHEN NEW.node_id = '{nodeId}'
            BEGIN SELECT RAISE(ABORT, 'disk I/O error (test)'); END;
            """);
        return new Undo(() => Execute(harness, $"DROP TRIGGER IF EXISTS {name};"));
    }

    /// <summary>Deletes a note behind the App's back (as another process would), without a change notification.</summary>
    public static void DeleteBehindTheAppsBack(ShellHarness harness, NodeId nodeId) =>
        Execute(harness, $"PRAGMA foreign_keys = ON; DELETE FROM nodes WHERE id = '{nodeId}';");

    private static void Execute(ShellHarness harness, string sql)
    {
        var path = harness.Shell.Paths!.DatabasePath;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; " + sql;
        command.ExecuteNonQuery();
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose()
        {
            Interlocked.Exchange(ref _undo, null)?.Invoke();
        }
    }
}
