using Microsoft.Data.Sqlite;

namespace NimPad.Core.Tests.Support;

/// <summary>A unique directory under the system temp folder, deleted (with any SQLite pools cleared) on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nimpad-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        // Pooled read connections keep file handles open; close them before deleting.
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }
}
