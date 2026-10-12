using Microsoft.Data.Sqlite;
using NimPad.Core.Domain;
using NimPad.Core.Files;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Storage;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Storage;

public class SchemaUpgradeTests
{
    [Fact]
    public async Task UpgradesAVersionOneDatabaseWithoutLosingData()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("profiles", "local", "nimpad.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var workspaceId = WorkspaceId.New();
        var nodeId = NodeId.New();
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            connection.ExecuteNonQuery(SchemaMigrations.All[0].Sql);
            connection.ExecuteNonQuery("PRAGMA user_version = 1;");
            connection.ExecuteNonQuery(
                $"""
                INSERT INTO workspaces VALUES ('{workspaceId}', 'My Workspace', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
                INSERT INTO nodes (id, workspace_id, kind, name, position, created_at, updated_at, content_updated_at)
                VALUES ('{nodeId}', '{workspaceId}', 'document', 'old.md', 'a0', '2026-01-01T00:00:00.000Z',
                        '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
                INSERT INTO document_contents (node_id, text, format, checksum, char_count, preview, updated_at)
                VALUES ('{nodeId}', 'kept', 'markdown', 'sha256:x', 4, 'kept', '2026-01-01T00:00:00.000Z');
                INSERT INTO session_tabs (tab_id, node_id, tab_order, opened_at, updated_at)
                VALUES ('{Guid.CreateVersion7()}', '{nodeId}', 0, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
                INSERT INTO external_file_buffers (path, text, encoding, line_ending, updated_at)
                VALUES ('C:\notes.txt', 'draft', 'utf-8', 'crlf', '2026-01-01T00:00:00.000Z');
                """);
        }

        using var store = await TestStore.CreateAsync(temp);

        Assert.Equal(LocalDatabase.LatestSchemaVersion, store.Database.SchemaVersion);
        var content = await store.Documents.GetContentAsync(nodeId);
        Assert.Equal("kept", content.Text);
        Assert.Equal(1, content.Version);
        var tab = Assert.Single(await store.Sessions.LoadTabsAsync());
        Assert.Equal(nodeId, tab.NodeId);
        Assert.Equal(string.Empty, tab.Title);
        var buffer = Assert.Single(await store.Files.GetRecoveryBuffersAsync());
        Assert.Equal(TextEncodingInfo.Utf8, buffer.Encoding);
        Assert.Equal("draft", buffer.Text);
        Assert.Equal(workspaceId, store.Workspace.Id);
    }
}
