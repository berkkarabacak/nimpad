namespace NimPad.Presentation.Integration;

/// <summary>
/// The start-with-Windows entry: the value NimPad writes under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> and how a value found there is read back. Pure, so the
/// rules are testable anywhere; the App's <c>AutostartService</c> does the registry work.
/// </summary>
public static class AutostartCommand
{
    /// <summary>
    /// The Run value for <paramref name="executablePath"/>: <c>"C:\…\NimPad.exe" --background</c>. Portable copies
    /// register wherever their executable is.
    /// </summary>
    public static string Build(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("An executable path cannot contain quotes.", nameof(executablePath));
        }

        return "\"" + executablePath + "\" " + AppCommandLine.BackgroundOption;
    }

    /// <summary>The executable a Run value starts (its first, possibly quoted, token), or <see langword="null"/>.</summary>
    public static string? GetExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            var quoted = end < 0 ? text[1..] : text[1..end];
            return string.IsNullOrWhiteSpace(quoted) ? null : quoted;
        }

        var space = text.IndexOfAny([' ', '\t']);
        return space < 0 ? text : text[..space];
    }

    /// <summary>
    /// True when the Run value starts <paramref name="executablePath"/> (the comparison ignores case and normalizes
    /// both paths). A value that points at another copy of NimPad counts as off for this copy.
    /// </summary>
    public static bool PointsTo(string? command, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (GetExecutablePath(command) is not { } registered)
        {
            return false;
        }

        return string.Equals(Normalize(registered), Normalize(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when Task Manager's Startup apps page turned the entry off: the
    /// <c>Explorer\StartupApproved\Run</c> value of the same name exists and its first byte is odd (02 and 06 mean
    /// on, 03 and 07 off). A missing value means on.
    /// </summary>
    public static bool IsTurnedOffByUser(byte[]? startupApprovedData) =>
        startupApprovedData is { Length: > 0 } && (startupApprovedData[0] & 1) == 1;

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}
