namespace NimPad.Core.Domain;

/// <summary>The app theme setting.</summary>
public enum ThemePreference
{
    /// <summary>Follow the Windows app mode.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>
/// The user's settings (Settings window: Appearance and General). Stored one key per setting in <c>settings</c>;
/// missing or unreadable keys take the defaults below.
/// </summary>
public sealed record AppSettings
{
    /// <summary>The bundled editor font.</summary>
    public const string DefaultEditorFontFamily = "Cascadia Mono";

    /// <summary>The default editor font size in pixels.</summary>
    public const int DefaultEditorFontSize = 14;

    /// <summary>The smallest editor font size.</summary>
    public const int MinEditorFontSize = 8;

    /// <summary>The largest editor font size.</summary>
    public const int MaxEditorFontSize = 72;

    /// <summary>The default tab size.</summary>
    public const int DefaultTabSize = 4;

    /// <summary>The default global shortcut that brings NimPad forward.</summary>
    public const string DefaultGlobalHotkey = "Ctrl+Alt+N";

    /// <summary>The default editor zoom in percent.</summary>
    public const int DefaultZoom = 100;

    /// <summary>The smallest editor zoom in percent.</summary>
    public const int MinZoom = 25;

    /// <summary>The largest editor zoom in percent.</summary>
    public const int MaxZoom = 400;

    /// <summary>The tab sizes the settings offer.</summary>
    public static IReadOnlyList<int> AllowedTabSizes { get; } = [2, 4, 8];

    /// <summary>The defaults of a first run.</summary>
    public static AppSettings Default { get; } = new();

    /// <summary>Light, dark or follow Windows.</summary>
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>The editor font family.</summary>
    public string EditorFontFamily { get; init; } = DefaultEditorFontFamily;

    /// <summary>The editor font size in pixels (<see cref="MinEditorFontSize"/>–<see cref="MaxEditorFontSize"/>).</summary>
    public int EditorFontSize { get; init; } = DefaultEditorFontSize;

    /// <summary>Wrap long lines instead of scrolling sideways.</summary>
    public bool WordWrap { get; init; }

    /// <summary>Show line numbers.</summary>
    public bool ShowLineNumbers { get; init; } = true;

    /// <summary>The tab size: 2, 4 or 8.</summary>
    public int TabSize { get; init; } = DefaultTabSize;

    /// <summary>Reopen the previous tabs on start.</summary>
    public bool RestoreTabs { get; init; } = true;

    /// <summary>Start NimPad when the user signs in to Windows.</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>The global shortcut, such as <c>Ctrl+Alt+N</c>; empty turns it off.</summary>
    public string GlobalHotkey { get; init; } = DefaultGlobalHotkey;

    /// <summary>The editor zoom in percent (<see cref="MinZoom"/>–<see cref="MaxZoom"/>).</summary>
    public int Zoom { get; init; } = DefaultZoom;

    /// <summary>The problems that make these settings invalid; empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Enum.IsDefined(Theme))
        {
            problems.Add($"Unknown theme {Theme}.");
        }

        if (string.IsNullOrWhiteSpace(EditorFontFamily) || EditorFontFamily.Length > 200)
        {
            problems.Add("The editor font family must be 1–200 characters.");
        }

        if (EditorFontSize is < MinEditorFontSize or > MaxEditorFontSize)
        {
            problems.Add($"The editor font size must be {MinEditorFontSize}–{MaxEditorFontSize}.");
        }

        if (!AllowedTabSizes.Contains(TabSize))
        {
            problems.Add("The tab size must be 2, 4 or 8.");
        }

        if (GlobalHotkey is null || GlobalHotkey.Length > 100)
        {
            problems.Add("The global shortcut must be at most 100 characters.");
        }

        if (Zoom is < MinZoom or > MaxZoom)
        {
            problems.Add($"The zoom must be {MinZoom}–{MaxZoom} %.");
        }

        return problems;
    }
}
