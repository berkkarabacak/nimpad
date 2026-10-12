namespace NimPad.Presentation.Integration;

/// <summary>What a burst of file-system events for an open external file turned out to mean.</summary>
public enum ExternalFileChangeKind
{
    /// <summary>Nothing to tell the tab (the file could not be read, or it is still missing).</summary>
    None,

    /// <summary>The file carries the write time of NimPad's own last save: the event was our save.</summary>
    OwnSave,

    /// <summary>Another program changed (or replaced) the file.</summary>
    Changed,

    /// <summary>The file was moved, renamed or deleted.</summary>
    Missing,

    /// <summary>A missing file is back (moved back, restored, or recreated).</summary>
    Reappeared,
}

/// <summary>An open external file as found on disk after its events settled.</summary>
/// <param name="Exists">Whether the file exists.</param>
/// <param name="LastWriteTimeUtc">Its last write time (meaningful when it exists).</param>
public readonly record struct ExternalFileState(bool Exists, DateTime LastWriteTimeUtc)
{
    /// <summary>A missing file.</summary>
    public static ExternalFileState Missing { get; } = new(false, default);

    /// <summary>Reads the file's state, or <see langword="null"/> when it cannot be read (access denied, offline share).</summary>
    public static ExternalFileState? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new ExternalFileState(true, info.LastWriteTimeUtc) : Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Decides what file-system events mean for an open external file. Events are never trusted on their own (an atomic
/// save by NimPad or another editor shows up as renames, deletes and creates); the watcher waits until they settle,
/// reads the file once and asks <see cref="Evaluate"/>.
/// </summary>
public static class ExternalFileChanges
{
    /// <summary>Classifies the file's settled state.</summary>
    /// <param name="state">What the file looks like now, or <see langword="null"/> when it could not be read.</param>
    /// <param name="lastSavedWriteTimeUtc">
    /// The write time NimPad's own last save gave the file (<c>TabViewModel.LastSavedWriteTimeUtc</c>): a file that
    /// still carries it was not touched by anyone else.
    /// </param>
    /// <param name="reportedMissing">Whether the tab already knows the file is missing.</param>
    public static ExternalFileChangeKind Evaluate(ExternalFileState? state, DateTime? lastSavedWriteTimeUtc, bool reportedMissing)
    {
        if (state is not { } current)
        {
            return ExternalFileChangeKind.None;
        }

        if (!current.Exists)
        {
            return reportedMissing ? ExternalFileChangeKind.None : ExternalFileChangeKind.Missing;
        }

        if (reportedMissing)
        {
            return ExternalFileChangeKind.Reappeared;
        }

        return lastSavedWriteTimeUtc is { } saved && saved == current.LastWriteTimeUtc
            ? ExternalFileChangeKind.OwnSave
            : ExternalFileChangeKind.Changed;
    }
}
