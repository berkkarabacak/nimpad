using System.Text;

namespace NimPad.Presentation.Integration;

/// <summary>
/// NimPad's command line: <c>NimPad.exe [--data-dir &lt;path&gt;] [--no-single-instance] [--no-hotkey] [--background]
/// [--restarted] [files...]</c>.
/// </summary>
/// <remarks>
/// Options are case-insensitive and may come in any order; <c>--data-dir=&lt;path&gt;</c> also works, and everything
/// after <c>--</c> is a file. <see cref="Parse"/> never throws: unknown options land in <see cref="UnknownOptions"/>
/// (log them) and a <c>--data-dir</c> without a value is ignored.
/// </remarks>
public sealed record AppCommandLine
{
    /// <summary>Uses another data root (tests pass a temp folder).</summary>
    public const string DataDirOption = "--data-dir";

    /// <summary>Runs without the single-instance check (tests start several instances).</summary>
    public const string NoSingleInstanceOption = "--no-single-instance";

    /// <summary>Never registers the global shortcut with Windows (tests).</summary>
    public const string NoHotkeyOption = "--no-hotkey";

    /// <summary>Starts minimized (the start-with-Windows entry passes it).</summary>
    public const string BackgroundOption = "--background";

    /// <summary>Windows restarted NimPad after a crash, hang or update (<c>RegisterApplicationRestart</c>).</summary>
    public const string RestartedOption = "--restarted";

    /// <summary>The <c>--data-dir</c> value as given (relative paths resolve against the current directory), or <see langword="null"/>.</summary>
    public string? DataDirectory { get; init; }

    /// <summary><c>--no-single-instance</c>.</summary>
    public bool NoSingleInstance { get; init; }

    /// <summary><c>--no-hotkey</c>.</summary>
    public bool NoHotkey { get; init; }

    /// <summary><c>--background</c>: start minimized and do not take focus.</summary>
    public bool Background { get; init; }

    /// <summary><c>--restarted</c>: Windows restarted NimPad (recovery banners come from the shell either way).</summary>
    public bool Restarted { get; init; }

    /// <summary>The files to open, as given (see <see cref="ResolveFiles"/>).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Options NimPad does not know, for the log.</summary>
    public IReadOnlyList<string> UnknownOptions { get; init; } = [];

    /// <summary>Parses the arguments (without the executable name, as <c>StartupEventArgs.Args</c> gives them).</summary>
    public static AppCommandLine Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return new AppCommandLine();
        }

        string? dataDirectory = null;
        bool noSingleInstance = false, noHotkey = false, background = false, restarted = false, filesOnly = false;
        var files = new List<string>();
        var unknown = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (filesOnly || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                files.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                filesOnly = true;
                continue;
            }

            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? arg : arg[..separator];
            switch (name.ToLowerInvariant())
            {
                case DataDirOption:
                    var value = separator >= 0 ? arg[(separator + 1)..] : i + 1 < args.Count ? args[++i] : null;
                    dataDirectory = string.IsNullOrWhiteSpace(value) ? dataDirectory : value.Trim();
                    break;
                case NoSingleInstanceOption when separator < 0:
                    noSingleInstance = true;
                    break;
                case NoHotkeyOption when separator < 0:
                    noHotkey = true;
                    break;
                case BackgroundOption when separator < 0:
                    background = true;
                    break;
                case RestartedOption when separator < 0:
                    restarted = true;
                    break;
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return new AppCommandLine
        {
            DataDirectory = dataDirectory,
            NoSingleInstance = noSingleInstance,
            NoHotkey = noHotkey,
            Background = background,
            Restarted = restarted,
            Files = files,
            UnknownOptions = unknown,
        };
    }

    /// <summary>
    /// The files as full paths, resolved against <paramref name="currentDirectory"/> (a forwarded launch must resolve
    /// them itself: the running instance has another current directory). Unusable paths are left out.
    /// </summary>
    public IReadOnlyList<string> ResolveFiles(string currentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        var resolved = new List<string>(Files.Count);
        foreach (var file in Files)
        {
            if (TryGetFullPath(file, currentDirectory) is { } path)
            {
                resolved.Add(path);
            }
        }

        return resolved;
    }

    /// <summary>
    /// The arguments Windows passes when it restarts NimPad (<c>RegisterApplicationRestart</c>): <c>--restarted</c> plus
    /// the options that select the same data and test behavior. Files are not repeated; the session restores them.
    /// </summary>
    /// <param name="currentDirectory">Resolves a relative <c>--data-dir</c>, since the restarted process may start elsewhere.</param>
    public string BuildRestartArguments(string currentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        var builder = new StringBuilder(RestartedOption);
        if (DataDirectory is { } dataDirectory && TryGetFullPath(dataDirectory, currentDirectory) is { } fullDataDirectory)
        {
            builder.Append(' ').Append(DataDirOption).Append(' ').Append(WindowsCommandLine.Quote(fullDataDirectory));
        }

        if (NoSingleInstance)
        {
            builder.Append(' ').Append(NoSingleInstanceOption);
        }

        if (NoHotkey)
        {
            builder.Append(' ').Append(NoHotkeyOption);
        }

        return builder.ToString();
    }

    private static string? TryGetFullPath(string path, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim(), currentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>Builds Windows command lines that <c>CommandLineToArgvW</c> (and .NET's argument parsing) split back exactly.</summary>
public static class WindowsCommandLine
{
    /// <summary>
    /// Quotes one argument when it needs it: empty, or containing a space, tab, newline or quote. Inside quotes, a
    /// quote is escaped as <c>\"</c> and the backslashes before a quote (or the closing quote) are doubled.
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2).Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                // Before the closing quote every backslash is doubled.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>
    /// Splits a command line the way <c>CommandLineToArgvW</c> does for arguments after the program name (quotes
    /// group, <c>\"</c> is a literal quote, <c>2n</c> backslashes before a quote are <c>n</c> backslashes, <c>""</c>
    /// inside quotes is a literal quote).
    /// </summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        i--;
                    }
                }
                else
                {
                    current.Append('\\', backslashes);
                    i--;
                }

                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                hasToken = true;
                continue;
            }

            if (!inQuotes && c is ' ' or '\t' or '\n' or '\v')
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
