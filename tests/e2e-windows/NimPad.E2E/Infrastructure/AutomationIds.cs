namespace NimPad.E2E.Infrastructure;

/// <summary>
/// The UI Automation contract the tests are written against (AutomationIds, ItemStatus values). Every id the tests
/// use is listed here, so a contract change is a one-line edit. Ids are case-sensitive.
/// </summary>
internal static class AutomationIds
{
    // Main window (shell)
    public const string MainWindow = "MainWindow";
    public const string TabStrip = "TabStrip";

    /// <summary>Prefix of each tab item: <c>Tab_&lt;n&gt;</c>, n = 0-based visual index; Name = the tab title.</summary>
    public const string TabPrefix = "Tab_";

    public const string CloseButton = "CloseButton";
    public const string StatusPosition = "StatusPosition";
    public const string StatusEncoding = "StatusEncoding";
    public const string StatusSync = "StatusSync";
    public const string EmptyStateNewNote = "EmptyStateNewNote";
    public const string ConfirmDialog = "ConfirmDialog";

    // Editor
    public const string Editor = "Editor";
    public const string Breadcrumbs = "Breadcrumbs";

    // Explorer sidebar
    public const string ExplorerSearch = "ExplorerSearch";
    public const string ExplorerList = "ExplorerList";

    /// <summary>Prefix of each tree/note row: <c>ExplorerRow_&lt;nodeId&gt;</c>; Name = the node name.</summary>
    public const string ExplorerRowPrefix = "ExplorerRow_";

    public const string ExplorerRenameBox = "ExplorerRenameBox";
    public const string ExplorerNewFolder = "ExplorerNewFolder";

    /// <summary>Prefix of each search-result row: <c>ExplorerSearchResult_&lt;nodeId&gt;</c>; Name = the note name.</summary>
    public const string ExplorerSearchResultPrefix = "ExplorerSearchResult_";

    // Settings window
    public const string SettingsWindow = "SettingsWindow";
    public const string ThemeSegmented = "ThemeSegmented";
}

/// <summary>ItemStatus values from the UI Automation contract.</summary>
internal static class ItemStatuses
{
    /// <summary>A tab whose content is stored (nothing waiting to save).</summary>
    public const string Saved = "saved";

    /// <summary>The main window's effective theme marker.</summary>
    public const string ThemeLight = "theme:light";

    /// <summary>The main window's effective theme marker.</summary>
    public const string ThemeDark = "theme:dark";
}
