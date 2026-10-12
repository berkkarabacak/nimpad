using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Storage;
using NimPad.Core.Time;

namespace NimPad.Core.Services;

/// <summary>Workspaces and the installation's device identity.</summary>
public sealed class WorkspaceService
{
    /// <summary>The name of the workspace created on first run.</summary>
    public const string DefaultWorkspaceName = "My Workspace";

    private readonly LocalDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly IIdGenerator _ids;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The local database.</param>
    /// <param name="timeProvider">The clock; <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="ids">The ID generator; UUIDv7 by default.</param>
    public WorkspaceService(LocalDatabase database, TimeProvider? timeProvider = null, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ids = ids ?? new UuidV7Generator(_timeProvider);
    }

    /// <summary>
    /// On first run, creates the device row and the "My Workspace" workspace; afterwards returns the existing
    /// (oldest) workspace. Safe to call on every start.
    /// </summary>
    public Task<Workspace> EnsureDefaultWorkspaceAsync(CancellationToken cancellationToken = default) =>
        _database.Writer.ExecuteAsync(
            context =>
            {
                DeviceIdentity.GetOrCreate(context, _timeProvider, _ids);
                using (var command = context.CreateCommand(
                    "SELECT id, name, created_at, updated_at FROM workspaces ORDER BY created_at, id LIMIT 1;"))
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return ReadWorkspace(reader);
                    }
                }

                var now = Timestamps.Now(_timeProvider);
                var workspace = new Workspace(_ids.NewWorkspaceId(), DefaultWorkspaceName, now, now);
                context.Execute(
                    "INSERT INTO workspaces (id, name, created_at, updated_at) VALUES ($id, $name, $now, $now);",
                    ("$id", workspace.Id),
                    ("$name", workspace.Name),
                    ("$now", now));
                return workspace;
            },
            cancellationToken);

    /// <summary>All workspaces, oldest first.</summary>
    public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync<IReadOnlyList<Workspace>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, name, created_at, updated_at FROM workspaces ORDER BY created_at, id;";
                using var reader = command.ExecuteReader();
                var workspaces = new List<Workspace>();
                while (reader.Read())
                {
                    workspaces.Add(ReadWorkspace(reader));
                }

                return workspaces;
            },
            cancellationToken);

    /// <summary>This installation's device ID, or <see langword="null"/> before the first run completed.</summary>
    public Task<Guid?> GetDeviceIdAsync(CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync(
            connection => connection.ExecuteScalar("SELECT device_id FROM device ORDER BY created_at, device_id LIMIT 1;") is string id
                ? Uuid.Parse(id, "device_id")
                : (Guid?)null,
            cancellationToken);

    private static Workspace ReadWorkspace(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        WorkspaceId.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetTimestamp(2),
        reader.GetTimestamp(3));
}
