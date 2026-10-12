using NimPad.Core.Files;
using NimPad.Core.Ids;
using NimPad.Core.Storage;
using NimPad.Core.Sync;

namespace NimPad.Core.Services;

/// <summary>
/// The Core services wired over one <see cref="LocalDatabase"/> with a shared clock, ID generator and change
/// notifier. Composition roots (the App, tests) create one; it does not own the database.
/// </summary>
public sealed class CoreServices : IDisposable
{
    private CoreServices(LocalDatabase database, TimeProvider timeProvider, IIdGenerator ids)
    {
        Database = database;
        TimeProvider = timeProvider;
        Ids = ids;
        Notifier = new WorkspaceChangeNotifier();
        PendingOperations = new PendingOperationQueue(database, timeProvider, ids);
        Workspaces = new WorkspaceService(database, timeProvider, ids);
        Nodes = new NodeService(database, PendingOperations, Notifier, timeProvider, ids);
        Documents = new DocumentService(database, PendingOperations, Notifier, timeProvider);
        Autosave = new AutosaveScheduler(Documents, timeProvider);
        Search = new SearchService(database);
        SearchIndexer = new SearchIndexer(database, Notifier, timeProvider);
        Sessions = new SessionService(database, timeProvider);
        Settings = new SettingsService(database, timeProvider);
        ExternalFiles = new ExternalFileService(database, Nodes, Documents, Notifier, timeProvider);
        Backups = new BackupService(database, timeProvider);
    }

    /// <summary>The database the services use.</summary>
    public LocalDatabase Database { get; }

    /// <summary>The shared clock.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>The shared ID generator.</summary>
    public IIdGenerator Ids { get; }

    /// <summary>Reports committed workspace changes.</summary>
    public WorkspaceChangeNotifier Notifier { get; }

    /// <summary>The pending-operation queue (read API).</summary>
    public PendingOperationQueue PendingOperations { get; }

    /// <summary>Workspaces and device identity.</summary>
    public WorkspaceService Workspaces { get; }

    /// <summary>The workspace tree.</summary>
    public NodeService Nodes { get; }

    /// <summary>Document text, formats and links.</summary>
    public DocumentService Documents { get; }

    /// <summary>Debounced saving of open documents.</summary>
    public AutosaveScheduler Autosave { get; }

    /// <summary>Full-text search.</summary>
    public SearchService Search { get; }

    /// <summary>Background indexing of large documents (started by <see cref="Create"/>).</summary>
    public SearchIndexer SearchIndexer { get; }

    /// <summary>Tabs, the reopen stack and the window layout of this device.</summary>
    public SessionService Sessions { get; }

    /// <summary>User settings.</summary>
    public SettingsService Settings { get; }

    /// <summary>External files: open, save, recovery buffers, import and export.</summary>
    public ExternalFileService ExternalFiles { get; }

    /// <summary>Daily backups and shutdown maintenance.</summary>
    public BackupService Backups { get; }

    /// <summary>Creates the services and schedules the search index catch-up for documents flagged earlier.</summary>
    /// <param name="database">The opened database.</param>
    /// <param name="timeProvider">The clock; the database's clock by default.</param>
    /// <param name="ids">The ID generator; UUIDv7 from <paramref name="timeProvider"/> by default.</param>
    public static CoreServices Create(LocalDatabase database, TimeProvider? timeProvider = null, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        var clock = timeProvider ?? database.TimeProvider;
        var services = new CoreServices(database, clock, ids ?? new UuidV7Generator(clock));
        services.SearchIndexer.Start();
        return services;
    }

    /// <summary>
    /// Stops the autosave timers (without saving: call <see cref="AutosaveScheduler.FlushAllAsync"/> first), the
    /// background indexer and the change notifier (after delivering queued events). The database stays open.
    /// </summary>
    public void Dispose()
    {
        Autosave.Dispose();
        SearchIndexer.Dispose();
        Notifier.Dispose();
    }
}
