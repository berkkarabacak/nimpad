using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using NimPad.Core.Domain;
using NimPad.Core.Files;
using NimPad.Core.Services;
using NimPad.Core.Storage;
using NimPad.Core.Sync;

namespace NimPad.Core.Tests.Support;

/// <summary>
/// A database in a temp directory with the Core services, a fake clock starting at <see cref="Start"/> and the
/// default workspace created.
/// </summary>
internal sealed class TestStore : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 12, 13, 59, 43, 123, TimeSpan.Zero);

    private readonly bool _ownsDirectory;

    private TestStore(TempDirectory directory, bool ownsDirectory, FakeTimeProvider clock, LocalDatabase database, CoreServices services, Workspace workspace)
    {
        Directory = directory;
        _ownsDirectory = ownsDirectory;
        Clock = clock;
        Database = database;
        Services = services;
        Workspace = workspace;
    }

    public TempDirectory Directory { get; }

    public FakeTimeProvider Clock { get; }

    public LocalDatabase Database { get; }

    public CoreServices Services { get; }

    public Workspace Workspace { get; }

    public NodeService Nodes => Services.Nodes;

    public PendingOperationQueue Queue => Services.PendingOperations;

    public DocumentService Documents => Services.Documents;

    public SearchService Search => Services.Search;

    public SessionService Sessions => Services.Sessions;

    public SettingsService Settings => Services.Settings;

    public ExternalFileService Files => Services.ExternalFiles;

    public string DatabasePath => Directory.Combine("profiles", "local", "nimpad.db");

    public string BackupsDir => Directory.Combine("profiles", "local", "backups");

    public static async Task<TestStore> CreateAsync(TempDirectory? directory = null, FakeTimeProvider? clock = null)
    {
        var ownsDirectory = directory is null;
        directory ??= new TempDirectory();
        clock ??= new FakeTimeProvider(Start);
        var database = LocalDatabase.Open(
            directory.Combine("profiles", "local", "nimpad.db"),
            directory.Combine("profiles", "local", "backups"),
            clock);
        var services = CoreServices.Create(database, clock);
        var workspace = await services.Workspaces.EnsureDefaultWorkspaceAsync();
        return new TestStore(directory, ownsDirectory, clock, database, services, workspace);
    }

    /// <summary>Runs a query on a read connection and maps every row.</summary>
    public Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters) =>
        Database.Readers.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read())
            {
                rows.Add(map(reader));
            }

            return rows;
        });

    /// <summary>Runs a scalar query on a read connection.</summary>
    public async Task<long> CountAsync(string sql, params (string Name, object? Value)[] parameters) =>
        (await QueryAsync(sql, reader => reader.GetInt64(0), parameters)).Single();

    /// <summary>Runs raw SQL on the writer, bypassing the services (to simulate server state).</summary>
    public Task ExecuteRawAsync(string sql, params (string Name, object? Value)[] parameters) =>
        Database.Writer.ExecuteAsync(context => context.Execute(sql, parameters));

    public void Dispose()
    {
        Services.Dispose();
        Database.Dispose();
        if (_ownsDirectory)
        {
            Directory.Dispose();
        }
    }
}
