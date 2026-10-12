using Microsoft.Data.Sqlite;
using NimPad.Core.Storage;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Storage;

public class BackupServiceTests
{
    [Fact]
    public async Task CreatesOneBackupPerDayContainingTheData()
    {
        using var store = await TestStore.CreateAsync();
        await store.Nodes.CreateDocumentAsync(store.Workspace.Id, null, "kept.md", initialText: "# Kept");

        var first = await store.Services.Backups.RunDailyBackupAsync();
        var second = await store.Services.Backups.RunDailyBackupAsync();

        Assert.Equal(Path.Combine(store.BackupsDir, "nimpad-20261012.db"), first);
        Assert.Null(second);
        Assert.False(File.Exists(first + ".tmp"));
        using (var backup = new SqliteConnection($"Data Source={first};Mode=ReadOnly;Pooling=False"))
        {
            backup.Open();
            Assert.Equal(1L, backup.ExecuteScalar("SELECT count(*) FROM nodes WHERE name = 'kept.md';"));
            Assert.Equal("# Kept", backup.ExecuteScalar("SELECT text FROM document_contents;"));
            Assert.Equal("ok", backup.ExecuteScalar("PRAGMA quick_check;"));
        }

        store.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(Path.Combine(store.BackupsDir, "nimpad-20261013.db"), await store.Services.Backups.RunDailyBackupAsync());
        Assert.Equal(2, BackupService.ListBackups(store.BackupsDir).Count);
    }

    [Fact]
    public async Task KeepsOnlyTheNewestSevenBackups()
    {
        using var store = await TestStore.CreateAsync();
        Directory.CreateDirectory(store.BackupsDir);
        for (var day = 1; day <= 9; day++)
        {
            File.WriteAllText(Path.Combine(store.BackupsDir, $"nimpad-202610{day:D2}.db"), "old");
        }

        File.WriteAllText(Path.Combine(store.BackupsDir, "notes.txt"), "unrelated");

        await store.Services.Backups.RunDailyBackupAsync();

        var names = BackupService.ListBackups(store.BackupsDir).Select(Path.GetFileName).ToList();
        Assert.Equal(
            ["nimpad-20261012.db", "nimpad-20261009.db", "nimpad-20261008.db", "nimpad-20261007.db", "nimpad-20261006.db", "nimpad-20261005.db", "nimpad-20261004.db"],
            names);
        Assert.True(File.Exists(Path.Combine(store.BackupsDir, "notes.txt")));
    }

    [Fact]
    public async Task CheckpointTruncatesTheWal()
    {
        using var store = await TestStore.CreateAsync();
        for (var i = 0; i < 20; i++)
        {
            await store.Nodes.CreateFolderAsync(store.Workspace.Id, null, $"Folder {i}");
        }

        var wal = store.Database.DatabasePath + "-wal";
        Assert.True(new FileInfo(wal).Length > 0);

        Assert.True(await store.Services.Backups.CheckpointAndOptimizeAsync());

        Assert.Equal(0, new FileInfo(wal).Length);
        Assert.Equal(20, (await store.Nodes.GetAllNodesAsync(store.Workspace.Id)).Count);
    }
}
