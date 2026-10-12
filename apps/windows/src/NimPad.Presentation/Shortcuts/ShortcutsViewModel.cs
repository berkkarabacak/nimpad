using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Settings;

namespace NimPad.Presentation.Shortcuts;

/// <summary>One row of the shortcuts dialog: a label and its key caps.</summary>
/// <param name="Label">For example <c>Reopen closed tab</c>.</param>
/// <param name="Keys">The key caps, for example <c>["Ctrl", "Shift", "T"]</c>.</param>
public sealed record ShortcutViewModel(string Label, IReadOnlyList<string> Keys)
{
    /// <summary>The keys as text: <c>Ctrl+Shift+T</c>.</summary>
    public string KeysText => string.Join('+', Keys);
}

/// <summary>A group of the shortcuts dialog (NOTES &amp; TABS, EDITING, FIND, APP).</summary>
/// <param name="Title">The group heading, uppercase.</param>
/// <param name="Shortcuts">Its rows (after filtering).</param>
public sealed record ShortcutGroupViewModel(string Title, IReadOnlyList<ShortcutViewModel> Shortcuts);

/// <summary>The keyboard shortcuts dialog (Ctrl+/, design pages 12 and 27) with its filter field.</summary>
public sealed partial class ShortcutsViewModel : ObservableObject
{
    /// <summary>The dialog's footer (Phase 1 does not sync, so it does not say so).</summary>
    public const string FooterText = "Notes save on their own. Ctrl+S exports the current note as a file on this computer.";

    private readonly SettingsStore _settings;

    internal ShortcutsViewModel(SettingsStore settings)
    {
        _settings = settings;
        settings.Changed += (_, _) => ApplyFilter();
        ApplyFilter();
    }

    /// <summary>The filter text ("Search shortcuts").</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>The groups with rows that match the filter (all of them when it is empty).</summary>
    public ObservableCollection<ShortcutGroupViewModel> Groups { get; } = [];

    /// <summary>True when at least one row matches.</summary>
    [ObservableProperty]
    public partial bool HasResults { get; private set; }

    /// <summary><c>No shortcuts match “zoomm”</c> when nothing matches.</summary>
    [ObservableProperty]
    public partial string EmptyTitle { get; private set; } = string.Empty;

    /// <summary><c>Try “zoom” or a shorter word.</c> when nothing matches.</summary>
    [ObservableProperty]
    public partial string EmptyMessage { get; private set; } = string.Empty;

    /// <summary>Every group, unfiltered, with the current global shortcut.</summary>
    public IReadOnlyList<ShortcutGroupViewModel> AllGroups => BuildGroups(_settings.Current.GlobalHotkey);

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var all = AllGroups;
        var filtered = Apply(all, Filter);
        Groups.Clear();
        foreach (var group in filtered)
        {
            Groups.Add(group);
        }

        HasResults = filtered.Count > 0;
        if (HasResults)
        {
            EmptyTitle = string.Empty;
            EmptyMessage = string.Empty;
            return;
        }

        var query = Filter.Trim();
        EmptyTitle = "No shortcuts match " + DisplayText.Quote(query);
        var suggestion = Suggest(all, query);
        EmptyMessage = suggestion is null ? "Try a shorter word." : $"Try {DisplayText.Quote(suggestion)} or a shorter word.";
    }

    private static List<ShortcutGroupViewModel> Apply(IReadOnlyList<ShortcutGroupViewModel> groups, string filter)
    {
        var words = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [.. groups];
        }

        var result = new List<ShortcutGroupViewModel>();
        foreach (var group in groups)
        {
            var rows = group.Shortcuts.Where(row => words.All(word => Matches(row, word))).ToList();
            if (rows.Count > 0)
            {
                result.Add(group with { Shortcuts = rows });
            }
        }

        return result;
    }

    // A word matches the label, or one key cap ("F2"); a typed chord such as "ctrl+shift+t" matches those keys exactly.
    private static bool Matches(ShortcutViewModel row, string word)
    {
        if (word.Length > 1 && word.Contains('+', StringComparison.Ordinal))
        {
            var keys = word.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return keys.Length == row.Keys.Count && keys.Zip(row.Keys).All(pair => pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase));
        }

        return row.Label.Contains(word, StringComparison.CurrentCultureIgnoreCase)
            || row.Keys.Any(key => key.Equals(word, StringComparison.OrdinalIgnoreCase));
    }

    // The longest start of the query (two characters or more) that matches something: "zoomm" -> "zoom".
    private static string? Suggest(IReadOnlyList<ShortcutGroupViewModel> groups, string query)
    {
        for (var length = query.Length - 1; length >= 2; length--)
        {
            var candidate = query[..length].TrimEnd();
            if (candidate.Length >= 2 && Apply(groups, candidate).Count > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<ShortcutGroupViewModel> BuildGroups(string globalHotkey)
    {
        var global = HotkeyGesture.TryParse(globalHotkey, out var gesture, out _) ? gesture.Keys : (IReadOnlyList<string>)["Off"];
        return
        [
            new("NOTES & TABS",
            [
                new("New note", ["Ctrl", "N"]),
                new("Open file", ["Ctrl", "O"]),
                new("Close tab", ["Ctrl", "W"]),
                new("Next tab", ["Ctrl", "Tab"]),
                new("Previous tab", ["Ctrl", "Shift", "Tab"]),
                new("Reopen closed tab", ["Ctrl", "Shift", "T"]),
                new("Rename tab", ["F2"]),
                new("Go to tab 1–9", ["Ctrl", "1…9"]),
                new("Move tab left", ["Ctrl", "Shift", "←"]),
                new("Move tab right", ["Ctrl", "Shift", "→"]),
                new("Duplicate note", ["Ctrl", "Shift", "D"]),
                new("Undo move to Trash (in the sidebar)", ["Ctrl", "Z"]),
            ]),
            new("EDITING",
            [
                new("Undo", ["Ctrl", "Z"]),
                new("Redo", ["Ctrl", "Shift", "Z"]),
                new("Export note to file", ["Ctrl", "S"]),
                new("Save as", ["Ctrl", "Shift", "S"]),
                new("Zoom in", ["Ctrl", "+"]),
                new("Zoom out", ["Ctrl", "-"]),
                new("Reset zoom", ["Ctrl", "0"]),
            ]),
            new("FIND",
            [
                new("Find", ["Ctrl", "F"]),
                new("Replace", ["Ctrl", "H"]),
                new("Next match", ["Enter"]),
                new("Previous match", ["Shift", "Enter"]),
                new("Match case", ["Alt", "C"]),
                new("Whole word", ["Alt", "W"]),
                new("Regular expression", ["Alt", "R"]),
                new("Replace all", ["Ctrl", "Enter"]),
            ]),
            new("APP",
            [
                new("Toggle notes sidebar", ["Ctrl", "B"]),
                new("Settings", ["Ctrl", ","]),
                new("Keyboard shortcuts", ["Ctrl", "/"]),
                new("Next region", ["F6"]),
                new("Previous region", ["Shift", "F6"]),
                new("Open NimPad from anywhere", global),
            ]),
        ];
    }
}
