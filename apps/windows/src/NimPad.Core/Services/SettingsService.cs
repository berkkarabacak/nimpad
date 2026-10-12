using NimPad.Core.Domain;
using NimPad.Core.Storage;
using NimPad.Core.Time;

namespace NimPad.Core.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>, one row per setting in the <c>settings</c> table.
/// </summary>
/// <remarks>
/// Forward compatible in both directions: a key that is missing (an older database) or unreadable (written by a
/// newer NimPad with values this version does not know) takes its default, and saving writes only the keys listed in
/// <see cref="Keys"/>, so settings added by a newer version are kept.
/// </remarks>
public sealed class SettingsService
{
    private readonly LocalDatabase _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="database">The local database.</param>
    /// <param name="timeProvider">The clock for timestamps; <see cref="TimeProvider.System"/> by default.</param>
    public SettingsService(LocalDatabase database, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The storage keys of the settings this version knows.</summary>
    public static class Keys
    {
        /// <summary><c>"system"</c>, <c>"light"</c> or <c>"dark"</c>.</summary>
        public const string Theme = "appearance.theme";

        /// <summary>A string.</summary>
        public const string EditorFontFamily = "editor.fontFamily";

        /// <summary>An integer, in pixels.</summary>
        public const string EditorFontSize = "editor.fontSize";

        /// <summary>A boolean.</summary>
        public const string WordWrap = "editor.wordWrap";

        /// <summary>A boolean.</summary>
        public const string ShowLineNumbers = "editor.lineNumbers";

        /// <summary>2, 4 or 8.</summary>
        public const string TabSize = "editor.tabSize";

        /// <summary>An integer percentage.</summary>
        public const string Zoom = "editor.zoom";

        /// <summary>A boolean.</summary>
        public const string RestoreTabs = "general.restoreTabs";

        /// <summary>A boolean.</summary>
        public const string StartWithWindows = "general.startWithWindows";

        /// <summary>A string such as <c>"Ctrl+Alt+N"</c>; empty for none.</summary>
        public const string GlobalHotkey = "general.globalHotkey";
    }

    /// <summary>The saved settings, with defaults for missing or invalid values.</summary>
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync(
            connection =>
            {
                var values = KeyValueRows.ReadAll(connection, KeyValueRows.Settings);
                var settings = AppSettings.Default;

                // Each value is applied only when it is valid on its own, so one bad value never resets the others.
                AppSettings Apply(AppSettings candidate) => candidate.Validate().Count == 0 ? candidate : settings;

                if (KeyValueRows.TryGet<string>(values, Keys.Theme, out var theme) && TryParseTheme(theme, out var parsedTheme))
                {
                    settings = settings with { Theme = parsedTheme };
                }

                if (KeyValueRows.TryGet<string>(values, Keys.EditorFontFamily, out var family))
                {
                    settings = Apply(settings with { EditorFontFamily = family });
                }

                if (KeyValueRows.TryGet<int>(values, Keys.EditorFontSize, out var size))
                {
                    settings = Apply(settings with { EditorFontSize = size });
                }

                if (KeyValueRows.TryGet<bool>(values, Keys.WordWrap, out var wrap))
                {
                    settings = settings with { WordWrap = wrap };
                }

                if (KeyValueRows.TryGet<bool>(values, Keys.ShowLineNumbers, out var lineNumbers))
                {
                    settings = settings with { ShowLineNumbers = lineNumbers };
                }

                if (KeyValueRows.TryGet<int>(values, Keys.TabSize, out var tabSize))
                {
                    settings = Apply(settings with { TabSize = tabSize });
                }

                if (KeyValueRows.TryGet<int>(values, Keys.Zoom, out var zoom))
                {
                    settings = Apply(settings with { Zoom = zoom });
                }

                if (KeyValueRows.TryGet<bool>(values, Keys.RestoreTabs, out var restore))
                {
                    settings = settings with { RestoreTabs = restore };
                }

                if (KeyValueRows.TryGet<bool>(values, Keys.StartWithWindows, out var autostart))
                {
                    settings = settings with { StartWithWindows = autostart };
                }

                if (KeyValueRows.TryGet<string>(values, Keys.GlobalHotkey, out var hotkey))
                {
                    settings = Apply(settings with { GlobalHotkey = hotkey });
                }

                return settings;
            },
            cancellationToken);

    /// <summary>Saves every setting in one transaction (keys this version does not know are left alone).</summary>
    /// <exception cref="ArgumentException">The settings are invalid (see <see cref="AppSettings.Validate"/>).</exception>
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));
        }

        var rows = new (string Key, string Json)[]
        {
            (Keys.Theme, KeyValueRows.ToJson(ToWireName(settings.Theme))),
            (Keys.EditorFontFamily, KeyValueRows.ToJson(settings.EditorFontFamily)),
            (Keys.EditorFontSize, KeyValueRows.ToJson(settings.EditorFontSize)),
            (Keys.WordWrap, KeyValueRows.ToJson(settings.WordWrap)),
            (Keys.ShowLineNumbers, KeyValueRows.ToJson(settings.ShowLineNumbers)),
            (Keys.TabSize, KeyValueRows.ToJson(settings.TabSize)),
            (Keys.Zoom, KeyValueRows.ToJson(settings.Zoom)),
            (Keys.RestoreTabs, KeyValueRows.ToJson(settings.RestoreTabs)),
            (Keys.StartWithWindows, KeyValueRows.ToJson(settings.StartWithWindows)),
            (Keys.GlobalHotkey, KeyValueRows.ToJson(settings.GlobalHotkey)),
        };
        return _database.Writer.ExecuteAsync(
            context =>
            {
                var now = Timestamps.Now(_timeProvider);
                foreach (var (key, json) in rows)
                {
                    KeyValueRows.Upsert(context, KeyValueRows.Settings, key, json, now);
                }
            },
            cancellationToken);
    }

    private static string ToWireName(ThemePreference theme) => theme switch
    {
        ThemePreference.System => "system",
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown theme."),
    };

    private static bool TryParseTheme(string name, out ThemePreference theme)
    {
        (var ok, theme) = name switch
        {
            "system" => (true, ThemePreference.System),
            "light" => (true, ThemePreference.Light),
            "dark" => (true, ThemePreference.Dark),
            _ => (false, ThemePreference.System),
        };
        return ok;
    }
}
