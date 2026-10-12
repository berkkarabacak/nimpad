using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace NimPad.E2E.Infrastructure;

/// <summary>
/// The explorer sidebar of one app: opening it, tree rows (found by Name, as the contract says), the inline rename,
/// context menus, expand/collapse, drag and drop and search. Rows are looked up again before every action, because
/// the virtualized list may recycle row containers whenever the tree changes.
/// </summary>
internal sealed class ExplorerPane(AppSession app)
{
    /// <summary>
    /// True when the sidebar is open: its search box is on screen and, in the docked (Wide) layout the tests run at,
    /// left of the editor. A hidden sidebar may stay in the UI Automation tree with its old bounds (for example when its
    /// column is narrowed to zero and clips it); the editor then covers that area.
    /// </summary>
    public bool IsOpen
    {
        get
        {
            try
            {
                var search = app.FindByAutomationId(AutomationIds.ExplorerSearch);
                if (search is null || !Ui.IsShown(search))
                {
                    return false;
                }

                var editor = app.FindByAutomationId(AutomationIds.Editor);
                return editor is null || !Ui.IsShown(editor) || editor.BoundingRectangle.Left >= search.BoundingRectangle.Right - 4;
            }
            catch (Exception ex) when (Ui.IsTransient(ex))
            {
                return false;
            }
        }
    }

    /// <summary>Opens the sidebar with Ctrl+B unless it is already open (Ctrl+B toggles it). Returns the search box.</summary>
    public AutomationElement Open()
    {
        if (!IsOpen)
        {
            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_B);
        }

        return WaitUntilOpen();
    }

    /// <summary>
    /// Opens the sidebar with Ctrl+B, exercising the shortcut both ways: when the sidebar is already open (the default
    /// on a new profile) it is first hidden with Ctrl+B. Returns the search box.
    /// </summary>
    public AutomationElement OpenWithShortcut()
    {
        if (IsOpen)
        {
            app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_B);
            Ui.WaitUntil(() => !IsOpen, "Ctrl+B to hide the explorer sidebar");
        }

        app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_B);
        return WaitUntilOpen();
    }

    /// <summary>The explorer rows (<c>ExplorerRow_&lt;id&gt;</c>) with this Name, top to bottom.</summary>
    public IReadOnlyList<AutomationElement> FindRows(string name) =>
        FindListElements(AutomationIds.ExplorerRowPrefix, name);

    /// <summary>The explorer rows with this AutomationId, top to bottom (a note can have a Recent row and a tree row).</summary>
    public IReadOnlyList<AutomationElement> FindRowsById(string automationId) =>
        FindListElements(AutomationIds.ExplorerRowPrefix, name: null).Where(row => Ui.IdOf(row) == automationId).ToList();

    /// <summary>
    /// Waits for the workspace-tree row with this Name. The tree is the last section of the list (after Pinned and
    /// Recent), so when a note also has a Recent row, the lowest row is the tree row.
    /// </summary>
    public AutomationElement WaitForTreeRow(string name) =>
        Ui.WaitFor(() => FindRows(name).LastOrDefault(), $"the explorer row named {Ui.Describe(name)} ({AutomationIds.ExplorerRowPrefix}<id>)");

    /// <summary>Invokes the toolbar's New folder button (contract: ExplorerNewFolder) through UI Automation.</summary>
    public void ClickNewFolder()
    {
        app.EnsureForeground();
        Invoke(app.WaitForShown(AutomationIds.ExplorerNewFolder));
    }

    /// <summary>Right-clicks the tree row <paramref name="rowName"/> and chooses <paramref name="itemName"/> from its context menu.</summary>
    public void ChooseFromContextMenu(string rowName, string itemName)
    {
        app.EnsureForeground();
        WaitForTreeRow(rowName).RightClick();
        var item = Ui.WaitFor(() => FindMenuItem(itemName), $"the context menu item {Ui.Describe(itemName)} of {Ui.Describe(rowName)}");
        Invoke(item);

        // The menu closes once the command runs; let it go before the next step looks at keyboard focus.
        Ui.TryWaitUntil(() => FindMenuItem(itemName) is null, Ui.Timeout);
    }

    /// <summary>
    /// Finishes the inline rename that a new folder or note starts by itself: waits for the rename box
    /// (ExplorerRenameBox), replaces its text with <paramref name="newName"/>, presses Enter and waits for the row.
    /// </summary>
    public void CompleteInlineRename(string newName)
    {
        var box = app.WaitForShown(AutomationIds.ExplorerRenameBox);
        AppSession.TryFocus(box);
        app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        app.TypeText(newName);
        Ui.WaitForValue(
            () => Ui.ValueOf(app.FindByAutomationId(AutomationIds.ExplorerRenameBox)),
            value => value == newName,
            $"the rename box ({AutomationIds.ExplorerRenameBox}) to contain {Ui.Describe(newName)}");
        app.PressKeys(VirtualKeyShort.RETURN);
        app.WaitUntilGone(AutomationIds.ExplorerRenameBox);
        WaitForTreeRow(newName);
    }

    /// <summary>The ExpandCollapse state of a tree row (the contract says tree rows expose ExpandCollapse).</summary>
    public ExpandCollapseState? ExpandStateOf(string rowName) =>
        FindRows(rowName).LastOrDefault()?.Patterns.ExpandCollapse.PatternOrDefault?.ExpandCollapseState.ValueOrDefault;

    /// <summary>Collapses or expands the folder row <paramref name="rowName"/> through its ExpandCollapse pattern.</summary>
    public void SetExpanded(string rowName, bool expanded)
    {
        var pattern = WaitForTreeRow(rowName).Patterns.ExpandCollapse.PatternOrDefault
            ?? throw new InvalidOperationException($"The explorer row {Ui.Describe(rowName)} does not expose the ExpandCollapse pattern (contract).");
        if (expanded)
        {
            pattern.Expand();
        }
        else
        {
            pattern.Collapse();
        }
    }

    /// <summary>
    /// Drags the tree row <paramref name="sourceName"/> onto the middle of the tree row <paramref name="targetName"/>
    /// with the real mouse (the middle half of a folder row drops inside it). FlaUI's Mouse.Drag jumps straight to the
    /// end point (set_Position between Down and Up), which can skip WPF's drag threshold and every DragOver, so this
    /// moves in small steps: out of the drag threshold, across to the target, then pauses there so the drag loop
    /// delivers DragOver before the drop.
    /// </summary>
    public void DragOnto(string sourceName, string targetName)
    {
        app.EnsureForeground();
        var from = Center(WaitForTreeRow(sourceName).BoundingRectangle);
        var to = Center(WaitForTreeRow(targetName).BoundingRectangle);
        Mouse.MoveTo(from);
        Wait.UntilInputIsProcessed();
        Mouse.Down(MouseButton.Left);
        try
        {
            Wait.UntilInputIsProcessed();
            var direction = to.Y >= from.Y ? 1 : -1;
            Mouse.MoveTo(new Point(from.X, from.Y + (3 * direction)));
            Mouse.MoveTo(new Point(from.X, from.Y + (12 * direction)));
            Mouse.MoveTo(new Point(to.X, to.Y - (4 * direction)));
            Mouse.MoveTo(to);
            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(400));
            Mouse.MoveTo(new Point(to.X + 2, to.Y));
            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(400));
        }
        finally
        {
            Mouse.Up(MouseButton.Left);
            Wait.UntilInputIsProcessed();
        }
    }

    /// <summary>Types <paramref name="query"/> into the explorer search box (contract: ExplorerSearch, Value pattern).</summary>
    public void Search(string query)
    {
        var box = Open();
        AppSession.TryFocus(box);
        app.PressShortcut(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        app.TypeText(query);
        Ui.WaitForValue(
            () => Ui.ValueOf(app.FindByAutomationId(AutomationIds.ExplorerSearch)),
            value => value == query,
            $"the explorer search box ({AutomationIds.ExplorerSearch}) to contain {Ui.Describe(query)}");
    }

    /// <summary>The search-result rows (<c>ExplorerSearchResult_&lt;id&gt;</c>), top to bottom.</summary>
    public IReadOnlyList<AutomationElement> FindSearchResults() =>
        FindListElements(AutomationIds.ExplorerSearchResultPrefix, name: null);

    /// <summary>Waits for a search-result row with this Name.</summary>
    public AutomationElement WaitForSearchResult(string name) =>
        Ui.WaitFor(
            () => FindListElements(AutomationIds.ExplorerSearchResultPrefix, name).FirstOrDefault(),
            $"a search result named {Ui.Describe(name)} ({AutomationIds.ExplorerSearchResultPrefix}<id>)");

    /// <summary>Clears the search with Esc in the search box (the design's first Esc clears the field).</summary>
    public void ClearSearch()
    {
        AppSession.TryFocus(app.WaitForShown(AutomationIds.ExplorerSearch));
        app.PressKeys(VirtualKeyShort.ESCAPE);
        Ui.WaitForValue(
            () => Ui.ValueOf(app.FindByAutomationId(AutomationIds.ExplorerSearch)),
            value => value == string.Empty,
            $"Esc to clear the explorer search box ({AutomationIds.ExplorerSearch})");
        Ui.WaitUntil(() => FindSearchResults().Count == 0, "the search results to go away");
    }

    private AutomationElement WaitUntilOpen()
    {
        Ui.WaitUntil(() => IsOpen, $"the explorer sidebar to open ({AutomationIds.ExplorerSearch} on screen, left of the editor)");
        app.WaitForShown(AutomationIds.ExplorerList);
        return app.WaitForShown(AutomationIds.ExplorerSearch);
    }

    private IReadOnlyList<AutomationElement> FindListElements(string idPrefix, string? name)
    {
        if (app.FindByAutomationId(AutomationIds.ExplorerList) is not { } list)
        {
            return [];
        }

        var candidates = name is null
            ? list.FindAllDescendants()
            : list.FindAllDescendants(app.Automation.ConditionFactory.ByName(name));
        var matches = candidates
            .Where(element => Ui.IdOf(element)?.StartsWith(idPrefix, StringComparison.Ordinal) == true && Ui.IsShown(element))
            .Select(element => (Element: element, Bounds: element.BoundingRectangle));

        // One element per row: if a row exposes the id twice (its container and an element inside it), keep the outer one.
        var rows = new List<(AutomationElement Element, Rectangle Bounds)>();
        foreach (var match in matches.OrderByDescending(match => match.Bounds.Width * match.Bounds.Height))
        {
            var center = Center(match.Bounds);
            if (!rows.Any(row => row.Bounds.Contains(center)))
            {
                rows.Add(match);
            }
        }

        return rows.OrderBy(row => row.Bounds.Top).Select(row => row.Element).ToList();
    }

    private AutomationElement? FindMenuItem(string name)
    {
        var conditions = app.Automation.ConditionFactory;
        var condition = conditions.ByControlType(ControlType.MenuItem).And(conditions.ByName(name));

        // WPF shows a context menu in a popup window; UI Automation lists it under the main window or as a top-level
        // window of the process. Look in both.
        if (app.MainWindow.FindFirstDescendant(condition) is { } inWindow && Ui.IsShown(inWindow))
        {
            return inWindow;
        }

        return app.FindTopLevelElements()
            .Select(window => window.FindFirstDescendant(condition))
            .FirstOrDefault(item => item is not null && Ui.IsShown(item));
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.PatternOrDefault is { } invoke)
        {
            invoke.Invoke();
        }
        else
        {
            element.Click();
        }

        Wait.UntilInputIsProcessed();
    }

    private static Point Center(Rectangle bounds) => new(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
}
