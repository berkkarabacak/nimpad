using System.IO;

namespace NimPad.App.Services;

/// <summary>
/// The parsed command line: <c>NimPad.exe [--data-dir &lt;path&gt;] [--no-single-instance] [--no-hotkey] [--background]
/// [files...]</c>. Options are case-insensitive and also accept the <c>--name=value</c> form; <c>--</c> ends the options
/// (everything after it is a file). Parsing never throws: unknown options are collected in <see cref="UnknownOptions"/>
/// and a <c>--data-dir</c> without a value is reported in <see cref="Problems"/>.
/// </summary>
public sealed record StartupArgs
{
    /// <summary>The <c>--data-dir</c> option.</summary>
    public const string DataDirOption = "--data-dir";

    /// <summary>The <c>--no-single-instance</c> option.</summary>
    public const string NoSingleInstanceOption = "--no-single-instance";

    /// <summary>The <c>--no-hotkey</c> option.</summary>
    public const string NoHotkeyOption = "--no-hotkey";

    /// <summary>The <c>--background</c> option.</summary>
    public const string BackgroundOption = "--background";

    /// <summary>No options and no files.</summary>
    public static StartupArgs Empty { get; } = new();

    /// <summary>The data root override, or <see langword="null"/> for the default (or portable) location.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>True when this process must not hand its arguments to an already running NimPad (tests).</summary>
    public bool NoSingleInstance { get; init; }

    /// <summary>True when the global shortcut must not be registered (tests, or a second profile).</summary>
    public bool NoHotkey { get; init; }

    /// <summary>True when NimPad starts without showing its window (start with Windows).</summary>
    public bool Background { get; init; }

    /// <summary>Files to open as external tabs, made absolute against the current directory.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Options NimPad does not know (ignored, logged).</summary>
    public IReadOnlyList<string> UnknownOptions { get; init; } = [];

    /// <summary>Problems found while parsing (for the log), for example a <c>--data-dir</c> without a path.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Parses the arguments <see cref="System.Windows.Application"/> received (without the executable).</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="currentDirectory">The directory relative file paths resolve against; the process's by default.</param>
    public static StartupArgs Parse(IReadOnlyList<string>? args, string? currentDirectory = null)
    {
        if (args is null || args.Count == 0)
        {
            return Empty;
        }

        string? dataDir = null;
        bool noSingleInstance = false, noHotkey = false, background = false, optionsEnded = false;
        var files = new List<string>();
        var unknown = new List<string>();
        var problems = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (optionsEnded || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (ToFullPath(arg, currentDirectory) is { } file)
                {
                    files.Add(file);
                }
                else
                {
                    problems.Add($"Ignored the file argument {arg}: it is not a valid path.");
                }

                continue;
            }

            if (arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            var name = (separator < 0 ? arg : arg[..separator]).ToLowerInvariant();
            var inlineValue = separator < 0 ? null : arg[(separator + 1)..];
            switch (name)
            {
                case DataDirOption:
                    var value = inlineValue;
                    if (value is null && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        value = args[++i];
                    }

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        problems.Add("--data-dir needs a folder path; the default data folder is used.");
                    }
                    else
                    {
                        dataDir = value.Trim().Trim('"');
                    }

                    break;
                case NoSingleInstanceOption:
                    noSingleInstance = true;
                    break;
                case NoHotkeyOption:
                    noHotkey = true;
                    break;
                case BackgroundOption:
                    background = true;
                    break;
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return new StartupArgs
        {
            DataDirectory = dataDir,
            NoSingleInstance = noSingleInstance,
            NoHotkey = noHotkey,
            Background = background,
            Files = files,
            UnknownOptions = unknown,
            Problems = problems,
        };
    }

    /// <summary>The arguments as a command line for another NimPad process (single-instance forwarding).</summary>
    public IReadOnlyList<string> ToArguments()
    {
        var list = new List<string>();
        if (DataDirectory is { } dataDir)
        {
            list.Add(DataDirOption);
            list.Add(dataDir);
        }

        if (NoSingleInstance)
        {
            list.Add(NoSingleInstanceOption);
        }

        if (NoHotkey)
        {
            list.Add(NoHotkeyOption);
        }

        if (Background)
        {
            list.Add(BackgroundOption);
        }

        if (Files.Count > 0)
        {
            list.Add("--");
            list.AddRange(Files);
        }

        return list;
    }

    private static string? ToFullPath(string path, string? currentDirectory)
    {
        try
        {
            var trimmed = path.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                return null;
            }

            return currentDirectory is null ? Path.GetFullPath(trimmed) : Path.GetFullPath(trimmed, currentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
