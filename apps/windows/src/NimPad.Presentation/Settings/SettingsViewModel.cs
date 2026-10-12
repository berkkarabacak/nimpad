using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimPad.Core.Domain;
using NimPad.Presentation.Infrastructure;

namespace NimPad.Presentation.Settings;

/// <summary>The pages of the Settings window in Phase 1 (Account and Synchronization arrive with sign-in in Phase 2).</summary>
public enum SettingsPage
{
    /// <summary>Theme, editor font, size, wrap, line numbers, tab size.</summary>
    Appearance,

    /// <summary>Restore tabs, start with Windows, global shortcut.</summary>
    General,
}

/// <summary>One choice of the theme segmented control.</summary>
/// <param name="Theme">The value.</param>
/// <param name="Label">The label: Light, Dark or System.</param>
public sealed record ThemeOption(ThemePreference Theme, string Label);

/// <summary>
/// The Settings window (design pages 18 and 21). Every change applies live (the editor and the theme follow at once)
/// and is stored immediately.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>The smallest font size the stepper offers.</summary>
    public const int MinFontSize = 8;

    /// <summary>The largest font size the stepper offers.</summary>
    public const int MaxFontSize = 32;

    private readonly ViewModelContext _context;
    private bool _syncing;

    internal SettingsViewModel(ViewModelContext context)
    {
        _context = context;
        context.Settings.Changed += (_, _) => LoadFromStore();
        context.Platform.SystemTheme.Changed += (_, _) => UpdateEffectiveTheme();
        LoadFromStore();
    }

    /// <summary>The pages, in sidebar order.</summary>
    public IReadOnlyList<SettingsPage> Pages { get; } = [SettingsPage.Appearance, SettingsPage.General];

    /// <summary>The page shown.</summary>
    [ObservableProperty]
    public partial SettingsPage SelectedPage { get; set; }

    // ------------------------------------------------------------------ appearance

    /// <summary>The theme choices, in the design's order.</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new(ThemePreference.Light, "Light"),
        new(ThemePreference.Dark, "Dark"),
        new(ThemePreference.System, "System"),
    ];

    /// <summary>Light, Dark or follow Windows.</summary>
    [ObservableProperty]
    public partial ThemePreference Theme { get; set; }

    /// <summary>True when the app should draw dark (System follows Windows' app mode); the App's ThemeService follows it.</summary>
    [ObservableProperty]
    public partial bool EffectiveIsDark { get; private set; }

    /// <summary>The monospace fonts installed (with the current choice and the bundled default included).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> FontFamilies { get; private set; } = [];

    /// <summary>The editor font.</summary>
    [ObservableProperty]
    public partial string EditorFontFamily { get; set; } = AppSettings.DefaultEditorFontFamily;

    /// <summary>The editor font size in pixels (8–32).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontSizeText))]
    [NotifyCanExecuteChangedFor(nameof(IncreaseFontSizeCommand), nameof(DecreaseFontSizeCommand))]
    public partial int FontSize { get; set; } = AppSettings.DefaultEditorFontSize;

    /// <summary>The stepper's text, for example <c>14 px</c>.</summary>
    public string FontSizeText => FontSize.ToString(_context.Culture) + " px";

    /// <summary>Wrap long lines instead of scrolling sideways.</summary>
    [ObservableProperty]
    public partial bool WordWrap { get; set; }

    /// <summary>Show line numbers.</summary>
    [ObservableProperty]
    public partial bool ShowLineNumbers { get; set; }

    /// <summary>The tab size (2, 4 or 8).</summary>
    [ObservableProperty]
    public partial int TabSize { get; set; } = AppSettings.DefaultTabSize;

    /// <summary>The tab sizes offered.</summary>
    public IReadOnlyList<int> TabSizes => AppSettings.AllowedTabSizes;

    /// <summary>The live preview's two lines.</summary>
    public IReadOnlyList<string> PreviewLines { get; } = ["The quick brown fox jumps over the lazy dog", "0123456789 {}[]() => != ==="];

    /// <summary>One size larger.</summary>
    [RelayCommand(CanExecute = nameof(CanIncreaseFontSize))]
    private void IncreaseFontSize() => FontSize = Math.Min(FontSize + 1, MaxFontSize);

    /// <summary>One size smaller.</summary>
    [RelayCommand(CanExecute = nameof(CanDecreaseFontSize))]
    private void DecreaseFontSize() => FontSize = Math.Max(FontSize - 1, MinFontSize);

    private bool CanIncreaseFontSize() => FontSize < MaxFontSize;

    private bool CanDecreaseFontSize() => FontSize > MinFontSize;

    // ------------------------------------------------------------------ general

    /// <summary>Reopen the previous tabs on start.</summary>
    [ObservableProperty]
    public partial bool RestoreTabs { get; set; }

    /// <summary>Start NimPad when the user signs in to Windows.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary>The global shortcut, for example <c>Ctrl+Alt+N</c> (empty when off).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalShortcutKeys))]
    public partial string GlobalShortcut { get; private set; } = AppSettings.DefaultGlobalHotkey;

    /// <summary>The shortcut's key caps: <c>Ctrl</c> <c>Alt</c> <c>N</c>.</summary>
    public IReadOnlyList<string> GlobalShortcutKeys =>
        HotkeyGesture.TryParse(GlobalShortcut, out var gesture, out _) ? gesture.Keys : [];

    /// <summary>True while the view captures a new shortcut (after Change).</summary>
    [ObservableProperty]
    public partial bool IsCapturingShortcut { get; private set; }

    /// <summary>Why the captured shortcut can't be used (shown inline), or <see langword="null"/>.</summary>
    [ObservableProperty]
    public partial string? ShortcutError { get; private set; }

    /// <summary>Starts capturing a new global shortcut (the Change button); the view sends the next key gesture.</summary>
    [RelayCommand]
    public void BeginShortcutCapture()
    {
        ShortcutError = null;
        IsCapturingShortcut = true;
    }

    /// <summary>Stops capturing without changing the shortcut (Esc).</summary>
    [RelayCommand]
    public void CancelShortcutCapture()
    {
        IsCapturingShortcut = false;
        ShortcutError = null;
    }

    /// <summary>
    /// Validates and registers a captured gesture (for example <c>Ctrl+Alt+K</c>). On success the shortcut is stored;
    /// otherwise <see cref="ShortcutError"/> explains why (invalid, reserved, or another app uses it) and capturing
    /// continues.
    /// </summary>
    /// <returns><see langword="true"/> when the shortcut changed.</returns>
    public bool CompleteShortcutCapture(string gesture)
    {
        if (!HotkeyGesture.TryParse(gesture, out var parsed, out var error))
        {
            ShortcutError = error;
            return false;
        }

        var text = parsed.ToString();
        bool registered;
        string? conflict;
        try
        {
            registered = _context.Platform.GlobalHotkey.TryRegister(text, out conflict);
        }
        catch (Exception ex)
        {
            // The view calls this from a key handler: an exception must not reach the dispatcher.
            Trace.TraceWarning($"Registering the global shortcut {text} failed: {ex}");
            registered = false;
            conflict = $"{text} couldn't be registered. Try a different shortcut.";
        }

        if (!registered)
        {
            ShortcutError = conflict ?? $"Another app already uses {text}. Try a different shortcut.";
            return false;
        }

        ShortcutError = null;
        IsCapturingShortcut = false;
        GlobalShortcut = text;
        _context.Settings.Update(settings => settings with { GlobalHotkey = text });
        return true;
    }

    // ------------------------------------------------------------------ store

    partial void OnThemeChanged(ThemePreference value)
    {
        Push(settings => settings with { Theme = value });
        UpdateEffectiveTheme();
    }

    partial void OnEditorFontFamilyChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Push(settings => settings with { EditorFontFamily = value.Trim() });
        }
    }

    partial void OnFontSizeChanged(int value) => Push(settings => settings with { EditorFontSize = Math.Clamp(value, MinFontSize, MaxFontSize) });

    partial void OnWordWrapChanged(bool value) => Push(settings => settings with { WordWrap = value });

    partial void OnShowLineNumbersChanged(bool value) => Push(settings => settings with { ShowLineNumbers = value });

    partial void OnTabSizeChanged(int value)
    {
        if (AppSettings.AllowedTabSizes.Contains(value))
        {
            Push(settings => settings with { TabSize = value });
        }
    }

    partial void OnRestoreTabsChanged(bool value) => Push(settings => settings with { RestoreTabs = value });

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        try
        {
            _context.Platform.Autostart.Set(value);
            Push(settings => settings with { StartWithWindows = value });
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Changing start with Windows failed: {ex}");
            _context.Toasts.ShowError(value ? "NimPad couldn't be set to start with Windows." : "NimPad couldn't stop starting with Windows.");
            _syncing = true;
            StartWithWindows = !value;
            _syncing = false;
        }
    }

    private void Push(Func<AppSettings, AppSettings> change)
    {
        if (_syncing)
        {
            return;
        }

        try
        {
            _context.Settings.Update(change);
        }
        catch (ArgumentException ex)
        {
            Trace.TraceWarning($"Rejected setting: {ex.Message}");
            LoadFromStore();
        }
    }

    private void LoadFromStore()
    {
        _syncing = true;
        try
        {
            var settings = _context.Settings.Current;
            Theme = settings.Theme;
            EditorFontFamily = settings.EditorFontFamily;
            FontSize = Math.Clamp(settings.EditorFontSize, MinFontSize, MaxFontSize);
            WordWrap = settings.WordWrap;
            ShowLineNumbers = settings.ShowLineNumbers;
            TabSize = settings.TabSize;
            RestoreTabs = settings.RestoreTabs;
            StartWithWindows = SafeAutostartState(settings.StartWithWindows);
            GlobalShortcut = settings.GlobalHotkey;
            var families = _context.Platform.Fonts.MonospaceFamilies
                .Append(AppSettings.DefaultEditorFontFamily)
                .Append(settings.EditorFontFamily)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (!families.SequenceEqual(FontFamilies))
            {
                FontFamilies = families;
            }
        }
        finally
        {
            _syncing = false;
        }

        UpdateEffectiveTheme();
    }

    private bool SafeAutostartState(bool fallback)
    {
        try
        {
            return _context.Platform.Autostart.IsEnabled;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Reading start with Windows failed: {ex.Message}");
            return fallback;
        }
    }

    private void UpdateEffectiveTheme() => EffectiveIsDark = Theme switch
    {
        ThemePreference.Dark => true,
        ThemePreference.Light => false,
        _ => _context.Platform.SystemTheme.IsDarkMode,
    };
}
