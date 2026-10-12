namespace NimPad.Core.Storage;

/// <summary>How the data root was chosen.</summary>
public enum DataRootSource
{
    /// <summary>The <c>--data-dir</c> command-line override.</summary>
    Override,

    /// <summary>Portable mode: a <c>portable.txt</c> marker next to the executable; data lives in <c>.\data</c>.</summary>
    Portable,

    /// <summary>The per-user default: <c>%LOCALAPPDATA%\NimPad</c>.</summary>
    LocalAppData,
}

/// <summary>
/// The on-disk locations (architecture.md, "Storage locations"). Resolution is pure: the executable directory, the
/// local-app-data folder and the file-existence check are inputs, so it is testable on any OS.
/// </summary>
/// <param name="DataRoot">The root of all NimPad data.</param>
/// <param name="Source">How <paramref name="DataRoot"/> was chosen.</param>
public sealed record AppPaths(string DataRoot, DataRootSource Source)
{
    /// <summary>The marker file that switches to portable mode.</summary>
    public const string PortableMarkerFileName = "portable.txt";

    /// <summary>The database file name.</summary>
    public const string DatabaseFileName = "nimpad.db";

    /// <summary>The local (signed-out) profile. Phase 2 adds <c>profiles\{userId}</c>.</summary>
    public string ProfileDir => Path.Combine(DataRoot, "profiles", "local");

    /// <summary><c>{ProfileDir}\nimpad.db</c>.</summary>
    public string DatabasePath => Path.Combine(ProfileDir, DatabaseFileName);

    /// <summary><c>{ProfileDir}\backups</c>, holding <c>nimpad-YYYYMMDD.db</c>.</summary>
    public string BackupsDir => Path.Combine(ProfileDir, "backups");

    /// <summary><c>{DataRoot}\logs</c>, holding <c>nimpad-YYYYMMDD.log</c>.</summary>
    public string LogsDir => Path.Combine(DataRoot, "logs");

    /// <summary>True in portable mode.</summary>
    public bool IsPortable => Source == DataRootSource.Portable;

    /// <summary>
    /// Resolves the data root: <paramref name="dataDirOverride"/> when given (made absolute against the current
    /// directory); otherwise <c>{executableDirectory}\data</c> when a <c>portable.txt</c> marker exists next to the
    /// executable; otherwise <c>{localAppDataDirectory}\NimPad</c>.
    /// </summary>
    /// <param name="dataDirOverride">The parsed <c>--data-dir</c> value, or <see langword="null"/>/blank for none.</param>
    /// <param name="executableDirectory">The directory containing <c>NimPad.exe</c>.</param>
    /// <param name="localAppDataDirectory">The user's local application data folder.</param>
    /// <param name="fileExists">The file-existence check; <see cref="File.Exists"/> by default.</param>
    public static AppPaths Resolve(
        string? dataDirOverride,
        string executableDirectory,
        string localAppDataDirectory,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (!string.IsNullOrWhiteSpace(dataDirOverride))
        {
            return new AppPaths(Path.GetFullPath(dataDirOverride.Trim()), DataRootSource.Override);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        if (fileExists(Path.Combine(executableDirectory, PortableMarkerFileName)))
        {
            return new AppPaths(Path.GetFullPath(Path.Combine(executableDirectory, "data")), DataRootSource.Portable);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataDirectory);
        return new AppPaths(Path.GetFullPath(Path.Combine(localAppDataDirectory, NimPadInfo.ProductName)), DataRootSource.LocalAppData);
    }

    /// <summary>
    /// Resolves the paths for the running process: <see cref="AppContext.BaseDirectory"/> as the executable directory
    /// and <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
    /// </summary>
    public static AppPaths ResolveForCurrentProcess(string? dataDirOverride) =>
        Resolve(
            dataDirOverride,
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));

    /// <summary>Creates the profile, backups and logs directories.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ProfileDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(LogsDir);
    }
}
