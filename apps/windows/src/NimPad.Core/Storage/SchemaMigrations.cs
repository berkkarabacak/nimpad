using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace NimPad.Core.Storage;

/// <summary>
/// The local schema migrations: SQL files embedded from <c>Storage/Migrations</c>, named <c>NNNN_description.sql</c>
/// where NNNN is the schema version the file produces. <c>PRAGMA user_version</c> records the applied version; each
/// pending migration runs in its own transaction together with the version bump, so re-running is a no-op.
/// </summary>
internal static class SchemaMigrations
{
    private const string ResourcePrefix = "NimPad.Core.Migrations.";

    private static readonly Lazy<IReadOnlyList<Migration>> LazyAll = new(Load);

    /// <summary>Every embedded migration in version order.</summary>
    public static IReadOnlyList<Migration> All => LazyAll.Value;

    /// <summary>The version the newest migration produces.</summary>
    public static int LatestVersion => All.Count == 0 ? 0 : All[^1].Version;

    /// <summary>Reads <c>PRAGMA user_version</c>.</summary>
    public static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Applies every migration newer than the database's version.</summary>
    /// <returns>The resulting schema version.</returns>
    /// <exception cref="SchemaVersionException">The database was written by a newer NimPad.</exception>
    public static int Apply(SqliteConnection connection)
    {
        var current = GetVersion(connection);
        if (current > LatestVersion)
        {
            throw new SchemaVersionException(current, LatestVersion);
        }

        foreach (var migration in All)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // PRAGMA arguments cannot be bound; the version is an integer we control.
                command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version};");
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            current = migration.Version;
        }

        return current;
    }

    private static IReadOnlyList<Migration> Load()
    {
        var assembly = typeof(SchemaMigrations).Assembly;
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resource[ResourcePrefix.Length..];
            var underscore = fileName.IndexOf('_', StringComparison.Ordinal);
            if (underscore <= 0
                || !int.TryParse(fileName.AsSpan(0, underscore), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                || version <= 0)
            {
                throw new InvalidOperationException($"Migration resource '{resource}' must be named NNNN_description.sql.");
            }

            migrations.Add(new Migration(version, fileName, ReadResource(assembly, resource)));
        }

        migrations.Sort((x, y) => x.Version.CompareTo(y.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException(
                    $"Migrations must be numbered 1, 2, 3, … without gaps; found {migrations[i].Name} at position {i + 1}.");
            }
        }

        return migrations;
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded migration '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>One embedded migration.</summary>
    /// <param name="Version">The schema version it produces.</param>
    /// <param name="Name">The file name.</param>
    /// <param name="Sql">The SQL script.</param>
    public sealed record Migration(int Version, string Name, string Sql);
}

/// <summary>The database's schema version is newer than this build understands (it was opened by a newer NimPad).</summary>
public sealed class SchemaVersionException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public SchemaVersionException(int databaseVersion, int supportedVersion)
        : base($"The NimPad database uses schema version {databaseVersion}, but this version of NimPad supports up to {supportedVersion}. Update NimPad to open it.")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>The schema version found in the database.</summary>
    public int DatabaseVersion { get; }

    /// <summary>The newest schema version this build can migrate to.</summary>
    public int SupportedVersion { get; }
}
