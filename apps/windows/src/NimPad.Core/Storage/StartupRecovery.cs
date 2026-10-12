namespace NimPad.Core.Storage;

/// <summary>What the startup integrity check had to do.</summary>
public enum StartupRecoveryKind
{
    /// <summary>The database was healthy (or did not exist yet); nothing was changed.</summary>
    None,

    /// <summary>The database was damaged; it was moved aside and the newest healthy backup was restored.</summary>
    RestoredFromBackup,

    /// <summary>The database was damaged and no healthy backup existed; it was moved aside and a new, empty one created.</summary>
    CreatedFresh,
}

/// <summary>
/// The result of the startup integrity check (<c>PRAGMA quick_check</c>), for the banner that explains a recovery.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Problem">Why the database was considered damaged (SQLite's message), when it was.</param>
/// <param name="CorruptDatabasePath">Where the damaged database was moved, when it was.</param>
/// <param name="RestoredBackupPath">The backup that was restored, for <see cref="StartupRecoveryKind.RestoredFromBackup"/>.</param>
public sealed record StartupRecovery(
    StartupRecoveryKind Kind,
    string? Problem,
    string? CorruptDatabasePath,
    string? RestoredBackupPath)
{
    /// <summary>The database passed the check; no recovery happened.</summary>
    public static StartupRecovery Healthy { get; } = new(StartupRecoveryKind.None, null, null, null);

    /// <summary>True when the damaged database was replaced (restored or recreated).</summary>
    public bool Recovered => Kind != StartupRecoveryKind.None;
}
